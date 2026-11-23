using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using BotPlus.Eom.Core;

namespace BotPlus.Eom;

public sealed partial class ConduitThroughLightsCommand
{
    enum BoxPath { Standard, HeightFirst }

    // The preview uses an actual Revit commit in a group that is rolled back. A fitting can be accepted
    // by NewElbowFitting yet still fail when Revit finalizes the adjoining conduit.
    static LegChoices VerifySwitchBoxChoices(UIDocument ui, ConduitType type, FamilySymbol[] boxTypes,
        FamilySymbol? elbowType, Element source, FamilyInstance target, LegChoices choices)
    {
        var tried = new Dictionary<int, (Leg? Leg, string? Error)>();
        Leg Verify(Port start, Port end, bool align)
        {
            var key = start.Id ?? -1;
            if (!tried.TryGetValue(key, out var result))
            {
                var lead = choices.MakeLeg(start, end, align);
                result = FindSwitchLeg(ui, type, boxTypes, elbowType, source, target, lead with { End = end });
                tried.Add(key, result);
            }
            return result.Leg ?? throw new InvalidOperationException(result.Error);
        }
        var starts = choices.Starts;
        foreach (var start in starts)
        {
            var end = choices.Ends(start).FirstOrDefault();
            if (end == null) continue;
            try
            {
                Verify(start, end, false);
                starts = [start, .. starts.Where(other => other != start)];
                break;
            }
            catch (InvalidOperationException) { }
        }
        return choices with { Starts = starts, MakeLeg = Verify };
    }

    static (Leg? Leg, string? Error) FindSwitchLeg(UIDocument ui, ConduitType type, FamilySymbol[] boxTypes,
        FamilySymbol? elbowType, Element source, FamilyInstance target, Leg lead)
    {
        var doc = ui.Document;
        if (boxTypes.Length == 0)
            return (null, "Загрузите распределительную коробку АДСК или TSL с круглыми выводами короба.");
        string? reason = null;
        foreach (var candidate in SwitchLegs(lead.Start, lead.End, doc.Application.ShortCurveTolerance))
        {
            using var trial = new TransactionGroup(doc, "ЭОМ: пробная трасса до коробки");
            if (trial.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Revit не смог начать проверку трассы до коробки.");

            try
            {
                var (_, _, points) = CommitSwitchConnection(ui, type, boxTypes, elbowType, source, target, candidate);
                return (candidate with { Points = points }, null);
            }
            catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                reason ??= error.Message;
            }
            finally
            {
                if (trial.RollBack() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException("Revit не смог откатить пробное построение.");
            }
        }
        return (null, "Для выбранного выхода не удалось собрать трассу до выключателя. Выберите другой начальный вывод."
            + (reason == null ? "" : "\n" + reason));
    }

    static (Element Next, double Diameter, XYZ[] Points) CommitSwitchConnection(UIDocument ui, ConduitType type,
        FamilySymbol[] boxTypes, FamilySymbol? elbowType, Element source, FamilyInstance target, Leg main)
    {
        var doc = ui.Document;
        using var group = new TransactionGroup(doc, "ЭОМ: коробка и спуск к выключателю");
        if (group.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Revit не смог начать подключение выключателя.");

        var result = CommitLeg(doc, type, boxTypes, elbowType, source, target, main);
        var drop = PrepareLeg(ui, result.Next, null, target, FixturePoint(target), null, result.Diameter, 0, true);
        BuildSwitchDrop(ui, type, boxTypes, elbowType, result.Next, target, drop);
        if (group.Assimilate() != TransactionStatus.Committed)
            throw new InvalidOperationException("Revit отменил подключение выключателя.");
        return result;
    }

    static void BuildSwitchDrop(UIDocument ui, ConduitType type, FamilySymbol[] boxTypes, FamilySymbol? elbowType,
        Element box, FamilyInstance target, LegChoices drop)
    {
        string? reason = null;
        foreach (var start in drop.Starts)
        foreach (var end in drop.Ends(start))
        {
            try
            {
                var leg = drop.MakeLeg(start, end, false);
                CommitLeg(ui.Document, type, boxTypes, elbowType, box, target, leg);
                return;
            }
            catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            { reason ??= error.Message; }
        }
        throw new InvalidOperationException("Не удалось построить спуск от коробки к выключателю."
            + (reason == null ? "" : "\n" + reason));
    }

    static IEnumerable<Leg> SwitchLegs(Port start, Port end, double minimum)
    {
        var ends = new List<Port> { end };
        if (Math.Abs(start.Axis.Z) < 1 - DirectionTolerance)
        {
            var delta = Horizontal(end.Point - start.Point);
            var side = delta - start.Axis * delta.DotProduct(start.Axis);
            if (side.GetLength() > minimum)
            {
                var inlet = -side.Normalize();
                if (!ends.Any(port => port.Axis.DotProduct(inlet) > DirectionTolerance))
                    ends.Add(end with { Axis = inlet });
            }
        }

        var candidates = new List<Leg>();
        // A small positive gap lets the box inlet sit at the conduit elevation, still close to the ceiling.
        var gap = end.Point.Z - start.Point.Z;
        var inlets = ConduitRoutingRule.OfferAlignment(gap, Feet(50), PointTolerance) && Math.Abs(start.Axis.Z) < 1 - DirectionTolerance
            ? ends.Select(port => port with { Point = new XYZ(port.Point.X, port.Point.Y, start.Point.Z) }).Concat(ends)
            : ends;
        foreach (var mode in new[] { BoxPath.Standard, BoxPath.HeightFirst })
        foreach (var inlet in inlets)
        {
            XYZ[] points;
            try
            {
                points = mode == BoxPath.Standard
                    ? Path(start, inlet, minimum, false)
                    : SwitchPath(start, inlet, minimum);
            }
            catch (InvalidOperationException) { continue; }
            candidates.Add(new(start, inlet, XYZ.Zero, points, false, mode));
        }
        return candidates.OrderBy(leg => leg.Points.Length);
    }

    static XYZ[] SwitchPath(Port from, Port to, double minimum)
    {
        var start = from.Point;
        var end = to.Point;
        var u = from.Axis;
        var v = -to.Axis;
        var delta = end - start;
        var forward = delta.DotProduct(u);
        var side = Horizontal(delta) - u * forward;
        var parallel = u.DotProduct(v) > DirectionTolerance;
        if (Math.Abs(u.Z) > 1 - DirectionTolerance || Math.Abs(v.Z) > 1 - DirectionTolerance || forward <= minimum)
            throw new InvalidOperationException("У начала трассы недостаточно места для перехода по высоте.");

        if (!parallel || side.GetLength() <= minimum || Math.Abs(delta.Z) <= minimum || forward / 2 <= minimum)
            throw new InvalidOperationException("Для этого подхода нужен параллельный вывод коробки и место для перехода по высоте.");
        var first = start + u * (forward / 2);
        var bridge = first + XYZ.BasisZ * delta.Z;
        return [start, first, bridge, bridge + side, end];
    }
}
