using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Line = System.Windows.Shapes.Line;
using Orientation = System.Windows.Controls.Orientation;
using Rectangle = System.Windows.Shapes.Rectangle;
using Ellipse = System.Windows.Shapes.Ellipse;
using Panel = System.Windows.Controls.Panel;
using Path = System.Windows.Shapes.Path;

namespace BotPlus.Eom;

/// <summary>
/// Закрепляемая панель «BotPlus ЭТМ»: схема сети — источник → ВРУ и щиты в ряд, под каждым щитом компактный столбец автоматов групп,
/// нижестоящие щиты — ниже, под столбцом, с проводом от питающего автомата. Тип группы показан УГО по ГОСТ 21.614 вместо надписи.
/// Любую ветку можно свернуть (кнопка ± на блоке) или свернуть/развернуть всё. Клик по блоку показывает элемент на плане,
/// двойной клик по щиту выделяет всё, что от него питается. Точки на блоках — те же индикаторы статуса, что на плане.
/// Открывается немодальным окном поверх Revit.
/// </summary>
internal sealed class EtmPane
{
    enum Layout { Down, Column }
    enum Symbol { None, Source, Vru, Panel, Lighting, Socket, Power }

    sealed class Node
    {
        public string Key = "", Title = "", Sub = "", Details = "";
        public Color Accent;
        /// <summary>Индикаторы как на плане: статус подключения и цвет группы (null — без точек, например у щитов).</summary>
        public Color? Status, Group;
        public Symbol Icon;
        public ElementId[] Select = [];
        public bool Floating;
        public Layout Layout = Layout.Down;
        public double BoxW = 150, BoxH = 46, W, H, X, Y;
        public readonly List<Node> Children = [];
        public Rectangle? Frame;
        public int Count => Children.Count + Children.Sum(c => c.Count);
    }

    const double HGap = 18, VGap = 34, RowGap = 4, Indent = 18;
    static readonly Color SourceColor = Color.FromRgb(140, 140, 140), VruColor = Color.FromRgb(130, 70, 190), PanelColor = Color.FromRgb(6, 150, 215),
        LightColor = Color.FromRgb(230, 160, 20), SocketColor = Color.FromRgb(50, 160, 90), PowerColor = Color.FromRgb(210, 70, 60),
        FeederColor = Color.FromRgb(6, 150, 215), AirColor = Color.FromRgb(150, 150, 150);

    readonly DockPanel root = new();
    readonly Canvas canvas = new();
    readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly ScaleTransform zoom = new(1, 1);
    readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6, 8, 6) };
    readonly TextBlock status = new() { Margin = new Thickness(8, 0, 8, 4), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    /// <summary>Свёрнутые ветки по ключу элемента: сохраняются при «Обновить».</summary>
    readonly HashSet<string> collapsed = [];
    Node? selected, tree;
    List<Node> air = [];

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
        B("Свернуть всё", () => { collapsed.Clear(); if (tree != null) foreach (var n in All(tree).Where(n => n.Children.Count > 0 && n.Icon != Symbol.Source)) collapsed.Add(n.Key); collapsed.Add("AIR"); Redraw(); }, "Свернуть все щиты: видны только щиты верхнего уровня");
        B("Развернуть всё", () => { collapsed.Clear(); Redraw(); }, "Развернуть все щиты и группы");
        B("−", () => Zoom(1 / 1.25), "Уменьшить (Ctrl + колесо)");
        B("+", () => Zoom(1.25), "Увеличить (Ctrl + колесо)");
        B("Вписать", Fit, "Показать схему целиком по ширине панели");
        bar.Children.Add(Legend());

        var info = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Height = 130 };
        info.Themed(Border.BorderBrushProperty, "BP.Border").Themed(Border.BackgroundProperty, "BP.Panel");
        info.Child = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        details.Text = "Нажмите «Менеджер ЭТМ» или «Обновить», чтобы построить схему сети активного проекта. Щелчок по блоку — показать элемент на плане, двойной щелчок по щиту — выделить всё, что от него питается, ± — свернуть или развернуть ветку.";

        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(status, Dock.Bottom); DockPanel.SetDock(info, Dock.Bottom);
        root.Children.Add(bar); root.Children.Add(status); root.Children.Add(info); root.Children.Add(scroll);
    }

    static IEnumerable<Node> All(Node n) => n.Children.SelectMany(All).Prepend(n);

    static UIElement Legend()
    {
        var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var (symbol, color, text) in new[] { (Symbol.Source, SourceColor, "Источник"), (Symbol.Vru, VruColor, "ВРУ"), (Symbol.Panel, PanelColor, "Щит"),
                     (Symbol.Lighting, LightColor, "Освещение"), (Symbol.Socket, SocketColor, "Розетки"), (Symbol.Power, PowerColor, "Силовая"), (Symbol.None, AirColor, "Не подключено") })
        {
            UIElement mark = symbol == Symbol.None
                ? new Rectangle { Width = 14, Height = 10, Stroke = new SolidColorBrush(color), StrokeDashArray = [2, 1.5], StrokeThickness = 1.4 }
                : Glyph(symbol, new SolidColorBrush(color));
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

    /// <summary>
    /// УГО по ГОСТ 21.614 в упрощённом виде (16×16): светильник — круг с крестом, розетка — полуокружность с выводом,
    /// электроприёмник — круг с «М», щит — прямоугольник с зачернённой половиной, ВРУ — зачернённый прямоугольник, источник — трансформатор.
    /// </summary>
    static FrameworkElement Glyph(Symbol symbol, Brush brush)
    {
        var c = new Canvas { Width = 16, Height = 16, SnapsToDevicePixels = true };
        Shape S(Shape s) { s.Stroke = brush; s.StrokeThickness = 1.4; c.Children.Add(s); return s; }
        Path P(string data, bool fill = false) { var p = (Path)S(new Path { Data = Geometry.Parse(data), StrokeLineJoin = PenLineJoin.Round }); if (fill) p.Fill = brush; return p; }
        Ellipse E(double x, double y, double d) { var e = (Ellipse)S(new Ellipse { Width = d, Height = d }); Canvas.SetLeft(e, x); Canvas.SetTop(e, y); return e; }
        switch (symbol)
        {
            case Symbol.Lighting: E(2, 2, 12); P("M3.8,3.8 L12.2,12.2 M12.2,3.8 L3.8,12.2"); break;
            case Symbol.Socket: P("M2,12 A6,6 0 0 1 14,12 Z M8,6 L8,1.5"); break;
            case Symbol.Power: E(1.5, 1.5, 13); P("M5,11 L5,5.2 L8,9 L11,5.2 L11,11"); break;
            case Symbol.Panel: P("M1.5,4 L14.5,4 L14.5,12 L1.5,12 Z"); P("M1.5,12 L14.5,4 L14.5,12 Z", true); break;
            case Symbol.Vru: P("M1.5,4 L14.5,4 L14.5,12 L1.5,12 Z", true); break;
            case Symbol.Source: E(1, 4, 9); E(6, 4, 9); break;
        }
        return c;
    }

    Window? window;

    /// <summary>Показывает схему в немодальном окне поверх Revit (одно окно; закрытое создаётся заново).</summary>
    internal void ShowWindow(IntPtr owner)
    {
        if (window == null)
        {
            window = EomUi.Window("Менеджер ЭТМ", 1150, 760);
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

    Node PanelNode(PanelInfo p, Network net, EomSettings settings, EomCatalog catalog, HashSet<ElementId> visited)
    {
        visited.Add(p.Id);
        var load = EomCalc.PanelLoad(p.Circuits.Select(c => c.Input));
        double cos = load.S > 0 ? load.P / load.S : 1;
        bool vru = p.Name.Contains("ВРУ", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("ГРЩ", StringComparison.OrdinalIgnoreCase);
        var feeder = p.FeedCircuit == null ? null : net.AllCircuits.FirstOrDefault(c => c.Id == p.FeedCircuit);
        string source = feeder == null ? "от источника (питающая группа не задана)" : $"от {feeder.Input.Panel}, группа {feeder.Input.Number}";
        var kinds = p.Circuits.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count());
        int K(CircuitKind k) => kinds.TryGetValue(k, out var n) ? n : 0;
        var node = new Node
        {
            Key = "P" + p.Id.Value, Title = p.Name, Sub = $"{p.Circuits.Count} гр. · Pр {Kw(load.P)} кВт", Accent = vru ? VruColor : PanelColor,
            Icon = vru ? Symbol.Vru : Symbol.Panel, Select = [p.Id], BoxW = 160, Layout = Layout.Column,
            Details = $"{(vru ? "ВРУ" : "Щит")} {p.Name} ({p.Type})\nПитание: {source}\n" +
                $"Групп: {p.Circuits.Count} — освещение {K(CircuitKind.Lighting)}, розетки {K(CircuitKind.Sockets)}, силовые {K(CircuitKind.Power)}, питание щитов {K(CircuitKind.Feeder)}\n" +
                $"Pр = {Kw(load.P)} кВт, Qр = {Kw(load.Q)} квар, Sр = {Kw(load.S)} кВА, cos φ = {cos:0.00}, Iр = {EomCalc.Current(load.P, cos, 3, settings):0.#} А (3ф, Кс = 1)"
        };
        // Группы питания щитов — в конце столбца: их ветки широкие и иначе раздвигают обычные группы.
        foreach (var c in p.Circuits.OrderBy(c => c.Kind == CircuitKind.Feeder ? 1 : 0))
        {
            var child = CircuitNode(c, settings, catalog, groupIndex);
            foreach (var fed in net.Panels.Where(x => x.FeedCircuit == c.Id && !visited.Contains(x.Id)))
                child.Children.Add(PanelNode(fed, net, settings, catalog, visited));
            node.Children.Add(child);
        }
        return node;
    }

    static Node CircuitNode(CircuitInfo c, EomSettings settings, EomCatalog catalog, Dictionary<ElementId, int> groups)
    {
        var r = EomCalc.Select(c.Input, settings, catalog);
        string mark = EomSettings.Mark(settings.GroupMask, c.Input.Panel, c.Input.Number);
        var (color, kind, icon) = c.Kind switch
        {
            CircuitKind.Lighting => (LightColor, "освещение", Symbol.Lighting),
            CircuitKind.Sockets => (SocketColor, "розетки", Symbol.Socket),
            CircuitKind.Feeder => (FeederColor, "питание щита", Symbol.Panel),
            _ => (PowerColor, "силовая", Symbol.Power)
        };
        string breaker = c.RatingA > 0 ? $"{c.RatingA:0} А" : r.Breaker != null ? $"{r.Breaker.Rating:0} А*" : "—";
        string devices = string.Join("\n", c.Devices.GroupBy(d => d.Name).Select(g => $"  {g.Key} — {g.Count()} шт.").Take(8)) + (c.Devices.GroupBy(d => d.Name).Count() > 8 ? "\n  …" : "");
        return new Node
        {
            Key = "C" + c.Id.Value, Title = $"QF{c.Input.Number}  {breaker}  {Kw(c.Input.ActivePowerW)} кВт", Accent = r.Ok ? color : PowerColor, Icon = icon,
            BoxW = 184, BoxH = 24, Status = C(EomColors.Connected), Group = groups.TryGetValue(c.Id, out var g) ? C(EomColors.Group(g)) : null,
            Select = c.Devices.Count > 0 ? c.Devices.Select(d => d.Id).ToArray() : [c.Id],
            Details = $"Группа {mark}{(c.Input.Name.Length > 0 ? " — " + c.Input.Name : "")} ({kind})\n" +
                $"P = {Kw(c.Input.ActivePowerW)} кВт, cos φ = {c.Input.PowerFactor:0.00}, полюсов {c.Input.Poles}, длина {c.Input.LengthM:0.#} м (с запасом {r.CableLengthM:0.#} м)\n" +
                $"Iр = {r.CurrentA:0.#} А, ΔU = {r.VoltageDrop:0.##} %. Автомат: {(c.RatingA > 0 ? c.RatingA + " А" : "не задан")}, подбор {r.Breaker?.Name ?? "—"}. Кабель: {(c.Cable.Length > 0 ? c.Cable : "не задан")}, подбор {r.Cable?.Name ?? "—"}" +
                (r.Ok ? "" : $"\n⚠ {r.Problem}") + $"\nПриборы ({c.Devices.Count}):\n{devices}" + (c.RatingA > 0 ? "" : "\n* — номинал по автоподбору")
        };
    }

    static Symbol DeviceSymbol(DeviceInfo d) => d.Cat switch
    {
        BuiltInCategory.OST_LightingFixtures or BuiltInCategory.OST_LightingDevices => Symbol.Lighting,
        BuiltInCategory.OST_ElectricalFixtures => Symbol.Socket,
        BuiltInCategory.OST_ElectricalEquipment => Symbol.Panel,
        _ => Symbol.Power
    };

    static Node AirNode(string key, string title, string sub, Symbol icon, ElementId id, string details) =>
        new() { Key = key, Title = title, Sub = sub, Icon = icon, Accent = AirColor, Floating = true, Select = [id], BoxW = 160, BoxH = 40, Details = details,
            Status = icon == Symbol.Panel ? null : C(EomColors.Unconnected), Group = icon == Symbol.Panel ? null : C(EomColors.NoGroup) };

    /// <summary>Перечитывает сеть и перестраивает схему. Вызывать в контексте Revit API.</summary>
    internal void Refresh(Document doc)
    {
        var settings = EomStore.Settings(doc);
        var catalog = EomCatalog.Load();
        var net = Network.Read(doc, settings);
        var inPanels = net.Panels.SelectMany(p => p.Circuits).Select(c => c.Id).ToHashSet();
        var visited = new HashSet<ElementId>();
        // Тот же порядок, что у индикаторов на плане: индекс группы в Network.AllCircuits.
        groupIndex = net.AllCircuits.Select((c, i) => (c.Id, i)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().i);

        var sourceNode = new Node
        {
            Key = "S", Title = "Источник", Sub = "ТП / внешняя сеть", Accent = SourceColor, Icon = Symbol.Source, BoxW = 150,
            Details = "Условный источник питания (трансформаторная подстанция или внешняя сеть). К нему подключены щиты, у которых нет питающей группы в модели — обычно ВРУ."
        };
        air = [];
        foreach (var p in net.Panels.Where(p => p.FeedCircuit == null || !inPanels.Contains(p.FeedCircuit))
                     .OrderByDescending(p => p.Name.Contains("ВРУ", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("ГРЩ", StringComparison.OrdinalIgnoreCase)))
        {
            if (p.Circuits.Count == 0 && p.FeedCircuit == null)
                air.Add(AirNode("P" + p.Id.Value, p.Name, "Щит без групп", Symbol.Panel, p.Id, $"Щит {p.Name} ({p.Type}) не подключён: нет питающей группы и нет отходящих групп."));
            else sourceNode.Children.Add(PanelNode(p, net, settings, catalog, visited));
        }
        foreach (var p in net.Panels.Where(p => !visited.Contains(p.Id) && !air.Any(a => a.Select[0] == p.Id)))
            sourceNode.Children.Add(PanelNode(p, net, settings, catalog, visited));
        foreach (var c in net.Orphans)
        {
            var n = CircuitNode(c, settings, catalog, groupIndex); n.Floating = true; n.Accent = AirColor;
            n.Details = "Группа без щита. " + n.Details; air.Add(n);
        }
        foreach (var d in net.Unconnected)
            air.Add(AirNode("D" + d.Id.Value, d.Name.Split(':')[0], d.Category, DeviceSymbol(d), d.Id, $"Не подключено: {d.Category} — {d.Name} (ID {d.Id.Value}).\nПодключите командой «Подключить к щиту»."));

        tree = sourceNode;
        Redraw();
        status.Text = $"{doc.Title}: щитов {net.Panels.Count}, групп {net.AllCircuits.Count()}, не подключено {net.Unconnected.Count + net.Orphans.Count}. Обновлено {DateTime.Now:HH:mm}.";
        selected = null;
    }

    // ---------- раскладка ----------
    // Дерево идёт сверху вниз. Down — дети в ряд под блоком (источник → щиты верхнего уровня).
    // Column — щит: под ним компактный столбец автоматов, ниже столбца в ряд — щиты, питающиеся от его автоматов.
    // Провод к нижестоящему щиту выходит из питающего автомата вправо, по своей вертикали опускается под столбец и идёт к щиту.

    const double Lane = 6, Drop = 18;

    bool Open(Node n) => n.Children.Count > 0 && !collapsed.Contains(n.Key);

    /// <summary>Автоматы щита, у которых показаны нижестоящие щиты (сверху вниз), и сами эти щиты — слева направо в обратном порядке, чтобы провода не пересекались.</summary>
    List<Node> Feeders(Node panel) => panel.Children.Where(Open).ToList();
    List<Node> Subs(Node panel) => Enumerable.Reverse(Feeders(panel)).SelectMany(c => c.Children).ToList();

    void Measure(Node n)
    {
        n.W = n.BoxW; n.H = n.BoxH;
        if (!Open(n)) return;
        if (n.Layout == Layout.Down)
        {
            foreach (var c in n.Children) Measure(c);
            n.W = Math.Max(n.BoxW, n.Children.Sum(c => c.W) + HGap * (n.Children.Count - 1));
            n.H = n.BoxH + VGap + n.Children.Max(c => c.H);
            return;
        }
        // Column
        double colW = n.Children.Max(c => c.BoxW), colH = n.Children.Sum(c => c.BoxH) + RowGap * (n.Children.Count - 1);
        int lanes = Feeders(n).Count; var subs = Subs(n);
        foreach (var s in subs) Measure(s);
        double subsW = subs.Count == 0 ? 0 : subs.Sum(s => s.W) + HGap * (subs.Count - 1);
        n.W = Math.Max(Math.Max(n.BoxW, Indent + colW + 8 + lanes * Lane), subsW);
        n.H = n.BoxH + 10 + colH + (subs.Count == 0 ? 0 : 12 + lanes * Lane + Drop + subs.Max(s => s.H));
    }

    /// <summary>X, Y — левый верхний угол блока.</summary>
    void Place(Node n, double left, double top)
    {
        n.Y = top;
        n.X = n.Layout == Layout.Down ? left + (n.W - n.BoxW) / 2 : left;
        if (!Open(n)) return;
        if (n.Layout == Layout.Down)
        {
            double x = left + (n.W - (n.Children.Sum(c => c.W) + HGap * (n.Children.Count - 1))) / 2;
            foreach (var c in n.Children) { Place(c, x, top + n.BoxH + VGap); x += c.W + HGap; }
            return;
        }
        double y = top + n.BoxH + 10;
        foreach (var c in n.Children) { c.X = left + Indent; c.Y = y; c.W = c.BoxW; c.H = c.BoxH; y += c.BoxH + RowGap; }
        var subs = Subs(n);
        if (subs.Count == 0) return;
        double subTop = y - RowGap + 12 + Feeders(n).Count * Lane + Drop, sx = left;
        foreach (var s in subs) { Place(s, sx, subTop); sx += s.W + HGap; }
    }

    // ---------- отрисовка ----------

    void Redraw()
    {
        canvas.Children.Clear();
        if (tree == null) return;
        Measure(tree); Place(tree, 20, 20);
        double width = Math.Max(tree.W + 40, 900), y = tree.Y + tree.H + 40;
        DrawTree(tree);
        if (air.Count > 0)
        {
            bool open = !collapsed.Contains("AIR");
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(Toggle("AIR", open, air.Count));
            title.Children.Add(new TextBlock { Text = $"Не подключено — «в воздухе» ({air.Count})", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
            Canvas.SetLeft(title, 20); Canvas.SetTop(title, y); canvas.Children.Add(title);
            y += 28;
            if (open)
            {
                double x = 20;
                foreach (var n in air)
                {
                    if (x + n.BoxW > width - 20) { x = 20; y += n.BoxH + 16; }
                    n.X = x; n.Y = y; Box(n, false); x += n.BoxW + 16;
                }
                y += 56;
            }
        }
        canvas.Width = width; canvas.Height = y + 20;
        if (selected != null) foreach (var n in All(tree).Concat(air)) if (n.Key == selected.Key && n.Frame != null) { n.Frame.StrokeThickness = 3; selected = n; }
    }

    void DrawTree(Node n)
    {
        Box(n, n.Children.Count > 0);
        if (!Open(n)) return;
        if (n.Layout == Layout.Down)
        {
            double cx = n.X + n.BoxW / 2, busY = n.Y + n.BoxH + VGap / 2;
            Wire(cx, n.Y + n.BoxH, cx, busY);
            Wire(n.Children.Min(c => c.X + c.BoxW / 2), busY, n.Children.Max(c => c.X + c.BoxW / 2), busY);
            foreach (var c in n.Children) { Wire(c.X + c.BoxW / 2, busY, c.X + c.BoxW / 2, c.Y); DrawTree(c); }
            return;
        }
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
            var targets = f.Children.Select(s => s.X + s.BoxW / 2).Append(lane).ToList();
            Wire(targets.Min(), bus, targets.Max(), bus);
            foreach (var s in f.Children) { Wire(s.X + s.BoxW / 2, bus, s.X + s.BoxW / 2, s.Y); DrawTree(s); }
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
        button.MouseLeftButtonDown += (_, e) => { if (!collapsed.Remove(key)) collapsed.Add(key); Redraw(); e.Handled = true; };
        return button;
    }

    void Box(Node n, bool toggle)
    {
        var accent = new SolidColorBrush(n.Accent);
        bool compact = n.BoxH < 30;
        var grid = new Grid { Width = n.BoxW, Height = n.BoxH, Cursor = Cursors.Hand, ToolTip = n.Details };
        var frame = new Rectangle { RadiusX = 3, RadiusY = 3, Stroke = accent, StrokeThickness = 1.4 };
        frame.SetResourceReference(Shape.FillProperty, "BP.Panel");
        if (n.Floating) { frame.StrokeDashArray = [4, 3]; grid.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 5, Opacity = 0.35 }; }
        grid.Children.Add(frame);
        var row = new DockPanel { Margin = new Thickness(5, 0, 4, 0), LastChildFill = true };
        var icon = Glyph(n.Icon, accent); icon.VerticalAlignment = VerticalAlignment.Center; icon.Margin = new Thickness(0, 0, 5, 0);
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        if (toggle)
        {
            bool open = !collapsed.Contains(n.Key);
            var t = Toggle(n.Key, open, n.Count); t.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(t, Dock.Right); row.Children.Add(t);
        }
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
        grid.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && n.Children.Count > 0) PickBranch(n); else Pick(n); e.Handled = true; };
        Canvas.SetLeft(grid, n.X); Canvas.SetTop(grid, n.Y);
        canvas.Children.Add(grid);
    }

    /// <summary>Двойной клик по щиту: выделить щит и всё, что от него питается — приборы его групп и нижестоящие щиты со своими приборами.</summary>
    void PickBranch(Node n)
    {
        var ids = All(n).SelectMany(x => x.Select).Distinct().ToArray();
        Pick(n, ids);
        int panels = All(n).Count(x => x.Icon is Symbol.Panel or Symbol.Vru && x.Layout == Layout.Column) - 1, groups = All(n).Count(x => x.Key.StartsWith('C'));
        details.Text = $"Выделено всё, что питается от «{n.Title}»: {ids.Length} элементов — групп {groups}, нижестоящих щитов {panels}.\n\n" + n.Details;
    }

    void Pick(Node n) => Pick(n, n.Select);

    void Pick(Node n, ElementId[] select)
    {
        if (selected?.Frame is { } old) old.StrokeThickness = 1.4;
        selected = n;
        if (n.Frame != null) n.Frame.StrokeThickness = 3;
        details.Text = n.Details;
        if (select.Length == 0) return;
        var ids = select.ToList();
        App.Queue?.Post(app =>
        {
            if (app.ActiveUIDocument is not { } ui) return;
            var existing = ids.Where(id => ui.Document.GetElement(id) != null).ToList();
            if (existing.Count == 0) return;
            ui.Selection.SetElementIds(existing);
            try { ui.ShowElements(existing); } catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
        });
    }
}
