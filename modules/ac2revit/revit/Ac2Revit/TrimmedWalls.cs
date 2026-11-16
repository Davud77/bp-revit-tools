using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>
/// A wall that Archicad trimmed at the top (the fascia under an outdoor stair, a gable under a roof) is a nominal prism in the
/// API but a polygon in the exact IFC body. Such a wall is rebuilt as a native Revit wall by profile with the type, level and
/// orientation of the nominal one; the nominal wall is deleted in the same transaction. Runs before openings exist, so nothing
/// hosted is lost. The profile is checked against the extents of the IFC body; otherwise the nominal wall stays.
/// </summary>
public static class TrimmedWalls
{
    public static HashSet<string> Run(Document doc, Manifest manifest, string rowsPath, Report report, Action<string, int, int>? progress = null)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(rowsPath)) return done;
        using var json = JsonDocument.Parse(File.ReadAllText(rowsPath));
        var rows = json.RootElement.GetProperty("elements").EnumerateArray().ToList();
        if (rows.Count == 0) return done;
        string project = manifest.Source.ProjectId;
        var geometry = new Geometry(doc, manifest.Coordinates.Transform);
        var index = Storage.Index(doc, project);
        int n = 0;
        foreach (var row in rows)
        {
            progress?.Invoke("trimmedWalls", n++, rows.Count);
            string key = row.S("sourceGuid");
            try
            {
                string? reason = Replace(doc, manifest, geometry, index, key, row, out var created);
                if (reason == null && created != null) { done.Add(key); report.Add(key, "Wall", "replaced", "Wall trimmed in Archicad rebuilt as a native Revit wall by the profile of its exact IFC body.", created.UniqueId); }
                else report.Add(key, "Wall", "notice", "Trimmed wall kept nominal: " + reason);
            }
            catch (Exception ex) { report.Add(key, "Wall", "notice", "Trimmed wall kept nominal: " + ex.Message); }
        }
        return done;
    }

    static string? Replace(Document doc, Manifest manifest, Geometry geometry, IReadOnlyDictionary<string, Element> index, string key, JsonElement row, out Wall? result)
    {
        result = null;
        if (!index.TryGetValue(key, out var element) || element is not Wall old) return "the nominal wall is not in the model";
        var points = row.GetProperty("profile").EnumerateArray().Select(p => geometry.Xyz(new Point { X = p.D("x"), Y = p.D("y"), Z = p.D("z") })).ToList();
        var curves = new List<Curve>();
        for (int i = 0; i < points.Count; ++i)
        {
            var a = points[i]; var b = points[(i + 1) % points.Count];
            if (a.DistanceTo(b) < doc.Application.ShortCurveTolerance * 4) return "profile has an edge shorter than Revit can sketch";
            curves.Add(Line.CreateBound(a, b));
        }
        var level = doc.GetElement(old.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId()) as Level;
        if (level == null) return "the nominal wall has no base level";
        var oldOrientation = old.Orientation;
        bool roomBounding = old.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING)?.AsInteger() == 1;
        string? tagHash = Storage.Read(old)?.Hash; string raw = Storage.RawJson(old);
        var oldTypeId = old.WallType.Id; string mark = old.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""; var oldId = old.Id;

        using var transaction = new Transaction(doc, "Native wall by profile");
        transaction.Start();
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new DropWarnings()); options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);
        try
        {
            // The nominal wall goes first: a new wall on top of it would be joined to it and Revit could not keep that join.
            doc.Delete(oldId);
            var wall = Wall.Create(doc, curves, oldTypeId, level.Id, false);
            // Joins of a profile wall with its neighbours are made later from the source topology; automatic ones can be refused at commit.
            try { WallUtils.DisallowWallJoinAtEnd(wall, 0); WallUtils.DisallowWallJoinAtEnd(wall, 1); } catch (Exception) { }
            try { foreach (var joined in JoinGeometryUtils.GetJoinedElements(doc, wall).ToList()) JoinGeometryUtils.UnjoinGeometry(doc, wall, doc.GetElement(joined)); } catch (Exception) { }
            // Revit places the profile with its lowest point on the level and limits the top; state the real base and height.
            wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(ElementId.InvalidElementId);
            wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(points.Max(p => p.Z) - points.Min(p => p.Z));
            wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.Set(points.Min(p => p.Z) - level.Elevation);
            wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING)?.Set(roomBounding ? 1 : 0);
            doc.Regenerate();
            if (wall.Orientation.DotProduct(oldOrientation) < 0) { wall.Flip(); doc.Regenerate(); }
            var box = SolidBox(wall) ?? throw new InvalidDataException("Revit produced no wall geometry");
            var bbox = row.GetProperty("bbox");
            var lo = bbox.GetProperty("min").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var hi = bbox.GetProperty("max").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var a = geometry.Xyz(new Point { X = lo[0], Y = lo[1], Z = lo[2] }); var b = geometry.Xyz(new Point { X = hi[0], Y = hi[1], Z = hi[2] });
            double tol = Geometry.Feet(0.003);
            // Revit places a profile wall relative to its location line; bring the body to the IFC body across the wall.
            var expectedCenter = (a + b) / 2;
            var normal = new XYZ(oldOrientation.X, oldOrientation.Y, 0).Normalize();
            double across = (((box.Min + box.Max) / 2) - expectedCenter).DotProduct(normal);
            if (Math.Abs(across) > Geometry.Feet(0.0005) && Math.Abs(across) < Geometry.Feet(1))
            {
                ElementTransformUtils.MoveElement(doc, wall.Id, normal.Multiply(-across)); doc.Regenerate();
                box = SolidBox(wall)!;
            }
            double[] delta = [box.Min.X - Math.Min(a.X, b.X), box.Max.X - Math.Max(a.X, b.X), box.Min.Y - Math.Min(a.Y, b.Y), box.Max.Y - Math.Max(a.Y, b.Y), box.Min.Z - Math.Min(a.Z, b.Z), box.Max.Z - Math.Max(a.Z, b.Z)];
            if (delta.Any(v => Math.Abs(v) > tol))
            {
                string D(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters)).ToString();
                string info = "profile z " + D(points.Min(p => p.Z)) + ".." + D(points.Max(p => p.Z)) + ", solid z " + D(box.Min.Z) + ".." + D(box.Max.Z) + ", level " + D(level.Elevation)
                    + ", height " + D(wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0) + ", base offset " + D(wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0) + ", sketch " + wall.SketchId.Value;
                transaction.RollBack(); return "wall extents differ from the IFC body by (mm) " + string.Join(", ", delta.Select(v => D(v))) + "; " + info;
            }
            Storage.Tag(wall, manifest.Source.ProjectId, key, tagHash ?? "trimmed-wall", raw);
            SourceMark.Set(wall, mark);
            if (transaction.Commit() != TransactionStatus.Committed) return "Revit rolled back the wall: " + DropWarnings.LastError;
            result = wall;
            return null;
        }
        catch (Exception ex)
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            return ex.Message;
        }
    }

    /// <summary>Extents of the actual solids of the wall (the element box can include unfolded sketch geometry).</summary>
    static BoundingBoxXYZ? SolidBox(Wall wall)
    {
        var solids = NativeGeometry.Solids(wall);
        try
        {
            XYZ? min = null, max = null;
            foreach (var solid in solids)
                foreach (Autodesk.Revit.DB.Edge edge in solid.Edges)
                    foreach (var p in edge.Tessellate())
                    {
                        min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                        max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
                    }
            return min == null ? null : new BoundingBoxXYZ { Min = min, Max = max! };
        }
        finally { foreach (var s in solids) try { s.Dispose(); } catch (Exception) { } }
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
