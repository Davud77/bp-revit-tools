using Autodesk.Revit.DB;

namespace Ac2Revit;

/// <summary>
/// Archicad trims the top of a wall that runs into a roof (Solid Element Operations), so nothing sticks out above the
/// roof. Revit keeps the nominal wall height and the wall pokes through the roof. Revit's own answer is "attach top":
/// the wall top follows the underside of the roof. Applied to every wall whose axis passes under a native roof's
/// underside between the wall's base and top.
/// </summary>
public static class WallRoofAttach
{
    public static Report Run(Document doc, string project, Action<string, int, int>? progress = null)
    {
        var report = new Report { ProjectId = project, Mode = "wallRoofAttach" };
        var index = Storage.Index(doc, project);
        var roofs = index.Values.OfType<RoofBase>().ToList();
        var walls = index.Where(p => p.Value is Wall w && w.Location is LocationCurve lc && lc.Curve is Line).Select(p => (Key: p.Key, Wall: (Wall)p.Value)).ToList();
        if (roofs.Count == 0 || walls.Count == 0) { report.Add("wall-roof-attach", "WallRoofAttach", "notice", "No native roof or wall to attach."); return report; }
        double tolerance = Geometry.Feet(0.02);
        var roofSolids = roofs.Select(r => (Roof: r, Solids: NativeGeometry.Solids(r), Box: r.get_BoundingBox(null))).Where(r => r.Box != null).ToList();
        int attached = 0, already = 0, refused = 0, done = 0;
        try
        {
            var pairs = new List<(string Key, Wall Wall, RoofBase Roof)>();
            foreach (var (key, wall) in walls)
            {
                if (done++ % 200 == 0) progress?.Invoke("wallRoofAttach", done - 1, walls.Count);
                var box = wall.get_BoundingBox(null);
                if (box == null) continue;
                var line = (Line)((LocationCurve)wall.Location).Curve;
                double bottom = box.Min.Z, top = box.Max.Z;
                foreach (var (roof, solids, roofBox) in roofSolids)
                {
                    if (roofBox!.Max.X < box.Min.X - tolerance || roofBox.Min.X > box.Max.X + tolerance || roofBox.Max.Y < box.Min.Y - tolerance || roofBox.Min.Y > box.Max.Y + tolerance) continue;
                    double? under = Underside(solids, line, bottom, top);
                    if (under.HasValue && under.Value > bottom + tolerance && under.Value < top - tolerance) { pairs.Add((key, wall, roof)); break; }
                }
            }
            foreach (var batch in pairs.Chunk(200))
            {
                using var transaction = new Transaction(doc, "Attach wall tops to roofs");
                transaction.Start();
                var options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(new DropWarnings()); options.SetClearAfterRollback(true);
                transaction.SetFailureHandlingOptions(options);
                foreach (var (key, wall, roof) in batch)
                {
                    try
                    {
                        if (wall.GetAttachmentIds(AttachmentLocation.Top).Contains(roof.Id)) { already++; continue; }
                        if (!Wall.IsValidTargetAttachment(doc, roof.Id)) { refused++; report.Add(key, "WallRoofAttach", "notice", "Revit does not accept this roof as a wall top target"); continue; }
                        wall.AddAttachment(roof.Id, AttachmentLocation.Top);
                        attached++;
                    }
                    catch (Exception ex) { refused++; report.Add(key, "WallRoofAttach", "notice", "Wall top not attached: " + ex.Message); }
                }
                if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidDataException("Wall-to-roof attachment rolled back");
            }
        }
        finally { foreach (var (_, solids, _) in roofSolids) foreach (var s in solids) try { s.Dispose(); } catch (Exception) { } }
        progress?.Invoke("wallRoofAttach", walls.Count, walls.Count);
        report.Add("wall-roof-attach", "WallRoofAttach", "updated", $"Wall tops attached to native roofs: {attached} attached, {already} already attached, {refused} refused by Revit.");
        return report;
    }

    /// <summary>Lowest point of the roof solids above the wall axis (checked at three points), or null if the axis is not under a roof.</summary>
    static double? Underside(IReadOnlyList<Solid> solids, Line axis, double bottom, double top)
    {
        double? lowest = null;
        var options = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside };
        foreach (double t in new[] { 0.1, 0.5, 0.9 })
        {
            var p = axis.Evaluate(t, true);
            var vertical = Line.CreateBound(new XYZ(p.X, p.Y, bottom - Geometry.Feet(1)), new XYZ(p.X, p.Y, top + Geometry.Feet(30)));
            foreach (var solid in solids)
            {
                SolidCurveIntersection? hit = null;
                try { hit = solid.IntersectWithCurve(vertical, options); } catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
                if (hit == null) continue;
                for (int i = 0; i < hit.SegmentCount; ++i)
                {
                    var segment = hit.GetCurveSegment(i);
                    double z = Math.Min(segment.GetEndPoint(0).Z, segment.GetEndPoint(1).Z);
                    lowest = lowest.HasValue ? Math.Min(lowest.Value, z) : z;
                }
            }
        }
        return lowest;
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
