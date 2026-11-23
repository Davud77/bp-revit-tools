using Autodesk.Revit.DB;

namespace Ac2Revit;

/// <summary>
/// Archicad resolves overlapping elements by priority, so a wall that runs up into a slab, or around a column or
/// beam, is simply not drawn inside it. Revit keeps both solids: shared volume shows as stripes and coplanar faces.
/// Revit's own answer is Join Geometry with the structure cutting the wall: it removes only the volume the two
/// share, so walls that end below the slab, or continue above it (parapets), keep their shape. Priority: slab, column
/// and beam cut walls; a column cuts the beam that runs into it.
/// </summary>
public static class SlabWallJoin
{
    public static void Run(Document doc, IReadOnlyDictionary<string, Element> imported, double tolerance, Report report, Action<string, int, int>? progress = null)
    {
        var floors = imported.Where(p => p.Value is Floor).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        var walls = imported.Where(p => p.Value is Wall).ToList();
        Join(doc, floors, walls, tolerance, report, progress, "slabWallJoins", "Slabs cut the walls");
        var columns = Structure(doc, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns);
        var beams = Structure(doc, BuiltInCategory.OST_StructuralFraming);
        Join(doc, columns, walls, tolerance, report, progress, "columnWallJoins", "Columns cut the walls");
        Join(doc, beams, walls, tolerance, report, progress, "beamWallJoins", "Beams cut the walls");
        Join(doc, columns, beams, tolerance, report, progress, "columnBeamJoins", "Columns cut the beams");
    }

    static List<KeyValuePair<string, Element>> Structure(Document doc, params BuiltInCategory[] categories) =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WherePasses(new ElementMulticategoryFilter(categories))
            .Select(e => new KeyValuePair<string, Element>(e.UniqueId, e)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();

    static void Join(Document doc, List<KeyValuePair<string, Element>> cutters, List<KeyValuePair<string, Element>> targets, double tolerance,
        Report report, Action<string, int, int>? progress, string stage, string summary)
    {
        var boxes = targets.Select(p => (p.Key, Element: p.Value, Box: p.Value.get_BoundingBox(null))).Where(w => w.Box != null).ToList();
        int joined = 0, already = 0, skipped = 0, candidates = 0, done = 0;
        foreach (var (cutterKey, cutter) in cutters)
        {
            progress?.Invoke(stage, done++, cutters.Count);
            var cutterBox = cutter.get_BoundingBox(null);
            if (cutterBox == null) continue;
            var near = boxes.Where(w => w.Element.Id != cutter.Id && Overlap(w.Box!, cutterBox, tolerance)).ToList();
            if (near.Count == 0) continue;
            var cutterSolids = NativeGeometry.Solids(cutter);
            try
            {
                var hits = new List<(string Key, Element Target)>();
                foreach (var (key, target, _) in near)
                {
                    var targetSolids = NativeGeometry.Solids(target);
                    try { if (Shares(cutterSolids, targetSolids)) hits.Add((key, target)); }
                    finally { foreach (var s in targetSolids) try { s.Dispose(); } catch (Exception) { } }
                }
                if (hits.Count == 0) continue;
                candidates += hits.Count;
                // A refused join rolls the whole transaction back; halve the group until the offending element stands alone.
                void Apply(List<(string Key, Element Target)> group)
                {
                    using var transaction = new Transaction(doc, summary);
                    transaction.Start();
                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new DropWarnings());
                    options.SetClearAfterRollback(true);
                    transaction.SetFailureHandlingOptions(options);
                    int before = joined, beforeAlready = already;
                    foreach (var (key, target) in group)
                    {
                        try
                        {
                            if (JoinGeometryUtils.AreElementsJoined(doc, cutter, target)) already++;
                            else { JoinGeometryUtils.JoinGeometry(doc, cutter, target); joined++; }
                            if (!JoinGeometryUtils.IsCuttingElementInJoin(doc, cutter, target))
                                JoinGeometryUtils.SwitchJoinOrder(doc, cutter, target);
                        }
                        catch (Exception ex) { skipped++; report.Add(key, "SlabWallJoin", "skipped", stage + " " + cutterKey + ": " + ex.Message); }
                    }
                    if (transaction.Commit() == TransactionStatus.Committed) return;
                    joined = before; already = beforeAlready;
                    if (group.Count > 1) { Apply(group.Take(group.Count / 2).ToList()); Apply(group.Skip(group.Count / 2).ToList()); }
                    else { skipped++; report.Add(group[0].Key, "SlabWallJoin", "skipped", stage + " " + cutterKey + ": Revit rolled back this join"); }
                }
                Apply(hits);
            }
            finally { foreach (var s in cutterSolids) try { s.Dispose(); } catch (Exception) { } }
        }
        progress?.Invoke(stage, cutters.Count, cutters.Count);
        report.Add(stage, "SlabWallJoin", "joined",
            $"{summary} they share volume with: {joined} joined, {already} already joined, {skipped} refused by Revit, {candidates} pairs sharing volume.");
    }

    static bool Overlap(BoundingBoxXYZ a, BoundingBoxXYZ b, double tolerance) =>
        a.Min.X < b.Max.X - tolerance && a.Max.X > b.Min.X + tolerance &&
        a.Min.Y < b.Max.Y - tolerance && a.Max.Y > b.Min.Y + tolerance &&
        a.Min.Z < b.Max.Z - tolerance && a.Max.Z > b.Min.Z + tolerance;

    static bool Shares(IReadOnlyList<Solid> cutter, IReadOnlyList<Solid> target)
    {
        foreach (var f in cutter)
            foreach (var w in target)
            {
                Solid? common = null;
                try
                {
                    common = BooleanOperationsUtils.ExecuteBooleanOperation(f, w, BooleanOperationsType.Intersect);
                    // 0.1 litre: above geometric noise, far below any real overlap of structure and a wall.
                    if (common != null && common.Volume > 1e-4 / 0.0283168466) return true;
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
                catch (Autodesk.Revit.Exceptions.InternalException) { }
                finally { common?.Dispose(); }
            }
        return false;
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
