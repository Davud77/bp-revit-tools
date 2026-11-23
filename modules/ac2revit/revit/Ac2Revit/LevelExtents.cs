using Autodesk.Revit.DB;

namespace Ac2Revit;

/// <summary>
/// A level created by the API keeps the template's default length, so in elevations and sections the building runs
/// past the level lines. The 3D (model) extent of every level is stretched over the imported building plus a margin
/// for the level head, in each elevation and section direction; model extents are shared by all parallel views.
/// </summary>
public static class LevelExtents
{
    public const double MarginMeters = 1.5;

    public static Report Run(Document doc, string project, Action<string, int, int>? progress = null)
    {
        var report = new Report { ProjectId = project, Mode = "levelExtents" };
        var box = BuildingBox(doc, project);
        if (box == null) { report.Add("level-extents", "LevelExtents", "notice", "No imported building element to measure."); return report; }
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v.ViewType is ViewType.Elevation or ViewType.Section).ToList();
        int set = 0, failed = 0, done = 0;
        var directions = new HashSet<(long, long)>();
        using var transaction = new Transaction(doc, "Level extents over the building");
        transaction.Start();
        var options = transaction.GetFailureHandlingOptions(); options.SetFailuresPreprocessor(new DropWarnings()); transaction.SetFailureHandlingOptions(options);
        foreach (var view in views)
        {
            progress?.Invoke("levelExtents", done++, views.Count);
            // Model extents are shared by parallel views: one view per direction is enough.
            var right = view.RightDirection;
            var key = ((long)Math.Round(Math.Abs(right.X) * 1000), (long)Math.Round(Math.Abs(right.Y) * 1000));
            if (!directions.Add(key)) continue;
            foreach (var level in levels)
            {
                try
                {
                    var curves = level.GetCurvesInView(DatumExtentType.Model, view);
                    if (curves.Count == 0 || curves[0] is not Line line) continue;
                    var start = line.GetEndPoint(0); var direction = line.Direction;
                    var along = Corners(box).Select(c => (c - start).DotProduct(direction)).ToList();
                    double margin = Geometry.Feet(MarginMeters);
                    var a = start + direction * (along.Min() - margin); var b = start + direction * (along.Max() + margin);
                    level.SetCurveInView(DatumExtentType.Model, view, Line.CreateBound(new XYZ(a.X, a.Y, start.Z), new XYZ(b.X, b.Y, start.Z)));
                    set++;
                }
                catch (Exception ex) { failed++; report.Add(level.UniqueId, "LevelExtents", "skipped", view.Name + ": " + ex.Message); }
            }
        }
        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Level extents were rolled back");
        progress?.Invoke("levelExtents", views.Count, views.Count);
        report.Add("level-extents", "LevelExtents", "verified", $"Level model extents set over the building ±{MarginMeters} m: {set} level/direction pairs, {failed} refused.");
        return report;
    }

    /// <summary>Extents of the imported building: everything Ac2Revit made except levels, grids, terrain and annotation.</summary>
    static BoundingBoxXYZ? BuildingBox(Document doc, string project)
    {
        XYZ? lo = null, hi = null;
        foreach (var element in Storage.Index(doc, project).Values)
        {
            if (element is Level or Grid or Toposolid or View || element.Category == null || element.Category.CategoryType != CategoryType.Model) continue;
            var b = element.get_BoundingBox(null);
            if (b == null) continue;
            lo = lo == null ? b.Min : new XYZ(Math.Min(lo.X, b.Min.X), Math.Min(lo.Y, b.Min.Y), Math.Min(lo.Z, b.Min.Z));
            hi = hi == null ? b.Max : new XYZ(Math.Max(hi.X, b.Max.X), Math.Max(hi.Y, b.Max.Y), Math.Max(hi.Z, b.Max.Z));
        }
        return lo == null ? null : new BoundingBoxXYZ { Min = lo, Max = hi! };
    }

    static IEnumerable<XYZ> Corners(BoundingBoxXYZ b)
    {
        foreach (var x in new[] { b.Min.X, b.Max.X })
            foreach (var y in new[] { b.Min.Y, b.Max.Y })
                yield return new XYZ(x, y, 0);
    }

    sealed class DropWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var message in accessor.GetFailureMessages())
                if (message.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(message);
            return FailureProcessingResult.Continue;
        }
    }
}
