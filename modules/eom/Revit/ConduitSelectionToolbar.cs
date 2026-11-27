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
    sealed record Selection(Document Document, ElementId View, ElementId[] SelectedIds, ElementId[] Ids)
    {
        internal bool Matches(Selection? other) => other != null && Document.Equals(other.Document) && View == other.View
            && SelectedIds.SequenceEqual(other.SelectedIds) && Ids.SequenceEqual(other.Ids);
    }
    static Selection? current;
    static Window? palette;
    static ToggleButton? mode;
    static Button? reverse;
    static Button? connectPanel;
    static Button? connectSwitch;
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

    static ElementId[] SelectedIds(UIDocument ui) => ui.Selection.GetElementIds().OrderBy(id => id.Value).ToArray();

    static List<Element> ReadSelection(Document doc, IEnumerable<ElementId> ids)
    {
        return ids.Select(doc.GetElement).OfType<Element>().Select(element =>
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
            if (LightingSwitchConnection.Advance(ui, args)) { Hide(); return; }
            var selectedIds = SelectedIds(ui);
            var elements = ReadSelection(ui.Document, selectedIds);
            if (elements.Count == 0) { Hide(); return; }

            var selection = new Selection(ui.Document, ui.ActiveView.Id, selectedIds, elements.Select(e => e.Id).ToArray());
            if (!selection.Matches(current) || palette is { IsVisible: false })
            {
                Hide();
                Open(ui, selection, elements);
            }
            UpdateMode(elements);
            if (reverse != null) reverse.IsEnabled = !ui.Document.IsReadOnly;
            if (connectPanel != null) connectPanel.IsEnabled = !ui.Document.IsReadOnly;
            if (connectSwitch != null) connectSwitch.IsEnabled = !ui.Document.IsReadOnly;
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
            Title = FloatingToolbar.Title, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, ShowActivated = false, ShowInTaskbar = false
        };
        palette = window;
        BotPlusTheme.Apply(window);
        new WindowInteropHelper(window).Owner = ui.Application.MainWindowHandle;
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
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
            reverse = IconButton(FloatingToolbarIcons.ReverseWiring, "Развернуть УГО", "Для выбранных трасс и участков, подключённых к выбранным фитингам", () =>
                Post(selection, (active, selected) =>
                {
                    var targets = WiringTargets(selected);
                    WiringSymbols.Reverse(active.Document, targets, active.ActiveView);
                }));
            controls.Children.Add(reverse);
        }
        if (elements.Count == 1 && elements[0] is Conduit && ui.ActiveView is ViewPlan)
            controls.Children.Add(IconButton(FloatingToolbarIcons.CreateSimilar, "Создать аналог", "Запустить «Гофра, труба» с типом и диаметром выбранного участка", () =>
                Post(selection, (active, targets) => ConduitThroughLightsCommand.Route(active, (Conduit)targets[0]))));
        if (selection.SelectedIds.SequenceEqual(selection.Ids) && elements.All(IsPanelSource))
        {
            connectPanel = IconButton(FloatingToolbarIcons.ConnectPanel, "Подключить к панели", "Создать силовую цепь и выбрать щит на виде", () =>
                Connect(selection, active => ConnectCommand.ConnectToPanel(active, selection.Ids)));
            controls.Children.Add(connectPanel);
            if (elements.All(e => e.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures))
            {
                connectSwitch = IconButton(FloatingToolbarIcons.ConnectSwitch, "Подключить к выключателю", "Создать систему коммутации и выбрать выключатель на виде", () =>
                    Connect(selection, active => LightingSwitchConnection.Start(active, selection.Ids)));
                controls.Children.Add(connectSwitch);
            }
        }
        window.Content = FloatingToolbar.Layout(controls, () => Hide(keepSelection: true));
        window.SourceInitialized += (_, _) => RouteTools.AnchorPalette(window, ui, selection.View);
        window.Show();
    }

    static Button IconButton(string geometry, string name, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = FloatingToolbarIcons.Draw(geometry), Width = 34, Height = 32,
            Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4), ToolTip = $"{name}\n{tooltip}"
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    static bool IsPanelSource(Element element) => element is FamilyInstance && element.Category?.BuiltInCategory is
        BuiltInCategory.OST_LightingFixtures or BuiltInCategory.OST_ElectricalFixtures or BuiltInCategory.OST_ElectricalEquipment;

    static void Connect(Selection selection, Action<UIDocument> action) => Post(selection, (ui, _) =>
    {
        if (ui.Document.IsReadOnly) return;
        Hide();
        try { action(ui); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
    });

    static void Post(Selection selection, Action<UIDocument, List<Element>> action) => App.Queue?.Post(app =>
    {
        if (app.ActiveUIDocument is not { } active || !active.Document.Equals(selection.Document) || active.ActiveView.Id != selection.View) return;
        var selectedIds = SelectedIds(active);
        if (!selectedIds.SequenceEqual(selection.SelectedIds)) return;
        var targets = ReadSelection(active.Document, selectedIds);
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

    internal static void Hide(bool keepSelection = false)
    {
        var window = palette;
        palette = null;
        if (!keepSelection) current = null;
        mode = null;
        reverse = null;
        connectPanel = null;
        connectSwitch = null;
        window?.Close();
    }
}
