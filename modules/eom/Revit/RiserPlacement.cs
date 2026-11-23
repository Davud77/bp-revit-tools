using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace BotPlus.Eom;

internal static class RiserPlacement
{
    internal sealed record Size(ElementId Type, double Width, double Height);
    internal sealed record Settings(Size Size, double Bottom, double Top, bool Split);

    internal static bool Run(UIDocument ui, bool tray, Size defaults)
    {
        var doc = ui.Document;
        if (ui.ActiveView is not ViewPlan { GenLevel: { } baseLevel })
            throw new InvalidOperationException("Для установки стояка откройте план с базовым уровнем.");
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(level => level.ProjectElevation).ThenBy(level => level.Name).ToArray();
        var types = new FilteredElementCollector(doc).OfClass(tray ? typeof(CableTrayType) : typeof(ConduitType))
            .Cast<ElementType>().OrderBy(type => type.Name).ToArray();
        if (types.Length == 0) throw new InvalidOperationException("В проекте нет типов для выбранной трассы.");
        if (RiserWindow.Edit(ui, tray, levels, baseLevel, types, defaults) is not { } settings) return false;

        var minimum = Math.Max(doc.Application.ShortCurveTolerance, 0.1 / 12); // ограничение MEPCurve.Create — 1/10 дюйма
        var heights = new List<double> { settings.Bottom };
        if (settings.Split)
        {
            foreach (var z in levels.Select(level => level.ProjectElevation).Distinct())
                if (z > settings.Bottom + 1e-6 && z < settings.Top - 1e-6) heights.Add(z);
        }
        heights.Add(settings.Top);
        if (heights.Zip(heights.Skip(1), (low, high) => high - low).Any(length => length <= minimum))
            throw new InvalidOperationException("Между отметками недостаточно места для участка. Измените отметки или отключите разбивку по уровням.");

        var point = ui.Selection.PickPoint(ObjectSnapTypes.Endpoints, "Укажите положение стояка на плане. Esc — отмена.");
        XYZ At(double z) => new(point.X, point.Y, z);

        using var transaction = new Transaction(doc, "ЭОМ: установка стояка");
        var failures = ConduitThroughLightsCommand.StartRouteTransaction(transaction);
        var segments = new List<MEPCurve>();
        for (int i = 0; i + 1 < heights.Count; i++)
        {
            var start = At(heights[i]);
            var end = At(heights[i + 1]);
            var level = levels.LastOrDefault(l => l.ProjectElevation <= start.Z + 1e-6) ?? levels[0];
            var type = doc.GetElement(settings.Size.Type);
            if (type is ConduitType conduitType)
                segments.Add(ConduitThroughLightsCommand.BuildSegment(doc, conduitType, level, start, end, settings.Size.Width));
            else
            {
                var segment = CableTray.Create(doc, settings.Size.Type, start, end, level.Id);
                SetSize(segment, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, settings.Size.Width, "ширину");
                SetSize(segment, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, settings.Size.Height, "высоту");
                segments.Add(segment);
            }
        }
        doc.Regenerate();

        for (int i = 1; i < segments.Count; i++)
        {
            var joint = At(heights[i]);
            var lower = EndAt(segments[i - 1], joint);
            var upper = EndAt(segments[i], joint);
            if (!lower.IsConnectedTo(upper)) lower.ConnectTo(upper);
            doc.Regenerate();
            if (!lower.IsConnectedTo(upper) && !lower.IsConnected)
                throw new InvalidOperationException("Revit не соединил участки стояка на уровне.");
        }
        ConduitThroughLightsCommand.FinishRouteTransaction(transaction, failures, "Revit отменил создание стояка.");
        ui.RefreshActiveView();
        return true;
    }

    static void SetSize(CableTray segment, BuiltInParameter id, double size, string name)
    {
        var parameter = segment.get_Parameter(id);
        if (parameter == null || Math.Abs(parameter.AsDouble() - size) > 1e-6 && (!parameter.Set(size) || Math.Abs(parameter.AsDouble() - size) > 1e-6))
            throw new InvalidOperationException($"Выбранный тип лотка не поддерживает заданную {name}.");
    }

    static Connector EndAt(MEPCurve segment, XYZ point) => segment.ConnectorManager.Connectors.Cast<Connector>()
        .Where(port => port.ConnectorType == ConnectorType.End)
        .MinBy(port => port.Origin.DistanceTo(point))
        ?? throw new InvalidOperationException("У созданного участка стояка отсутствует коннектор.");

    internal static double Feet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    internal static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
}
