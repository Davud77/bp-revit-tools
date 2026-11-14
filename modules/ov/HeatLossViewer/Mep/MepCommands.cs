using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using View = Autodesk.Revit.DB.View;

namespace HeatLossViewer.Mep;

/// <summary>Moves (and turns) the first element so its nearest free connector meets the reference connector. Repeats until Esc.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class MepConnectCommand : IExternalCommand
{
    const string Title = "Соединить коннекторы";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        while (true)
        {
            var a = Mep.Pick(ui, "Присоединяемый элемент: щёлкните рядом с его свободным коннектором. Esc — выход");
            if (a is null) break;
            var b = Mep.Pick(ui, "Опорный элемент: щёлкните рядом с его свободным коннектором. Esc — выход", e => e.Id != a.Value.Element.Id);
            if (b is null) break;
            Mep.Run(doc, Title, () => Connect(doc, a.Value.Element, a.Value.Point, b.Value.Element, b.Value.Point, ui.ActiveView), Title);
        }
        return Result.Succeeded;
    }

    internal static void Connect(Document doc, Element moving, XYZ movingPick, Element reference, XYZ referencePick, View view)
    {
        var cm = Mep.Nearest(Mep.Ends(moving).Where(c => !Mep.Connected(c)), movingPick, view)
            ?? throw new InvalidOperationException("У присоединяемого элемента нет свободных коннекторов.");
        var cr = Mep.Nearest(Mep.Ends(reference).Where(c => !Mep.Connected(c)), referencePick, view)
            ?? throw new InvalidOperationException("У опорного элемента нет свободных коннекторов.");
        if (Mep.Ends(moving).Any(c => c.Id != cm.Id && Mep.Connected(c)))
            throw new InvalidOperationException("Присоединяемый элемент уже подключён к сети и при перемещении оторвался бы от неё. " +
                "Отключите его или выберите свободный элемент.");
        if (Mep.Locked(moving) is { } why) throw new InvalidOperationException($"Присоединяемый элемент {why}.");
        if (cm.Domain != cr.Domain) throw new InvalidOperationException("Коннекторы относятся к разным инженерным системам.");

        int id = cm.Id;
        var target = cr.CoordinateSystem;
        // 1. Directions opposite: connectors of a joint face each other.
        var turn = MepGeometry.Align(Mep.V(cm.CoordinateSystem.BasisZ), -Mep.V(target.BasisZ), Mep.V(target.BasisX));
        if (Math.Abs(turn.Angle) > MepGeometry.AngleTolerance)
            ElementTransformUtils.RotateElement(doc, moving.Id, Line.CreateUnbound(cm.Origin, Mep.X(turn.Axis)), turn.Angle);
        doc.Regenerate();
        cm = Mep.Find(moving, id);
        // 2. Non-round sections: align width with width (either sense), so the joint needs no extra twist.
        if (cm.Shape != ConnectorProfileType.Round)
        {
            var options = MepGeometry.OrientAbout(Mep.V(target.BasisZ), Mep.V(cm.CoordinateSystem.BasisX), Mep.V(target.BasisX));
            if (options.Count > 0)
            {
                double twist = options.OrderBy(o => Math.Abs(o.Angle)).First().Angle;
                if (Math.Abs(twist) > MepGeometry.AngleTolerance)
                    ElementTransformUtils.RotateElement(doc, moving.Id, Line.CreateUnbound(cm.Origin, target.BasisZ), twist);
                doc.Regenerate();
                cm = Mep.Find(moving, id);
            }
        }
        // 3. Origins coincide.
        var shift = cr.Origin - cm.Origin;
        if (shift.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, moving.Id, shift);
        doc.Regenerate();
        Mep.Join(doc, Mep.Find(moving, id), cr);
    }
}

/// <summary>Joins straight segments on one line: closes gaps by extending free ends and adds unions/transitions.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class MepConnectLineCommand : IExternalCommand
{
    const string Title = "Соединить участки";
    static bool Straight(Element e) => e is MEPCurve and not FlexDuct and not Autodesk.Revit.DB.Plumbing.FlexPipe && e.Location is LocationCurve { Curve: Line };

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        var curves = ui.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null && Straight(e)).Cast<MEPCurve>().ToList();
        if (curves.Count < 2)
        {
            try
            {
                curves = ui.Selection.PickObjects(ObjectType.Element, new ElementFilter(Straight), "Выберите прямолинейные участки на одной прямой и нажмите «Готово»")
                    .Select(r => doc.GetElement(r)).Cast<MEPCurve>().ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        }
        int joined = 0;
        bool ok = Mep.Run(doc, Title, () => joined = Join(doc, curves), Title);
        if (ok) TaskDialog.Show(Title, joined == 0 ? "Участки уже соединены." : $"Создано соединений: {joined}.");
        return ok ? Result.Succeeded : Result.Failed;
    }

    internal static int Join(Document doc, IReadOnlyList<MEPCurve> curves)
    {
        if (curves.Count < 2) throw new InvalidOperationException("Выберите не менее двух участков.");
        if (curves.Select(c => c.Category.Id).Distinct().Count() > 1) throw new InvalidOperationException("Участки должны быть одной категории.");
        Mep.EnsureEditable(doc, curves.Select(c => c.Id));
        double tol = doc.Application.ShortCurveTolerance;
        var lines = curves.Select(c => (Line)((LocationCurve)c.Location).Curve).ToList();
        var chain = MepGeometry.OrderCollinear(lines.Select(l => (Mep.V(l.GetEndPoint(0)), Mep.V(l.GetEndPoint(1)))).ToList(), tol);
        int joined = 0;
        foreach (var gap in chain.Gaps)
        {
            var from = curves[gap.From]; var to = curves[gap.To];
            var toLine = (Line)((LocationCurve)to.Location).Curve;
            var fromLine = (Line)((LocationCurve)from.Location).Curve;
            // Facing ends: the end of «from» nearest to «to» and vice versa.
            XYZ toNear = new[] { toLine.GetEndPoint(0), toLine.GetEndPoint(1) }.OrderBy(p => fromLine.Distance(p)).First();
            var cFrom = Mep.NearestEnd(from, toNear);
            var cTo = Mep.NearestEnd(to, cFrom.Origin);
            if (Mep.Neighbors(cFrom).Any(n => n.Owner.Id == to.Id) || Mep.Neighbors(cFrom).Any(n => Mep.Neighbors(n).Any(m => m.Owner.Id == to.Id)))
                continue; // already joined, directly or through a fitting
            if (Mep.Connected(cFrom) || Mep.Connected(cTo))
                throw new InvalidOperationException($"Между участками {from.Id} и {to.Id} уже есть другое подключение.");
            if (Mep.SameSize(cFrom, cTo))
            {
                if (gap.Gap > tol)
                {
                    // Extend only the free end; the other end and its neighbours stay where they are.
                    int end = fromLine.GetEndPoint(1).DistanceTo(cFrom.Origin) < fromLine.GetEndPoint(0).DistanceTo(cFrom.Origin) ? 1 : 0;
                    var fixedPoint = fromLine.GetEndPoint(1 - end);
                    ((LocationCurve)from.Location).Curve = end == 1 ? Line.CreateBound(fixedPoint, cTo.Origin) : Line.CreateBound(cTo.Origin, fixedPoint);
                    doc.Regenerate();
                    cFrom = Mep.NearestEnd(from, cTo.Origin);
                }
                Mep.Join(doc, cFrom, cTo);
            }
            else doc.Create.NewTransitionFitting(cFrom, cTo);
            joined++;
        }
        return joined;
    }
}

/// <summary>Connects air terminals to a duct: optional flexible part, rigid riser, takeoff or tee.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class MepDiffuserCommand : IExternalCommand
{
    const string Title = "Подключить диффузоры";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        var settings = MepToolSettings.Load();
        var window = new DiffuserWindow(doc, settings);
        new WindowInteropHelper(window) { Owner = data.Application.MainWindowHandle };
        if (window.ShowDialog() != true) return Result.Cancelled;
        settings.Save();
        while (true)
        {
            var terminal = Mep.Pick(ui, "Выберите диффузор. Esc — выход", e => e.Category?.BuiltInCategory == BuiltInCategory.OST_DuctTerminal);
            if (terminal is null) break;
            var duct = Mep.Pick(ui, "Выберите воздуховод для подключения. Esc — выход", e => e is Duct);
            if (duct is null) break;
            Mep.Run(doc, Title, () => Connect(doc, (FamilyInstance)terminal.Value.Element, (Duct)duct.Value.Element, settings), Title);
        }
        return Result.Succeeded;
    }

    static void Connect(Document doc, FamilyInstance terminal, Duct main, MepToolSettings settings)
    {
        var free = Mep.Ends(terminal).Where(c => !Mep.Connected(c)).ToList();
        if (free.Count != 1) throw new InvalidOperationException($"У диффузора должен быть один свободный коннектор, найдено: {free.Count}.");
        var tc = free[0];
        Mep.EnsureEditable(doc, [main.Id]);
        var options = tc.Shape switch { ConnectorProfileType.Round => settings.Round, ConnectorProfileType.Rectangular => settings.Rectangular, _ => settings.Oval };
        var mainLine = (Line)((LocationCurve)main.Location).Curve;
        var mainEnd = Mep.Ends(main).First();
        double half = (mainEnd.Shape == ConnectorProfileType.Round ? mainEnd.Radius : mainEnd.Height / 2);
        double flex = options.Use ? UnitUtils.ConvertToInternalUnits(options.LengthMm, UnitTypeId.Millimeters) : 0;
        double tol = doc.Application.ShortCurveTolerance;
        var route = MepGeometry.RouteDiffuser(Mep.V(tc.Origin), Mep.V(tc.CoordinateSystem.BasisZ), Mep.V(mainLine.GetEndPoint(0)), Mep.V(mainLine.GetEndPoint(1)),
            half, flex, settings.Tee, tol);

        var ductType = main.DuctType.Shape == tc.Shape ? main.DuctType
            : new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>().FirstOrDefault(t => t.Shape == tc.Shape)
              ?? throw new InvalidOperationException("В проекте нет типа воздуховода с формой коннектора диффузора.");
        var system = main.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM).AsElementId();
        var level = main.ReferenceLevel?.Id ?? throw new InvalidOperationException("У воздуховода нет базового уровня.");

        Duct Segment(XYZ a, XYZ b)
        {
            var d = Duct.Create(doc, system, ductType.Id, level, a, b);
            if (tc.Shape == ConnectorProfileType.Round) d.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM).Set(tc.Radius * 2);
            else { d.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM).Set(tc.Width); d.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM).Set(tc.Height); }
            return d;
        }

        var riser = Segment(Mep.X(route.RiserBottom), Mep.X(route.RiserTop));
        doc.Regenerate();
        var bottom = Mep.NearestEnd(riser, Mep.X(route.RiserBottom));
        if (tc.Shape != ConnectorProfileType.Round)
        {
            // Match the diffuser neck: rotate the vertical riser about its axis so the sections coincide.
            var twist = MepGeometry.OrientAbout(V3.ZAxis, Mep.V(bottom.CoordinateSystem.BasisX), Mep.V(tc.CoordinateSystem.BasisX));
            if (twist.Count > 0 && Math.Abs(twist.OrderBy(o => Math.Abs(o.Angle)).First().Angle) > MepGeometry.AngleTolerance)
                ElementTransformUtils.RotateElement(doc, riser.Id, Line.CreateUnbound(tc.Origin, XYZ.BasisZ), twist.OrderBy(o => Math.Abs(o.Angle)).First().Angle);
            doc.Regenerate();
            bottom = Mep.NearestEnd(riser, Mep.X(route.RiserBottom));
        }
        if (flex > 0)
        {
            var flexType = new FilteredElementCollector(doc).OfClass(typeof(FlexDuctType)).Cast<FlexDuctType>()
                .FirstOrDefault(t => t.Shape == tc.Shape && t.Name == options.Type)
                ?? throw new InvalidOperationException($"Не найден тип гибкого воздуховода «{options.Type}».");
            doc.Create.NewFlexDuct(tc, bottom, flexType);
        }
        else tc.ConnectTo(bottom);

        Connector branch = Mep.NearestEnd(riser, Mep.X(route.RiserTop));
        if (route.Horizontal)
        {
            var leg = Segment(Mep.X(route.RiserTop), Mep.X(route.TapPoint));
            doc.Regenerate();
            doc.Create.NewElbowFitting(branch, Mep.NearestEnd(leg, Mep.X(route.RiserTop)));
            branch = Mep.NearestEnd(leg, Mep.X(route.TapPoint));
        }
        if (settings.Tee)
        {
            var otherId = MechanicalUtils.BreakCurve(doc, main.Id, Mep.X(route.TapPoint));
            doc.Regenerate();
            var other = (Duct)doc.GetElement(otherId);
            doc.Create.NewTeeFitting(Mep.NearestEnd(main, Mep.X(route.TapPoint)), Mep.NearestEnd(other, Mep.X(route.TapPoint)), branch);
        }
        else doc.Create.NewTakeoffFitting(branch, main);
    }
}

/// <summary>Disconnects the connected connector nearest to each click. Repeats until Esc.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class MepDisconnectCommand : IExternalCommand
{
    const string Title = "Разъединить";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        while (true)
        {
            var pick = Mep.Pick(ui, "Щёлкните рядом с подключенным коннектором. Esc — выход");
            if (pick is null) break;
            Mep.Run(doc, Title, () =>
            {
                var c = Mep.Nearest(Mep.Connectors(pick.Value.Element).Where(Mep.Connected), pick.Value.Point, ui.ActiveView)
                    ?? throw new InvalidOperationException("У элемента нет подключенных коннекторов.");
                foreach (var n in Mep.Neighbors(c)) c.DisconnectFrom(n);
            }, Title);
        }
        return Result.Succeeded;
    }
}

/// <summary>Cuts the connections between a branch and the element it hangs on, then selects the branch.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class MepDetachBranchCommand : IExternalCommand
{
    const string Title = "Отключить ветку";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        var branch = Mep.Pick(ui, "Выберите любой элемент отключаемой ветки");
        if (branch is null) return Result.Cancelled;
        var main = Mep.Pick(ui, "Выберите элемент системы, от которого отключить ветку", e => e.Id != branch.Value.Element.Id);
        if (main is null) return Result.Cancelled;
        HashSet<ElementId> part = [];
        int cut = 0;
        bool ok = Mep.Run(doc, Title, () =>
        {
            var mainId = main.Value.Element.Id;
            IEnumerable<(ElementId, bool, bool)> Edges(ElementId id) =>
                Mep.Connectors(doc.GetElement(id)).SelectMany(Mep.Neighbors).Select(n => (n.Owner.Id, false, n.Owner.Id == mainId));
            part = MepGeometry.Reachable(branch.Value.Element.Id, Edges, true);
            foreach (var id in part)
                foreach (var c in Mep.Connectors(doc.GetElement(id)))
                    foreach (var n in Mep.Neighbors(c).Where(n => n.Owner.Id == mainId)) { c.DisconnectFrom(n); cut++; }
            if (cut == 0) throw new InvalidOperationException("Ветка не подключена к выбранному элементу.");
        }, Title);
        if (!ok) return Result.Failed;
        ui.Selection.SetElementIds(part.ToList());
        TaskDialog.Show(Title, $"Разорвано соединений: {cut}.\nЭлементов в отключенной ветке: {part.Count} (выделены).");
        return Result.Succeeded;
    }
}

/// <summary>Trial rotation of an element (and optionally its connected part) about a connector axis.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class MepRotateCommand : IExternalCommand
{
    const string Title = "Вращать вокруг коннектора";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        Element? element = ui.Selection.GetElementIds().Count == 1 ? doc.GetElement(ui.Selection.GetElementIds().First()) : null;
        if (element is null || !Mep.HasConnectors(element))
        {
            var pick = Mep.Pick(ui, "Выберите вращаемый элемент");
            if (pick is null) return Result.Cancelled;
            element = pick.Value.Element;
        }
        var refPick = Mep.Pick(ui, "Щёлкните рядом с коннектором, задающим ось вращения (можно у другого элемента)");
        if (refPick is null) return Result.Cancelled;
        var reference = Mep.Nearest(Mep.Ends(refPick.Value.Element), refPick.Value.Point, ui.ActiveView);
        if (reference is null) { TaskDialog.Show(Title, "У выбранного элемента нет коннекторов с направлением."); return Result.Cancelled; }
        var origin = reference.Origin;
        var axis = Mep.X(MepGeometry.ViewerAxis(Mep.V(reference.CoordinateSystem.BasisZ), reference.Owner.Id == element.Id));
        var target = element;
        return Trial(data, Title, "Положительное направление — против часовой стрелки, если смотреть со стороны опорного коннектора на элемент.", true, false, (window, angle) =>
        {
            var ids = Mep.Moving(doc, target, reference, window.RotateConnected, window.RotateAxial, origin, axis);
            Mep.EnsureEditable(doc, ids);
            ElementTransformUtils.RotateElements(doc, ids, Line.CreateUnbound(origin, axis), angle);
        });
    }

    /// <summary>Shared trial loop: TransactionGroup + one committed transaction per click.</summary>
    internal static Result Trial(ExternalCommandData data, string title, string hint, bool options, bool rightAngle, Action<TrialRotationWindow, double> rotate)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        var settings = MepToolSettings.Load();
        using var group = new TransactionGroup(doc, "BotPlus: " + title);
        group.Start();
        var window = new TrialRotationWindow(title, hint, settings, rightAngle, options);
        new WindowInteropHelper(window) { Owner = data.Application.MainWindowHandle };
        window.Rotate = angle =>
        {
            using var t = new Transaction(doc, "BotPlus: поворот");
            try
            {
                t.Start();
                rotate(window, angle);
                if (t.Commit() != TransactionStatus.Committed) return "Revit отменил поворот из-за ошибок модели.";
                ui.RefreshActiveView();
                return null;
            }
            catch (Exception error)
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                return error is InvalidOperationException ? error.Message : "Revit отклонил поворот: " + error.Message;
            }
        };
        bool accepted = window.ShowDialog() == true;
        if (accepted) group.Assimilate(); else group.RollBack();
        if (options) { settings.RotateConnected = window.RotateConnected; settings.RotateAxial = window.RotateAxial; }
        if (!rightAngle) { try { MepGeometry.ParseAngle(window.AngleText); settings.Remember(window.AngleText.Trim()); } catch (InvalidOperationException) { } }
        try { settings.Save(); } catch (System.IO.IOException) { }
        return accepted ? Result.Succeeded : Result.Cancelled;
    }
}

/// <summary>Turns an element so its picked connector runs along a reference MEP curve (either sense, user's choice).</summary>
[Transaction(TransactionMode.Manual)]
public sealed class MepOrientCommand : IExternalCommand
{
    const string Title = "Ориентировать по кривой";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        var pick = Mep.Pick(ui, "Выберите фитинг или элемент рядом с коннектором, который нужно направить");
        if (pick is null) return Result.Cancelled;
        Element curvePick;
        try { curvePick = doc.GetElement(ui.Selection.PickObject(ObjectType.Element, new ElementFilter(e => e is MEPCurve && e.Location is LocationCurve { Curve: Line }), "Выберите опорную прямую MEP-кривую")); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        var element = pick.Value.Element;
        var line = (Line)((LocationCurve)curvePick.Location).Curve;
        bool ok = Mep.Run(doc, Title, () =>
        {
            var moving = Mep.Nearest(Mep.Ends(element), pick.Value.Point, ui.ActiveView) ?? throw new InvalidOperationException("У элемента нет коннекторов.");
            if (Mep.Connected(moving)) throw new InvalidOperationException("Выбранный коннектор подключён: щёлкните рядом со свободным коннектором, который нужно направить.");
            var pivots = Mep.Ends(element).Where(c => c.Id != moving.Id && Mep.Connected(c)).ToList();
            if (pivots.Count > 1) throw new InvalidOperationException("Элемент подключён несколькими коннекторами: поворот разорвал бы сеть. Оставьте одно подключение.");
            var d = Mep.V(moving.CoordinateSystem.BasisZ); var u = Mep.V(line.Direction);
            XYZ origin; V3 axis;
            IReadOnlyList<(double Angle, bool Along)> solutions;
            if (pivots.Count == 1)
            {
                // Turn about the existing joint: the joint stays valid, only reachable directions are offered.
                origin = pivots[0].Origin; axis = Mep.V(pivots[0].CoordinateSystem.BasisZ);
                solutions = MepGeometry.OrientAbout(axis, d, u);
                if (solutions.Count == 0) throw new InvalidOperationException("Направление кривой недостижимо поворотом вокруг подключения элемента: " +
                    "угол между осью подключения и коннектором не совпадает с углом до кривой.");
            }
            else
            {
                origin = moving.Origin;
                var along = MepGeometry.Align(d, u); var against = MepGeometry.Align(d, -u);
                axis = along.Axis;
                solutions = [(along.Angle, true), (against.Angle, false)];
                if (Math.Abs(along.Axis.Cross(against.Axis).Length) > 1e-6)
                {
                    // Two different axes: apply each with its own axis.
                    var chosen = Choose(solutions);
                    if (chosen is null) throw new System.OperationCanceledException();
                    var r = chosen.Value.Along ? along : against;
                    Rotate(doc, element, pivots, moving, origin, Mep.X(r.Axis), r.Angle);
                    return;
                }
            }
            var pickAngle = solutions.Count == 1 ? solutions[0] : Choose(solutions) ?? throw new System.OperationCanceledException();
            Rotate(doc, element, pivots, moving, origin, Mep.X(axis), pickAngle.Angle);
        }, Title);
        return ok ? Result.Succeeded : Result.Cancelled;
    }

    static void Rotate(Document doc, Element element, List<Connector> pivots, Connector moving, XYZ origin, XYZ axis, double angle)
    {
        if (Math.Abs(angle) < MepGeometry.AngleTolerance) return;
        // Downstream part (connected through the picked connector) turns with the element.
        var ids = pivots.Count == 1 ? Mep.Moving(doc, element, pivots[0], true, true, origin, axis) : Mep.Moving(doc, element, moving, true, true, origin, axis);
        Mep.EnsureEditable(doc, ids);
        ElementTransformUtils.RotateElements(doc, ids, Line.CreateUnbound(origin, axis), angle);
    }

    static (double Angle, bool Along)? Choose(IReadOnlyList<(double Angle, bool Along)> options)
    {
        var dialog = new TaskDialog(Title) { MainInstruction = "Возможны два направления", CommonButtons = TaskDialogCommonButtons.Cancel };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "По направлению кривой", $"Поворот на {Deg(options[0].Angle)}°");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Против направления кривой", $"Поворот на {Deg(options[1].Angle)}°");
        return dialog.Show() switch
        {
            TaskDialogResult.CommandLink1 => options.First(o => o.Along),
            TaskDialogResult.CommandLink2 => options.First(o => !o.Along),
            _ => null
        };
    }

    static string Deg(double radians) => Math.Round(radians * 180 / Math.PI, 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>Turns cable trays about their own axis by 90° (on edge and back), with accept/cancel.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class TrayRotateCommand : IExternalCommand
{
    const string Title = "Лоток на ребро";
    static bool Tray(Element e) => e is CableTray && e.Location is LocationCurve { Curve: Line };

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        var trays = ui.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null && Tray(e)).ToList();
        if (trays.Count == 0)
        {
            try { trays = ui.Selection.PickObjects(ObjectType.Element, new ElementFilter(Tray), "Выберите прямые кабельные лотки и нажмите «Готово»").Select(doc.GetElement).ToList(); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        }
        var connected = trays.Where(t => Mep.Connectors(t).Any(Mep.Connected)).Select(t => t.Id).ToList();
        trays = trays.Where(t => !connected.Contains(t.Id)).ToList();
        if (connected.Count > 0)
            TaskDialog.Show(Title, $"Пропущено подключенных лотков: {connected.Count}. Поворот вокруг оси разорвал бы фитинги; отключите их и повторите.");
        if (trays.Count == 0) return Result.Cancelled;
        var ids = trays.Select(t => t.Id).ToList();
        return MepRotateCommand.Trial(data, Title, $"Лотков: {trays.Count}. Поворот на 90° вокруг собственной оси каждого лотка.", false, true, (_, angle) =>
        {
            Mep.EnsureEditable(doc, ids);
            foreach (var id in ids)
            {
                var line = (Line)((LocationCurve)doc.GetElement(id).Location).Curve;
                ElementTransformUtils.RotateElement(doc, id, Line.CreateUnbound(line.GetEndPoint(0), line.Direction), angle);
            }
        });
    }
}
