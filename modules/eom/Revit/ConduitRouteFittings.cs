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

    static ConduitType ChooseType(Document doc, Element source)
    {
        if (source is Conduit conduit) return (ConduitType)doc.GetElement(conduit.GetTypeId());
        var connected = Ports(source).SelectMany(p => p.AllRefs.Cast<Connector>()).Select(p => p.Owner)
            .OfType<Conduit>().FirstOrDefault();
        if (connected != null) return (ConduitType)doc.GetElement(connected.GetTypeId());
        var preferred = doc.GetDefaultElementTypeId(ElementTypeGroup.ConduitType);
        return new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).Cast<ConduitType>()
            .Where(t => t.IsWithFitting).OrderByDescending(t => t.Id == preferred).ThenBy(t => t.Id.Value).FirstOrDefault()
            ?? throw new InvalidOperationException("В проекте нет типа короба с соединительными деталями.");
    }

    static void BuildPath(Document doc, ConduitType type, Level level, FamilySymbol? elbowType, Connector start,
        Connector end, XYZ[] points, double diameter)
    {
        var corners = new (Connector Inlet, Connector Outlet)?[points.Length];
        for (int i = 1; i < points.Length - 1; i++)
        {
            var inlet = (points[i - 1] - points[i]).Normalize();
            var outlet = (points[i + 1] - points[i]).Normalize();
            if (Math.Abs(inlet.DotProduct(outlet)) > 1 - DirectionTolerance)
                throw new InvalidOperationException("Оси участков не образуют угол 90°.");
            if (elbowType != null && Math.Abs(inlet.Z) < 1 - DirectionTolerance && Math.Abs(outlet.Z) < 1 - DirectionTolerance)
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
            if (from != null) Join(from, inlet);
            if (to != null) Join(outlet, to);
            pieces[i] = (segment, inlet.Id, outlet.Id);
        }

        for (int i = 1; i < points.Length - 1; i++)
        {
            if (corners[i] != null) continue;
            var before = pieces[i - 1];
            var after = pieces[i];
            var beforeEnd = before.Segment.ConnectorManager.Lookup(before.End);
            var afterStart = after.Segment.ConnectorManager.Lookup(after.Start);
            try
            {
                var fitting = doc.Create.NewElbowFitting(beforeEnd, afterStart);
                doc.Regenerate();
                var attached = Ports(fitting).SelectMany(p => p.AllRefs.Cast<Connector>()).Select(p => p.Owner.Id).ToHashSet();
                if (!attached.Contains(before.Segment.Id) || !attached.Contains(after.Segment.Id))
                    throw new InvalidOperationException("Revit не подключил оба участка к отводу.");
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException error)
                when (error is Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                throw new InvalidOperationException($"Не удалось поставить отвод №{i}: не хватает места либо для выбранного типа или диаметра Revit не нашёл подходящий отвод. {error.Message}", error);
            }
        }

        for (int i = 0; i < pieces.Length; i++)
        {
            var piece = pieces[i];
            var a = piece.Segment.ConnectorManager.Lookup(piece.Start);
            var b = piece.Segment.ConnectorManager.Lookup(piece.End);
            if ((b.Origin - a.Origin).DotProduct((points[i + 1] - points[i]).Normalize()) <= doc.Application.ShortCurveTolerance)
                throw new InvalidOperationException($"После установки отводов на участке №{i + 1} не осталось места для короба.");
            if (!a.IsConnected || !b.IsConnected)
                throw new InvalidOperationException($"Revit не подключил оба конца участка №{i + 1}.");
        }
        if (!start.IsConnectedTo(pieces[0].Segment.ConnectorManager.Lookup(pieces[0].Start))
            || !end.IsConnectedTo(pieces[^1].Segment.ConnectorManager.Lookup(pieces[^1].End)))
            throw new InvalidOperationException("После установки отводов крайние участки отсоединились от выбранных выводов.");
    }

    static (Connector Inlet, Connector Outlet)? PutPlanElbow(Document doc, FamilySymbol symbol, Level level,
        XYZ corner, XYZ incoming, XYZ outgoing, double diameter)
    {
        if (!symbol.IsActive) symbol.Activate();
        var elbow = doc.Create.NewFamilyInstance(corner, symbol, level, StructuralType.NonStructural);
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
