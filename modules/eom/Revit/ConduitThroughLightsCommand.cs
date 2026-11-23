using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BotPlus.Eom.Core;
using System.Windows.Input;
using RevitCanceled = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace BotPlus.Eom;

[Transaction(TransactionMode.Manual)]
public sealed partial class ConduitThroughLightsCommand : EomCommand
{
    const string Title = "Короб, труба";
    const string BoxFamily = "TSL_CNF_т_Короб_Коробка распределительная";
    const string ElbowFamily = "TSL_CNF_т_Короб_Поворот на 90°";
    const double PointTolerance = 0.0001; // футы
    const double DirectionTolerance = 0.99999;

    sealed record Port(int? Id, XYZ Point, XYZ Axis, double? Diameter, bool NeedsBox = false)
    {
        public override string ToString() => Id is { } id
            ? $"№{id} · Ø{Mm(Diameter!.Value):0.##} мм · {Direction(Axis.X)}/{Direction(Axis.Y)}/{Direction(Axis.Z)}"
            : "Вывод распределительной коробки";
        static string Direction(double component) => component.ToString("+0.##;-0.##;0");
    }

    sealed record Leg(Port Start, Port End, XYZ Shift, XYZ[] Points, bool PlanarLights, BoxPath BoxPath = BoxPath.Standard);
    sealed record Pick(Element Element, XYZ Point, int? PortId = null);
    sealed record HoverLeg(XYZ[] Points, XYZ ConnectorPoint);

    protected override Result Run(UIDocument ui) => Route(ui);

    internal static Result Route(UIDocument ui)
    {
        using var tools = RouteTools.OpenConduit(ui);
        Result result;
        try { result = Draw(ui, tools); }
        catch (RevitCanceled) { result = Result.Cancelled; }
        return tools.BuiltRiser ? Result.Succeeded : result;
    }

    static Result Draw(UIDocument ui, RouteTools tools)
    {
        var doc = ui.Document;
        var settings = EomStore.Settings(doc);
        var boxTypes = BoxTypes(doc);
        var elbowType = FittingType(doc, ElbowFamily);
        var start = tools.Pick(() => PickBody(ui, e => IsLight(e) || e is Conduit || IsFitting(e), null,
            "Выберите начальный вывод. Shift+щелчок — поставить выключатель на стену."));
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return PlaceRoutedSwitch(ui, start);
        var source = start.Element;
        var type = ChooseType(doc, source);
        var visited = new HashSet<ElementId> { source.Id };
        XYZ? sourceHit = start.Point;
        int? sourcePortId = start.PortId;
        double? diameter = source is Conduit conduit ? conduit.Diameter : null;
        bool built = false;

        while (true)
        {
            Pick pick;
            try
            {
                var selectedSize = diameter ?? (sourceHit == null ? null
                    : Nearest(FreePorts(source, null).Where(port => sourcePortId == null || port.Id == sourcePortId),
                        sourceHit, ui.ActiveView is ViewPlan).FirstOrDefault()?.Diameter);
                tools.UseConduit(type.Id, selectedSize);
                pick = tools.Pick(() => PickBody(ui, e => !visited.Contains(e.Id) && (IsLight(e) || IsSwitch(e)), selectedSize,
                    "Щёлкните по метке нужного вывода. Щелчок по корпусу открывает выбор. Esc — завершить.",
                    (element, point, portId) => PreviewPath(ui, source, sourceHit, (FamilyInstance)element, point, diameter,
                        Feet(settings.RouteAlignmentMm), sourcePortId, portId)));
            }
            catch (RevitCanceled) { return built ? Result.Succeeded : Result.Cancelled; }
            catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                TaskDialog.Show(Title, error.Message);
                return built ? Result.Succeeded : Result.Cancelled;
            }

            var target = (FamilyInstance)pick.Element;
            try
            {
                bool switchBox = IsSwitch(target);
                var targetPoint = switchBox
                    ? SwitchBoxPlacement.Find(ui, target, Feet(settings.SwitchBoxOffsetMm))
                    : null;
                var changePort = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                var choices = PrepareLeg(ui, source, sourceHit, target, pick.Point, targetPoint, diameter,
                    Feet(settings.RouteAlignmentMm), false, sourcePortId, changePort ? null : pick.PortId);
                Leg? leg;
                if (switchBox)
                {
                    var verified = VerifySwitchBoxChoices(ui, type, boxTypes, elbowType, source, target, choices);
                    leg = ReviewLeg(ui, verified, true, false);
                }
                else if (changePort || pick.PortId == null && choices.Ends(choices.Starts[0]).Length > 1)
                    leg = ReviewLeg(ui, choices, false, false);
                else
                {
                    leg = SelectClickedLeg(choices);
                    if (leg.Shift.GetLength() > PointTolerance && !ConfirmAlignment(leg.Shift))
                        continue;
                }
                if (leg == null) continue;
                if (!built) RouteCommand.PrepareWiring(ui);
                var (next, size, _) = switchBox
                    ? CommitSwitchConnection(ui, type, boxTypes, elbowType, source, target, leg)
                    : CommitLeg(doc, type, boxTypes, elbowType, source, target, leg);
                source = next;
                sourceHit = null;
                sourcePortId = null;
                diameter = size;
                visited.Add(target.Id);
                built = true;
                ui.RefreshActiveView();

                if (FreePorts(source, diameter).Length == 0)
                {
                    TaskDialog.Show(Title, "У последнего прибора или коробки нет свободного выхода для продолжения трассы.");
                    return Result.Succeeded;
                }
            }
            catch (RevitCanceled) { }
            catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                TaskDialog.Show(Title, error.Message);
                if (built && FreePorts(source, diameter).Length == 0) return Result.Succeeded;
            }
        }
    }

    static Leg? ReviewLeg(UIDocument ui, LegChoices choices, bool switchBox, bool branch) =>
        ConduitRoutePreview.Review(ui, choices.Starts, choices.Ends, choices.CanAlign, choices.MakeLeg, switchBox, branch);

    static Leg SelectClickedLeg(LegChoices choices)
    {
        var start = choices.Starts[0];
        var end = choices.Ends(start).FirstOrDefault()
            ?? throw new InvalidOperationException("У прибора нет свободного соединителя нужного диаметра. Shift+щелчок открывает выбор соединителей.");
        return choices.MakeLeg(start, end, true);
    }

    static bool ConfirmAlignment(XYZ shift)
    {
        var dialog = new TaskDialog(Title)
        {
            MainInstruction = $"Сдвинуть второй светильник на {Mm(shift.GetLength()):0.##} мм?",
            MainContent = "Высота сохранится. После выравнивания BotPlus соединит выводы прямым участком.",
            CommonButtons = TaskDialogCommonButtons.Cancel
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Выровнять и построить");
        return dialog.Show() == TaskDialogResult.CommandLink1;
    }

    static HoverLeg? PreviewPath(UIDocument ui, Element source, XYZ? sourceHit, FamilyInstance target, XYZ hit,
        double? diameter, double alignment, int? sourcePortId, int? targetPortId)
    {
        if (IsSwitch(target)) return null;
        var (starts, ends, _, makeLeg) = PrepareLeg(ui, source, sourceHit, target, hit, null, diameter, alignment, false,
            sourcePortId, targetPortId);
        var start = starts[0];
        var end = ends(start).FirstOrDefault();
        return end == null ? null : new(makeLeg(start, end, true).Points, end.Point);
    }

    sealed record LegChoices(Port[] Starts, Func<Port, Port[]> Ends, Func<Port, Port, bool> CanAlign,
        Func<Port, Port, bool, Leg> MakeLeg);

    static LegChoices PrepareLeg(UIDocument ui, Element source, XYZ? sourceHit, FamilyInstance target, XYZ targetHit,
        XYZ? switchBox, double? diameter, double alignment, bool branch, int? sourcePortId = null, int? targetPortId = null)
    {
        var sourcePorts = FreePorts(source, diameter).Where(port => sourcePortId == null || port.Id == sourcePortId).ToArray();
        bool sourceBox = sourcePortId == null && sourcePorts.Length == 0 && IsLight(source) && Ports(source).Length == 0;
        if (sourcePorts.Length == 0 && !sourceBox)
            throw new InvalidOperationException("У начала трассы нет свободного круглого вывода нужного диаметра.");

        var targetPorts = switchBox == null
            ? FreePorts(target, diameter).Where(port => targetPortId == null || port.Id == targetPortId).ToArray()
            : [];
        bool targetBox = switchBox != null || !branch && IsLight(target) && Ports(target).Length == 0;
        if (targetPorts.Length == 0 && !targetBox)
            throw new InvalidOperationException("У прибора нет свободного круглого вывода нужного диаметра.");

        var destination = switchBox ?? (targetBox ? FixturePoint(target) : targetPorts[0].Point);
        if (sourceBox)
        {
            var fixture = (FamilyInstance)source;
            var point = FixturePoint(fixture);
            var heading = Toward(point, destination);
            var transform = fixture.GetTransform();
            var axes = new[] { transform.BasisX, transform.BasisY }.Select(Horizontal)
                .Where(axis => axis.GetLength() > PointTolerance).Select(axis => axis.Normalize());
            var axis = axes.SelectMany(direction => new[] { direction, -direction })
                .OrderByDescending(direction => direction.DotProduct(heading)).FirstOrDefault() ?? heading;
            sourcePorts = [new(null, point, axis, null, true)];
        }

        bool plan = IsLight(target);
        bool canMove = plan && !target.Pinned && target.GroupId == ElementId.InvalidElementId && target.Location is LocationPoint;
        var orderedSources = sourceHit != null
            ? Nearest(sourcePorts, sourceHit, ui.ActiveView is ViewPlan)
            : sourcePorts.OrderByDescending(p => p.Axis.DotProduct((destination - p.Point).Normalize())).ToArray();

        Port[] CandidateEnds(Port start)
        {
            if (!targetBox)
                return Nearest(targetPorts.Where(p => start.Diameter == null || SameSize(p.Diameter, start.Diameter)),
                    targetHit, ui.ActiveView is ViewPlan);

            var point = destination;
            if (switchBox == null)
                point = new XYZ(point.X, point.Y, start.Point.Z + (Math.Abs(start.Axis.Z) > DirectionTolerance ? start.Axis.Z * Feet(150) : 0));
            var inlet = Math.Abs(start.Axis.Z) < 1 - DirectionTolerance && start.Axis.DotProduct(point - start.Point) > PointTolerance
                ? -start.Axis
                : -Toward(start.Point, point);
            return [new(null, point, inlet, start.Diameter, true)];
        }

        Leg MakeLeg(Port start, Port end, bool align)
        {
            if (OrientBoxToTarget(start, end))
                start = start with { Axis = -end.Axis, Diameter = end.Diameter };
            var shift = align ? AlignmentShift(start, end, alignment) : XYZ.Zero;
            if (!canMove && shift.GetLength() > PointTolerance)
                throw new InvalidOperationException("Для прямой трассы требуется сдвиг светильника. Revit запрещает сдвиг закреплённого прибора или прибора в группе.");
            var moved = end with { Point = end.Point + shift };
            if (switchBox != null)
                return SwitchLegs(start, end, ui.Document.Application.ShortCurveTolerance).FirstOrDefault()
                    ?? throw new InvalidOperationException("К коробке нельзя подойти под 90° из выбранного вывода. Выберите другой начальный вывод.");
            return new(start, end, shift, Path(start, moved, ui.Document.Application.ShortCurveTolerance, plan), plan);
        }

        bool CanAlign(Port start, Port to)
        {
            if (!canMove) return false;
            if (OrientBoxToTarget(start, to)) start = start with { Axis = -to.Axis };
            return AlignmentShift(start, to, alignment).GetLength() > PointTolerance;
        }

        bool CanBuild(Port start, Port end)
        {
            try { MakeLeg(start, end, true); return true; }
            catch (InvalidOperationException) { return false; }
        }

        Port[] Ends(Port start) => CandidateEnds(start).OrderByDescending(end => CanBuild(start, end))
            .ThenByDescending(end => plan && start.Axis.DotProduct(-end.Axis) >= DirectionTolerance).ToArray();
        orderedSources = orderedSources.OrderByDescending(start => CandidateEnds(start).Any(end => CanBuild(start, end))).ToArray();
        return new(orderedSources, Ends, CanAlign, MakeLeg);
    }

    static bool OrientBoxToTarget(Port start, Port end) =>
        start.NeedsBox && !end.NeedsBox && Math.Abs(end.Axis.Z) < 1 - DirectionTolerance
        && (-end.Axis).DotProduct(end.Point - start.Point) > PointTolerance;

    static XYZ AlignmentShift(Port from, Port to, double tolerance)
    {
        if (Math.Abs(from.Axis.Z) > 1 - DirectionTolerance || from.Axis.DotProduct(-to.Axis) < DirectionTolerance
            || Math.Abs(from.Point.Z - to.Point.Z) > PointTolerance)
            return XYZ.Zero;
        var delta = Horizontal(to.Point - from.Point);
        var forward = delta.DotProduct(from.Axis);
        if (forward <= PointTolerance) return XYZ.Zero;
        var transverse = delta - from.Axis * forward;
        return ConduitRoutingRule.OfferAlignment(transverse.GetLength(), tolerance, PointTolerance) ? -transverse : XYZ.Zero;
    }

    static XYZ[] Path(Port from, Port to, double minimum, bool planarLights)
    {
        var start = from.Point;
        var end = to.Point;
        var u = from.Axis;
        var v = -to.Axis;
        var delta = end - start;
        var forward = delta.DotProduct(u);
        var cross = delta - u * forward;
        double parallel = u.DotProduct(v);
        bool horizontalEnds = Math.Abs(u.Z) < 1 - DirectionTolerance && Math.Abs(v.Z) < 1 - DirectionTolerance;

        if (horizontalEnds && planarLights && Math.Abs(delta.Z) > PointTolerance)
            throw new InvalidOperationException("Выводы на разных отметках. Для этого участка нужен отдельный переход по высоте.");

        if (parallel >= DirectionTolerance)
        {
            var lead = ConduitRoutingRule.ParallelLead(forward, cross.GetLength(), PointTolerance, minimum);
            if (lead == 0) return [start, end];
            var first = start + u * lead;
            var last = end - u * lead;
            var side = Horizontal(last - first);
            if (horizontalEnds && Math.Abs(last.Z - first.Z) > PointTolerance && side.GetLength() > PointTolerance)
            {
                if (side.GetLength() <= minimum || Math.Abs(last.Z - first.Z) <= minimum)
                    throw new InvalidOperationException("Для перехода на отметку потолка не хватает места между отводами.");
                return [start, first, first + side, last, end];
            }
            return [start, first, last, end];
        }

        if (Math.Abs(parallel) <= 1 - DirectionTolerance)
        {
            var approach = delta.DotProduct(v);
            if (forward > minimum && approach > minimum)
            {
                var first = start + u * forward;
                var last = end - v * approach;
                if (first.DistanceTo(last) <= PointTolerance) return [start, first, end];
                if (first.DistanceTo(last) > minimum) return [start, first, last, end];
            }
        }

        throw new InvalidOperationException("Выводы направлены так, что между ними не помещаются участки с поворотами 90°. Выберите другой соединитель.");
    }

    static (Element Next, double Diameter, XYZ[] Points) CommitLeg(Document doc, ConduitType type, FamilySymbol[] boxTypes,
        FamilySymbol? elbowType, Element source, FamilyInstance target, Leg leg)
    {
        var level = doc.GetElement(source.LevelId) as Level
            ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .MinBy(l => Math.Abs(l.ProjectElevation - leg.Start.Point.Z))
            ?? throw new InvalidOperationException("В проекте нет уровня для построения короба.");
        if ((leg.Start.NeedsBox || leg.End.NeedsBox) && boxTypes.Length == 0)
            throw new InvalidOperationException($"Загрузите коробку АДСК («ADSK_Коробка_Открытая_Крестовина») или «{BoxFamily}» в проект.");

        bool align = leg.Shift.GetLength() > PointTolerance;
        using var group = align ? new TransactionGroup(doc, "ЭОМ: выравнивание и построение трассы") : null;
        if (group != null && group.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Revit не смог начать выравнивание.");
        var size = leg.Start.Diameter ?? leg.End.Diameter;
        XYZ? expected = null;
        if (align)
        {
            using var move = new Transaction(doc, "ЭОМ: выравнивание светильника");
            var moveFailures = StartRouteTransaction(move);
            expected = FixturePoint(target) + leg.Shift;
            ElementTransformUtils.MoveElement(doc, target.Id, leg.Shift);
            doc.Regenerate();
            if (FixturePoint(target).DistanceTo(expected) > PointTolerance)
                throw new InvalidOperationException("Основа или ограничения светильника не позволяют сдвинуть его с сохранением отметки.");
            FinishRouteTransaction(move, moveFailures, "Не удалось выровнять светильник. Проверьте его основу и существующие соединения.");
        }

        using var transaction = new Transaction(doc, "ЭОМ: участок трассы освещения");
        var routeFailures = StartRouteTransaction(transaction);
        if (leg.Start.NeedsBox)
            source = PutCompatibleBox(doc, boxTypes, level, leg.Start.Point, leg.Start.Axis, size);
        var start = ResolvePort(source, leg.Start, size);
        size ??= start.Radius * 2;

        Element next = target;
        if (leg.End.NeedsBox)
            next = PutCompatibleBox(doc, boxTypes, level, leg.End.Point + leg.Shift, leg.End.Axis, size);
        var end = ResolvePort(next, leg.End, size);

        var points = leg.BoxPath == BoxPath.Standard
            ? Path(Snapshot(start), Snapshot(end), doc.Application.ShortCurveTolerance, leg.PlanarLights)
            : SwitchPath(Snapshot(start), Snapshot(end), doc.Application.ShortCurveTolerance);
        if (expected != null && (FixturePoint(target).DistanceTo(expected) > PointTolerance || points.Length != 2))
            throw new InvalidOperationException("После сдвига выводы не совпали по оси на исходной отметке. Проверьте ограничения светильника.");
        BuildPath(doc, type, level, elbowType, start, end, points, size.Value);
        if (expected != null && FixturePoint(target).DistanceTo(expected) > PointTolerance)
            throw new InvalidOperationException("При подключении Revit изменил положение выровненного светильника.");

        FinishRouteTransaction(transaction, routeFailures, "Revit отменил построение участка. Проверьте соединители и место для отводов.");
        if (group != null && group.Assimilate() != TransactionStatus.Committed)
            throw new InvalidOperationException("Revit отменил выравнивание и построение участка.");
        return (next, size.Value, points);
    }

    internal static RouteFailures StartRouteTransaction(Transaction transaction)
    {
        if (transaction.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Revit не смог начать изменение модели.");
        var handler = new RouteFailures();
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
            .SetFailuresPreprocessor(handler).SetClearAfterRollback(true));
        return handler;
    }

    internal static void FinishRouteTransaction(Transaction transaction, RouteFailures failures, string message)
    {
        if (transaction.Commit() == TransactionStatus.Committed) return;
        throw new InvalidOperationException(failures.Message is { } detail ? $"{message}\n{detail}" : message);
    }

    internal sealed class RouteFailures : IFailuresPreprocessor
    {
        internal string? Message { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failures)
        {
            var descriptions = failures.GetFailureMessages()
                .Where(failure => failure.GetSeverity() >= FailureSeverity.Error)
                .Select(failure => failure.GetDescriptionText()).Distinct().ToArray();
            if (descriptions.Length == 0) return FailureProcessingResult.Continue;
            Message = string.Join("\n", descriptions);
            return FailureProcessingResult.ProceedWithRollBack;
        }
    }

    static Connector ResolvePort(Element element, Port planned, double? diameter)
    {
        var port = planned.Id is { } id
            ? Ports(element).FirstOrDefault(p => p.Id == id)
            : FacingPort(element, planned.Axis, diameter);
        if (port == null || port.IsConnected || !MatchesDiameter(port, diameter))
            throw new InvalidOperationException("Выбранный вывод занят или его диаметр не подходит.");
        return port;
    }

    static Port[] FreePorts(Element element, double? diameter) => Ports(element)
        .Where(p => !p.IsConnected && MatchesDiameter(p, diameter)).OrderBy(p => p.Id).Select(Snapshot).ToArray();

    static Port Snapshot(Connector port) => new(port.Id, port.Origin, port.CoordinateSystem.BasisZ, port.Radius * 2);

    static Port[] Nearest(IEnumerable<Port> ports, XYZ point, bool plan) => ports
        .OrderBy(p => (plan ? Horizontal(p.Point - point) : p.Point - point).GetLength())
        .ThenBy(p => p.Id).ToArray();

    static XYZ Toward(XYZ from, XYZ to)
    {
        var delta = Horizontal(to - from);
        if (delta.GetLength() <= PointTolerance)
            throw new InvalidOperationException("Точки подключения совпадают в плане.");
        return delta.Normalize();
    }

    static bool SameSize(double? a, double? b) => a == null || b == null || Math.Abs(a.Value - b.Value) <= PointTolerance;
    static bool IsLight(Element e) => e.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures && e is FamilyInstance;
    static bool IsSwitch(Element e) => e.Category?.BuiltInCategory == BuiltInCategory.OST_LightingDevices && e is FamilyInstance;
    static bool IsFitting(Element e) => e.Category?.BuiltInCategory == BuiltInCategory.OST_ConduitFitting && e is FamilyInstance;
    static double Feet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
}
