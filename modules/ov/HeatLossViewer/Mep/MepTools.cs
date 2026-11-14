using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BotPlus.Shared;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using GroupBox = System.Windows.Controls.GroupBox;
using View = Autodesk.Revit.DB.View;

namespace HeatLossViewer.Mep;

internal sealed class ElementFilter(Func<Element, bool> allow) : ISelectionFilter
{
    public bool AllowElement(Element elem) => allow(elem);
    public bool AllowReference(Reference reference, XYZ position) => true;
}

/// <summary>Connector graph access shared by the connection and rotation commands.</summary>
internal static class Mep
{
    internal static V3 V(XYZ p) => new(p.X, p.Y, p.Z);
    internal static XYZ X(V3 p) => new(p.X, p.Y, p.Z);

    internal static ConnectorManager? Manager(Element e) => e switch
    {
        MEPCurve c => c.ConnectorManager,
        FamilyInstance f => f.MEPModel?.ConnectorManager,
        FabricationPart p => p.ConnectorManager,
        _ => null
    };

    static bool Physical(Connector c) => (c.ConnectorType & ConnectorType.Physical) != 0;

    internal static List<Connector> Connectors(Element e) =>
        Manager(e)?.Connectors.Cast<Connector>().Where(Physical).ToList() ?? [];

    /// <summary>End connectors only: they have a coordinate system (direction and section axes).</summary>
    internal static List<Connector> Ends(Element e) => Connectors(e).Where(c => c.ConnectorType == ConnectorType.End).ToList();

    internal static List<Connector> Neighbors(Connector c) =>
        c.AllRefs.Cast<Connector>().Where(r => r.Owner.Id != c.Owner.Id && Physical(r)).ToList();

    internal static bool Connected(Connector c) => Neighbors(c).Count > 0;

    internal static bool Same(Connector a, Connector b) => a.Owner.Id == b.Owner.Id && a.Id == b.Id;

    internal static bool HasConnectors(Element e) => Connectors(e).Count > 0;

    internal static Connector? Nearest(IEnumerable<Connector> connectors, XYZ pick, View? view)
    {
        var list = connectors.ToList();
        V3? dir = view is View3D or ViewPlan or ViewSection ? V(view.ViewDirection) : null;
        int i = MepGeometry.Nearest(list.Select(c => V(c.Origin)).ToList(), V(pick), dir);
        return i < 0 ? null : list[i];
    }

    internal static Connector Find(Element e, int connectorId) =>
        Connectors(e).FirstOrDefault(c => c.Id == connectorId) ?? throw new InvalidOperationException("Коннектор больше не существует.");

    internal static Connector NearestEnd(Element e, XYZ point) =>
        Ends(e).OrderBy(c => c.Origin.DistanceTo(point)).FirstOrDefault() ?? throw new InvalidOperationException($"У элемента {e.Id} нет коннекторов.");

    internal static (Element Element, XYZ Point)? Pick(UIDocument ui, string prompt, Func<Element, bool>? allow = null)
    {
        try
        {
            var r = ui.Selection.PickObject(ObjectType.Element, new ElementFilter(e => HasConnectors(e) && (allow?.Invoke(e) ?? true)), prompt);
            var e = ui.Document.GetElement(r);
            return (e, r.GlobalPoint ?? Center(e));
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
    }

    internal static XYZ Center(Element e)
    {
        if (e.Location is LocationPoint p) return p.Point;
        if (e.Location is LocationCurve c) return c.Curve.Evaluate(0.5, true);
        var box = e.get_BoundingBox(null) ?? throw new InvalidOperationException($"У элемента {e.Id} нет геометрии.");
        return (box.Min + box.Max) / 2;
    }

    /// <summary>Reason the element must not be moved, or null.</summary>
    internal static string? Locked(Element e)
    {
        if (e.Pinned) return "закреплён";
        if (e.GroupId != ElementId.InvalidElementId) return "входит в группу";
        var doc = e.Document;
        if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, e.Id) == CheckoutStatus.OwnedByOtherUser) return "занят другим пользователем";
        return null;
    }

    internal static void EnsureEditable(Document doc, IEnumerable<ElementId> ids)
    {
        var problems = ids.Select(doc.GetElement).Where(e => e != null).Select(e => (e!.Id, Reason: Locked(e))).Where(x => x.Reason != null).Take(5).ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException("Нельзя изменить элементы:\n" + string.Join("\n", problems.Select(p => $"• {p.Id}: {p.Reason}")));
    }

    internal static bool SameSize(Connector a, Connector b)
    {
        double tol = a.Owner.Document.Application.ShortCurveTolerance / 10;
        if (a.Shape != b.Shape) return false;
        return a.Shape == ConnectorProfileType.Round
            ? Math.Abs(a.Radius - b.Radius) < tol
            : Math.Abs(a.Width - b.Width) < tol && Math.Abs(a.Height - b.Height) < tol;
    }

    /// <summary>Gives a straight segment the section of <paramref name="target"/> through native size parameters.</summary>
    internal static void Resize(MEPCurve curve, Connector target)
    {
        void Set(BuiltInParameter id, double value)
        {
            var p = curve.get_Parameter(id);
            if (p is null || p.IsReadOnly) throw new InvalidOperationException($"Нельзя изменить размер элемента {curve.Id}.");
            p.Set(value);
        }
        if (target.Shape == ConnectorProfileType.Round)
            Set(curve switch
            {
                Autodesk.Revit.DB.Plumbing.Pipe => BuiltInParameter.RBS_PIPE_DIAMETER_PARAM,
                Autodesk.Revit.DB.Electrical.Conduit => BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM,
                _ => BuiltInParameter.RBS_CURVE_DIAMETER_PARAM
            }, target.Radius * 2);
        else if (curve is Autodesk.Revit.DB.Electrical.CableTray)
        { Set(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, target.Width); Set(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, target.Height); }
        else { Set(BuiltInParameter.RBS_CURVE_WIDTH_PARAM, target.Width); Set(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, target.Height); }
    }

    /// <summary>Joins two coincident connectors natively: union/transition between segments, direct connection otherwise.</summary>
    internal static void Join(Document doc, Connector moving, Connector reference)
    {
        if (moving.Domain != reference.Domain) throw new InvalidOperationException("Коннекторы относятся к разным инженерным системам (домены различаются).");
        bool curves = moving.Owner is MEPCurve && reference.Owner is MEPCurve;
        if (!SameSize(moving, reference))
        {
            if (curves) { doc.Create.NewTransitionFitting(moving, reference); return; }
            if (moving.Owner is MEPCurve curve && moving.Shape == reference.Shape)
            {
                Resize(curve, reference);
                doc.Regenerate();
                moving = Find(curve, moving.Id);
            }
            else throw new InvalidOperationException("Размеры или формы коннекторов различаются, а переход между семействами не предусмотрен. " +
                "Выберите участок воздуховода/трубы присоединяемым элементом.");
        }
        if (curves)
        {
            try { doc.Create.NewUnionFitting(moving, reference); return; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { } // No union in routing preferences: join directly.
        }
        moving.ConnectTo(reference);
    }

    /// <summary>Elements that move with <paramref name="start"/>: reach through connectors, never across the reference.</summary>
    internal static HashSet<ElementId> Moving(Document doc, Element start, Connector reference, bool connected, bool includeAxial, XYZ axisOrigin, XYZ axisDirection)
    {
        if (!connected) return [start.Id];
        double tol = doc.Application.ShortCurveTolerance;
        bool own = reference.Owner.Id == start.Id;
        bool Axial(Connector c) => c.Origin.DistanceTo(axisOrigin + axisDirection * (c.Origin - axisOrigin).DotProduct(axisDirection)) < tol
            && c.ConnectorType == ConnectorType.End && Math.Abs(c.CoordinateSystem.BasisZ.DotProduct(axisDirection)) > 0.9999;
        IEnumerable<(ElementId, bool, bool)> Edges(ElementId id)
        {
            var e = doc.GetElement(id);
            foreach (var c in Connectors(e))
                foreach (var n in Neighbors(c))
                {
                    bool blocked = Same(c, reference) || Same(n, reference) || (!own && n.Owner.Id == reference.Owner.Id);
                    yield return (n.Owner.Id, Axial(c), blocked);
                }
        }
        return MepGeometry.Reachable(start.Id, Edges, includeAxial);
    }

    internal static ConnectorProfileType DuctShape(ElementType type) => type switch
    {
        DuctType d => d.Shape,
        FlexDuctType f => f.Shape,
        _ => ConnectorProfileType.Invalid
    };

    internal static void ShowError(string title, Exception error) =>
        Autodesk.Revit.UI.TaskDialog.Show(title, error is InvalidOperationException ? error.Message : "Revit отклонил операцию: " + error.Message);

    /// <summary>One click = one transaction; failure rolls back that click only and keeps the loop running.</summary>
    internal static bool Run(Document doc, string name, Action action, string title)
    {
        using var t = new Transaction(doc, "BotPlus: " + name);
        try
        {
            t.Start();
            action();
            if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit отменил операцию из-за ошибок модели.");
            return true;
        }
        catch (Exception error)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            // Esc or a cancelled choice: nothing to report.
            if (error is not (Autodesk.Revit.Exceptions.OperationCanceledException or System.OperationCanceledException)) ShowError(title, error);
            return false;
        }
    }
}

/// <summary>
/// Trial rotations inside a TransactionGroup: every click commits a rotation so the user sees it;
/// «Принять» assimilates the group, «Отмена» or closing rolls all of them back.
/// </summary>
internal sealed class TrialRotationWindow : Window
{
    readonly ComboBox angle = new() { IsEditable = true, MinWidth = 90 };
    readonly TextBlock status = new() { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly CheckBox connected = new() { Content = "Вращать подключенные элементы", Margin = new Thickness(0, 8, 0, 0) };
    readonly CheckBox axial = new() { Content = "Включая продольно подключенные", Margin = new Thickness(18, 4, 0, 0) };
    int count;
    internal Func<double, string?>? Rotate;
    internal bool RotateConnected => connected.IsChecked == true;
    internal bool RotateAxial => axial.IsChecked == true;
    internal string AngleText => angle.Text;

    internal TrialRotationWindow(string title, string hint, MepToolSettings settings, bool fixedRightAngle, bool options)
    {
        Title = title; Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Topmost = true;
        var root = new StackPanel { Margin = new Thickness(14) }; Content = root;
        root.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        if (!fixedRightAngle)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            line.Children.Add(new TextBlock { Text = "Угол, °", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            foreach (var h in settings.AngleHistory) angle.Items.Add(h);
            angle.Text = settings.Angle;
            line.Children.Add(angle);
            foreach (var a in new[] { "15", "30", "45", "60", "90", "180" })
            {
                var b = new Button { Content = a, MinWidth = 34, Margin = new Thickness(4, 0, 0, 0) };
                b.Click += (_, _) => angle.Text = a;
                line.Children.Add(b);
            }
            root.Children.Add(line);
        }
        else angle.Text = "90";
        if (options)
        {
            connected.IsChecked = settings.RotateConnected; axial.IsChecked = settings.RotateAxial;
            axial.IsEnabled = RotateConnected;
            connected.Checked += (_, _) => axial.IsEnabled = true; connected.Unchecked += (_, _) => axial.IsEnabled = false;
            root.Children.Add(connected); root.Children.Add(axial);
        }
        var turn = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var ccw = new Button { Content = "↺ Против часовой", MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
        var cw = new Button { Content = "↻ По часовой", MinWidth = 140 };
        ccw.Click += (_, _) => Turn(+1); cw.Click += (_, _) => Turn(-1);
        turn.Children.Add(ccw); turn.Children.Add(cw); root.Children.Add(turn);
        root.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Принять", IsDefault = true, MinWidth = 85, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok); buttons.Children.Add(cancel); root.Children.Add(buttons);
        BotPlusTheme.Apply(this);
    }

    void Turn(int sign)
    {
        double radians;
        try { radians = MepGeometry.ParseAngle(angle.Text); }
        catch (InvalidOperationException e) { status.Text = e.Message; return; }
        string? error = Rotate?.Invoke(sign * radians);
        if (error == null) count++;
        status.Text = error ?? $"Выполнено поворотов: {count}. «Принять» — сохранить, «Отмена» — вернуть исходное положение.";
    }
}

/// <summary>Diffuser connection options: tap kind and an optional flexible part per connector shape.</summary>
internal sealed class DiffuserWindow : Window
{
    readonly RadioButton takeoff = new() { Content = "Врезка", Margin = new Thickness(0, 0, 16, 0) }, tee = new() { Content = "Тройник" };
    readonly List<(ConnectorProfileType Shape, CheckBox Use, ComboBox Type, System.Windows.Controls.TextBox Length)> rows = [];
    readonly MepToolSettings settings;

    internal DiffuserWindow(Document doc, MepToolSettings settings)
    {
        this.settings = settings;
        Title = "Подключение диффузоров"; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(14) }; Content = root;
        var kind = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        kind.Children.Add(new TextBlock { Text = "Ответвление:", Margin = new Thickness(0, 0, 10, 0) });
        kind.Children.Add(takeoff); kind.Children.Add(tee);
        (settings.Tee ? tee : takeoff).IsChecked = true;
        root.Children.Add(kind);
        var types = new FilteredElementCollector(doc).OfClass(typeof(FlexDuctType)).Cast<FlexDuctType>().ToList();
        foreach (var (shape, label, options) in new[]
        {
            (ConnectorProfileType.Round, "Круглый коннектор", settings.Round),
            (ConnectorProfileType.Rectangular, "Прямоугольный коннектор", settings.Rectangular),
            (ConnectorProfileType.Oval, "Овальный коннектор", settings.Oval)
        })
        {
            var names = types.Where(t => t.Shape == shape).Select(t => t.Name).OrderBy(n => n).ToList();
            var use = new CheckBox { Content = "Гибкий участок", IsChecked = options.Use && names.Count > 0, IsEnabled = names.Count > 0, VerticalAlignment = VerticalAlignment.Center };
            var type = new ComboBox { ItemsSource = names, MinWidth = 180, Margin = new Thickness(10, 0, 10, 0), SelectedItem = names.Contains(options.Type) ? options.Type : names.FirstOrDefault() };
            var length = new System.Windows.Controls.TextBox { Text = options.LengthMm.ToString(System.Globalization.CultureInfo.CurrentCulture), MinWidth = 60 };
            var box = new StackPanel { Orientation = Orientation.Horizontal };
            box.Children.Add(use); box.Children.Add(type);
            box.Children.Add(new TextBlock { Text = "Длина в проекции, мм", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            box.Children.Add(length);
            root.Children.Add(new GroupBox { Header = label + (names.Count == 0 ? " — в проекте нет типов гибких воздуховодов этой формы" : ""), Content = box, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 6) });
            rows.Add((shape, use, type, length));
        }
        root.Children.Add(new TextBlock { Text = "Далее выбирайте диффузор, затем воздуховод. Esc — завершить. Поддерживается один свободный вертикальный коннектор диффузора и горизонтальный воздуховод.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 85, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok); buttons.Children.Add(cancel); root.Children.Add(buttons);
        BotPlusTheme.Apply(this);
    }

    void Accept()
    {
        settings.Tee = tee.IsChecked == true;
        foreach (var (shape, use, type, length) in rows)
        {
            var options = shape switch { ConnectorProfileType.Round => settings.Round, ConnectorProfileType.Rectangular => settings.Rectangular, _ => settings.Oval };
            if (!double.TryParse(length.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mm) || mm <= 0 || mm > 10000)
            { System.Windows.MessageBox.Show(this, "Длина гибкого участка — от 1 до 10000 мм.", Title); return; }
            options.Use = use.IsChecked == true; options.Type = type.SelectedItem as string ?? ""; options.LengthMm = mm;
        }
        DialogResult = true;
    }
}
