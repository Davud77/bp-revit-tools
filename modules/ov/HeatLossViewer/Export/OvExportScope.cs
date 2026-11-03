namespace HeatLossViewer.Export;

public static class OvExportScope
{
    public static int ExcludeUnboundOpenings(BuildingData data)
    {
        var rooms = data.Rooms.Select(r => r.Key).ToHashSet();
        int removed = data.Openings.RemoveAll(o => (o.Kind is "window" or "door") && !o.RoomKeys.Any(rooms.Contains));
        if (removed > 0) data.Warnings.Add($"Из выгрузки ОВ исключено окон и дверей без расчётных помещений: {removed}. Элементы в Revit сохранены.");
        return removed;
    }
}
