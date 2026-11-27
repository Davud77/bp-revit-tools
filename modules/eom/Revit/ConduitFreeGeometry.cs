using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using BotPlus.Eom.Core;

namespace BotPlus.Eom;

public sealed partial class ConduitThroughLightsCommand
{
    const double FreeAngleTolerance = 0.01; // degrees
    sealed record FreeBend(double Angle, double Allowance);
    sealed record FreeSketch(XYZ[] Points, string? Hint = null, bool Invalid = false);
    static double FreeMinimum(Document doc) => Math.Max(doc.Application.ShortCurveTolerance, Feet(2.54));

    static FreeBend[] CheckFreeBends(Document doc, ConduitType type, Level level, FamilySymbol? elbowType, Port start)
    {
        var supported = new List<FreeBend>();
        double length = Math.Max(Feet(2000), start.Diameter!.Value * 20);
        var prior = RouteService.Current;
        RouteService.Current = WiringSymbols.None;
        try
        {
            foreach (var angle in ConduitRoutingRule.BendAngles)
            {
                using var trial = new TransactionGroup(doc, "ЭОМ: проверка угла отвода");
                if (trial.Start() != TransactionStatus.Started) break;
                try
                {
                    using var transaction = new Transaction(doc, "ЭОМ: пробный отвод");
                    var failures = StartRouteTransaction(transaction);
                    var corner = start.Point;
                    var direction = Turn(XYZ.BasisX, angle);
                    var (first, last) = BuildPath(doc, type, level, elbowType, null, null,
                        [corner - XYZ.BasisX * length, corner, corner + direction * length], start.Diameter.Value, free: true);
                    FinishRouteTransaction(transaction, failures, "Revit не принял пробный угол отвода.");
                    var a = EndAt(first, corner);
                    var b = EndAt(last, corner);
                    var installed = Math.Abs(TurnBetween(a.CoordinateSystem.BasisZ, -b.CoordinateSystem.BasisZ));
                    if (Math.Abs(installed - angle) <= FreeAngleTolerance
                        && OnAxis(a.Origin - corner, XYZ.BasisX) && OnAxis(b.Origin - corner, direction))
                    {
                        // У зеркального отвода вход и выход могут поменяться местами.
                        var allowance = Math.Max(a.Origin.DistanceTo(corner), b.Origin.DistanceTo(corner));
                        supported.Add(new(angle, allowance));
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
                {
                    System.Diagnostics.Trace.WriteLine(error.Message);
                }
                finally
                {
                    if (trial.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("Revit не откатил проверку отвода.");
                }
            }
        }
        finally { RouteService.Current = prior; }
        return supported.ToArray();
    }

    static XYZ Turn(XYZ vector, double degrees)
    {
        var angle = degrees * Math.PI / 180;
        return vector * Math.Cos(angle) + XYZ.BasisZ.CrossProduct(vector) * Math.Sin(angle);
    }

    static double TurnBetween(XYZ a, XYZ b) =>
        Math.Atan2(a.CrossProduct(b).Z, a.DotProduct(b)) * 180 / Math.PI;

    static XYZ FreeDirection(Port start, IReadOnlyList<XYZ> vertices) => vertices.Count switch
    {
        0 => start.Axis,
        1 => (vertices[0] - start.Point).Normalize(),
        _ => (vertices[^1] - vertices[^2]).Normalize()
    };

    static (XYZ Point, double Angle) SnapFreePoint(Port start, IReadOnlyList<XYZ> vertices, XYZ cursor,
        IReadOnlyList<double> angles, double minimum)
    {
        var tail = vertices.Count == 0 ? start.Point : vertices[^1];
        var delta = Horizontal(cursor - tail);
        var incoming = FreeDirection(start, vertices);
        if (incoming.GetLength() <= PointTolerance)
        {
            double heading = ConduitRoutingRule.SnapHeading(TurnBetween(XYZ.BasisX, delta));
            var snapped = Turn(XYZ.BasisX, heading);
            double forwardOnHeading = delta.DotProduct(snapped);
            if (forwardOnHeading <= minimum) throw new InvalidOperationException("Точка слишком близко.");
            return (tail + snapped * forwardOnHeading, heading);
        }

        // От физического вывода первый участок идёт по его оси. Следующие щелчки задают повороты.
        var angle = vertices.Count == 0 ? 0 : ConduitRoutingRule.SnapTurn(TurnBetween(incoming, delta), angles);
        var direction = Turn(incoming, angle);
        var forward = delta.DotProduct(direction);
        if (forward <= minimum)
            throw new InvalidOperationException("Укажите точку дальше по направлению трассы.");
        return (tail + direction * forward, angle);
    }

    static XYZ[] CheckedFreePoints(IEnumerable<XYZ> points, FreeBend[] bends, double minimum)
    {
        var distinct = new List<XYZ>();
        foreach (var point in points)
        {
            if (distinct.Count > 0 && point.DistanceTo(distinct[^1]) <= minimum)
                throw new InvalidOperationException("Точки трассы слишком близко.");
            if (distinct.Count > 1)
            {
                var incoming = (distinct[^1] - distinct[^2]).Normalize();
                var outgoing = point - distinct[^1];
                if (outgoing.DotProduct(incoming) > 0 && OnAxis(outgoing, incoming))
                    distinct.RemoveAt(distinct.Count - 1);
            }
            distinct.Add(point);
        }

        var fittings = new FreeBend?[distinct.Count];
        for (int i = 1; i < distinct.Count - 1; i++)
        {
            var measured = Math.Abs(TurnBetween(distinct[i] - distinct[i - 1], distinct[i + 1] - distinct[i]));
            fittings[i] = bends.FirstOrDefault(bend => Math.Abs(bend.Angle - measured) <= FreeAngleTolerance)
                ?? throw new InvalidOperationException($"У трубы нет проверенного отвода {measured:0.##}°.");
        }
        for (int i = 1; i < distinct.Count; i++)
        {
            double allowance = (fittings[i - 1]?.Allowance ?? 0) + (fittings[i]?.Allowance ?? 0);
            if (distinct[i].DistanceTo(distinct[i - 1]) - allowance <= minimum)
                throw new InvalidOperationException("Между точками не хватает длины под отводы.");
        }
        return distinct.ToArray();
    }

    static XYZ[] ConnectFreePoints(Port start, IReadOnlyList<XYZ> vertices, Port end, FreeBend[] bends, double minimum)
    {
        var tail = vertices.Count == 0 ? start.Point : vertices[^1];
        var incoming = FreeDirection(start, vertices);
        var approach = -end.Axis;
        var delta = end.Point - tail;
        var angles = bends.Select(bend => bend.Angle).ToArray();
        var candidates = new List<XYZ[]>();

        void Add(params XYZ[] finish)
        {
            if (vertices.Count == 0 && incoming.GetLength() > PointTolerance)
            {
                var first = finish[0] - tail;
                if (!OnAxis(first, incoming) || first.DotProduct(incoming) <= minimum) return;
            }
            try { candidates.Add(CheckedFreePoints([start.Point, .. vertices, .. finish], bends, minimum)); }
            catch (InvalidOperationException) { }
        }
        if (delta.DotProduct(approach) > minimum && OnAxis(delta, approach)) Add(end.Point);

        var turns = angles.SelectMany(angle => new[] { angle, -angle }).Prepend(0);
        var directions = incoming.GetLength() <= PointTolerance
            ? turns.Select(angle => Turn(approach, angle))
            : turns.Select(angle => Turn(incoming, angle));
        foreach (var direction in directions)
        {
            var divisor = direction.CrossProduct(approach).Z;
            if (Math.Abs(divisor) <= PointTolerance) continue;
            var first = delta.CrossProduct(approach).Z / divisor;
            var last = direction.CrossProduct(delta).Z / divisor;
            if (first > minimum && last > minimum)
                Add(tail + first * direction, end.Point);
        }
        if (incoming.GetLength() > PointTolerance)
        {
            try
            {
                var automatic = Path(start with { Point = tail, Axis = incoming }, end, minimum, true);
                Add(automatic[1..]);
            }
            catch (InvalidOperationException) { }
        }
        return candidates.OrderBy(points => points.Length)
            .ThenBy(points => points.Zip(points.Skip(1), (a, b) => a.DistanceTo(b)).Sum()).FirstOrDefault()
            ?? throw new InvalidOperationException("К выводу нельзя подойти с разрешёнными углами. Добавьте точку поворота или выберите другой вывод.");
    }
}
