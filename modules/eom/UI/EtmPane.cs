using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Line = System.Windows.Shapes.Line;
using Orientation = System.Windows.Controls.Orientation;
using Rectangle = System.Windows.Shapes.Rectangle;
using Ellipse = System.Windows.Shapes.Ellipse;
using Panel = System.Windows.Controls.Panel;
using Visibility = System.Windows.Visibility;

namespace BotPlus.Eom;

/// <summary>
/// Схема питания по реальным цепям проекта, с эскизами типов семейств Revit.
/// Ветки раскрываются кнопкой ±. Кнопки на карточках показывают элементы и отключают оборудование от питающей группы.
/// Открывается немодальным окном поверх Revit.
/// </summary>
internal sealed class EtmPane
{
    sealed class Node
    {
        public string Key = "", Title = "", Sub = "", Details = "";
        public Color Accent;
        /// <summary>Индикаторы как на плане: статус подключения и цвет группы (null — без точек, например у щитов).</summary>
        public Color? Status, Group;
        public BitmapSource? Preview;
        public ElementId[] Select = [];
        public ElementId? FeedCircuit;
        public bool Floating;
        public bool InitiallyCollapsed;
        public bool IsLoad;
        public double BoxW = 150, BoxH = 46, W, H, X, Y;
        public readonly List<Node> Children = [];
        public Rectangle? Frame;
        public int Count => Children.Count + Children.Sum(c => c.Count);
    }

    const double HGap = 18, RowGap = 4, Indent = 18;
    static readonly Color VruColor = Color.FromRgb(130, 70, 190), PanelColor = Color.FromRgb(6, 150, 215),
        LightColor = Color.FromRgb(230, 160, 20), SocketColor = Color.FromRgb(50, 160, 90), PowerColor = Color.FromRgb(210, 70, 60),
        FeederColor = Color.FromRgb(6, 150, 215), AirColor = Color.FromRgb(150, 150, 150);

    readonly DockPanel root = new();
    readonly Canvas canvas = new();
    readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly Border devicesPane = new() { Width = 310, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1, 0, 0, 0) };
    readonly StackPanel devicesList = new() { Margin = new Thickness(10, 0, 10, 8) };
    readonly Button devicesToggle = new() { Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 4, 2) };
    List<(string Level, List<Node> Devices)> deviceFloors = [];
    bool devicesNeedLayout = true;
    readonly ScaleTransform zoom = new(1, 1);
    readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6, 8, 6) };
    readonly TextBlock status = new() { Margin = new Thickness(8, 0, 8, 4), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    readonly Dictionary<string, bool> branchOverrides = [];
    Node? selected;
    List<Node> roots = [];
    List<Node> air = [];
    Document? shownDocument;
    bool canDisconnect;
    bool refreshPending;
    HashSet<ElementId> displayedNetworkIds = [];

    internal EtmPane()
    {
        BotPlusTheme.Apply(root);
        canvas.LayoutTransform = zoom;
        canvas.HorizontalAlignment = HorizontalAlignment.Left; canvas.VerticalAlignment = VerticalAlignment.Top;
        canvas.Themed(Panel.BackgroundProperty, "BP.Input");
        scroll.Content = canvas;
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            Zoom(e.Delta > 0 ? 1.15 : 1 / 1.15); e.Handled = true;
        };

        var bar = new WrapPanel { Margin = new Thickness(4) };
        void B(string text, Action action, string tip)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 4, 2), ToolTip = tip, MinWidth = 32 };
            b.Click += (_, _) => action(); bar.Children.Add(b);
        }
        B("Обновить", () => App.Queue?.Post(app => { if (app.ActiveUIDocument is { } ui) Refresh(ui.Document); }), "Перечитать сеть активного проекта");
        B("Свернуть всё", () => SetAllBranches(false), "Свернуть все ветки оборудования");
        B("Развернуть всё", () => SetAllBranches(true), "Развернуть все щиты и группы");
        B("−", () => Zoom(1 / 1.25), "Уменьшить (Ctrl + колесо)");
        B("+", () => Zoom(1.25), "Увеличить (Ctrl + колесо)");
        B("Вписать", Fit, "Показать схему целиком по ширине панели");
        devicesToggle.Click += (_, _) =>
        {
            devicesPane.Visibility = devicesPane.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            UpdateDevicesPane();
        };
        bar.Children.Add(devicesToggle);
        bar.Children.Add(Legend());

        var right = new DockPanel();
        var caption = new TextBlock { Text = "Неподключённые приборы", FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 8, 10, 6) };
        DockPanel.SetDock(caption, Dock.Top);
        right.Children.Add(caption);
        right.Children.Add(new ScrollViewer { Content = devicesList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        devicesPane.Child = right;
        devicesPane.Themed(Border.BorderBrushProperty, "BP.Border").Themed(Border.BackgroundProperty, "BP.Panel");
        UpdateDevicesPane();

        var info = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Height = 130 };
        info.Themed(Border.BorderBrushProperty, "BP.Border").Themed(Border.BackgroundProperty, "BP.Panel");
        info.Child = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        details.Text = "Глаз показывает элементы на виде Revit. Крестик отключает прибор от питающей группы. Чтобы увидеть приборы группы, разверните её кнопкой +.";

        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(status, Dock.Bottom); DockPanel.SetDock(info, Dock.Bottom);
        DockPanel.SetDock(devicesPane, Dock.Right);
        root.Children.Add(bar); root.Children.Add(status); root.Children.Add(info); root.Children.Add(devicesPane); root.Children.Add(scroll);
    }

    static IEnumerable<Node> All(Node n) => n.Children.SelectMany(All).Prepend(n);

    void SetAllBranches(bool open)
    {
        branchOverrides.Clear();
        branchOverrides["AIR"] = open;
        foreach (var node in roots.Concat(air).SelectMany(All).Where(n => n.Children.Count > 0))
            branchOverrides[node.Key] = open;
        Redraw();
    }

    static UIElement Legend()
    {
        var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var (color, text) in new[] { (VruColor, "ВРУ"), (PanelColor, "Щит"), (LightColor, "Освещение"),
                     (SocketColor, "Розетки"), (PowerColor, "Силовая"), (AirColor, "Не подключено") })
        {
            UIElement mark = color == AirColor
                ? new Rectangle { Width = 14, Height = 10, Stroke = new SolidColorBrush(color), StrokeDashArray = [2, 1.5], StrokeThickness = 1.4 }
                : Dot(color);
            panel.Children.Add(new Border { Child = mark, Margin = new Thickness(8, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        }
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        dots.Children.Add(Dot(C(EomColors.Connected))); dots.Children.Add(Dot(C(EomColors.Group(2))));
        panel.Children.Add(dots);
        panel.Children.Add(new TextBlock { Text = "статус и группа — как индикаторы на плане", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    static Color C((byte R, byte G, byte B) c) => Color.FromRgb(c.R, c.G, c.B);
    static Ellipse Dot(Color color) => new() { Width = 8, Height = 8, Fill = new SolidColorBrush(color), Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };

    Window? window;

    internal void OnDocumentChanged(DocumentChangedEventArgs e)
    {
        if (window?.IsVisible != true || !e.GetDocument().Equals(shownDocument)) return;
        var doc = e.GetDocument();
        refreshPending |= e.GetDeletedElementIds().Any(displayedNetworkIds.Contains) || e.GetAddedElementIds().Concat(e.GetModifiedElementIds())
            .Select(doc.GetElement).Any(element => element is ElectricalSystem || element?.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalEquipment);
    }

    internal void OnIdling(UIApplication app)
    {
        if (window?.IsVisible != true || app.ActiveUIDocument?.Document is not { IsFamilyDocument: false } doc) return;
        if (refreshPending || !doc.Equals(shownDocument)) Refresh(doc);
    }

    /// <summary>Показывает схему в немодальном окне поверх Revit (одно окно; закрытое создаётся заново).</summary>
    internal void ShowWindow(IntPtr owner)
    {
        if (window == null)
        {
            window = EomUi.Window("Менеджер ЭТМ", 1150, 760);
            window.MinWidth = 660;
            window.Content = root;
            new System.Windows.Interop.WindowInteropHelper(window) { Owner = owner };
            window.Closed += (_, _) => { if (window != null) window.Content = null; window = null; };
        }
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    void Zoom(double factor)
    {
        double value = Math.Clamp(zoom.ScaleX * factor, 0.2, 3);
        zoom.ScaleX = zoom.ScaleY = value;
    }
    void Fit()
    {
        if (canvas.Width <= 0 || scroll.ViewportWidth <= 0) return;
        double value = Math.Clamp((scroll.ViewportWidth - 10) / canvas.Width, 0.2, 1.5);
        zoom.ScaleX = zoom.ScaleY = value;
    }

    // ---------- модель схемы ----------

    static string Kw(double w) => (w / 1000).ToString("0.##");

    Dictionary<ElementId, int> groupIndex = [];

    Node PanelNode(PanelInfo p, Network net, EomSettings settings, EomCatalog catalog, HashSet<ElementId> visited,
        Func<ElementId, BitmapSource?> preview, HashSet<ElementId> sources)
    {
        visited.Add(p.Id);
        var load = EomCalc.PanelLoad(p.Circuits.Select(c => c.Input));
        double cos = load.S > 0 ? load.P / load.S : 1;
        bool vru = p.Name.Contains("ВРУ", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("ГРЩ", StringComparison.OrdinalIgnoreCase);
        var feeder = p.FeedCircuit == null ? null : net.AllCircuits.FirstOrDefault(c => c.Id == p.FeedCircuit);
        string source = feeder == null ? "питающая группа не назначена" : $"от {feeder.Input.Panel}, группа {feeder.Input.Number}";
        string kind = sources.Contains(p.Id) ? "Источник" : vru ? "ВРУ" : "Щит";
        var kinds = p.Circuits.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count());
        int K(CircuitKind k) => kinds.TryGetValue(k, out var n) ? n : 0;
        var node = new Node
        {
            Key = "P" + p.Id.Value, Title = p.Name,
            Sub = sources.Contains(p.Id) ? $"Источник · {p.Circuits.Count} гр." : $"{p.Circuits.Count} гр. · Pр {Kw(load.P)} кВт",
            Accent = vru ? VruColor : PanelColor,
            Preview = preview(p.Id), Select = [p.Id], FeedCircuit = p.FeedCircuit, BoxW = 210,
            Details = $"{kind} {p.Name} ({p.Type})\nПитание: {source}\n" +
                $"Групп: {p.Circuits.Count} — освещение {K(CircuitKind.Lighting)}, розетки {K(CircuitKind.Sockets)}, силовые {K(CircuitKind.Power)}, питание щитов {K(CircuitKind.Feeder)}\n" +
                $"Pр = {Kw(load.P)} кВт, Qр = {Kw(load.Q)} квар, Sр = {Kw(load.S)} кВА, cos φ = {cos:0.00}, Iр = {EomCalc.Current(load.P, cos, 3, settings):0.#} А (3ф, Кс = 1)"
        };
        // Группы питания щитов — в конце столбца: их ветки широкие и иначе раздвигают обычные группы.
        foreach (var c in p.Circuits.OrderBy(c => c.Kind == CircuitKind.Feeder ? 1 : 0))
        {
            var child = CircuitNode(c, settings, catalog, groupIndex, preview);
            foreach (var fed in net.Panels.Where(x => x.FeedCircuit == c.Id && !visited.Contains(x.Id)))
                child.Children.Add(PanelNode(fed, net, settings, catalog, visited, preview, sources));
            node.Children.Add(child);
        }
        return node;
    }

    static Node CircuitNode(CircuitInfo c, EomSettings settings, EomCatalog catalog, Dictionary<ElementId, int> groups, Func<ElementId, BitmapSource?> preview)
    {
        var r = EomCalc.Select(c.Input, settings, catalog);
        string mark = EomSettings.Mark(settings.GroupMask, c.Input.Panel, c.Input.Number);
        var (color, kind) = c.Kind switch
        {
            CircuitKind.Lighting => (LightColor, "освещение"),
            CircuitKind.Sockets => (SocketColor, "розетки"),
            CircuitKind.Feeder => (FeederColor, "питание щита"),
            _ => (PowerColor, "силовая")
        };
        string breaker = c.RatingA > 0 ? $"{c.RatingA:0} А" : r.Breaker != null ? $"{r.Breaker.Rating:0} А*" : "—";
        string devices = string.Join("\n", c.Devices.GroupBy(d => d.Name).Select(g => $"  {g.Key} — {g.Count()} шт.").Take(8)) + (c.Devices.GroupBy(d => d.Name).Count() > 8 ? "\n  …" : "");
        var node = new Node
        {
            Key = "C" + c.Id.Value, Title = $"QF{c.Input.Number}  {breaker}  {Kw(c.Input.ActivePowerW)} кВт", Accent = r.Ok ? color : PowerColor,
            Preview = c.Devices.OrderByDescending(d => d.IsPanel).ThenBy(d => d.Name, NaturalComparer.Instance)
                .ThenBy(d => d.Id.Value).Select(d => preview(d.Id)).FirstOrDefault(p => p != null),
            BoxW = 212, BoxH = 28, Status = C(EomColors.Connected), Group = groups.TryGetValue(c.Id, out var g) ? C(EomColors.Group(g)) : null,
            InitiallyCollapsed = !c.Devices.Any(d => d.IsPanel),
            Select = c.Devices.Count > 0 ? c.Devices.Select(d => d.Id).ToArray() : [c.Id],
            Details = $"Группа {mark}{(c.Input.Name.Length > 0 ? " — " + c.Input.Name : "")} ({kind})\n" +
                $"P = {Kw(c.Input.ActivePowerW)} кВт, cos φ = {c.Input.PowerFactor:0.00}, полюсов {c.Input.Poles}, длина {c.Input.LengthM:0.#} м (с запасом {r.CableLengthM:0.#} м)\n" +
                $"Iр = {r.CurrentA:0.#} А, ΔU = {r.VoltageDrop:0.##} %. Автомат: {(c.RatingA > 0 ? c.RatingA + " А" : "не задан")}, подбор {r.Breaker?.Name ?? "—"}. Кабель: {(c.Cable.Length > 0 ? c.Cable : "не задан")}, подбор {r.Cable?.Name ?? "—"}" +
                (r.Ok ? "" : $"\n⚠ {r.Problem}") + $"\nПриборы ({c.Devices.Count}):\n{devices}" + (c.RatingA > 0 ? "" : "\n* — номинал по автоподбору")
        };
        foreach (var device in c.Devices.Where(d => !d.IsPanel).OrderBy(d => d.Name, NaturalComparer.Instance).ThenBy(d => d.Id.Value))
            node.Children.Add(new Node
            {
                Key = $"C{c.Id.Value}D{device.Id.Value}", Title = device.Name, Sub = device.Category, Accent = color,
                Preview = preview(device.Id), Select = [device.Id], FeedCircuit = c.Id, BoxW = 234, BoxH = 40, IsLoad = true,
                Status = node.Status, Group = node.Group, Details = $"{device.Category} — {device.Name}\nПитающая группа {mark}"
            });
        return node;
    }

    static bool IsSupply(Element? element)
    {
        if (element is not FamilyInstance instance) return false;
        static bool Transformer(Element element) => element.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)
            is { StorageType: StorageType.Integer } part && part.AsInteger() == (int)PartType.Transformer;
        if (Transformer(instance) || Transformer(instance.Symbol) || Transformer(instance.Symbol.Family)) return true;
        var names = new[] { instance.Symbol.FamilyName, instance.Symbol.Name };
        return names.Any(name =>
            new[] { "источник", "source", "ТП" }.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase)
            || new[] { "трансформатор", "transformer", "генератор", "generator", "источник питания", "power source" }
                .Any(text => name.Contains(text, StringComparison.OrdinalIgnoreCase)));
    }

    static Node AirNode(string key, string title, string sub, BitmapSource? preview, ElementId id, string details, bool device = false) =>
        new() { Key = key, Title = title, Sub = sub, Preview = preview, Accent = AirColor, Floating = true, Select = [id], BoxW = 160, BoxH = 40, Details = details,
            Status = device ? C(EomColors.Unconnected) : null, Group = device ? C(EomColors.NoGroup) : null };

    static Level? DeviceLevel(Document doc, Element? element)
    {
        if (element == null) return null;
        if (doc.GetElement(element.LevelId) is Level level) return level;
        foreach (var id in new[] { BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.FAMILY_LEVEL_PARAM })
            if (element.get_Parameter(id) is { StorageType: StorageType.ElementId } p && doc.GetElement(p.AsElementId()) is Level reference)
                return reference;
        if (element is FamilyInstance { Host: { } host } && host.Document.Equals(doc))
            return host as Level ?? doc.GetElement(host.LevelId) as Level;
        return null;
    }

    void UpdateDevicesPane()
    {
        bool visible = devicesPane.Visibility == Visibility.Visible;
        int count = deviceFloors.Sum(f => f.Devices.Count);
        devicesToggle.Content = $"{(visible ? "Скрыть" : "Показать")} неподключённые ({count})";
        if (!visible || !devicesNeedLayout) return;

        devicesList.Children.Clear();
        if (count == 0) devicesList.Children.Add(new TextBlock { Text = "Неподключённых приборов нет", Margin = new Thickness(0, 6, 0, 0) });
        foreach (var floor in deviceFloors)
        {
            devicesList.Children.Add(new TextBlock { Text = $"{floor.Level} · {floor.Devices.Count}", TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 5) });
            foreach (var device in floor.Devices)
            {
                var tile = CreateBox(device, false);
                tile.Width = double.NaN;
                tile.Margin = new Thickness(0, 0, 0, 4);
                devicesList.Children.Add(tile);
            }
        }
        devicesNeedLayout = false;
    }

    /// <summary>Перечитывает сеть и перестраивает схему. Вызывать в контексте Revit API.</summary>
    internal void Refresh(Document doc)
    {
        if (!doc.Equals(shownDocument)) branchOverrides.Clear();
        shownDocument = doc;
        canDisconnect = !doc.IsReadOnly;
        refreshPending = false;
        var settings = EomStore.Settings(doc);
        var catalog = EomCatalog.Load();
        var net = Network.Read(doc, settings);
        displayedNetworkIds = net.Panels.Select(p => p.Id).Concat(net.AllCircuits.Select(c => c.Id))
            .Concat(net.AllCircuits.SelectMany(c => c.Devices).Select(d => d.Id)).Concat(net.Unconnected.Select(d => d.Id)).ToHashSet();
        var inPanels = net.Panels.SelectMany(p => p.Circuits).Select(c => c.Id).ToHashSet();
        var visited = new HashSet<ElementId>();
        // Тот же порядок, что у индикаторов на плане: индекс группы в Network.AllCircuits.
        groupIndex = net.AllCircuits.Select((c, i) => (c.Id, i)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().i);

        var previews = new Dictionary<ElementId, BitmapSource?>();
        BitmapSource? Preview(ElementId id)
        {
            if (doc.GetElement(id) is not FamilyInstance instance) return null;
            var type = instance.Symbol;
            if (!previews.TryGetValue(type.Id, out var image)) previews[type.Id] = image = EquipmentPane.Preview(type, 48);
            return image;
        }
        var sources = net.Panels.Where(p => IsSupply(doc.GetElement(p.Id))).Select(p => p.Id).ToHashSet();
        roots = [];
        air = [];
        foreach (var p in net.Panels.Where(p => p.FeedCircuit == null || !inPanels.Contains(p.FeedCircuit))
                     .OrderByDescending(p => sources.Contains(p.Id))
                     .ThenByDescending(p => p.Name.Contains("ВРУ", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("ГРЩ", StringComparison.OrdinalIgnoreCase)))
        {
            if (p.Circuits.Count == 0 && p.FeedCircuit == null && !sources.Contains(p.Id))
                air.Add(AirNode("P" + p.Id.Value, p.Name, "Щит без групп", Preview(p.Id), p.Id, $"Щит {p.Name} ({p.Type}) не подключён: нет питающей группы и нет отходящих групп."));
            else roots.Add(PanelNode(p, net, settings, catalog, visited, Preview, sources));
        }
        foreach (var p in net.Panels.Where(p => !visited.Contains(p.Id) && !air.Any(a => a.Select[0] == p.Id)))
            roots.Add(PanelNode(p, net, settings, catalog, visited, Preview, sources));
        foreach (var c in net.Orphans)
        {
            var n = CircuitNode(c, settings, catalog, groupIndex, Preview); n.Floating = true; n.Accent = AirColor;
            n.Details = "Группа без щита. " + n.Details; air.Add(n);
        }
        deviceFloors = net.Unconnected.Select(d => (Device: d, Level: DeviceLevel(doc, doc.GetElement(d.Id))))
            .GroupBy(x => x.Level?.Id)
            .OrderByDescending(g => g.First().Level?.ProjectElevation ?? double.NegativeInfinity)
            .ThenBy(g => g.First().Level?.Name ?? "", NaturalComparer.Instance)
            .Select(g => (Level: g.First().Level?.Name ?? "Без уровня", Devices: g.OrderBy(x => x.Device.Name, NaturalComparer.Instance)
                .ThenBy(x => x.Device.Id.Value).Select(x =>
                {
                    var d = x.Device;
                    return AirNode("D" + d.Id.Value, d.Name, d.Category, Preview(d.Id), d.Id,
                        $"Не подключено: {d.Category} — {d.Name} (ID {d.Id.Value}).\nУровень: {x.Level?.Name ?? "Без уровня"}.\nПодключите командой «Подключить к щиту».", device: true);
                }).ToList())).ToList();
        devicesList.Children.Clear();
        devicesNeedLayout = true;
        selected = null;
        UpdateDevicesPane();

        Redraw();
        status.Text = $"{doc.Title}: щитов {net.Panels.Count}, групп {net.AllCircuits.Count()}, не подключено {net.Unconnected.Count + net.Orphans.Count}. Обновлено {DateTime.Now:HH:mm}.";
    }

    // ---------- раскладка ----------
    // Независимые ветки начинаются с оборудования верхнего уровня. Под каждым щитом — столбец его групп.
    // Провод к нижестоящему щиту выходит из питающего автомата вправо, по своей вертикали опускается под столбец и идёт к щиту.

    const double Lane = 6, Drop = 18;

    bool Open(Node n) => n.Children.Count > 0 && branchOverrides.GetValueOrDefault(n.Key, !n.InitiallyCollapsed);

    /// <summary>Раскрытые группы и колонки отходящих щитов и приборов. Обратный порядок оставляет место для проводов.</summary>
    List<Node> Feeders(Node panel) => panel.Children.Where(Open).ToList();
    static IEnumerable<Node[]> Columns(Node group)
    {
        foreach (var panel in group.Children.Where(n => !n.IsLoad)) yield return [panel];
        var loads = group.Children.Where(n => n.IsLoad).ToArray();
        if (loads.Length > 0) yield return loads;
    }

    List<Node[]> Subs(Node panel) => Enumerable.Reverse(Feeders(panel)).SelectMany(Columns).ToList();

    void Measure(Node n)
    {
        n.W = n.BoxW; n.H = n.BoxH;
        if (!Open(n)) return;
        double colW = n.Children.Max(c => c.BoxW), colH = n.Children.Sum(c => c.BoxH) + RowGap * (n.Children.Count - 1);
        int lanes = Feeders(n).Count; var subs = Subs(n);
        foreach (var s in subs.SelectMany(column => column)) Measure(s);
        double subsW = subs.Count == 0 ? 0 : subs.Sum(column => column.Max(s => s.W)) + HGap * (subs.Count - 1);
        n.W = Math.Max(Math.Max(n.BoxW, Indent + colW + 8 + lanes * Lane), subsW);
        n.H = n.BoxH + 10 + colH + (subs.Count == 0 ? 0 : 12 + lanes * Lane + Drop
            + subs.Max(column => column.Sum(s => s.H) + RowGap * (column.Length - 1)));
    }

    /// <summary>X, Y — левый верхний угол блока.</summary>
    void Place(Node n, double left, double top)
    {
        n.Y = top;
        n.X = left;
        if (!Open(n)) return;
        double y = top + n.BoxH + 10;
        foreach (var c in n.Children) { c.X = left + Indent; c.Y = y; c.W = c.BoxW; c.H = c.BoxH; y += c.BoxH + RowGap; }
        var subs = Subs(n);
        if (subs.Count == 0) return;
        double subTop = y - RowGap + 12 + Feeders(n).Count * Lane + Drop, sx = left;
        foreach (var column in subs)
        {
            double sy = subTop;
            foreach (var s in column) { Place(s, sx, sy); sy += s.H + RowGap; }
            sx += column.Max(s => s.W) + HGap;
        }
    }

    // ---------- отрисовка ----------

    void Redraw()
    {
        canvas.Children.Clear();
        double xNext = 20, y = 20;
        foreach (var branch in roots)
        {
            Measure(branch);
            Place(branch, xNext, 20);
            DrawTree(branch);
            xNext += branch.W + HGap;
            y = Math.Max(y, branch.Y + branch.H + 40);
        }
        double width = Math.Max(xNext - HGap + 20, 900);
        if (air.Count > 0)
        {
            bool open = branchOverrides.GetValueOrDefault("AIR", true);
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(Toggle("AIR", open, air.Count));
            title.Children.Add(new TextBlock { Text = $"Не подключено — «в воздухе» ({air.Count})", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
            Canvas.SetLeft(title, 20); Canvas.SetTop(title, y); canvas.Children.Add(title);
            y += 28;
            if (open)
            {
                double x = 20, rowHeight = 0;
                foreach (var n in air)
                {
                    Measure(n);
                    if (x > 20 && x + n.W > width - 20) { x = 20; y += rowHeight + 16; rowHeight = 0; }
                    width = Math.Max(width, x + n.W + 20);
                    Place(n, x, y);
                    DrawTree(n);
                    x += n.W + 16;
                    rowHeight = Math.Max(rowHeight, n.H);
                }
                y += rowHeight + 16;
            }
        }
        canvas.Width = width; canvas.Height = y + 20;
        if (selected != null) foreach (var n in roots.Concat(air).SelectMany(All)) if (n.Key == selected.Key && n.Frame != null) { n.Frame.StrokeThickness = 3; selected = n; }
    }

    void DrawTree(Node n)
    {
        Box(n, n.Children.Count > 0);
        if (!Open(n)) return;
        // Щит: столбец автоматов на общей вертикальной шине.
        double spine = n.X + Indent / 2, last = n.Children[^1].Y + n.Children[^1].BoxH / 2;
        Wire(spine, n.Y + n.BoxH, spine, last);
        foreach (var c in n.Children) { Wire(spine, c.Y + c.BoxH / 2, c.X, c.Y + c.BoxH / 2); Box(c, c.Children.Count > 0); }
        var feeders = Feeders(n);
        if (feeders.Count == 0) return;
        double colRight = n.X + Indent + n.Children.Max(c => c.BoxW) + 8, colBottom = n.Children[^1].Y + n.Children[^1].BoxH + 12;
        for (int i = 0; i < feeders.Count; i++)
        {
            var f = feeders[i];
            int k = feeders.Count - 1 - i; // верхний автомат — дальняя вертикаль и нижняя горизонталь
            double lane = colRight + k * Lane, bus = colBottom + k * Lane, mid = f.Y + f.BoxH / 2;
            Wire(f.X + f.BoxW, mid, lane, mid);
            Wire(lane, mid, lane, bus);
            var columns = Columns(f).ToArray();
            var targets = columns.Select(c => c[0].X + c[0].BoxW / 2).Append(lane).ToList();
            Wire(targets.Min(), bus, targets.Max(), bus);
            foreach (var column in columns)
            {
                double x = column[0].X + column[0].BoxW / 2;
                Wire(x, bus, x, column[^1].Y);
                foreach (var s in column) DrawTree(s);
            }
        }
    }

    void Wire(double x1, double y1, double x2, double y2)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = 1.4, SnapsToDevicePixels = true };
        line.SetResourceReference(Shape.StrokeProperty, "BP.Muted");
        canvas.Children.Add(line);
    }

    /// <summary>Кнопка ± ветки: свернуть или развернуть только её.</summary>
    Border Toggle(string key, bool open, int count)
    {
        var glyph = new TextBlock { Text = open ? "−" : "+", FontWeight = FontWeights.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
        var button = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), Child = glyph, Cursor = Cursors.Hand,
            ToolTip = open ? "Свернуть ветку" : $"Развернуть ветку ({count})", VerticalAlignment = VerticalAlignment.Center };
        button.Themed(Border.BorderBrushProperty, "BP.Border").Themed(Border.BackgroundProperty, "BP.Input");
        button.MouseLeftButtonDown += (_, e) => { branchOverrides[key] = !open; Redraw(); e.Handled = true; };
        return button;
    }

    void Box(Node n, bool toggle)
    {
        var tile = CreateBox(n, toggle);
        Canvas.SetLeft(tile, n.X); Canvas.SetTop(tile, n.Y);
        canvas.Children.Add(tile);
    }

    Grid CreateBox(Node n, bool toggle)
    {
        var accent = new SolidColorBrush(n.Accent);
        bool compact = n.BoxH < 30;
        var grid = new Grid { Width = n.BoxW, Height = n.BoxH, ToolTip = n.Details };
        var frame = new Rectangle { RadiusX = 3, RadiusY = 3, Stroke = accent, StrokeThickness = 1.4 };
        frame.SetResourceReference(Shape.FillProperty, "BP.Panel");
        if (n.Floating) { frame.StrokeDashArray = [4, 3]; grid.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 5, Opacity = 0.35 }; }
        grid.Children.Add(frame);
        var row = new DockPanel { Margin = new Thickness(5, 0, 4, 0), LastChildFill = true };
        if (n.Preview is { } preview)
        {
            double size = compact ? 18 : 32;
            var icon = new Image { Source = preview, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
            DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        }
        if (toggle)
        {
            bool open = Open(n);
            var t = Toggle(n.Key, open, n.Count); t.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(t, Dock.Right); row.Children.Add(t);
        }
        if (n.FeedCircuit != null)
            AddAction(FloatingToolbarIcons.Close, "Отключить от питающей группы", () => Disconnect(n), canDisconnect);
        AddAction(FloatingToolbarIcons.Eye, "Показать в модели", () => Pick(n));
        if (n.Status is { } status)
        {
            var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0),
                ToolTip = n.Status == C(EomColors.Unconnected) ? "Не подключён (как красная точка на плане)" : "Подключён; вторая точка — цвет группы на плане" };
            dots.Children.Add(Dot(status)); if (n.Group is { } group) dots.Children.Add(Dot(group));
            DockPanel.SetDock(dots, Dock.Right); row.Children.Add(dots);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = n.Title, FontWeight = compact ? FontWeights.Normal : FontWeights.SemiBold, FontSize = compact ? 11.5 : 12, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!compact)
        {
            var s = new TextBlock { Text = n.Sub, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
            s.SetResourceReference(TextBlock.ForegroundProperty, "BP.Muted");
            text.Children.Add(s);
        }
        row.Children.Add(text);
        grid.Children.Add(row);
        n.Frame = frame;
        return grid;

        void AddAction(string icon, string label, Action action, bool enabled = true)
        {
            var button = new Button
            {
                Content = FloatingToolbarIcons.Draw(icon, 13), Width = 23, Height = 23,
                Padding = new Thickness(0), Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = label, IsEnabled = enabled
            };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => action();
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
        }
    }

    void Pick(Node n)
    {
        if (selected?.Frame is { } old) old.StrokeThickness = 1.4;
        selected = n;
        if (n.Frame != null) n.Frame.StrokeThickness = 3;
        details.Text = n.Details;
        var ids = n.Select;
        var source = shownDocument;
        App.Queue?.Post(app =>
        {
            if (app.ActiveUIDocument is not { } ui || !ui.Document.Equals(source)) return;
            var existing = ids.Where(id => ui.Document.GetElement(id) != null).ToList();
            if (existing.Count == 0) return;
            ui.Selection.SetElementIds(existing);
            try { ui.ShowElements(existing); } catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
        });
    }

    void Disconnect(Node node)
    {
        if (node.FeedCircuit is not { } circuitId || node.Select.Length != 1) return;
        var equipmentId = node.Select[0];
        var source = shownDocument;
        App.Queue?.Post(app =>
        {
            if (app.ActiveUIDocument?.Document is not { IsReadOnly: false } doc || !doc.Equals(source)) return;
            if (doc.GetElement(equipmentId) is not FamilyInstance equipment
                || doc.GetElement(circuitId) is not ElectricalSystem { SystemType: ElectricalSystemType.PowerCircuit } circuit
                || !circuit.Elements.Contains(equipment))
            {
                Refresh(doc);
                return;
            }

            using var edit = new Transaction(doc, "ЭОМ: отключить оборудование от группы");
            if (edit.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Revit не начал изменение цепи.");
            using var member = new ElementSet();
            member.Insert(equipment);
            circuit.RemoveFromCircuit(member);
            doc.Regenerate();
            if (doc.GetElement(circuitId) is ElectricalSystem current && current.Elements.Contains(equipment))
                throw new InvalidOperationException("Revit сохранил связь оборудования с питающей группой.");
            if (edit.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit отменил отключение оборудования.");
            Refresh(doc);
            details.Text = $"«{node.Title}» отключено от питающей группы.";
        });
    }
}
