using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitCanceled = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace BotPlus.Eom;

public sealed partial class ConduitThroughLightsCommand
{
    static bool DrawFreeFromPoint(UIDocument ui, RouteTools tools, XYZ point, FamilySymbol? elbowType, Conduit? exemplar)
    {
        if (ui.ActiveView is not ViewPlan { GenLevel: { } level }) return false;
        double offset = exemplar?.Location is LocationCurve location
            ? location.Curve.Evaluate(0.5, true).Z - level.ProjectElevation : Feet(2500);
        if (FreeSettings(ui, level, offset, exemplar?.Diameter ?? Feet(20), true) is not { } settings) return false;
        var type = ChooseType(ui.Document, exemplar);
        var start = new Port(null, AtElevation(point, settings.Height), XYZ.Zero, settings.Diameter);
        return DrawFree(ui, tools, null, start, type, level, [], elbowType, []);
    }

    static bool? DrawFreeFromElement(UIDocument ui, RouteTools tools, Element source, XYZ? hit, int? portId,
        double? diameter, ConduitType type, FamilySymbol[] boxTypes, FamilySymbol? elbowType, XYZ first)
    {
        if (ui.ActiveView is not ViewPlan { GenLevel: { } viewLevel }) return false;
        try
        {
            double height = RoutingPoint(source, hit, portId).Z;
            if (IsRiser(source) && portId == null)
            {
                if (FreeSettings(ui, viewLevel, height - viewLevel.ProjectElevation, ((Conduit)source).Diameter, false) is not { } settings)
                    return null;
                height = settings.Height;
            }
            var (start, firstPoint) = FirstFree(source, hit, portId, diameter, first, height);
            var level = ui.Document.GetElement(source.LevelId) as Level ?? viewLevel;
            return DrawFree(ui, tools, source, start, type, level, boxTypes, elbowType, [firstPoint]);
        }
        catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
        {
            System.Diagnostics.Trace.WriteLine(error.Message);
            return null;
        }
    }

    static (Port Start, XYZ Point) FirstFree(Element source, XYZ? hit, int? portId, double? diameter, XYZ cursor, double height)
    {
        var firstPoint = AtElevation(cursor, height);
        var compatible = CandidatePorts(source, hit, portId, firstPoint, true, height)
            .Where(port => Math.Abs(port.Point.Z - height) <= PointTolerance && Math.Abs(port.Axis.Z) <= PointTolerance).ToArray();
        if (compatible.Length == 0 && IsLight(source) && Ports(source).Length == 0)
        {
            var anchor = AtElevation(RoutingPoint(source, hit, portId), height);
            compatible = [new(null, anchor, Toward(anchor, firstPoint), diameter ?? Feet(20), NeedsBox: true)];
        }
        if (compatible.Length == 0)
            throw new InvalidOperationException("На этой отметке нет горизонтального вывода.");
        var start = compatible.OrderByDescending(port => SameSize(port.Diameter, diameter))
            .ThenByDescending(port => port.Axis.DotProduct((firstPoint - port.Point).Normalize())).First();
        var (point, _) = SnapFreePoint(start, [], firstPoint, [], FreeMinimum(source.Document));
        return (start, point);
    }

    static FreeSketch PreviewFirstFree(Element source, XYZ? hit, int? portId, double? diameter, XYZ cursor)
    {
        try
        {
            var (start, point) = FirstFree(source, hit, portId, diameter, cursor, RoutingPoint(source, hit, portId).Z);
            return new([start.Point, point]);
        }
        catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
        { return new([], error.Message, true); }
    }

    static bool DrawFree(UIDocument ui, RouteTools tools, Element? source, Port start, ConduitType type, Level level,
        FamilySymbol[] boxTypes, FamilySymbol? elbowType, List<XYZ> vertices)
    {
        var doc = ui.Document;
        var minimum = FreeMinimum(doc);
        var diameter = start.Diameter!.Value;
        var bends = CheckFreeBends(doc, type, level, elbowType, start);
        var angles = bends.Select(bend => bend.Angle).ToArray();
        var allowed = angles.Length > 0 ? string.Join(", ", angles.Select(angle => $"{angle:0.##}°")) : "без поворотов";
        bool prepared = false;
        tools.UseConduit(type.Id, diameter);
        void Prepare()
        {
            if (prepared) return;
            RouteCommand.PrepareWiring(ui);
            prepared = true;
        }

        Port[] Ends(Element element, XYZ hit, int? marker)
        {
            var from = vertices.Count == 0 ? start.Point : vertices[^1];
            var candidates = CandidatePorts(element, hit, marker, from, true, start.Point.Z);
            if (IsRiser(element) && vertices.Count > 0 && marker == null)
            {
                var heading = FreeDirection(start, vertices);
                var approaches = angles.SelectMany(angle => new[] { angle, -angle }).Prepend(0)
                    .SelectMany(angle => CandidatePorts(element, hit, null, from, true, start.Point.Z, Turn(heading, angle)));
                candidates = candidates.Concat(approaches).DistinctBy(port => Math.Round(TurnBetween(XYZ.BasisX, port.Axis), 4)).ToArray();
            }
            if (candidates.Length == 0) throw new InvalidOperationException(PortMessage(element, diameter, candidates));
            var flat = candidates.Where(port => Math.Abs(port.Axis.Z) <= PointTolerance && Math.Abs(port.Point.Z - start.Point.Z) <= PointTolerance).ToArray();
            if (flat.Length == 0)
                throw new InvalidOperationException($"На отметке свободной трассы {Mm(start.Point.Z):0.##} мм нет совместимого горизонтального вывода.");
            return Nearest(flat, hit, true).OrderByDescending(port => SameSize(port.Diameter, diameter)).ToArray();
        }

        (XYZ Point, double Angle, XYZ[] Path) Next(XYZ cursor)
        {
            var (point, angle) = SnapFreePoint(start, vertices, cursor, angles, minimum);
            return (point, angle, CheckedFreePoints([start.Point, .. vertices, point], bends, minimum));
        }

        FreeSketch Sketch(XYZ cursor)
        {
            try
            {
                var (_, angle, path) = Next(cursor);
                return new(path, $"{angle:0.##}°");
            }
            catch (InvalidOperationException error) { return new([start.Point, .. vertices], error.Message, true); }
        }

        IEnumerable<(Port End, XYZ[] Points)> Approaches(Element target, XYZ hit, int? marker)
        {
            foreach (var end in Ends(target, hit, marker))
            {
                XYZ[] points;
                try { points = ConnectFreePoints(start, vertices, end, bends, minimum); }
                catch (InvalidOperationException) { continue; }
                yield return (end, points);
            }
        }

        while (true)
        {
            Pick pick;
            try
            {
                pick = tools.Pick(() => PickBody(ui, e => e.Id != source?.Id && !IsSwitch(e) && IsRouteTarget(e), diameter,
                    $"Свободная трасса на отметке {Mm(start.Point.Z):0.##} мм. Углы {allowed}. Shift+щелчок убирает точку, Esc строит трассу.",
                    (element, hit, marker) =>
                    {
                        try
                        {
                            var (end, points) = Approaches(element, hit, marker).FirstOrDefault();
                            if (end != null) return new(points, end.Point);
                            return new([start.Point, .. vertices], hit, "Добавьте точку для подхода к выводу.", true);
                        }
                        catch (InvalidOperationException error) { return new([start.Point, .. vertices], hit, error.Message, true); }
                    },
                    Sketch,
                    allowFree: true, riserElevation: start.Point.Z, excluded: e => e.Id == source?.Id || IsSwitch(e)));
            }
            catch (RevitCanceled)
            {
                if (vertices.Count == 0) return false;
                try
                {
                    var points = CheckedFreePoints([start.Point, .. vertices], bends, minimum);
                    Prepare();
                    CommitFree(doc, type, level, boxTypes, elbowType, source, start, null, null, points);
                    ui.RefreshActiveView();
                    return true;
                }
                catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
                {
                    TaskDialog.Show(Title, error.Message);
                    return false;
                }
            }

            if (pick.Element is not { } target)
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    if (vertices.Count > 0) vertices.RemoveAt(vertices.Count - 1);
                    continue;
                }
                try
                {
                    var (point, _, _) = Next(pick.Point);
                    vertices.Add(point);
                }
                catch (InvalidOperationException) { } // Та же причина уже показана у курсора.
                continue;
            }

            (Port End, XYZ[] Points) approach;
            try { approach = Approaches(target, pick.Point, pick.PortId).FirstOrDefault(); }
            catch (InvalidOperationException) { continue; }
            if (approach.End == null || !ConfirmDiameter(diameter, approach.End.Diameter)) continue;
            try
            {
                Prepare();
                CommitFree(doc, type, level, boxTypes, elbowType, source, start, target, approach.End, approach.Points);
                ui.RefreshActiveView();
                return true;
            }
            catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            { TaskDialog.Show(Title, error.Message); }
        }
    }

    static void CommitFree(Document doc, ConduitType type, Level level, FamilySymbol[] boxTypes, FamilySymbol? elbowType,
        Element? source, Port start, Element? target, Port? end, XYZ[] points)
    {
        var diameter = start.Diameter!.Value;
        using var transaction = new Transaction(doc, "ЭОМ: свободная трасса");
        var failures = StartRouteTransaction(transaction);
        if (start.NeedsBox)
            source = PutCompatibleBox(doc, boxTypes, level, start.Point, start.Axis, diameter);
        Connector? from = source == null ? null : start.Tap ? InsertTap(doc, (Conduit)source, start) : ResolvePort(source, start, diameter);
        Connector? to = target == null || end == null ? null : end.Tap ? InsertTap(doc, (Conduit)target, end) : ResolvePort(target, end, end.Diameter);
        BuildPath(doc, type, level, elbowType, from, to, points, diameter, free: true,
            startElbow: start.TerminalElbow, endElbow: end?.TerminalElbow == true);
        FinishRouteTransaction(transaction, failures, "Revit отменил построение свободной трассы.");
    }

    static XYZ AtElevation(XYZ point, double height) => new(point.X, point.Y, height);

    static (double Height, double Diameter)? FreeSettings(UIDocument ui, Level level, double offset, double diameter, bool editSize)
    {
        var window = EomUi.Window("Свободная прокладка", 460, 260);
        window.SizeToContent = SizeToContent.Height;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        new WindowInteropHelper(window).Owner = ui.Application.MainWindowHandle;
        var height = EomUi.Number(Mm(offset));
        var size = EomUi.Number(Mm(diameter));
        var fields = new StackPanel();
        fields.Children.Add(EomUi.Row($"Смещение от уровня «{level.Name}»", height, "мм"));
        if (editSize) fields.Children.Add(EomUi.Row("Диаметр трубы", size, "мм"));
        (double Height, double Diameter)? settings = null;
        var proceed = EomUi.Button("Начать трассу", () =>
        {
            double offsetMm = EomUi.Parse(height, "Смещение");
            double diameterMm = editSize ? EomUi.Parse(size, "Диаметр") : Mm(diameter);
            if (!double.IsFinite(offsetMm) || !double.IsFinite(diameterMm) || diameterMm <= 0)
                throw new InvalidOperationException("Введите допустимое смещение и положительный диаметр.");
            settings = (level.ProjectElevation + Feet(offsetMm), Feet(diameterMm));
            window.DialogResult = true;
        }, true);
        EomUi.Layout(window, fields, bottom: EomUi.Buttons(new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 }, proceed));
        return window.ShowDialog() == true ? settings : null;
    }
}
