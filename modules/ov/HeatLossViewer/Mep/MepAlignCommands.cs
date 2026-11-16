using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using View = Autodesk.Revit.DB.View;

namespace HeatLossViewer.Mep;

/// <summary>Axis of a picked element: a straight segment's line, otherwise the connector nearest to the click.</summary>
internal static class AlignAxes
{
    internal static (XYZ Point, XYZ Direction) Of(Element element, XYZ pick, View view)
    {
        if (element is MEPCurve && element.Location is LocationCurve { Curve: Line line })
            return (line.Project(pick)?.XYZPoint ?? line.Origin, line.Direction);
        var c = Mep.Nearest(Mep.Ends(element), pick, view) ?? throw new InvalidOperationException($"У элемента {element.Id} нет коннекторов с направлением.");
        return (c.Origin, c.CoordinateSystem.BasisZ);
    }

    internal static bool Straight(Element e) => e is MEPCurve and not FlexDuct and not FlexPipe && e.Location is LocationCurve { Curve: Line };

    internal static void Move(Document doc, Element element, XYZ shift)
    {
        if (shift.GetLength() < doc.Application.ShortCurveTolerance / 100) return;
        Mep.EnsureEditable(doc, [element.Id]);
        ElementTransformUtils.MoveElement(doc, element.Id, shift);
    }
}

/// <summary>«Выровнять»: опорный элемент, затем выравниваемый; повтор до Esc.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class MepAlignAxisCommand : IExternalCommand
{
    const string Title = "Выровнять";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        while (true)
        {
            var reference = Mep.Pick(ui, "Опорный элемент (у фитинга — рядом с коннектором, задающим ось). Esc — выход");
            if (reference is null) break;
            var moving = Mep.Pick(ui, "Выравниваемый элемент. Esc — выход", e => e.Id != reference.Value.Element.Id);
            if (moving is null) break;
            Mep.Run(doc, Title, () =>
            {
                var (rp, rd) = AlignAxes.Of(reference.Value.Element, reference.Value.Point, ui.ActiveView);
                var (mp, md) = AlignAxes.Of(moving.Value.Element, moving.Value.Point, ui.ActiveView);
                AlignAxes.Move(doc, moving.Value.Element, Mep.X(MepAlignGeometry.AxisShift(Mep.V(mp), Mep.V(md), Mep.V(rp), Mep.V(rd))));
            }, Title);
        }
        return Result.Succeeded;
    }
}

/// <summary>«Выровнять по высоте»: перемещение по Z до пересечения осей; повтор до Esc.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class MepAlignHeightCommand : IExternalCommand
{
    const string Title = "Выровнять по высоте";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        while (true)
        {
            var reference = Mep.Pick(ui, "Опорный элемент. Esc — выход");
            if (reference is null) break;
            var moving = Mep.Pick(ui, "Элемент, который нужно поднять или опустить. Esc — выход", e => e.Id != reference.Value.Element.Id);
            if (moving is null) break;
            Mep.Run(doc, Title, () =>
            {
                var (rp, rd) = AlignAxes.Of(reference.Value.Element, reference.Value.Point, ui.ActiveView);
                var (mp, md) = AlignAxes.Of(moving.Value.Element, moving.Value.Point, ui.ActiveView);
                double dz = MepAlignGeometry.HeightShift(Mep.V(mp), Mep.V(md), Mep.V(rp), Mep.V(rd));
                AlignAxes.Move(doc, moving.Value.Element, new XYZ(0, 0, dz));
            }, Title);
        }
        return Result.Succeeded;
    }
}

/// <summary>«Выровнять вертикально»: нижний конец остаётся, верхний ставится строго над ним.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class MepAlignVerticalCommand : IExternalCommand
{
    const string Title = "Выровнять вертикально";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        var curves = ui.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null && AlignAxes.Straight(e)).ToList();
        if (curves.Count == 0)
        {
            try { curves = ui.Selection.PickObjects(ObjectType.Element, new ElementFilter(AlignAxes.Straight), "Выберите наклонные стояки и нажмите «Готово»").Select(doc.GetElement).ToList(); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        }
        int changed = 0;
        bool ok = Mep.Run(doc, Title, () =>
        {
            Mep.EnsureEditable(doc, curves.Select(c => c.Id));
            foreach (var curve in curves)
            {
                var location = (LocationCurve)curve.Location;
                var line = (Line)location.Curve;
                var top = MepAlignGeometry.Plumb(Mep.V(line.GetEndPoint(0)), Mep.V(line.GetEndPoint(1)), out bool upperIsEnd);
                var target = Mep.X(top);
                var current = line.GetEndPoint(upperIsEnd ? 1 : 0);
                if (current.DistanceTo(target) < doc.Application.ShortCurveTolerance / 100) continue;
                location.Curve = upperIsEnd ? Line.CreateBound(line.GetEndPoint(0), target) : Line.CreateBound(target, line.GetEndPoint(1));
                changed++;
            }
        }, Title);
        if (ok) TaskDialog.Show(Title, changed == 0 ? "Элементы уже вертикальны." : $"Выровнено элементов: {changed}.");
        return ok ? Result.Succeeded : Result.Failed;
    }
}

/// <summary>«Задать расстояние»: между осями или гранями по горизонтали или вертикали; базовый элемент, затем выравниваемые до Esc.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class MepAlignDistanceCommand : IExternalCommand
{
    const string Title = "Задать расстояние";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        var settings = MepToolSettings.Load();
        var window = new AlignDistanceWindow(settings);
        new WindowInteropHelper(window) { Owner = data.Application.MainWindowHandle };
        if (window.ShowDialog() != true) return Result.Cancelled;
        settings.RememberDistance(window.DistanceText);
        try { settings.Save(); } catch (System.IO.IOException) { }
        double target = UnitUtils.ConvertToInternalUnits(window.DistanceMm, UnitTypeId.Millimeters);

        var preselected = ui.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null && AlignAxes.Straight(e)).ToList();
        var baseElement = Mep.Pick(ui, "Базовый элемент. Esc — выход", e => AlignAxes.Straight(e) && preselected.All(p => p.Id != e.Id));
        if (baseElement is null) return Result.Cancelled;
        void Apply(Element moving)
        {
            Mep.Run(doc, Title, () =>
            {
                var bl = (Line)((LocationCurve)baseElement.Value.Element.Location).Curve;
                var ml = (Line)((LocationCurve)moving.Location).Curve;
                var dir = MepAlignGeometry.DistanceDirection(Mep.V(ml.Origin), Mep.V(ml.Direction), Mep.V(bl.Origin), Mep.V(bl.Direction), window.Horizontal);
                double gap = window.Faces ? Half((MEPCurve)moving, Mep.X(dir), window.Insulation) + Half((MEPCurve)baseElement.Value.Element, Mep.X(dir), window.Insulation) : 0;
                var shift = MepAlignGeometry.DistanceShift(Mep.V(ml.Origin), Mep.V(ml.Direction), Mep.V(bl.Origin), Mep.V(bl.Direction), target, window.Horizontal, gap);
                AlignAxes.Move(doc, moving, Mep.X(shift));
            }, Title);
        }
        if (preselected.Count > 0) { foreach (var e in preselected) Apply(e); return Result.Succeeded; }
        while (true)
        {
            var moving = Mep.Pick(ui, "Элемент, который нужно поставить на заданное расстояние. Esc — выход",
                e => AlignAxes.Straight(e) && e.Id != baseElement.Value.Element.Id);
            if (moving is null) break;
            Apply(moving.Value.Element);
        }
        return Result.Succeeded;
    }

    /// <summary>Half the section across <paramref name="direction"/> (outer size of pipes), plus insulation when asked.</summary>
    static double Half(MEPCurve curve, XYZ direction, bool insulation)
    {
        var c = Mep.Ends(curve).First();
        double half;
        if (c.Shape == ConnectorProfileType.Round)
        {
            double outer = curve.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER) is { HasValue: true } p ? p.AsDouble() : 0;
            half = outer > 0 ? outer / 2 : c.Radius;
        }
        else
        {
            var cs = c.CoordinateSystem;
            half = 0.5 * (Math.Abs(cs.BasisX.DotProduct(direction)) * c.Width + Math.Abs(cs.BasisY.DotProduct(direction)) * c.Height);
        }
        if (insulation && curve.get_Parameter(BuiltInParameter.RBS_REFERENCE_INSULATION_THICKNESS) is { HasValue: true, StorageType: StorageType.Double } ins)
            half += ins.AsDouble();
        return half;
    }
}

/// <summary>
/// «Мультипостроение»: параллельные трассы продолжаются от свободных концов к указанным точкам с поворотом 10–90° (произвольным или
/// ближайшим фиксированным), с отводами и сохранением уклона; точка прямо по ходу только удлиняет или укорачивает трассы. Повтор до Esc.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class MepMultiRouteCommand : IExternalCommand
{
    const string Title = "Мультипостроение";
    static bool Routable(Element e) => AlignAxes.Straight(e) && !MepAlignGeometry.Vertical(Mep.V(((Line)((LocationCurve)e.Location).Curve).Direction));

    sealed class Route(MEPCurve curve, int freeEnd)
    {
        public MEPCurve Curve = curve;
        public int FreeEnd = freeEnd;
        public Line Line => (Line)((LocationCurve)Curve.Location).Curve;
        public XYZ End => Line.GetEndPoint(FreeEnd);
        public XYZ Direction => (End - Line.GetEndPoint(1 - FreeEnd)).Normalize();
    }

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        var doc = ui.Document;
        if (ui.ActiveView is not ViewPlan) { TaskDialog.Show(Title, "Откройте план: точки поворота указываются на плане."); return Result.Cancelled; }
        var settings = MepToolSettings.Load();
        var window = new MultiRouteWindow(settings);
        new WindowInteropHelper(window) { Owner = data.Application.MainWindowHandle };
        if (window.ShowDialog() != true) return Result.Cancelled;
        try { settings.Save(); } catch (System.IO.IOException) { }
        var angles = settings.MultiFixedAngles ? MepAlignGeometry.ParseAngles(settings.MultiAngles) : null;

        var curves = ui.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null && Routable(e)).Cast<MEPCurve>().ToList();
        if (curves.Count == 0)
        {
            try { curves = ui.Selection.PickObjects(ObjectType.Element, new ElementFilter(Routable), "Выберите параллельные трассы и нажмите «Готово»").Select(doc.GetElement).Cast<MEPCurve>().ToList(); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        }
        Reference basePick;
        try { basePick = ui.Selection.PickObject(ObjectType.Element, new ElementFilter(e => curves.Any(c => c.Id == e.Id)), "Базовая трасса: щёлкните ближе к концу, от которого продолжать"); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        var baseCurve = curves.First(c => c.Id == basePick.ElementId);
        var baseLine = (Line)((LocationCurve)baseCurve.Location).Curve;
        var click = basePick.GlobalPoint ?? baseLine.Evaluate(1, true);
        int baseEnd = baseLine.GetEndPoint(1).DistanceTo(click) < baseLine.GetEndPoint(0).DistanceTo(click) ? 1 : 0;
        var u1 = (baseLine.GetEndPoint(baseEnd) - baseLine.GetEndPoint(1 - baseEnd));
        var routes = curves.Select(c =>
        {
            var l = (Line)((LocationCurve)c.Location).Curve;
            return new Route(c, (l.GetEndPoint(1) - l.GetEndPoint(0)).DotProduct(u1) >= 0 ? 1 : 0);
        }).ToList();
        int baseIndex = routes.FindIndex(r => r.Curve.Id == baseCurve.Id);
        var busy = routes.Where(r => Mep.Connected(Mep.NearestEnd(r.Curve, r.End))).Select(r => r.Curve.Id).ToList();
        if (busy.Count > 0) { TaskDialog.Show(Title, "Концы трасс, от которых нужно продолжать, уже подключены: " + string.Join(", ", busy)); return Result.Cancelled; }

        double minLength = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);
        using var group = new TransactionGroup(doc, "BotPlus: " + Title);
        group.Start();
        int steps = 0;
        while (true)
        {
            XYZ point;
            try { point = ui.Selection.PickPoint("Точка поворота или конца трасс. Esc — завершить"); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { TaskDialog.Show(Title, "Не задана рабочая плоскость вида."); break; }
            var next = routes.ToList();
            bool ok = Mep.Run(doc, Title, () =>
            {
                Mep.EnsureEditable(doc, routes.Select(r => r.Curve.Id));
                var step = MepAlignGeometry.MultiRoute(routes.Select(r => new RouteEnd(Mep.V(r.End), Mep.V(r.Direction))).ToList(), baseIndex, Mep.V(point), angles, minLength);
                for (int i = 0; i < routes.Count; i++)
                {
                    var route = routes[i]; var leg = step.Legs[i];
                    var line = route.Line;
                    var fixedPoint = line.GetEndPoint(1 - route.FreeEnd);
                    var corner = Mep.X(leg.Corner);
                    if (fixedPoint.DistanceTo(corner) < minLength) throw new InvalidOperationException($"Трасса {route.Curve.Id} укорачивается до нуля — укажите точку дальше.");
                    ((LocationCurve)route.Curve.Location).Curve = route.FreeEnd == 1 ? Line.CreateBound(fixedPoint, corner) : Line.CreateBound(corner, fixedPoint);
                    if (step.Straight) continue;
                    var copy = (MEPCurve)doc.GetElement(ElementTransformUtils.CopyElement(doc, route.Curve.Id, XYZ.Zero).First());
                    ((LocationCurve)copy.Location).Curve = Line.CreateBound(corner, Mep.X(leg.End));
                    doc.Regenerate();
                    doc.Create.NewElbowFitting(Mep.NearestEnd(route.Curve, corner), Mep.NearestEnd(copy, corner));
                    next[i] = new Route(copy, 1);
                }
            }, Title);
            // The route list advances only when the step committed; a failed step leaves it as it was.
            if (ok) { routes = next; steps++; }
        }
        if (settings.MultiSingleUndo) group.Assimilate(); else group.Commit();
        return steps > 0 ? Result.Succeeded : Result.Cancelled;
    }
}
