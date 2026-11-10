using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>
/// Builds a Revit "roof by footprint" for pitched Archicad roofs and keeps it only if it reproduces the exact IFC
/// mesh of that roof (volume, area of the upper surface, extents). Anything else stays exact IFC geometry
/// with the reason in the report, never a simplified shape.
/// </summary>
public static class NativeRoofs
{
    const double VolumeTolerance = 0.03, AreaTolerance = 0.03, ExtentToleranceMeters = 0.03;

    public static HashSet<string> Run(Document doc, string project, string ifcHash, Manifest manifest, IReadOnlyList<JsonElement> records, Report report)
    {
        var built = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (records.Count == 0) return built;
        var geometry = new Geometry(doc, manifest.Coordinates.Transform);
        var index = Storage.Index(doc, project);
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        foreach (var record in records)
        {
            string key = record.S("sourceGuid");
            try
            {
                string? reason = Build(doc, project, ifcHash, manifest, geometry, index, levels, record, false, out var roof);
                if (reason != null)
                {
                    string? second = Build(doc, project, ifcHash, manifest, geometry, index, levels, record, true, out roof);
                    reason = second == null ? null : reason + "; by the mesh silhouette: " + second;
                }
                if (reason == null && roof != null)
                {
                    built.Add(key);
                    report.Add(key, "Roof", "created", "Native Revit roof by footprint; volume, upper-surface area and extents agree with the exact IFC mesh. Exact IFC geometry is not duplicated.", roof.UniqueId);
                }
                else report.Add(key, "Roof", "notice", "Native roof not built, exact IFC geometry kept: " + reason);
            }
            catch (Exception ex) { report.Add(key, "Roof", "notice", "Native roof not built, exact IFC geometry kept: " + ex.Message + Where(ex)); }
        }
        return built;
    }

    static string? Build(Document doc, string project, string ifcHash, Manifest manifest, Geometry geometry, IReadOnlyDictionary<string, Element> index,
        List<Level> levels, JsonElement record, bool silhouette, out Element? result)
    {
        result = null;
        if (!record.TryGetProperty("rawSource", out var raw) || !raw.TryGetProperty("details", out var details)) return "source roof details are missing";
        if (details.S("roofClass") != "MultiPlane" && details.S("roofClass") != "SinglePlane") return "roof class " + details.S("roofClass") + " has no footprint rule";
        var sourceLayers = new List<(double Width, string Function, string MaterialKey)>();
        if (details.S("structureType") == "Composite")
        {
            string composite = details.GetProperty("compositeId").S("guid");
            var sourceType = manifest.Types.FirstOrDefault(t => composite.Length > 0 && string.Equals(t.SourceCompositeGuid, composite, StringComparison.OrdinalIgnoreCase));
            if (sourceType == null) return "the roof's composite is not among the manifest constructions";
            sourceLayers = sourceType.Layers.Select(l => (l.Width, l.Function, l.MaterialKey)).ToList();
        }
        else if (details.S("structureType") != "Basic") return "roof structure " + details.S("structureType") + " is not mapped";
        var vertices = record.GetProperty("vertices").EnumerateArray().Select(p => (X: p.D("x"), Y: p.D("y"), Z: p.D("z"))).ToList();
        var triangles = record.GetProperty("faces").EnumerateArray().Select(f => f.EnumerateArray().Select(i => i.GetInt32()).ToArray()).ToList();
        List<RoofPlanner.P> outline; IReadOnlyList<IReadOnlyList<RoofPlanner.P>> holes;
        try
        {
            if (silhouette) { var s = RoofPlanner.Silhouette(vertices, triangles); outline = s.Outer.ToList(); holes = s.Holes; }
            else
            {
                if (!details.TryGetProperty("polygonOutline", out var outlineJson)) return "roof outline is missing";
                outline = outlineJson.EnumerateArray().Select(p => new RoofPlanner.P(p.D("x"), p.D("y"))).ToList();
                holes = details.TryGetProperty("holes", out var holesJson)
                    ? holesJson.EnumerateArray().Select(h => (IReadOnlyList<RoofPlanner.P>)h.GetProperty("polygonOutline").EnumerateArray().Select(p => new RoofPlanner.P(p.D("x"), p.D("y"))).ToList()).ToList()
                    : new List<IReadOnlyList<RoofPlanner.P>>();
            }
        }
        catch (InvalidDataException ex) { return ex.Message; }
        RoofPlanner.Plan plan;
        try { plan = RoofPlanner.Build(vertices, triangles, outline, holes); }
        catch (InvalidDataException ex) { return ex.Message; }

        double thicknessMeters = details.D("thickness");
        if (!double.IsFinite(thicknessMeters) || thicknessMeters <= 0) return "invalid roof thickness";
        Material? MaterialOf(string guid) => guid.Length > 0 && index.TryGetValue("material:" + guid.ToUpperInvariant(), out var found) ? found as Material : null;
        if (sourceLayers.Count == 0)
            sourceLayers.Add((thicknessMeters, "Core", details.TryGetProperty("buildingMaterialId", out var basic) ? basic.S("guid") : ""));
        double layersTotal = sourceLayers.Sum(l => l.Width);
        if (Math.Abs(layersTotal - thicknessMeters) > 1e-6) return "composite layers do not add up to the roof thickness";

        double dz = manifest.Coordinates.Transform.Translation.Z;
        double eave = Geometry.Feet(plan.EaveZ + dz);
        var level = levels.LastOrDefault(l => l.Elevation <= eave + Geometry.Feet(0.001)) ?? levels.FirstOrDefault();
        if (level == null) return "the model has no level";
        var allXy = vertices.Select(v => geometry.Xyz(new Point { X = v.X, Y = v.Y, Z = v.Z })).ToList();
        double minX = allXy.Min(p => p.X), maxX = allXy.Max(p => p.X), minY = allXy.Min(p => p.Y), maxY = allXy.Max(p => p.Y);
        double meshMinZ = Geometry.Feet(plan.MinZ + dz), meshMaxZ = Geometry.Feet(plan.MaxZ + dz);
        double meanSlope = plan.Outer.Where(e => e.Slope.HasValue).Select(e => e.Slope!.Value).DefaultIfEmpty(0).Average();
        double cos = 1 / Math.Sqrt(1 + meanSlope * meanSlope);
        // Revit may measure a sloped roof's thickness along the normal or vertically; the mesh decides which one matches.
        var thicknesses = meanSlope < 1e-6 ? new[] { thicknessMeters } : new[] { thicknessMeters, thicknessMeters / cos, thicknessMeters * cos };
        string lastProblem = "no thickness reproduced the mesh";
        foreach (double candidate in thicknesses)
        {
            using var transaction = new Transaction(doc, "Native roof");
            transaction.Start();
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new DropWarnings());
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
            try
            {
                var type = EnsureType(doc, sourceLayers.Select(l => (l.Width * candidate / thicknessMeters, l.Function, MaterialOf(l.MaterialKey))).ToList());
                var profile = new CurveArray();
                var edgeCurves = new List<Curve>();
                foreach (var e in plan.Outer)
                {
                    var a = Flat(geometry, e.A, level.Elevation); var b = Flat(geometry, e.B, level.Elevation);
                    var line = Line.CreateBound(a, b); profile.Append(line); edgeCurves.Add(line);
                }
                foreach (var hole in plan.Holes)
                    for (int i = 0; i < hole.Count; ++i)
                        profile.Append(Line.CreateBound(Flat(geometry, hole[i], level.Elevation), Flat(geometry, hole[(i + 1) % hole.Count], level.Elevation)));
                var mapping = new ModelCurveArray();
                var roof = doc.Create.NewFootPrintRoof(profile, level, type, out mapping);
                roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(eave - level.Elevation);
                foreach (ModelCurve curve in mapping)
                {
                    var mid = curve.GeometryCurve.Evaluate(0.5, true);
                    int best = -1; double distance = double.MaxValue;
                    for (int i = 0; i < plan.Outer.Count; ++i)
                    {
                        var m = edgeCurves[i].Evaluate(0.5, true);
                        double d = Math.Sqrt((m.X - mid.X) * (m.X - mid.X) + (m.Y - mid.Y) * (m.Y - mid.Y));
                        if (d < distance) { distance = d; best = i; }
                    }
                    bool isOuter = best >= 0 && distance < Geometry.Feet(0.002);
                    var slope = isOuter ? plan.Outer[best].Slope : null;
                    roof.set_DefinesSlope(curve, slope.HasValue);
                    if (slope.HasValue) roof.set_SlopeAngle(curve, slope.Value);
                    double lift = slope.HasValue ? plan.Outer[best].TopZ - plan.EaveZ : 0;
                    if (Math.Abs(lift) > 0.001) roof.set_Offset(curve, Geometry.Feet(lift));
                }
                doc.Regenerate();
                var box = roof.get_BoundingBox(null);
                if (box == null) throw new InvalidDataException("Revit produced no roof geometry; footprint " + string.Join(" | ", plan.Outer.Select(e => $"({e.A.X:0.##},{e.A.Y:0.##})->({e.B.X:0.##},{e.B.Y:0.##}) slope {(e.Slope.HasValue ? e.Slope.Value.ToString("0.###") : "-")} z {e.TopZ:0.###}")) + "; eave " + plan.EaveZ.ToString("0.###"));
                // The footprint reference plane differs from the top surface by the slab thickness; correct it once.
                double shift = meshMaxZ - box.Max.Z;
                if (Math.Abs(shift) > Geometry.Feet(0.001))
                {
                    roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(eave - level.Elevation + shift);
                    doc.Regenerate();
                    box = roof.get_BoundingBox(null)!;
                }
                var problem = Verify(roof, plan, box, minX, maxX, minY, maxY, meshMinZ, meshMaxZ);
                if (problem != null) { lastProblem = $"thickness {candidate * 1000:0} mm: {problem}"; transaction.RollBack(); continue; }
                Storage.Tag(roof, project, record.S("sourceGuid"), "native-roof:" + ifcHash, raw.GetRawText());
                SourceMark.Set(roof, record.S("mark"));
                if (transaction.Commit() != TransactionStatus.Committed) { lastProblem = "Revit rolled back the roof"; continue; }
                result = roof;
                return null;
            }
            catch (Exception ex)
            {
                if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                lastProblem = ex.Message + Where(ex);
            }
        }
        return lastProblem;
    }

    static string Where(Exception ex) => " @ " + (ex.StackTrace ?? "").Split((char)10).FirstOrDefault()?.Trim();

    static XYZ Flat(Geometry geometry, RoofPlanner.P p, double z)
    {
        var q = geometry.Xyz(new Point { X = p.X, Y = p.Y, Z = 0 });
        return new XYZ(q.X, q.Y, z);
    }

    static string? Verify(Element roof, RoofPlanner.Plan plan, BoundingBoxXYZ box, double minX, double maxX, double minY, double maxY, double meshMinZ, double meshMaxZ)
    {
        double tol = Geometry.Feet(ExtentToleranceMeters);
        if (Math.Abs(box.Min.X - minX) > tol || Math.Abs(box.Max.X - maxX) > tol || Math.Abs(box.Min.Y - minY) > tol || Math.Abs(box.Max.Y - maxY) > tol)
            return "plan extents differ from the IFC mesh";
        if (Math.Abs(box.Max.Z - meshMaxZ) > tol) return "top level differs from the IFC mesh";
        if (Math.Abs(box.Min.Z - meshMinZ) > tol) return "lowest level differs from the IFC mesh";
        var solids = NativeGeometry.Solids(roof);
        try
        {
            double volume = UnitUtils.ConvertFromInternalUnits(solids.Sum(s => s.Volume), UnitTypeId.CubicMeters);
            if (Math.Abs(volume - plan.MeshVolume) > VolumeTolerance * plan.MeshVolume)
                return $"volume {volume:0.###} m3 differs from the IFC mesh {plan.MeshVolume:0.###} m3";
            double area = 0;
            foreach (var solid in solids)
                foreach (Face face in solid.Faces)
                    if (face is PlanarFace planar && planar.FaceNormal.Z > 0.02) area += UnitUtils.ConvertFromInternalUnits(planar.Area, UnitTypeId.SquareMeters);
            if (Math.Abs(area - plan.TopArea) > AreaTolerance * plan.TopArea)
                return $"upper surface {area:0.###} m2 differs from the IFC mesh {plan.TopArea:0.###} m2";
        }
        finally { foreach (var s in solids) try { s.Dispose(); } catch (Exception) { } }
        return null;
    }

    static RoofType EnsureType(Document doc, List<(double Width, string Function, Material? Material)> source)
    {
        double total = source.Sum(l => l.Width);
        string name = SourceLabel.Format($"Крыша {total * 1000:0} мм" + (source.Count == 1 ? (source[0].Material == null ? "" : ", " + source[0].Material!.Name) : $", {source.Count} сл. " + string.Join("+", source.Select(l => (l.Width * 1000).ToString("0")))));
        var all = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().ToList();
        var existing = all.FirstOrDefault(t => t.Name == name);
        if (existing != null) return existing;
        var basis = all.FirstOrDefault(t => t.FamilyName.Contains("Базов", StringComparison.OrdinalIgnoreCase) || t.FamilyName.Contains("Basic", StringComparison.OrdinalIgnoreCase)) ?? all.First();
        var cores = source.Select((l, i) => (l, i)).Where(x => x.l.Function == "Core").Select(x => x.i).ToList();
        if (cores.Count == 0) cores = source.Select((l, i) => (l, i)).Where(x => x.l.Function is not ("Finish" or "Membrane")).Select(x => x.i).ToList();
        if (cores.Count == 0) cores = [source.Select((l, i) => (l, i)).Where(x => x.l.Function != "Membrane").OrderByDescending(x => x.l.Width).First().i];
        int first = cores[0], last = cores[^1];
        var layers = source.Select((l, i) => new CompoundStructureLayer(Geometry.Feet(l.Width),
            l.Function switch { "Core" => MaterialFunctionAssignment.Structure, "Membrane" => MaterialFunctionAssignment.Membrane, "Finish" => i < first ? MaterialFunctionAssignment.Finish1 : MaterialFunctionAssignment.Finish2, _ => MaterialFunctionAssignment.Substrate },
            l.Material?.Id ?? ElementId.InvalidElementId)).ToList();
        var type = (RoofType)basis.Duplicate(name);
        using var structure = CompoundStructure.CreateSimpleCompoundStructure(layers);
        structure.SetNumberOfShellLayers(ShellLayerType.Exterior, first);
        structure.SetNumberOfShellLayers(ShellLayerType.Interior, source.Count - last - 1);
        if (!structure.IsValid(doc, out var errors, out _)) throw new InvalidDataException("Invalid roof structure: " + string.Join(", ", errors.Select(p => p.Key + ":" + p.Value)));
        type.SetCompoundStructure(structure);
        return type;
    }

    sealed class DropWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool error = false;
            foreach (var message in accessor.GetFailureMessages())
                if (message.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(message); else error = true;
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
