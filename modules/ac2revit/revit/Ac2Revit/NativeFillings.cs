using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>
/// Doors and windows that were exact IFC geometry without a category's behaviour become real Revit door and window families:
/// the exact IFC body of the filling, in the axes of its wall, becomes the geometry of a loadable family made from Revit's own
/// door or window template. Identical bodies share one family; the instance is hosted in the native wall, so marks, schedules,
/// filters and tags work. The wall opening itself is the separate native cut made by <see cref="OpeningStage"/>.
/// Anything that cannot be reproduced to 3 mm stays exact IFC geometry.
/// </summary>
public static class NativeFillings
{
    public static HashSet<string> Run(Document doc, string project, string ifcSha, string folder, IReadOnlyList<JsonElement> records,
        Dictionary<string, Element> index, Report report, Action<int, int>? progress = null)
    {
        var built = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (records.Count == 0) return built;
        var templates = new Dictionary<string, string?>();
        var symbols = new Dictionary<string, FamilySymbol>();
        var broken = new HashSet<string>();
        int n = 0;
        foreach (var record in records)
        {
            progress?.Invoke(n++, records.Count);
            string key = record.S("sourceGuid");
            try
            {
                string? reason = Place(doc, project, ifcSha, folder, record, index, templates, symbols, broken, out var instance);
                if (reason == null && instance != null)
                {
                    built.Add(key); index[key] = instance;
                    report.Add(key, record.S("kind"), "created", "Native Revit " + (record.S("kind") == "Door" ? "door" : "window") + " family from the exact IFC body, hosted in the native wall; extents and volume agree with the IFC body.", instance.UniqueId);
                }
                else report.Add(key, record.S("kind"), "notice", "Native family not built, exact IFC geometry kept: " + reason);
            }
            catch (Exception ex) { report.Add(key, record.S("kind"), "notice", "Native family not built, exact IFC geometry kept: " + ex.Message); }
        }
        return built;
    }

    static string? Place(Document doc, string project, string ifcSha, string folder, JsonElement record, Dictionary<string, Element> index,
        Dictionary<string, string?> templates, Dictionary<string, FamilySymbol> symbols, HashSet<string> broken, out FamilyInstance? result)
    {
        result = null;
        string kind = record.S("kind");
        if (index.GetValueOrDefault(record.S("hostGuid")) is not Wall wall || wall.Location is not LocationCurve { Curve: Line axis }) return "the host is not a native straight wall";
        var points = record.GetProperty("vertices").Read<List<Point>>().Select(NativeGeometry.At).ToList();
        var faces = record.GetProperty("faces").Read<List<List<int>>>();
        var min = new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
        var max = new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
        var center = (min + max) / 2;
        double angle = Math.Atan2(axis.Direction.Y, axis.Direction.X);
        var local = Transform.CreateRotation(XYZ.BasisZ, -angle).Multiply(Transform.CreateTranslation(center.Negate()));
        var localPoints = points.Select(local.OfPoint).ToList();
        var styles = IfcSurfaceStyles.Read(record, faces.Count);

        string shapeKey = Hash(kind, localPoints, faces, styles);
        if (broken.Contains(shapeKey)) return "the body of this type cannot be built as a family solid";
        string? problemWithFamily = null;
        FamilySymbol? Symbol(string variantKey, List<XYZ> shapePoints)
        {
            if (broken.Contains(variantKey)) { problemWithFamily = "the body of this type cannot be built as a family solid"; return null; }
            if (symbols.TryGetValue(variantKey, out var cached)) return cached;
            string familyKey = "native-filling-family:" + variantKey;
            FamilySymbol made;
            if (index.GetValueOrDefault(familyKey) is Family known) made = (FamilySymbol)doc.GetElement(known.GetFamilySymbolIds().First());
            else
            {
                string? templatePath = Template(doc.Application, kind, templates);
                if (templatePath == null) { problemWithFamily = "Revit has no " + (kind == "Door" ? "door" : "window") + " family template"; return null; }
                string file = Path.Combine(folder, "AC2R_" + kind + "_" + variantKey[..20] + ".rfa");
                try { BuildFamily(doc.Application, templatePath, file, shapePoints, faces, styles, record); }
                catch (Exception ex) { broken.Add(variantKey); problemWithFamily = ex.Message; return null; }
                // Called both outside and inside the instance transaction.
                using var load = doc.IsModifiable ? null : new Transaction(doc, "Load exact filling family");
                load?.Start();
                if (!doc.LoadFamily(file, out var family) || family == null) { if (load?.GetStatus() == TransactionStatus.Started) load.RollBack(); broken.Add(variantKey); problemWithFamily = "Revit could not load the family"; return null; }
                SourceNames.RenameFamily(doc, family, (kind == "Door" ? "Дверь" : "Окно") + " — " + record.S("mark"));
                Storage.Tag(family, project, familyKey, ifcSha, "");
                load?.Commit();
                index[familyKey] = family;
                made = (FamilySymbol)doc.GetElement(family.GetFamilySymbolIds().First());
            }
            symbols[variantKey] = made;
            return made;
        }
        var symbol = Symbol(shapeKey, localPoints);
        if (symbol == null) return problemWithFamily;

        var level = doc.GetElement(wall.LevelId) as Level;
        if (level == null) return "the host wall has no level";
        FamilyInstance? placed = null;
        // One attempt = one transaction. A hosted instance cannot be moved across its wall, so a measured offset is put into a shifted family (loaded outside the transaction) and the attempt is repeated.
        string? Attempt(FamilySymbol symbol, bool allowShift, out XYZ? needShift)
        {
        needShift = null; XYZ? delta0 = null;
        using var transaction = new Transaction(doc, "Native " + kind + " " + record.S("sourceGuid"));
        transaction.Start();
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new DropWarnings()); options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);
        try
        {
            if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
            var instance = doc.Create.NewFamilyInstance(new XYZ(center.X, center.Y, level.Elevation), symbol, wall, level, StructuralType.NonStructural);
            // The family origin is the middle of the IFC body; a door or window is placed by its sill, so state the height of that origin.
            var sill = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (sill != null && !sill.IsReadOnly) sill.Set(center.Z - level.Elevation);
            doc.Regenerate();
            // The template may add its own vertical offset; correct the sill by what is measured.
            if (sill != null && !sill.IsReadOnly)
            {
                Error(instance, min, max, out var lift);
                if (lift != null && Math.Abs(lift.Z) > Geometry.Feet(0.003) && Math.Abs(lift.Z) < Geometry.Feet(10)) { sill.Set(sill.AsDouble() + lift.Z); doc.Regenerate(); }
            }
            // The instance axes follow the wall; flip until its body is where the IFC body is.
            string? problem = null; double bestError = double.MaxValue; int bestStep = 0;
            for (int step = 0; step < 4; ++step)
            {
                if (step == 1) instance.flipFacing();
                else if (step == 2) instance.flipHand();
                else if (step == 3) instance.flipFacing();
                doc.Regenerate();
                double error = Error(instance, min, max, out _);
                if (error < bestError) { bestError = error; bestStep = step; }
                if (error < Geometry.Feet(0.003)) break;
            }
            if (bestError >= Geometry.Feet(0.003))
            {
                // Restore the best flip state, then shift the instance onto the IFC body.
                for (int step = 0; step < 4; ++step)
                {
                    if (step == 1) instance.flipFacing(); else if (step == 2) instance.flipHand(); else if (step == 3) instance.flipFacing();
                    if (step == bestStep) break;
                }
                doc.Regenerate();
                Error(instance, min, max, out var delta); delta0 = delta;
                if (allowShift && delta != null && delta.GetLength() < Geometry.Feet(5))
                {
                    needShift = instance.GetTotalTransform().Inverse.OfVector(delta);
                    transaction.RollBack();
                    return "needs a shifted family";
                }
                bestError = Error(instance, min, max, out _);
            }
            if (bestError >= Geometry.Feet(0.003)) { transaction.RollBack(); return $"the placed body differs from the IFC body by {UnitUtils.ConvertFromInternalUnits(bestError, UnitTypeId.Millimeters):0.#} mm; allowShift=" + allowShift + " delta=" + (delta0 == null ? "null" : Math.Round(UnitUtils.ConvertFromInternalUnits(delta0.GetLength(), UnitTypeId.Millimeters)).ToString()) + "; " + LastBox; }
            double meshVolume = Shells(faces).Where(s => s.Closed).Sum(s => Math.Abs(s.Faces.Sum(i => points[faces[i][0]].DotProduct(points[faces[i][1]].CrossProduct(points[faces[i][2]]))) / 6))
                + Shells(faces).Where(s => !s.Closed).Sum(s => s.Faces.Sum(i => (points[faces[i][1]] - points[faces[i][0]]).CrossProduct(points[faces[i][2]] - points[faces[i][0]]).GetLength() / 2)) * SheetThickness;
            var solids = NativeGeometry.Solids(instance);
            try
            {
                double volume = solids.Sum(s => s.Volume);
                if (Math.Abs(volume - meshVolume) > 0.01 * meshVolume) { transaction.RollBack(); return "volume differs from the IFC body"; }
            }
            finally { foreach (var s in solids) try { s.Dispose(); } catch (Exception) { } }
            Storage.Tag(instance, project, record.S("sourceGuid"), "native-filling:" + ifcSha, record.GetProperty("rawSource").GetRawText());
            SourceMark.Set(instance, record.S("mark"));
            DropWarnings.LastError = ""; if (transaction.Commit() != TransactionStatus.Committed) return "Revit rolled back the instance: " + DropWarnings.LastError + " [family contents: " + LastFamilyContents + "]";
            placed = instance;
            return problem;
        }
        catch (Exception ex)
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            return ex.Message;
        }
        }
        string? outcome = Attempt(symbol, true, out var shiftNeeded);
        if (shiftNeeded != null)
        {
            var shiftedPoints = localPoints.Select(p => p + shiftNeeded).ToList();
            var shiftedSymbol = Symbol(Hash(kind, shiftedPoints, faces, styles), shiftedPoints);
            if (shiftedSymbol == null) return problemWithFamily;
            outcome = Attempt(shiftedSymbol, false, out _);
        }
        result = placed;
        return outcome;
    }

    /// <summary>Largest distance of the instance's extents from the IFC body's extents; delta = how to move it.</summary>
    public static string LastBox = "";
    static double Error(FamilyInstance instance, XYZ min, XYZ max, out XYZ? delta)
    {
        delta = null;
        var solids = NativeGeometry.Solids(instance);
        try
        {
            XYZ? lo = null, hi = null;
            foreach (var solid in solids)
                foreach (Autodesk.Revit.DB.Edge edge in solid.Edges)
                    foreach (var p in edge.Tessellate())
                    {
                        lo = lo == null ? p : new XYZ(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
                        hi = hi == null ? p : new XYZ(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
                    }
            if (lo == null) return double.MaxValue;
            delta = (min + max) / 2 - (lo + hi!) / 2;
            string M(double v) => Math.Round(UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Millimeters)).ToString();
            LastBox = $"instance {M(lo.X)},{M(lo.Y)},{M(lo.Z)}..{M(hi!.X)},{M(hi.Y)},{M(hi.Z)}; expected {M(min.X)},{M(min.Y)},{M(min.Z)}..{M(max.X)},{M(max.Y)},{M(max.Z)}";
            return new[] { Math.Abs(lo.X - min.X), Math.Abs(lo.Y - min.Y), Math.Abs(lo.Z - min.Z), Math.Abs(hi!.X - max.X), Math.Abs(hi.Y - max.Y), Math.Abs(hi.Z - max.Z) }.Max();
        }
        finally { foreach (var s in solids) try { s.Dispose(); } catch (Exception) { } }
    }

    static string Hash(string kind, List<XYZ> points, List<List<int>> faces, (IReadOnlyList<IfcSurfaceStyles.Style> Styles, IReadOnlyList<int> Faces)? styles)
    {
        var text = new StringBuilder(kind);
        foreach (var p in points) text.Append('|').Append(Math.Round(UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Millimeters))).Append(',').Append(Math.Round(UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Millimeters))).Append(',').Append(Math.Round(UnitUtils.ConvertFromInternalUnits(p.Z, UnitTypeId.Millimeters)));
        foreach (var f in faces) text.Append('#').Append(string.Join(",", f));
        if (styles != null) { text.Append('@').Append(string.Join(";", styles.Value.Styles.Select(s => s.MaterialName))).Append(string.Join(",", styles.Value.Faces)); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    static string? Template(Autodesk.Revit.ApplicationServices.Application app, string kind, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(kind, out var known)) return known;
        string[] names = kind == "Door" ? ["Метрическая система, дверь.rft", "Metric Door.rft"] : ["Метрическая система, окно.rft", "Metric Window.rft"];
        string? found = null;
        string root = app.FamilyTemplatePath;
        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            foreach (var name in names)
            {
                found = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) break;
            }
        cache[kind] = found;
        return found;
    }

    static void BuildFamily(Autodesk.Revit.ApplicationServices.Application app, string template, string file, List<XYZ> points, List<List<int>> faces,
        (IReadOnlyList<IfcSurfaceStyles.Style> Styles, IReadOnlyList<int> Faces)? styles, JsonElement record)
    {
        if (File.Exists(file)) File.Delete(file);
        var family = app.NewFamilyDocument(template);
        try
        {
            var materials = new Dictionary<IfcSurfaceStyles.Style, ElementId>();
            if (styles != null)
            {
                using var materialTransaction = new Transaction(family, "Surface materials");
                materialTransaction.Start();
                foreach (var style in styles.Value.Styles.Distinct())
                {
                    var id = Material.Create(family, style.MaterialName);
                    var material = (Material)family.GetElement(id);
                    material.Color = new Color((byte)style.R, (byte)style.G, (byte)style.B); material.Transparency = style.Transparency;
                    materials[style] = id;
                }
                materialTransaction.Commit();
            }
            var solids = BuildSolids(points, faces, styles, materials);
            using (var t = new Transaction(family, "Exact filling geometry"))
            {
                t.Start();
                var options = t.GetFailureHandlingOptions(); options.SetFailuresPreprocessor(new DropWarnings()); t.SetFailureHandlingOptions(options);
                // The template's own frame, sash and cut: only the exact body remains.
                var oldForms = new FilteredElementCollector(family).OfClass(typeof(GenericForm)).Select(e => e.Id).ToList();
                // The template's own wall opening cut would try to cut the host again; the native cut is a separate element.
                oldForms.AddRange(new FilteredElementCollector(family).OfClass(typeof(Opening)).Select(e => e.Id));
                if (oldForms.Count > 0) family.Delete(oldForms);
                foreach (var solid in solids) FreeFormElement.Create(family, solid);
                family.Regenerate();
                LastFamilyContents = string.Join(", ", new FilteredElementCollector(family).WhereElementIsNotElementType().GroupBy(e => e.GetType().Name + "/" + (e.Category?.Name ?? "-")).Select(g => g.Key + "x" + g.Count()));
                if (t.Commit() != TransactionStatus.Committed) throw new InvalidDataException("family geometry rolled back");
            }
            family.SaveAs(file, new SaveAsOptions { OverwriteExistingFile = true });
        }
        finally { family.Close(false); }
    }

    public static string LastFamilyContents = "";
    public const double SheetThickness = 0.004 / 0.3048; // a glass pane given as a surface becomes a 4 mm plate

    /// <summary>
    /// Solids of a filling body. Closed shells are built as they are (exact, per shell; the BRep builder takes the ones the tessellation builder
    /// refuses). A shell that is an open planar sheet (glass without thickness) becomes a plate of <see cref="SheetThickness"/>.
    /// </summary>
    static List<Solid> BuildSolids(List<XYZ> points, List<List<int>> faces, (IReadOnlyList<IfcSurfaceStyles.Style> Styles, IReadOnlyList<int> Faces)? styles, Dictionary<IfcSurfaceStyles.Style, ElementId> materials)
    {
        ElementId MaterialOf(int face) => styles != null && styles.Value.Faces[face] >= 0 ? materials[styles.Value.Styles[styles.Value.Faces[face]]] : ElementId.InvalidElementId;
        var solids = new List<Solid>();
        foreach (var shell in Shells(faces))
        {
            if (shell.Closed)
            {
                try
                {
                    using var builder = new TessellatedShapeBuilder();
                    builder.OpenConnectedFaceSet(true);
                    foreach (int i in shell.Faces)
                    {
                        using var face = new TessellatedFace(faces[i].Select(k => points[k]).ToList(), MaterialOf(i));
                        if (!builder.DoesFaceHaveEnoughLoopsAndVertices(face)) throw new InvalidDataException("Invalid solid face");
                        builder.AddFace(face);
                    }
                    builder.CloseConnectedFaceSet(); builder.Target = TessellatedShapeBuilderTarget.Solid; builder.Fallback = TessellatedShapeBuilderFallback.Abort; builder.Build();
                    var result = builder.GetBuildResult(); var geometry = result.GetGeometricalObjects();
                    if (result.HasInvalidData || geometry.Count == 0 || geometry.Any(g => g is not Solid)) throw new InvalidDataException("the shell cannot form a solid");
                    solids.AddRange(geometry.Cast<Solid>());
                }
                catch (Exception)
                {
                    try { solids.AddRange(NativeGeometry.BrepSolids(points, shell.Faces.Select(i => faces[i]).ToList())); }
                    catch (Exception)
                    {
                        // Edges below the tolerance of the builders: weld points closer than 0.5 mm; the placed body is still checked against the IFC body.
                        solids.AddRange(Welded(points, faces, shell.Faces, MaterialOf));
                    }
                }
                continue;
            }
            // Open shell (glass as a surface): each triangle becomes a thin plate along its own normal.
            foreach (int i in shell.Faces) solids.Add(Plate(points[faces[i][0]], points[faces[i][1]], points[faces[i][2]], MaterialOf(i)));
        }
        if (solids.Count == 0) throw new InvalidDataException("the body has no solids");
        return solids;
    }

    static List<Solid> Welded(List<XYZ> points, List<List<int>> faces, List<int> shell, Func<int, ElementId> materialOf)
    {
        Exception? last = null;
        foreach (double mm in new[] { 0.5, 2.0, 5.0 })
        {
            try { return Welded(points, faces, shell, materialOf, mm); }
            catch (Exception ex) { last = ex; }
        }
        throw last!;
    }

    static List<Solid> Welded(List<XYZ> points, List<List<int>> faces, List<int> shell, Func<int, ElementId> materialOf, double toleranceMm)
    {
        double cell = toleranceMm / 304.8;
        var representative = new Dictionary<(long, long, long), int>(); var map = new Dictionary<int, int>();
        int Weld(int index)
        {
            if (map.TryGetValue(index, out int known)) return known;
            var p = points[index]; var key = ((long)Math.Round(p.X / cell), (long)Math.Round(p.Y / cell), (long)Math.Round(p.Z / cell));
            if (!representative.TryGetValue(key, out int rep)) representative[key] = rep = index;
            return map[index] = rep;
        }
        using var builder = new TessellatedShapeBuilder();
        builder.OpenConnectedFaceSet(true);
        foreach (int i in shell)
        {
            var ring = faces[i].Select(Weld).ToList();
            if (ring.Distinct().Count() < 3) continue;
            using var face = new TessellatedFace(ring.Select(k => points[k]).ToList(), materialOf(i));
            if (!builder.DoesFaceHaveEnoughLoopsAndVertices(face)) continue;
            builder.AddFace(face);
        }
        builder.CloseConnectedFaceSet(); builder.Target = TessellatedShapeBuilderTarget.Solid; builder.Fallback = TessellatedShapeBuilderFallback.Abort; builder.Build();
        var result = builder.GetBuildResult(); var geometry = result.GetGeometricalObjects();
        if (result.HasInvalidData || geometry.Count == 0 || geometry.Any(g => g is not Solid)) throw new InvalidDataException("the shell cannot form a solid even after welding points closer than 0.5 mm");
        return geometry.Cast<Solid>().ToList();
    }

    static Solid Plate(XYZ a, XYZ b, XYZ c, ElementId material)
    {
        var n = (b - a).CrossProduct(c - a).Normalize(); var h = n * (SheetThickness / 2);
        using var builder = new TessellatedShapeBuilder();
        builder.OpenConnectedFaceSet(true);
        void Add(params XYZ[] ring) { using var f = new TessellatedFace(ring.ToList(), material); builder.AddFace(f); }
        Add(a + h, b + h, c + h); Add(a - h, c - h, b - h);
        Add(a - h, b - h, b + h, a + h); Add(b - h, c - h, c + h, b + h); Add(c - h, a - h, a + h, c + h);
        builder.CloseConnectedFaceSet(); builder.Target = TessellatedShapeBuilderTarget.Solid; builder.Fallback = TessellatedShapeBuilderFallback.Abort; builder.Build();
        var result = builder.GetBuildResult(); var geometry = result.GetGeometricalObjects();
        if (result.HasInvalidData || geometry.Count != 1 || geometry[0] is not Solid solid) throw new InvalidDataException("a glass plate cannot be built");
        return solid;
    }

    /// <summary>Triangles grouped into shells by shared edges; Closed = every edge is shared by exactly two triangles.</summary>
    static List<(List<int> Faces, bool Closed)> Shells(List<List<int>> faces)
    {
        var parent = Enumerable.Range(0, faces.Count).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var first = new Dictionary<(int, int), int>(); var uses = new Dictionary<(int, int), int>();
        for (int i = 0; i < faces.Count; ++i)
            for (int k = 0; k < 3; ++k)
            {
                int a = faces[i][k], b = faces[i][(k + 1) % 3]; var edge = (Math.Min(a, b), Math.Max(a, b));
                uses[edge] = uses.GetValueOrDefault(edge) + 1;
                if (first.TryGetValue(edge, out int other)) parent[Find(i)] = Find(other); else first[edge] = i;
            }
        var groups = Enumerable.Range(0, faces.Count).GroupBy(Find).Select(g => g.ToList()).ToList();
        return groups.Select(g => (g, g.All(i => Enumerable.Range(0, 3).All(k => uses[(Math.Min(faces[i][k], faces[i][(k + 1) % 3]), Math.Max(faces[i][k], faces[i][(k + 1) % 3]))] == 2)))).ToList();
    }

    sealed class DropWarnings : IFailuresPreprocessor
    {
        public static string LastError = "";
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool error = false;
            foreach (var message in accessor.GetFailureMessages())
                if (message.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(message); else { error = true; LastError = message.GetDescriptionText(); }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
