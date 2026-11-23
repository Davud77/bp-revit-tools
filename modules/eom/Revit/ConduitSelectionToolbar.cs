using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace BotPlus.Eom;

/// <summary>Панель контекста для выбранных трасс, фитингов и семейств ЭОМ.</summary>
internal static class ConduitSelectionToolbar
{
    sealed record Selection(Document Document, ElementId View, ElementId[] Ids)
    {
        internal bool Matches(Selection? other) => other != null && Document.Equals(other.Document) && View == other.View && Ids.SequenceEqual(other.Ids);
    }
    static Selection? current;
    static Window? palette;
    static ToggleButton? mode;
    static Button? reverse;
    static bool? displayedMode;

    static ConnectorSet? Connectors(FamilyInstance family) => family.MEPModel?.ConnectorManager?.Connectors;

    static bool IsFitting(Element element) => element.Category?.BuiltInCategory is BuiltInCategory.OST_ConduitFitting or BuiltInCategory.OST_CableTrayFitting;

    static bool IsSupported(Element element)
    {
        if (element is Conduit or CableTray || IsFitting(element)) return true;
        if (element is not FamilyInstance family || family.ViewSpecific) return false;
        if (element.Category is { } category && Network.DeviceCategories.Contains(category.BuiltInCategory)) return true;
        return EomStore.RouteEmergency(element) != null || Connectors(family)?.Cast<Connector>().Any(c =>
            c.Domain is Domain.DomainElectrical or Domain.DomainCableTrayConduit) == true;
    }

    static List<Element> ReadSelection(UIDocument ui)
    {
        var doc = ui.Document;
        return ui.Selection.GetElementIds().Select(doc.GetElement).OfType<Element>().Select(element =>
        {
            if (element is FamilyInstance { Symbol.FamilyName: WiringSymbols.Family } && EomStore.WiringHost(element) is { } id)
                return doc.GetElement(new ElementId(id)) ?? element;
            return element;
        }).Where(IsSupported).DistinctBy(e => e.Id).OrderBy(e => e.Id.Value).ToList();
    }

    static List<Element> WiringTargets(IEnumerable<Element> elements)
    {
        var targets = new Dictionary<ElementId, Element>();
        foreach (var element in elements)
        {
            if (element is Conduit or CableTray) { targets.TryAdd(element.Id, element); continue; }
            if (!IsFitting(element) || element is not FamilyInstance fitting || Connectors(fitting) is not { } connectors) continue;
            foreach (Connector port in connectors)
            {
                if (port.Domain != Domain.DomainCableTrayConduit || port.ConnectorType != ConnectorType.End || !port.IsConnected) continue;
                foreach (Connector other in port.AllRefs)
                    if (other.Owner is Conduit or CableTray && port.IsConnectedTo(other)) targets.TryAdd(other.Owner.Id, other.Owner);
            }
        }
        return targets.Values.OrderBy(e => e.Id.Value).ToList();
    }

    internal static void OnIdling(object? sender, IdlingEventArgs args)
    {
        try
        {
            if (RouteTools.IsDrawing || sender is not UIApplication { ActiveUIDocument: { } ui } || ui.Document.IsFamilyDocument
                || ui.ActiveView.IsTemplate || !ui.ActiveView.AreGraphicsOverridesAllowed())
            { Hide(); return; }
            var elements = ReadSelection(ui);
            if (elements.Count == 0) { Hide(); return; }

            var selection = new Selection(ui.Document, ui.ActiveView.Id, elements.Select(e => e.Id).ToArray());
            if (!selection.Matches(current) || palette?.IsVisible != true)
            {
                Hide();
                Open(ui, selection, elements);
            }
            UpdateMode(elements);
            if (reverse != null) reverse.IsEnabled = !ui.Document.IsReadOnly;
        }
        catch (Exception error)
        {
            Hide();
            System.Diagnostics.Trace.WriteLine(error);
        }
    }

    static void UpdateMode(IReadOnlyCollection<Element> elements)
    {
        int emergency = elements.Count(e => EomStore.RouteEmergency(e) == true);
        displayedMode = emergency == 0 ? false : emergency == elements.Count ? true : null;
        if (mode == null || mode.IsChecked == displayedMode) return;
        if (displayedMode is { } uniform) SystemModeToggle.ShowMode(mode, uniform);
        else
        {
            mode.IsChecked = null;
            mode.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "BP.Text");
            mode.ToolTip = "Смешанный выбор. Нажмите «А», чтобы сделать все выбранные элементы аварийными.";
        }
    }

    static void Open(UIDocument ui, Selection selection, List<Element> elements)
    {
        current = selection;
        var window = new Window
        {
            Title = "Floating Toolbar", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, ShowActivated = false, ShowInTaskbar = false
        };
        palette = window;
        BotPlusTheme.Apply(window);
        new WindowInteropHelper(window).Owner = ui.Application.MainWindowHandle;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        controls.Children.Add(new TextBlock { Text = "Floating Toolbar", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 8, 0) });
        var toggle = new ToggleButton { Content = FloatingToolbarIcons.Draw(FloatingToolbarIcons.Emergency), Width = 34, Height = 32, Padding = new Thickness(4),
            IsThreeState = true, IsEnabled = !ui.Document.IsReadOnly };
        mode = toggle;
        AutomationProperties.SetName(toggle, "Аварийная система выбранных элементов");
        SystemModeToggle.ShowMode(toggle, false);
        toggle.Click += (_, _) =>
        {
            bool emergency = displayedMode != true;
            displayedMode = emergency;
            SystemModeToggle.ShowMode(toggle, emergency);
            Post(selection, (active, targets) => Edit(active.Document, "ЭОМ: система выбранных элементов",
                () => WiringSymbols.Recolor(active.Document, targets, active.ActiveView, emergency)));
        };
        controls.Children.Add(toggle);
        if (ui.ActiveView is ViewPlan && elements.Any(e => e is Conduit or CableTray || IsFitting(e)))
        {
            reverse = IconButton(FloatingToolbarIcons.ReverseWiring, "Развернуть УГО", "Развернуть УГО на выбранных трассах и участках, подключённых к выбранным фитингам", () =>
                Post(selection, (active, selected) =>
                {
                    var targets = WiringTargets(selected);
                    if (targets.Count == 0) throw new InvalidOperationException("У выбранных фитингов нет подключённых участков трассы.");
                    (int Reversed, int Skipped) result = default;
                    Edit(active.Document, "ЭОМ: групповой разворот УГО", () => result = WiringSymbols.Reverse(active.Document, targets, active.ActiveView));
                    if (result.Skipped > 0) TaskDialog.Show("BotPlus ЭОМ", $"Развёрнуто участков: {result.Reversed}. Пропущено без подходящего УГО: {result.Skipped}.");
                }));
            controls.Children.Add(reverse);
        }
        if (elements.Count == 1 && elements[0] is Conduit && ui.ActiveView is ViewPlan)
            controls.Children.Add(IconButton(FloatingToolbarIcons.CreateSimilar, "Создать аналог", "Создать аналог: запустить «Гофра, труба» с типом и диаметром выбранного участка", () =>
                Post(selection, (active, targets) => ConduitThroughLightsCommand.Route(active, (Conduit)targets[0]))));
        window.Content = new Border { Child = controls, BorderThickness = new Thickness(1) }.Themed(Border.BorderBrushProperty, "BP.Border");
        window.SourceInitialized += (_, _) => RouteTools.AnchorPalette(window, ui, selection.View);
        window.Show();
    }

    static Button IconButton(string geometry, string name, string tooltip, Action action)
    {
        var button = new Button { Content = FloatingToolbarIcons.Draw(geometry), Width = 34, Height = 32,
            Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4), ToolTip = tooltip };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    static void Post(Selection selection, Action<UIDocument, List<Element>> action) => App.Queue?.Post(app =>
    {
        if (app.ActiveUIDocument is not { } active || !active.Document.Equals(selection.Document) || active.ActiveView.Id != selection.View) return;
        var targets = ReadSelection(active);
        if (!targets.Select(e => e.Id).SequenceEqual(selection.Ids)) return;
        action(active, targets);
    });

    static void Edit(Document doc, string title, Action action)
    {
        using var transaction = new Transaction(doc, title);
        if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Не удалось начать изменение.");
        action();
        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit отменил изменение.");
    }

    internal static void Hide()
    {
        var window = palette;
        palette = null;
        current = null;
        mode = null;
        reverse = null;
        window?.Close();
    }
}
