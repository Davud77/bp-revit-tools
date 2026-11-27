using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;

namespace BotPlus.Eom;

public sealed partial class ConduitThroughLightsCommand
{
    static FamilySymbol? FittingType(Document doc, string familyName) => new FilteredElementCollector(doc)
        .OfCategory(BuiltInCategory.OST_ConduitFitting).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
        .Where(s => string.Equals(s.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(s => s.Name == familyName).ThenBy(s => s.Id.Value).FirstOrDefault();

    static FamilySymbol[] BoxTypes(Document doc)
    {
        string[] families = ["ADSK_Коробка_Открытая_Крестовина", "ADSK_Коробка_Открытая_Соединение", BoxFamily];
        return new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ConduitFitting)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Select(symbol => (Symbol: symbol, Priority: Array.IndexOf(families, symbol.FamilyName)))
            .Where(item => item.Priority >= 0 && item.Symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
            .OrderBy(item => item.Priority).ThenBy(item => item.Symbol.Id.Value).Select(item => item.Symbol).ToArray();
    }

    static ConduitType ChooseType(Document doc, Element? source)
    {
        if (source is Conduit conduit) return (ConduitType)doc.GetElement(conduit.GetTypeId());
        var connected = (source == null ? [] : Ports(source)).SelectMany(p => p.AllRefs.Cast<Connector>()).Select(p => p.Owner)
            .OfType<Conduit>().FirstOrDefault();
        if (connected != null) return (ConduitType)doc.GetElement(connected.GetTypeId());
        var preferred = doc.GetDefaultElementTypeId(ElementTypeGroup.ConduitType);
        return new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).Cast<ConduitType>()
            .Where(t => t.IsWithFitting).OrderByDescending(t => t.Id == preferred).ThenBy(t => t.Id.Value).FirstOrDefault()
            ?? throw new InvalidOperationException("В проекте нет типа короба с соединительными деталями.");
    }

    static (Conduit First, Conduit Last) BuildPath(Document doc, ConduitType type, Level level, FamilySymbol? elbowType, Connector? start,
        Connector? end, XYZ[] points, double diameter, bool free = false, bool startElbow = false, bool endElbow = false,
        bool panelDrop = false)
    {
        var corners = new (Connector Inlet, Connector Outlet)?[points.Length];
        Element? firstOwner = start?.Owner, lastOwner = end?.Owner;
        int? firstPortId = start?.Id, lastPortId = end?.Id;
        for (int i = 1; i < points.Length - 1; i++)
        {
            var inlet = (points[i - 1] - points[i]).Normalize();
            var outlet = (points[i + 1] - points[i]).Normalize();
            bool rightAngle = Math.Abs(inlet.DotProduct(outlet)) <= 1 - DirectionTolerance;
            if (!free && !rightAngle)
                throw new InvalidOperationException("Оси участков не образуют угол 90°.");
            if (rightAngle && elbowType != null && Math.Abs(inlet.Z) < 1 - DirectionTolerance && Math.Abs(outlet.Z) < 1 - DirectionTolerance)
            {
                using var trial = new SubTransaction(doc);
                trial.Start();
                corners[i] = PutPlanElbow(doc, elbowType, level, points[i], inlet, outlet, diameter);
                if (corners[i] == null) trial.RollBack();
                else trial.Commit();
            }
        }

        var pieces = new (Conduit Segment, int Start, int End)[points.Length - 1];
        for (int i = 0; i < pieces.Length; i++)
        {
            var from = i == 0 ? start : corners[i]?.Outlet;
            var to = i == pieces.Length - 1 ? end : corners[i + 1]?.Inlet;
            var a = from?.Origin ?? points[i];
            var b = to?.Origin ?? points[i + 1];
            var axis = (points[i + 1] - points[i]).Normalize();
            if ((b - a).DotProduct(axis) <= doc.Application.ShortCurveTolerance)
                throw new InvalidOperationException($"На участке №{i + 1} не хватает места между выводами и отводами. Увеличьте расстояние или выберите другие выводы.");

            var segment = BuildSegment(doc, type, level, a, b, diameter);
            var inlet = EndAt(segment, a);
            var outlet = EndAt(segment, b);
            pieces[i] = (segment, inlet.Id, outlet.Id);
            if (from != null && (i != 0 || !startElbow)) JoinRouteEnd(doc, inlet, from);
            if (to != null && (i != pieces.Length - 1 || !endElbow))
                JoinRouteEnd(doc, segment.ConnectorManager.Lookup(pieces[i].End), to);
        }

        for (int i = 1; i < points.Length - 1; i++)
        {
            if (corners[i] != null) continue;
            var before = pieces[i - 1];
            var after = pieces[i];
            var beforeEnd = before.Segment.ConnectorManager.Lookup(before.End);
            var afterStart = after.Segment.ConnectorManager.Lookup(after.Start);
            JoinElbow(doc, beforeEnd, afterStart, $"в точке №{i}");
        }

        var first = pieces[0];
        var last = pieces[^1];
        start = firstOwner == null ? null : (IsPanel(firstOwner) ? PanelTerminals(firstOwner) : Ports(firstOwner)).Single(c => c.Id == firstPortId);
        end = lastOwner == null ? null : (IsPanel(lastOwner) ? PanelTerminals(lastOwner) : Ports(lastOwner)).Single(c => c.Id == lastPortId);
        if (startElbow && start != null) JoinElbow(doc, start, first.Segment.ConnectorManager.Lookup(first.Start), "на торце стояка");
        if (endElbow && end != null) JoinElbow(doc, last.Segment.ConnectorManager.Lookup(last.End), end, "на торце стояка");

        for (int i = 0; i < pieces.Length; i++)
        {
            var piece = pieces[i];
            var a = piece.Segment.ConnectorManager.Lookup(piece.Start);
            var b = piece.Segment.ConnectorManager.Lookup(piece.End);
            if (free && !panelDrop && (Math.Abs(a.Origin.Z - points[0].Z) > PointTolerance || Math.Abs(b.Origin.Z - points[0].Z) > PointTolerance))
                throw new InvalidOperationException("При установке фитинга Revit изменил отметку участка. Свободная трасса должна оставаться горизонтальной.");
            if ((b.Origin - a.Origin).DotProduct((points[i + 1] - points[i]).Normalize()) <= doc.Application.ShortCurveTolerance)
                throw new InvalidOperationException($"После установки отводов на участке №{i + 1} не осталось места для короба.");
            if ((i > 0 || start != null) && !a.IsConnected || (i < pieces.Length - 1 || end != null) && !b.IsConnected)
                throw new InvalidOperationException($"Revit не подключил оба конца участка №{i + 1}.");
        }
        if (start != null && !startElbow && !RouteEndConnected(start, first.Segment.ConnectorManager.Lookup(first.Start))
            || end != null && !endElbow && !RouteEndConnected(end, last.Segment.ConnectorManager.Lookup(last.End)))
            throw new InvalidOperationException("После установки отводов крайние участки отсоединились от выбранных выводов.");
        return (first.Segment, last.Segment);
    }

    static bool TerminalConnected(Connector first, Connector second) => first.IsConnectedTo(second) ||
        first.AllRefs.Cast<Connector>().Where(p => p.IsConnected && p.Owner is FamilyInstance && first.IsConnectedTo(p))
            .Any(p => Ports(p.Owner).Any(c => c.Id != p.Id && second.IsConnectedTo(c)));

    static bool RouteEndConnected(Connector terminal, Connector conduit) =>
        IsPanel(terminal.Owner) ? terminal.IsConnectedTo(conduit) : TerminalConnected(terminal, conduit);

    static void JoinRouteEnd(Document doc, Connector conduit, Connector target)
    {
        if (IsPanel(target.Owner))
        {
            var inletPoint = target.Origin;
            try { Join(conduit, target); }
            catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                throw new InvalidOperationException($"Revit не выполнил прямое подключение к вводу щита №{target.Id}. Проверьте свободу вывода и место для подхода. {error.Message}", error);
            }
            doc.Regenerate();
            if (!conduit.IsConnectedTo(target) || target.Origin.DistanceTo(inletPoint) > PointTolerance)
                throw new InvalidOperationException("Revit не подтвердил прямую связь с вводом щита или изменил его положение.");
            return;
        }
        if (MatchesDiameter(target, conduit.Radius * 2)) { Join(conduit, target); return; }
        if (conduit.IsConnected || target.IsConnected || conduit.Origin.DistanceTo(target.Origin) > PointTolerance
            || conduit.CoordinateSystem.BasisZ.DotProduct(target.CoordinateSystem.BasisZ) > -DirectionTolerance)
            throw new InvalidOperationException("Для перехода нужны свободные соосные выводы.");

        var segment = conduit.Owner;
        var appliance = target.Owner;
        int conduitId = conduit.Id, targetId = target.Id;
        var fixedPoint = target.Origin;
        using (var trial = new SubTransaction(doc))
        {
            trial.Start();
            try
            {
                doc.Create.NewTransitionFitting(conduit, target);
                doc.Regenerate();
                if (TerminalConnected(conduit, target) && target.Origin.DistanceTo(fixedPoint) <= PointTolerance)
                {
                    if (trial.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit отменил переход.");
                    return;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            if (trial.RollBack() != TransactionStatus.RolledBack)
                throw new InvalidOperationException("Revit не смог откатить пробную установку перехода.");
        }

        conduit = Ports(segment).Single(c => c.Id == conduitId);
        target = Ports(appliance).Single(c => c.Id == targetId);
        try { conduit.ConnectTo(target); }
        catch (Autodesk.Revit.Exceptions.ApplicationException error)
        {
            throw new InvalidOperationException("Revit не подключил выводы разных диаметров. Проверьте переход в типе трубы и место для фитинга.", error);
        }
        doc.Regenerate();
        if (!TerminalConnected(conduit, target) || target.Origin.DistanceTo(fixedPoint) > PointTolerance)
            throw new InvalidOperationException("Соединение разных диаметров не подтверждено или Revit сдвинул вывод. Участок отменён.");
    }

    static void JoinElbow(Document doc, Connector before, Connector after, string location)
    {
        double degrees = (-before.CoordinateSystem.BasisZ).AngleTo(after.CoordinateSystem.BasisZ) * 180 / Math.PI;
        try
        {
            var fitting = doc.Create.NewElbowFitting(before, after);
            doc.Regenerate();
            var ports = Ports(fitting);
            if (!ports.Any(p => before.IsConnectedTo(p)) || !ports.Any(p => after.IsConnectedTo(p)))
                throw new InvalidOperationException("Revit не подключил оба конца к отводу.");
        }
        catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
        {
            throw new InvalidOperationException($"Не удалось поставить отвод {location} ({degrees:0.#}°). Проверьте поддержку угла и место между отводами. {error.Message}", error);
        }
    }

    static (Connector Inlet, Connector Outlet)? PutPlanElbow(Document doc, FamilySymbol symbol, Level level,
        XYZ corner, XYZ incoming, XYZ outgoing, double diameter)
    {
        if (!symbol.IsActive) symbol.Activate();
        var elbow = doc.Create.NewFamilyInstance(corner, symbol, level, StructuralType.NonStructural);
        if (elbow.Category?.BuiltInCategory != BuiltInCategory.OST_ConduitFitting) RouteService.StyleNewElement(elbow);
        doc.Regenerate();
        var ports = FitPorts(doc, elbow, diameter);
        if (ports.Length != 2 || ports.Any(p => !MatchesDiameter(p, diameter))) return null;
        var first = ports[0];
        var second = ports[1];
        XYZ firstAxis = first.CoordinateSystem.BasisZ;
        XYZ secondAxis = second.CoordinateSystem.BasisZ;
        double orientation = firstAxis.CrossProduct(secondAxis).Z;
        if (Math.Abs(orientation) < DirectionTolerance)
            throw new InvalidOperationException("Для поворота требуются два горизонтальных вывода под 90°.");
        if (orientation * incoming.CrossProduct(outgoing).Z < 0)
        {
            (first, second) = (second, first);
            firstAxis = secondAxis;
        }
        double angle = Math.Atan2(firstAxis.CrossProduct(incoming).Z, firstAxis.DotProduct(incoming));
        if (Math.Abs(angle) > PointTolerance)
            ElementTransformUtils.RotateElement(doc, elbow.Id, Line.CreateUnbound(corner, XYZ.BasisZ), angle);
        doc.Regenerate();
        var origin = first.Origin;
        if (Math.Abs(origin.Z - second.Origin.Z) > PointTolerance)
            throw new InvalidOperationException("Выводы отвода TSL расположены на разных отметках.");
        var intersection = origin + incoming * (second.Origin - origin).DotProduct(incoming);
        ElementTransformUtils.MoveElement(doc, elbow.Id, corner - intersection);
        doc.Regenerate();
        return (first, second);
    }

    static FamilyInstance PutCompatibleBox(Document doc, FamilySymbol[] symbols, Level level, XYZ center, XYZ facing, double? diameter)
    {
        if (facing.GetLength() < doc.Application.ShortCurveTolerance)
            throw new InvalidOperationException("Недостаточно места между коробкой и началом участка.");
        var extent = new XYZ(PointTolerance, PointTolerance, PointTolerance);
        var types = symbols.Select(s => s.Id).ToHashSet();
        var nearby = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ConduitFitting)
            .OfClass(typeof(FamilyInstance)).WherePasses(new BoundingBoxIntersectsFilter(new Outline(center - extent, center + extent)))
            .Cast<FamilyInstance>().Any(box => types.Contains(box.GetTypeId())
                && box.Location is LocationPoint point && Horizontal(point.Point - center).GetLength() <= PointTolerance);
        if (nearby) throw new InvalidOperationException("В этой точке уже есть распределительная коробка. Её можно выбрать как начало трассы.");

        foreach (var symbol in symbols)
        {
            using var trial = new SubTransaction(doc);
            trial.Start();
            var box = PutBox(doc, symbol, level, center, facing, diameter);
            if (box != null && trial.Commit() == TransactionStatus.Committed) return box;
            if (trial.GetStatus() == TransactionStatus.Started) trial.RollBack();
        }
        throw new InvalidOperationException("У загруженных распределительных коробок АДСК и TSL нет двух свободных круглых выводов подходящего диаметра.");
    }

    static FamilyInstance? PutBox(Document doc, FamilySymbol symbol, Level level, XYZ center, XYZ facing, double? diameter)
    {
        if (!symbol.IsActive) symbol.Activate();
        var box = doc.Create.NewFamilyInstance(new XYZ(center.X, center.Y, level.ProjectElevation), symbol, level, StructuralType.NonStructural);
        if (box.Category?.BuiltInCategory != BuiltInCategory.OST_ConduitFitting) RouteService.StyleNewElement(box);
        doc.Regenerate();
        var ports = FitPorts(doc, box, diameter);
        var compatible = ports.Where(p => !p.IsConnected && MatchesDiameter(p, diameter)).ToArray();
        if (compatible.Length < 2 || compatible.All(p => Math.Abs(p.CoordinateSystem.BasisZ.Z) >= 1 - DirectionTolerance))
            return null;
        var inlet = FacingPort(box, facing, diameter);
        var current = inlet.CoordinateSystem.BasisZ;
        var direction = facing.Normalize();
        double angle = Math.Atan2(current.CrossProduct(direction).Z, current.DotProduct(direction));
        if (Math.Abs(angle) > PointTolerance)
            ElementTransformUtils.RotateElement(doc, box.Id, Line.CreateUnbound(center, XYZ.BasisZ), angle);
        double shift = center.Z - inlet.Origin.Z;
        if (Math.Abs(shift) > PointTolerance)
            ElementTransformUtils.MoveElement(doc, box.Id, XYZ.BasisZ * shift);
        doc.Regenerate();
        return box;
    }

    static Connector[] FitPorts(Document doc, FamilyInstance fitting, double? diameter)
    {
        var ports = Ports(fitting);
        if (diameter == null || ports.All(p => MatchesDiameter(p, diameter))) return ports;
        foreach (var port in ports.Where(p => !MatchesDiameter(p, diameter)))
        {
            try { port.Radius = diameter.Value / 2; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                // Вывод задан фиксированным размером семейства.
            }
        }
        doc.Regenerate();
        return Ports(fitting);
    }

    static Connector FacingPort(Element box, XYZ facing, double? diameter) => Ports(box)
        .Where(p => Math.Abs(p.CoordinateSystem.BasisZ.Z) < 1 - DirectionTolerance && MatchesDiameter(p, diameter))
        .OrderByDescending(p => p.CoordinateSystem.BasisZ.DotProduct(facing.Normalize())).ThenBy(p => p.Id).FirstOrDefault()
        ?? throw new InvalidOperationException("У распределительной коробки нет горизонтального вывода нужного диаметра.");

    internal static Conduit BuildSegment(Document doc, ConduitType type, Level level, XYZ start, XYZ end, double diameter)
    {
        if (start.DistanceTo(end) <= doc.Application.ShortCurveTolerance)
            throw new InvalidOperationException("Недостаточно места для участка короба.");
        var segment = Conduit.Create(doc, type.Id, start, end, level.Id);
        var parameter = segment.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
        if (Math.Abs(segment.Diameter - diameter) > PointTolerance && (parameter == null || !parameter.Set(diameter)))
            throw new InvalidOperationException("Выбранный тип короба не поддерживает диаметр вывода.");
        doc.Regenerate();
        if (Math.Abs(segment.Diameter - diameter) > PointTolerance)
            throw new InvalidOperationException("Revit округлил диаметр до другого типоразмера. Выберите совместимый тип короба.");
        return segment;
    }

    static Connector EndAt(Conduit segment, XYZ point) => Ports(segment).MinBy(p => p.Origin.DistanceTo(point))
        ?? throw new InvalidOperationException("Revit не создал коннектор участка короба.");

    static void Join(Connector a, Connector b)
    {
        if (a.IsConnectedTo(b)) return;
        if (a.IsConnected || b.IsConnected || a.Origin.DistanceTo(b.Origin) > PointTolerance
            || a.CoordinateSystem.BasisZ.DotProduct(b.CoordinateSystem.BasisZ) > -DirectionTolerance)
            throw new InvalidOperationException("Концы короба и вывода не совпадают или уже заняты.");
        a.ConnectTo(b);
        if (!a.IsConnectedTo(b)) throw new InvalidOperationException("Revit не соединил короб с выводом.");
    }

    static Connector[] Ports(Element element)
    {
        var manager = element switch
        {
            Conduit conduit => conduit.ConnectorManager,
            FamilyInstance instance => instance.MEPModel?.ConnectorManager,
            _ => null
        };
        return manager?.Connectors.Cast<Connector>().Where(p => p.Domain == Domain.DomainCableTrayConduit
            && p.ConnectorType == ConnectorType.End && p.Shape == ConnectorProfileType.Round).ToArray() ?? [];
    }

    static XYZ FixturePoint(FamilyInstance fixture) => (fixture.Location as LocationPoint)?.Point
        ?? throw new InvalidOperationException("У выбранного светильника нет точки установки.");

    static bool MatchesDiameter(Connector port, double? diameter) => diameter == null || Math.Abs(port.Radius * 2 - diameter.Value) <= PointTolerance;
    static XYZ Horizontal(XYZ point) => new(point.X, point.Y, 0);
}
