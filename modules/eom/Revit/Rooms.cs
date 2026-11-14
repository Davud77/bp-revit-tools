using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;

namespace BotPlus.Eom;

/// <summary>Помещение проекта или связи АР. Transform переводит координаты связи в проект.</summary>
internal sealed record RoomRef(Room Room, Transform Transform, string Source)
{
    public string Number => Room.Number ?? "";
    public string Name => Room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? Room.Name;
    public double AreaM2 => UnitUtils.ConvertFromInternalUnits(Room.Area, UnitTypeId.SquareMeters);
    public double PerimeterM => UnitUtils.ConvertFromInternalUnits(Room.Perimeter, UnitTypeId.Meters);
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
    /// Расставляет count экземпляров равномерной сеткой внутри помещения. Для Г-образных и сложных контуров сетка строится
    /// по габариту с запасом по числу точек, а точки вне помещения отбрасываются (Room.IsPointInRoom). Требует транзакции.
    /// </summary>
    internal static int Place(Document doc, RoomRef room, FamilySymbol symbol, int count, double mountM)
    {
        var box = room.Room.get_BoundingBox(null);
        if (box == null || count <= 0) return 0;
        double z = room.Room.Level?.Elevation ?? box.Min.Z;
        double probeZ = z + UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Meters);
        double boxArea = UnitUtils.ConvertFromInternalUnits((box.Max.X - box.Min.X) * (box.Max.Y - box.Min.Y), UnitTypeId.SquareMeters);
        double fill = Math.Clamp(room.AreaM2 / Math.Max(boxArea, 1e-6), 0.05, 1);
        List<XYZ> points = [];
        for (int target = count, attempt = 0; attempt < 6 && points.Count < count; attempt++, target = (int)Math.Ceiling(target * 1.25))
        {
            int gridCount = (int)Math.Ceiling(target / fill);
            points = Lighting.Grid(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y, gridCount)
                .Select(p => new XYZ(p.X, p.Y, probeZ)).Where(room.Room.IsPointInRoom).ToList();
        }
        if (points.Count > count) points = points.Take(count).ToList();
        if (!symbol.IsActive) symbol.Activate();
        var level = HostLevel(doc, room.Transform.OfPoint(new XYZ(0, 0, z)).Z);
        double offset = UnitUtils.ConvertToInternalUnits(mountM, UnitTypeId.Meters);
        int placed = 0;
        foreach (var p in points)
        {
            var host = room.Transform.OfPoint(new XYZ(p.X, p.Y, z));
            var instance = doc.Create.NewFamilyInstance(new XYZ(host.X, host.Y, level.Elevation), symbol, level, StructuralType.NonStructural);
            var elevation = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if (elevation is { IsReadOnly: false }) elevation.Set(host.Z - level.Elevation + offset);
            placed++;
        }
        return placed;
    }

    static Level HostLevel(Document doc, double elevation) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().MinBy(l => Math.Abs(l.Elevation - elevation))
        ?? throw new InvalidOperationException("В проекте нет уровней.");
}
