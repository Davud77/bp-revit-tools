using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace HeatLossViewer.Export;

public sealed class RevitRoomExtractor
{
    private static double M(double value) => UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Meters);

    public BuildingData Extract(Document host)
    {
        var conditions = ProjectConditionsStore.Read(host);
        var data = new BuildingData
        {
            ProjectName = string.IsNullOrWhiteSpace(conditions.Name) ? host.Title : conditions.Name,
            ProjectConditions = conditions, OvLibrary = RevitOvLibrary.Read(host)
        };
        using var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
        var rooms = Rooms(host);
        if (rooms.Count > 0) AddRooms(rooms, Transform.Identity, "host", host.Title, options, data);
        else ReadLinks(host, Transform.Identity, "host", options, data, new HashSet<Document> { host });
        data.Rooms.Sort((a, b) =>
        {
            int c = a.Elevation.CompareTo(b.Elevation);
            return c == 0 ? string.Compare(a.Number, b.Number, StringComparison.Ordinal) : c;
        });
        OvExportScope.ExcludeUnboundOpenings(data);
        int noVolume = data.Rooms.Count(r => r.Volume <= 0);
        if (noVolume > 0) data.Warnings.Add($"У {noVolume} помещений объём не рассчитан Revit. Высота — UnboundedHeight, объём для расчёта — площадь × высота (демо).");
        return data;
    }

    private static List<Room> Rooms(Document doc) => new FilteredElementCollector(doc)
        .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
        .Where(r => r.Area > 0).ToList();

    private void ReadLinks(Document doc, Transform parent, string path, SpatialElementBoundaryOptions options,
        BuildingData data, HashSet<Document> ancestors)
    {
        foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            var linked = link.GetLinkDocument();
            if (linked is null) { data.Warnings.Add($"Связь «{link.Name}» не загружена, пропущена."); continue; }
            if (ancestors.Contains(linked)) continue;
            string source = path + "/" + link.UniqueId;
            var transform = parent.Multiply(link.GetTotalTransform());
            if (transform.BasisZ.DistanceTo(XYZ.BasisZ) > 1e-7)
            { data.Warnings.Add($"Связь «{link.Name}» наклонена или перевёрнута по Z, пропущена."); continue; }
            AddRooms(Rooms(linked), transform, source, link.Name, options, data);
            ancestors.Add(linked);
            ReadLinks(linked, transform, source, options, data, ancestors);
            ancestors.Remove(linked);
        }
    }

    private void AddRooms(List<Room> rooms, Transform transform, string source, string instance,
        SpatialElementBoundaryOptions options, BuildingData data)
    {
        using var envelope = rooms.Count > 0 ? new RevitEnvelopeExtractor(rooms[0].Document, options, source, transform) : null;
        var exported = new Dictionary<long, RoomData>();
        foreach (var room in rooms)
        {
            try
            {
                var dto = Convert(room, transform, source, instance, options);
                if (dto is null) data.Warnings.Add($"{instance}, помещение {room.Id.Value}: нет замкнутого контура или положительной высоты.");
                else
                {
                    data.Rooms.Add(dto);
                    exported[room.Id.Value] = dto;
                    try { envelope!.ReadSurfaces(room, dto); }
                    catch (Exception ex) { dto.Surfaces.Clear(); data.Warnings.Add($"Помещение {room.Id.Value}: ограждения — бета ({ex.Message})."); }
                }
            }
            catch (Exception ex) { data.Warnings.Add($"{instance}, помещение {room.Id.Value}: {ex.Message}"); }
        }
        if (envelope is not null) envelope.ReadOpenings(rooms, exported, data);
    }

    private static RoomData? Convert(Room room, Transform transform, string source, string instance, SpatialElementBoundaryOptions options)
    {
        var loops = room.GetBoundarySegments(options);
        if (loops is null || loops.Count == 0) return null;
        var rings = new List<List<double[]>>();
        double joinTolerance = room.Document.Application.ShortCurveTolerance;
        double? baseZ = null;
        foreach (var loop in loops)
        {
            var points = new List<XYZ>();
            foreach (var segment in loop)
            {
                using var curve = segment.GetCurve();
                var tessellated = curve.Tessellate().ToList();
                if (points.Count > 0 && points[^1].DistanceTo(tessellated[^1]) < points[^1].DistanceTo(tessellated[0]))
                    tessellated.Reverse();
                double gap = points.Count > 0 ? points[^1].DistanceTo(tessellated[0]) : 0;
                if (gap > joinTolerance)
                    throw new InvalidOperationException($"Разрыв контура {M(gap) * 1000:F3} мм превышает допуск {M(joinTolerance) * 1000:F3} мм.");
                if (points.Count > 0) tessellated[0] = points[^1];
                foreach (var p in tessellated)
                    if (points.Count == 0 || points[^1].DistanceTo(p) > 1e-7) points.Add(p);
            }
            double closureGap = points.Count > 0 ? points[0].DistanceTo(points[^1]) : 0;
            if (points.Count < 4 || closureGap > joinTolerance)
                throw new InvalidOperationException($"Незамкнутый контур: зазор {M(closureGap) * 1000:F3} мм, допуск {M(joinTolerance) * 1000:F3} мм.");
            points.RemoveAt(points.Count - 1);
            var transformed = points.Select(transform.OfPoint).ToList();
            baseZ ??= transformed[0].Z;
            if (transformed.Any(p => Math.Abs(p.Z - baseZ.Value) > 1e-5))
                throw new InvalidOperationException("Контуры не лежат в одной горизонтальной плоскости.");
            rings.Add(transformed.Select(p => new[] { M(p.X), M(p.Y) }).ToList());
        }
        var polygons = ContourTopology.Build(rings);
        if (polygons.Count == 0) return null;
        double area = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters);
        double volume = UnitUtils.ConvertFromInternalUnits(room.Volume, UnitTypeId.CubicMeters);
        double height = volume > 0 ? volume / area : M(room.UnboundedHeight);
        if (!double.IsFinite(height) || height <= 0) return null;
        var level = room.Level;
        double elevation = M(transform.OfPoint(new XYZ(0, 0, level?.ProjectElevation ?? 0)).Z);
        return new RoomData
        {
            Id = room.Id.Value, Key = source + "/" + room.UniqueId,
            SourceDocument = room.Document.Title, SourceInstance = instance,
            Number = room.Number ?? "", Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name,
            LevelName = level?.Name ?? "Без уровня", LevelKey = source + "/level/" + (level?.UniqueId ?? "none"),
            Elevation = elevation, BaseElevation = M(baseZ!.Value), Area = area, Volume = volume,
            Height = height, HeightSource = volume > 0 ? "volume/area" : "unboundedHeight",
            Contour = polygons[0].Contour, Polygons = polygons, BoundariesCount = loops.Sum(l => l.Count)
        };
    }
}
