using Autodesk.Revit.DB;

namespace BotPlus.Eom;

public sealed partial class ConduitThroughLightsCommand
{
    static bool IsPanel(Element element) => element is FamilyInstance
        && element.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalEquipment;

    static Connector[] PanelTerminals(Element element) =>
        (element as FamilyInstance)?.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>()
            .Where(c => c.Domain == Domain.DomainCableTrayConduit && c.ConnectorType == ConnectorType.End
                && c.Shape is ConnectorProfileType.Round or ConnectorProfileType.Rectangular).ToArray() ?? [];

    static Port PanelPort(Connector connector) => new(connector.Id, connector.Origin, connector.CoordinateSystem.BasisZ,
        connector.Shape == ConnectorProfileType.Round ? connector.Radius * 2 : null,
        RectangularPanelInlet: connector.Shape == ConnectorProfileType.Rectangular);

    static Connector PanelTerminal(Element element, Port port) => PanelTerminals(element)
        .SingleOrDefault(connector => connector.Id == port.Id && !connector.IsConnected)
        ?? throw new InvalidOperationException("Выбранный ввод щита занят или недоступен.");

    static bool DirectPanelJoint(Element source, Element target, Port start, Port end) =>
        (source is Autodesk.Revit.DB.Electrical.Conduit && IsPanel(target)
            || target is Autodesk.Revit.DB.Electrical.Conduit && IsPanel(source))
        && !start.Tap && !end.Tap && !start.TerminalElbow && !end.TerminalElbow
        && start.Point.DistanceTo(end.Point) <= PointTolerance && start.Axis.DotProduct(-end.Axis) >= DirectionTolerance;

    static XYZ[] PanelPath(Port start, Port end, double minimum)
    {
        bool Valid(XYZ[] path)
        {
            var vectors = path.Zip(path.Skip(1), (a, b) => b - a).ToArray();
            if (vectors.Any(v => v.GetLength() <= minimum)) return false;
            var axes = vectors.Select(v => v.Normalize()).ToArray();
            return axes[0].DotProduct(start.Axis) >= DirectionTolerance && axes[^1].DotProduct(-end.Axis) >= DirectionTolerance
                && axes.Zip(axes.Skip(1), (left, right) => Math.Abs(left.DotProduct(right))).All(cos => cos <= 1 - DirectionTolerance);
        }

        try
        {
            var direct = Path(start, end, minimum, false);
            if (Valid(direct)) return direct;
        }
        catch (InvalidOperationException) { }

        var horizontal = Math.Abs(start.Axis.Z) < PointTolerance ? start.Axis
            : Math.Abs(end.Axis.Z) < PointTolerance ? end.Axis : XYZ.BasisX;
        XYZ[] basis = [horizontal, XYZ.BasisZ.CrossProduct(horizontal), XYZ.BasisZ];
        int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        var clearance = Math.Max(Feet(150), minimum * 2);
        var paths = new List<XYZ[]>();
        foreach (var lead in new[] { clearance, clearance * 2 })
        foreach (var tail in new[] { clearance, clearance * 2 })
        {
            var first = start.Point + start.Axis * lead;
            var last = end.Point + end.Axis * tail;
            var delta = last - first;
            foreach (var order in orders)
            {
                var points = new List<XYZ> { start.Point, first };
                foreach (var i in order)
                {
                    var component = basis[i] * delta.DotProduct(basis[i]);
                    if (component.GetLength() > PointTolerance) points.Add(points[^1] + component);
                }
                if (points[^1].DistanceTo(last) > PointTolerance) continue;
                points.Add(end.Point);
                for (int i = points.Count - 2; i > 0; i--)
                {
                    var a = (points[i] - points[i - 1]).Normalize();
                    var b = (points[i + 1] - points[i]).Normalize();
                    if (a.DotProduct(b) >= DirectionTolerance) points.RemoveAt(i);
                }
                var path = points.ToArray();
                if (Valid(path)) paths.Add(path);
            }
        }
        return paths.OrderBy(path => path.Length)
            .ThenBy(path => path.Zip(path.Skip(1), (a, b) => a.DistanceTo(b)).Sum()).FirstOrDefault()
            ?? throw new InvalidOperationException("К выбранному вводу щита не помещается подвод с поворотами 90°. Выберите другой ввод.");
    }
}
