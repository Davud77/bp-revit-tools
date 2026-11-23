using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;

namespace BotPlus.Eom;

public sealed partial class ConduitThroughLightsCommand
{
    static Port[] CandidatePorts(Element element, XYZ? hit, int? portId, XYZ other, bool inPlan,
        double? riserElevation = null, XYZ? riserDirection = null)
    {
        if (element is Conduit conduit && hit != null && portId == null)
            return TapPorts(conduit, hit, other, inPlan, riserElevation, riserDirection);
        return FreePorts(element, null).Where(port => portId == null || port.Id == portId).ToArray();
    }

    static bool IsVertical(XYZ axis) => Math.Abs(axis.Z) >= DirectionTolerance;
    static bool IsRiser(Element element) => element is Conduit && element.Location is LocationCurve { Curve: Line line }
        && IsVertical(line.Direction);

    static XYZ AxisPoint(Line line, XYZ hit, bool inPlan, double? riserElevation = null)
    {
        var a = line.GetEndPoint(0);
        var span = line.GetEndPoint(1) - a;
        var projected = inPlan ? Horizontal(span) : span;
        double square = projected.DotProduct(projected);
        if (inPlan && IsVertical(line.Direction))
            return a + span * (riserElevation is { } z ? Math.Clamp((z - a.Z) / span.Z, 0, 1) : 0.5);
        if (square <= PointTolerance * PointTolerance) return a + span * 0.5;
        double fraction = Math.Clamp((hit - a).DotProduct(projected) / square, 0, 1);
        return a + span * fraction;
    }

    static Port[] TapPorts(Conduit main, XYZ hit, XYZ other, bool inPlan, double? riserElevation = null, XYZ? riserDirection = null)
    {
        if (main.Location is not LocationCurve { Curve: Line line })
            throw new InvalidOperationException("Выберите прямой участок короба для ответвления.");
        bool vertical = IsVertical(line.Direction);
        var point = AxisPoint(line, hit, inPlan, riserElevation);
        var minimum = main.Document.Application.ShortCurveTolerance;
        if (inPlan && vertical && riserElevation is { } height && Math.Abs(point.Z - height) > PointTolerance)
            throw new InvalidOperationException($"Стояк не проходит через отметку трассы {Mm(height):0.##} мм. Выберите участок стояка, который пересекает эту отметку.");

        var axis = line.Direction;
        var aiming = other - point;
        var side = XYZ.BasisZ.CrossProduct(axis);
        if (side.GetLength() <= PointTolerance)
        {
            side = riserDirection != null ? Horizontal(riserDirection) : Horizontal(aiming);
            if (side.GetLength() <= PointTolerance) side = Horizontal(aiming);
            if (side.GetLength() <= PointTolerance) side = XYZ.BasisX;
        }
        side = side.Normalize();
        var normal = axis.CrossProduct(side).Normalize();
        var directions = new[] { side, -side, normal, -normal }.OrderByDescending(direction => direction.DotProduct(aiming));
        if (point.DistanceTo(line.GetEndPoint(0)) <= minimum || point.DistanceTo(line.GetEndPoint(1)) <= minimum)
        {
            if (vertical && Ports(main).FirstOrDefault(port => !port.IsConnected && port.Origin.DistanceTo(point) <= PointTolerance) is { } tip)
                return directions.Select(direction => Snapshot(tip) with { Axis = direction, TerminalElbow = true }).ToArray();
            throw new InvalidOperationException(vertical
                ? "На этой отметке точка внутри стояка слишком близко к торцу. Для отвода нужен свободный торец, а для врезки — место под тройник."
                : "Точка ответвления слишком близко к торцу короба. Выберите точку дальше от конца или щёлкните по метке свободного вывода.");
        }
        return directions.Select(direction => new Port(null, point, direction, main.Diameter, Tap: true)).ToArray();
    }

    static string PortMessage(Element element, double? expected, Port[] available)
    {
        var name = $"«{element.Name}»";
        if (available.Length > 0 && expected is { } size)
        {
            var sizes = string.Join(", ", available.Select(port => port.Diameter).OfType<double>()
                .Select(value => $"Ø{Mm(value):0.##} мм").Distinct());
            return $"Трасса Ø{Mm(size):0.##} мм. У {name} выбранный вывод или свободные выводы имеют другой диаметр ({sizes}). "
                + "Для соединения нужен переход соответствующего диаметра или участок подходящего размера.";
        }
        return Ports(element).Length == 0
            ? $"У {name} нет круглых концевых выводов короба."
            : $"У {name} нет свободного вывода. Выберите другой вывод или точку на прямом коробе.";
    }

    static Connector InsertTap(Document doc, Conduit main, Port tap)
    {
        if (main.Pinned || main.GroupId != ElementId.InvalidElementId || main.Location is not LocationCurve { Curve: Line line })
            throw new InvalidOperationException("Разделить можно прямой короб, если он не закреплён и не входит в группу.");
        var mainsize = main.Diameter;
        var axis = line.Direction;
        var a = line.GetEndPoint(0);
        var b = line.GetEndPoint(1);
        var level = doc.GetElement(main.LevelId) as Level
            ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .MinBy(item => Math.Abs(item.ProjectElevation - tap.Point.Z))
            ?? throw new InvalidOperationException("В проекте нет уровня для ответвления.");
        var fittings = BranchTypes(doc);
        if (fittings.Length == 0)
            throw new InvalidOperationException("Загрузите тройник короба или коробку «ADSK_Коробка_Открытая_Крестовина» с круглыми выводами.");

        string? reason = null;
        foreach (var type in fittings)
        {
            using var trial = new SubTransaction(doc);
            trial.Start();
            try
            {
                var (inlet, outlet, branch) = PlaceBranchFitting(doc, type, level, tap, axis, mainsize);
                var minimum = doc.Application.ShortCurveTolerance;
                if ((inlet.Origin - a).DotProduct(axis) <= minimum || (b - outlet.Origin).DotProduct(axis) <= minimum)
                    throw new InvalidOperationException("Между точкой ответвления и торцом не хватает места для соединительной детали.");
                SplitAndJoin(doc, main, a, b, inlet, outlet);
                if (trial.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit отклонил соединение ответвления.");
                return branch;
            }
            catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                reason ??= error.Message;
                if (trial.GetStatus() == TransactionStatus.Started) trial.RollBack();
            }
        }
        throw new InvalidOperationException(IsVertical(axis)
            ? $"Не удалось подключиться к стояку на отметке {Mm(tap.Point.Z):0.##} мм. Нужен тройник с двумя вертикальными выводами и боковым выводом Ø{Mm(mainsize):0.##} мм. {reason}"
            : $"Не удалось поставить ответвление на коробе Ø{Mm(mainsize):0.##} мм. {reason}");
    }

    static FamilySymbol[] BranchTypes(Document doc)
    {
        var tees = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ConduitFitting)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(symbol => symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased
                && (symbol.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)?.AsInteger() == (int)PartType.Tee
                    || symbol.FamilyName.Contains("тройник", StringComparison.OrdinalIgnoreCase)
                    || symbol.FamilyName.Contains("tee", StringComparison.OrdinalIgnoreCase)));
        return tees.Concat(BoxTypes(doc)).DistinctBy(symbol => symbol.Id).ToArray();
    }

    static (Connector Inlet, Connector Outlet, Connector Branch) PlaceBranchFitting(Document doc, FamilySymbol symbol,
        Level level, Port tap, XYZ axis, double diameter)
    {
        if (!symbol.IsActive) symbol.Activate();
        var fitting = doc.Create.NewFamilyInstance(new XYZ(tap.Point.X, tap.Point.Y, level.ProjectElevation),
            symbol, level, StructuralType.NonStructural);
        if (fitting.Category?.BuiltInCategory != BuiltInCategory.OST_ConduitFitting) RouteService.StyleNewElement(fitting);
        doc.Regenerate();
        var ports = FitPorts(doc, fitting, diameter).Where(port => MatchesDiameter(port, diameter)).ToArray();
        var desiredNormal = axis.CrossProduct(tap.Axis);
        bool flat = Math.Abs(desiredNormal.Z) >= DirectionTolerance;
        var triple = (from first in ports
                      from second in ports
                      from third in ports
                      where first.CoordinateSystem.BasisZ.DotProduct(second.CoordinateSystem.BasisZ) <= -DirectionTolerance
                         && Math.Abs(first.CoordinateSystem.BasisZ.DotProduct(third.CoordinateSystem.BasisZ)) <= 1 - DirectionTolerance
                      orderby flat && second.CoordinateSystem.BasisZ.CrossProduct(third.CoordinateSystem.BasisZ)
                          .DotProduct(desiredNormal) >= DirectionTolerance descending,
                          second.CoordinateSystem.BasisZ.DotProduct(axis) descending,
                          third.CoordinateSystem.BasisZ.DotProduct(tap.Axis) descending
                      select (first, second, third)).FirstOrDefault();
        if (triple.first == null)
            throw new InvalidOperationException($"У «{symbol.FamilyName}» нет трёх подходящих выводов Ø{Mm(diameter):0.##} мм.");

        var (inlet, outlet, branch) = triple;
        var pivot = inlet.Origin;
        var actualRun = outlet.CoordinateSystem.BasisZ;
        var rotationAxis = actualRun.CrossProduct(axis);
        if (rotationAxis.GetLength() > PointTolerance)
        {
            Rotate(doc, fitting, pivot, rotationAxis.Normalize(), actualRun.AngleTo(axis));
        }
        else if (actualRun.DotProduct(axis) < 0)
        {
            Rotate(doc, fitting, pivot, flat ? XYZ.BasisZ : branch.CoordinateSystem.BasisZ, Math.PI);
        }

        var actualBranch = branch.CoordinateSystem.BasisZ;
        Rotate(doc, fitting, pivot, axis, Math.Atan2(axis.DotProduct(actualBranch.CrossProduct(tap.Axis)), actualBranch.DotProduct(tap.Axis)));
        var runLine = inlet.Origin;
        var separation = branch.Origin - runLine;
        var center = runLine + axis * separation.DotProduct(axis);
        ElementTransformUtils.MoveElement(doc, fitting.Id, tap.Point - center);
        doc.Regenerate();

        if (!OnAxis(inlet.Origin - tap.Point, axis) || !OnAxis(outlet.Origin - tap.Point, axis)
            || !OnAxis(branch.Origin - tap.Point, tap.Axis) || branch.CoordinateSystem.BasisZ.DotProduct(tap.Axis) < DirectionTolerance)
            throw new InvalidOperationException("Оси выводов соединительной детали не совпадают с осями магистрали и ответвления.");
        return (inlet, outlet, branch);
    }

    static void Rotate(Document doc, Element fitting, XYZ pivot, XYZ axis, double angle)
    {
        if (Math.Abs(angle) <= PointTolerance) return;
        ElementTransformUtils.RotateElement(doc, fitting.Id, Line.CreateUnbound(pivot, axis), angle);
        doc.Regenerate();
    }

    static bool OnAxis(XYZ offset, XYZ axis) => (offset - axis * offset.DotProduct(axis)).GetLength() <= PointTolerance;

    static void SplitAndJoin(Document doc, Conduit main, XYZ a, XYZ b, Connector inlet, Connector outlet)
    {
        var left = EndAt(main, a);
        var right = EndAt(main, b);
        var leftPeer = ExteriorPeer(left);
        var rightPeer = ExteriorPeer(right);
        if (rightPeer != null) right.DisconnectFrom(rightPeer);

        var copy = ElementTransformUtils.CopyElement(doc, main.Id, XYZ.Zero).Select(doc.GetElement).OfType<Conduit>().Single();
        ((LocationCurve)main.Location).Curve = Line.CreateBound(a, inlet.Origin);
        ((LocationCurve)copy.Location).Curve = Line.CreateBound(outlet.Origin, b);
        doc.Regenerate();

        Join(EndAt(main, inlet.Origin), inlet);
        Join(outlet, EndAt(copy, outlet.Origin));
        if (leftPeer != null) Join(EndAt(main, a), leftPeer);
        if (rightPeer != null) Join(EndAt(copy, b), rightPeer);
        doc.Regenerate();
        if (!inlet.IsConnectedTo(EndAt(main, inlet.Origin)) || !outlet.IsConnectedTo(EndAt(copy, outlet.Origin)))
            throw new InvalidOperationException("Revit не сохранил оба соединения между магистралью и соединительной деталью.");
        CheckExterior(main, a, leftPeer);
        CheckExterior(copy, b, rightPeer);
    }

    static Connector? ExteriorPeer(Connector connector)
    {
        if (!connector.IsConnected) return null;
        var peers = connector.AllRefs.Cast<Connector>().Where(peer => peer.Owner.Id != connector.Owner.Id
            && peer.ConnectorType == ConnectorType.End && connector.IsConnectedTo(peer)).ToArray();
        if (peers.Length != 1)
            throw new InvalidOperationException("Не удалось однозначно определить соседний вывод магистрали.");
        return peers[0];
    }

    static void CheckExterior(Conduit segment, XYZ point, Connector? peer)
    {
        var end = EndAt(segment, point);
        if (end.Origin.DistanceTo(point) > PointTolerance || (peer == null ? end.IsConnected : !end.IsConnectedTo(peer)))
            throw new InvalidOperationException("При разделении изменилось существующее соединение на конце магистрали.");
    }
}
