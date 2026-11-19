using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>
/// Archicad walls with an arbitrary vertical profile (under an outdoor stair, a gable end) have no nominal prism.
/// They used to stay IFC geometry without layers, joins or room bounding. When the exact IFC body is a prism through the
/// wall thickness, its profile is read from the mesh and built as a native Revit wall by profile with the wall type of
/// the source composite. The result is kept only if volume and extents agree with the IFC body.
/// </summary>
public static class NativeProfileWalls
{
    public static HashSet<string> Run(Document doc, Manifest manifest, IReadOnlyList<JsonElement> records, Report report)
    {
        var built = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (records.Count == 0) return built;
        string project = manifest.Source.ProjectId;
        var geometry = new Geometry(doc, manifest.Coordinates.Transform);
        var index = Storage.Index(doc, project);
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        var sourceByGuid = manifest.Elements.Where(e => e.Kind == "Wall").ToDictionary(e => e.SourceGuid, StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            string key = record.S("sourceGuid");
            try
            {
                string? reason = Build(doc, manifest, geometry, index, levels, sourceByGuid, record, out var wall);
                if (reason == null && wall != null)
                {
                    built.Add(key);
                    report.Add(key, "Wall", "created", "Native Revit wall by profile; volume and extents agree with the exact IFC body.", wall.UniqueId);
                }
                else report.Add(key, "Wall", "notice", "Native profile wall not built, exact IFC geometry kept: " + reason);
            }
            catch (Exception ex) { report.Add(key, "Wall", "notice", "Native profile wall not built, exact IFC geometry kept: " + ex.Message); }
        }
        return built;
    }

    static string? Build(Document doc, Manifest manifest, Geometry geometry, IReadOnlyDictionary<string, Element> index, List<Level> levels,
        IReadOnlyDictionary<string, ElementRecord> sourceByGuid, JsonElement record, out Wall? result)
    {
        result = null;
        if (!record.TryGetProperty("rawSource", out var raw) || !raw.TryGetProperty("details", out var d)) return "source wall details are missing";
        if (d.S("profileType") != "Poly" || d.S("geometryType", "Straight") != "Straight" || Math.Abs(d.D("arcAngle")) > 1e-9) return "not a straight profile wall";
        var a = d.GetProperty("begCoordinate"); var b = d.GetProperty("endCoordinate");
        double ax = a.D("x"), ay = a.D("y"), dx = b.D("x") - ax, dy = b.D("y") - ay, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-6) return "wall axis is degenerate";
        double ux = dx / length, uy = dy / length, nx = -uy, ny = ux;

        var vertices = record.GetProperty("vertices").EnumerateArray().Select(p => (X: p.D("x"), Y: p.D("y"), Z: p.D("z"))).ToList();
        var faces = record.GetProperty("faces").EnumerateArray().Select(f => f.EnumerateArray().Select(i => i.GetInt32()).ToArray()).ToList();
        // Across the wall (t) the body must have exactly two parallel faces; along the wall (s) and up (z) it is the profile.
        var local = vertices.Select(v => (S: (v.X - ax) * ux + (v.Y - ay) * uy, T: (v.X - ax) * nx + (v.Y - ay) * ny, Z: v.Z)).ToList();
        double t0 = local.Min(v => v.T), t1 = local.Max(v => v.T), thickness = t1 - t0;
        if (local.Any(v => Math.Abs(v.T - t0) > 0.001 && Math.Abs(v.T - t1) > 0.001)) return "the wall body is not a prism through its thickness";
        if (thickness < 0.01) return "wall body is too thin";
        (IReadOnlyList<RoofPlanner.P> Outer, IReadOnlyList<IReadOnlyList<RoofPlanner.P>> Holes) profile;
        try { profile = RoofPlanner.Silhouette(local.Select(v => (X: v.S, Y: v.Z, Z: v.T)).ToList(), faces, 0.002, 0.0005); }
        catch (InvalidDataException ex) { return ex.Message; }
        if (profile.Holes.Count > 0) return "profile with openings is not supported by Wall.Create";

        // The Revit type of the source composite, with the same total thickness.
        string composite = d.TryGetProperty("compositeId", out var c) ? c.S("guid") : "";
        string material = d.TryGetProperty("buildingMaterialId", out var m) ? m.S("guid") : "";
        var candidates = manifest.Types.Where(t => t.Kind == "Wall" && Math.Abs(t.Layers.Sum(l => l.Width) - thickness) < 0.002 &&
            (composite.Length > 0 ? string.Equals(t.SourceCompositeGuid, composite, StringComparison.OrdinalIgnoreCase)
                                  : t.Layers.Count == 1 && string.Equals(t.Layers[0].MaterialKey, material, StringComparison.OrdinalIgnoreCase))).ToList();
        var wallType = candidates.Select(t => index.GetValueOrDefault(t.Key) as WallType).FirstOrDefault(t => t != null);
        if (wallType == null) return "the wall type of this construction is not in the model";

        double tMid = (t0 + t1) / 2;
        double minZ = vertices.Min(v => v.Z), maxZ = vertices.Max(v => v.Z);
        double dz = manifest.Coordinates.Transform.Translation.Z;
        var level = levels.LastOrDefault(l => l.Elevation <= Geometry.Feet(minZ + dz) + Geometry.Feet(0.001)) ?? levels.FirstOrDefault();
        if (level == null) return "the model has no level";
        XYZ At(RoofPlanner.P p) => geometry.Xyz(new Point { X = ax + ux * p.X + nx * tMid, Y = ay + uy * p.X + ny * tMid, Z = p.Y });
        var curves = new List<Curve>();
        var outline = profile.Outer;
        for (int i = 0; i < outline.Count; ++i) curves.Add(Line.CreateBound(At(outline[i]), At(outline[(i + 1) % outline.Count])));

        using var transaction = new Transaction(doc, "Native profile wall");
        transaction.Start();
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new DropWarnings()); options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);
        try
        {
            var wall = Wall.Create(doc, curves, wallType.Id, level.Id, false);
            wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(ElementId.InvalidElementId);
            double lowest = curves.Min(c => Math.Min(c.GetEndPoint(0).Z, c.GetEndPoint(1).Z)), highest = curves.Max(c => Math.Max(c.GetEndPoint(0).Z, c.GetEndPoint(1).Z));
            wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(highest - lowest);
            wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.Set(lowest - level.Elevation);
            wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING)?.Set(d.TryGetProperty("zoneRel", out var zone) && zone.GetString() != "Boundary" ? 0 : 1);
            doc.Regenerate();
            var box = wall.get_BoundingBox(null);
            if (box == null) throw new InvalidDataException("Revit produced no wall geometry");
            var meshPoints = vertices.Select(v => geometry.Xyz(new Point { X = v.X, Y = v.Y, Z = v.Z })).ToList();
            double tol = Geometry.Feet(0.003);
            if (Math.Abs(box.Min.X - meshPoints.Min(p => p.X)) > tol || Math.Abs(box.Max.X - meshPoints.Max(p => p.X)) > tol ||
                Math.Abs(box.Min.Y - meshPoints.Min(p => p.Y)) > tol || Math.Abs(box.Max.Y - meshPoints.Max(p => p.Y)) > tol ||
                Math.Abs(box.Min.Z - meshPoints.Min(p => p.Z)) > tol || Math.Abs(box.Max.Z - meshPoints.Max(p => p.Z)) > tol)
            { transaction.RollBack(); return "wall extents differ from the IFC body"; }
            double meshVolume = 0;
            foreach (var f in faces)
            {
                var p0 = vertices[f[0]]; var p1 = vertices[f[1]]; var p2 = vertices[f[2]];
                meshVolume += (p0.X * (p1.Y * p2.Z - p1.Z * p2.Y) - p0.Y * (p1.X * p2.Z - p1.Z * p2.X) + p0.Z * (p1.X * p2.Y - p1.Y * p2.X)) / 6;
            }
            meshVolume = Math.Abs(meshVolume);
            var solids = NativeGeometry.Solids(wall);
            double volume;
            try { volume = UnitUtils.ConvertFromInternalUnits(solids.Sum(s => s.Volume), UnitTypeId.CubicMeters); }
            finally { foreach (var s in solids) try { s.Dispose(); } catch (Exception) { } }
            if (Math.Abs(volume - meshVolume) > 0.01 * meshVolume) { transaction.RollBack(); return $"volume {volume:0.###} m3 differs from the IFC body {meshVolume:0.###} m3"; }
            Storage.Tag(wall, manifest.Source.ProjectId, record.S("sourceGuid"), "native-profile-wall:" + record.S("sourceGuid"), raw.GetRawText());
            SourceMark.Set(wall, sourceByGuid.TryGetValue(record.S("sourceGuid"), out var source) ? source.Mark : record.S("mark"));
            if (transaction.Commit() != TransactionStatus.Committed) return "Revit rolled back the wall";
            result = wall;
            return null;
        }
        catch (Exception ex)
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            return ex.Message;
        }
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
