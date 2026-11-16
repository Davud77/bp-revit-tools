using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;

namespace BotPlus.Eom;

/// <summary>Помещение проекта или связи АР. Transform переводит координаты связи в проект.</summary>
internal sealed record RoomRef(Room Room, Transform Transform, string Source)
{
    /// <summary>Ключ хранения светотехнических свойств.</summary>
    public string Key => Room.UniqueId;
    public string Number => Room.Number ?? "";
    public string Name => Room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? Room.Name;
    public double AreaM2 => UnitUtils.ConvertFromInternalUnits(Room.Area, UnitTypeId.SquareMeters);
    public double PerimeterM => UnitUtils.ConvertFromInternalUnits(Room.Perimeter, UnitTypeId.Meters);
    public double HeightM => UnitUtils.ConvertFromInternalUnits(Room.UnboundedHeight, UnitTypeId.Meters);

    /// <summary>Площадь, периметр, габарит по осям X/Y (в координатах файла помещения) и высота.</summary>
    public RoomGeometry Geometry()
    {
        var box = Room.get_BoundingBox(null);
        double M(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
        double x = box == null ? Math.Sqrt(AreaM2) : M(box.Max.X - box.Min.X), y = box == null ? Math.Sqrt(AreaM2) : M(box.Max.Y - box.Min.Y);
        return new(AreaM2, PerimeterM, x, y, HeightM > 0 ? HeightM : 3);
    }
}

internal static class Rooms
{
    /// <summary>Размещённые помещения проекта и всех загруженных связей.</summary>
    internal static List<RoomRef> Collect(Document doc)
    {
        var result = new List<RoomRef>();
        void Add(Document source, Transform t, string name)
        {
            foreach (var room in new FilteredElementCollector(source).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
                if (room.Area > 0 && room.Location != null) result.Add(new(room, t, name));
        }
        Add(doc, Transform.Identity, "Проект");
        foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            if (link.GetLinkDocument() is { } linked) Add(linked, link.GetTotalTransform(), linked.Title);
        return result.OrderBy(r => r.Source != "Проект").ThenBy(r => r.Number, NaturalComparer.Instance).ToList();
    }

    /// <summary>
    /// Расставляет светильники сеткой rows × cols в габарите помещения с отступом от стен в половину шага (как «Поместить» nanoCAD).
    /// Если контур заметно отличается от прямоугольника (Г-образный и т. п.), сетка уплотняется, точки вне помещения
    /// отбрасываются (Room.IsPointInRoom), пока не наберётся count. Возвращает UniqueId созданных экземпляров. Требует транзакции.
    /// </summary>
    internal static List<string> Place(Document doc, RoomRef room, FamilySymbol symbol, int rows, int cols, int count, double mountM)
    {
        var box = room.Room.get_BoundingBox(null);
        if (box == null || count <= 0) return [];
        double z = room.Room.Level?.Elevation ?? box.Min.Z;
        double probeZ = z + UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Meters);
        double boxArea = UnitUtils.ConvertFromInternalUnits((box.Max.X - box.Min.X) * (box.Max.Y - box.Min.Y), UnitTypeId.SquareMeters);
        double fill = Math.Clamp(room.AreaM2 / Math.Max(boxArea, 1e-6), 0.05, 1);
        List<XYZ> Inside(List<(double X, double Y)> grid) => grid.Select(p => new XYZ(p.X, p.Y, probeZ)).Where(room.Room.IsPointInRoom).ToList();

        List<XYZ> points = fill >= 0.95 && rows > 0 && cols > 0 ? Inside(Lighting.Grid(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y, rows, cols)) : [];
        for (int target = count, attempt = 0; attempt < 6 && points.Count < count; attempt++, target = (int)Math.Ceiling(target * 1.25))
            points = Inside(Lighting.Grid(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y, (int)Math.Ceiling(target / fill)));
        if (points.Count > count) points = points.Take(count).ToList();

        if (!symbol.IsActive) symbol.Activate();
        var level = HostLevel(doc, room.Transform.OfPoint(new XYZ(0, 0, z)).Z);
        double offset = UnitUtils.ConvertToInternalUnits(mountM, UnitTypeId.Meters);
        var placed = new List<string>();
        foreach (var p in points)
        {
            var host = room.Transform.OfPoint(new XYZ(p.X, p.Y, z));
            var instance = doc.Create.NewFamilyInstance(new XYZ(host.X, host.Y, level.Elevation), symbol, level, StructuralType.NonStructural);
            var elevation = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if (elevation is { IsReadOnly: false }) elevation.Set(host.Z - level.Elevation + offset);
            placed.Add(instance.UniqueId);
        }
        return placed;
    }

    /// <summary>Удаляет ранее расставленные BotPlus светильники помещения (уже удалённые вручную пропускаются).</summary>
    internal static int Remove(Document doc, IEnumerable<string> uniqueIds)
    {
        var ids = uniqueIds.Select(doc.GetElement).Where(e => e is FamilyInstance).Select(e => e.Id).ToList();
        if (ids.Count > 0) doc.Delete(ids);
        return ids.Count;
    }

    static Level HostLevel(Document doc, double elevation) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().MinBy(l => Math.Abs(l.Elevation - elevation))
        ?? throw new InvalidOperationException("В проекте нет уровней.");
}
