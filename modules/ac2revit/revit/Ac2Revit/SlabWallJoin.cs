using Autodesk.Revit.DB;

namespace Ac2Revit;

/// <summary>
/// Archicad resolves overlapping elements by priority, so a wall that runs up into a slab is simply not drawn
/// inside it. Revit keeps both solids: the wall top lying in or on the slab surface shows as stripes and
/// coplanar faces. Revit's own answer is Join Geometry with the floor cutting the wall: it removes only the
/// volume the two share, so walls that end below the slab, or continue above it (parapets), keep their shape.
/// </summary>
public static class SlabWallJoin
{
    public static void Run(Document doc, IReadOnlyDictionary<string, Element> imported, double tolerance, Report report, Action<string, int, int>? progress = null)
    {
        var floors = imported.Where(p => p.Value is Floor).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        var walls = imported.Where(p => p.Value is Wall).Select(p => (p.Key, Wall: (Wall)p.Value, Box: p.Value.get_BoundingBox(null)))
            .Where(w => w.Box != null).ToList();
        int joined = 0, already = 0, skipped = 0, candidates = 0, done = 0;
        foreach (var (floorKey, floorElement) in floors)
        {
            progress?.Invoke("slabWallJoins", done++, floors.Count);
            var floorBox = floorElement.get_BoundingBox(null);
            if (floorBox == null) continue;
            var near = walls.Where(w => Overlap(w.Box!, floorBox, tolerance)).ToList();
            if (near.Count == 0) continue;
            var floorSolids = NativeGeometry.Solids(floorElement);
            try
            {
                var hits = new List<(string Key, Wall Wall)>();
                foreach (var (key, wall, _) in near)
                {
                    var wallSolids = NativeGeometry.Solids(wall);
                    try { if (Shares(floorSolids, wallSolids)) hits.Add((key, wall)); }
                    finally { foreach (var s in wallSolids) try { s.Dispose(); } catch (Exception) { } }
                }
                if (hits.Count == 0) continue;
                candidates += hits.Count;
                // A refused join rolls the whole transaction back; halve the group until the offending wall stands alone.
                void Apply(List<(string Key, Wall Wall)> group)
                {
                    using var transaction = new Transaction(doc, "Slab cuts walls");
                    transaction.Start();
                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new DropWarnings());
                    options.SetClearAfterRollback(true);
                    transaction.SetFailureHandlingOptions(options);
                    int before = joined, beforeAlready = already;
                    foreach (var (key, wall) in group)
                    {
                        try
                        {
                            if (JoinGeometryUtils.AreElementsJoined(doc, floorElement, wall)) { already++; continue; }
                            JoinGeometryUtils.JoinGeometry(doc, floorElement, wall);
                            if (!JoinGeometryUtils.IsCuttingElementInJoin(doc, floorElement, wall))
                                JoinGeometryUtils.SwitchJoinOrder(doc, floorElement, wall);
                            joined++;
                        }
                        catch (Exception ex) { skipped++; report.Add(key, "SlabWallJoin", "skipped", "Floor " + floorKey + ": " + ex.Message); }
                    }
                    if (transaction.Commit() == TransactionStatus.Committed) return;
                    joined = before; already = beforeAlready;
                    if (group.Count > 1) { Apply(group.Take(group.Count / 2).ToList()); Apply(group.Skip(group.Count / 2).ToList()); }
                    else { skipped++; report.Add(group[0].Key, "SlabWallJoin", "skipped", "Floor " + floorKey + ": Revit rolled back this join"); }
                }
                Apply(hits);
            }
            finally { foreach (var s in floorSolids) try { s.Dispose(); } catch (Exception) { } }
        }
        progress?.Invoke("slabWallJoins", floors.Count, floors.Count);
        report.Add("slab-wall-joins", "SlabWallJoin", "joined",
            $"Slabs cut the walls they share volume with: {joined} joined, {already} already joined, {skipped} refused by Revit, {candidates} wall/slab pairs sharing volume.");
    }

    static bool Overlap(BoundingBoxXYZ a, BoundingBoxXYZ b, double tolerance) =>
        a.Min.X < b.Max.X - tolerance && a.Max.X > b.Min.X + tolerance &&
        a.Min.Y < b.Max.Y - tolerance && a.Max.Y > b.Min.Y + tolerance &&
        a.Min.Z < b.Max.Z - tolerance && a.Max.Z > b.Min.Z + tolerance;

    static bool Shares(IReadOnlyList<Solid> floor, IReadOnlyList<Solid> wall)
    {
        foreach (var f in floor)
            foreach (var w in wall)
            {
                Solid? common = null;
                try
                {
                    common = BooleanOperationsUtils.ExecuteBooleanOperation(f, w, BooleanOperationsType.Intersect);
                    // 0.1 litre: above geometric noise, far below any real overlap of a wall and a slab.
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
