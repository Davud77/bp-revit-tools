using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ComboBox = System.Windows.Controls.ComboBox;

namespace BotPlus.Eom;

/// <summary>
/// Выбор способа прокладки в строке параметров Revit, справа от штатных полей «Диаметр», «Отметка», «Применить».
/// Показывается, пока активна команда «Разместить короб» или «Разместить кабельный лоток» (с ленты ЭОМ или штатно).
/// У Revit нет API для строки параметров: полоса — отдельное окно, привязанное к её положению (как у марок BotPlus RT).
/// </summary>
internal static class RouteOptionsBar
{
    /// <summary>Заголовок строки параметров: «Изменить | Разместить Короб», «… Кабельный лоток», в английской версии — Place Conduit / Cable Tray.</summary>
    static bool IsRouteTool(string? text) => text != null &&
        (text.Contains("Разместить", StringComparison.OrdinalIgnoreCase) && (text.Contains("короб", StringComparison.OrdinalIgnoreCase) || text.Contains("лоток", StringComparison.OrdinalIgnoreCase))
         || text.Contains("Place Conduit", StringComparison.OrdinalIgnoreCase) || text.Contains("Place Cable Tray", StringComparison.OrdinalIgnoreCase));
    static DispatcherTimer? timer;
    static FrameworkElement? anchor;
    static Window? window;
    static ComboBox? combo;
    static IntPtr owner;

    internal static void Start(IntPtr mainWindow)
    {
        owner = mainWindow;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { try { Tick(); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); Hide(); } };
        timer.Start();
    }

    internal static void Stop()
    {
        timer?.Stop(); timer = null;
        window?.Close(); window = null;
    }

    static void Sync() { if (combo != null && (string?)combo.SelectedItem != RouteService.Current) combo.SelectedItem = RouteService.Current; }

    static FrameworkElement? Anchor()
    {
        if (anchor != null) return anchor;
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UIFramework.MainWindow")).FirstOrDefault(t => t != null);
        var main = type?.GetMethod("getMainWnd", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        anchor = main == null ? null : type!.GetField("m_optionBar", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main) as FrameworkElement;
        if (anchor == null) { timer?.Stop(); System.Diagnostics.Trace.WriteLine("BotPlus ЭОМ: строка параметров Revit не найдена."); }
        return anchor;
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int max);

    /// <summary>Элементы строки параметров в экранных пикселях: WPF-надписи и поля, а также окна Win32 (поля «Диаметр», «Отметка» — нативные).</summary>
    static List<(string Kind, string Text, Rect Bounds)> Items(FrameworkElement bar)
    {
        var items = new List<(string, string, Rect)>();
        foreach (var e in Descendants(bar).OfType<FrameworkElement>().Where(e => e.IsVisible && e.ActualWidth > 0))
        {
            if (e is HwndHost host && host.Handle != IntPtr.Zero)
            {
                EnumChildWindows(host.Handle, (h, _) =>
                {
                    if (!IsWindowVisible(h) || !GetWindowRect(h, out var r) || r.Right <= r.Left) return true;
                    var text = new System.Text.StringBuilder(256); GetWindowText(h, text, 256);
                    var cls = new System.Text.StringBuilder(64); GetClassName(h, cls, 64);
                    items.Add(("win32:" + cls, text.ToString(), new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)));
                    return true;
                }, IntPtr.Zero);
                continue;
            }
            string? label = e switch { TextBlock t => t.Text, ContentControl { Content: string c } => c, _ => null };
            if (label == null && e is not Control) continue;
            try
            {
                Point a = e.PointToScreen(new Point(0, 0)), b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
                items.Add((e.GetType().Name, label ?? "", new Rect(a, b)));
            }
            catch (InvalidOperationException) { }
        }
        // Нативные поля могут быть не внутри WPF-строки, а дочерними окнами главного окна поверх неё: берём те, что лежат в её полосе.
        if (!items.Any(i => i.Item1.StartsWith("win32:")) && owner != IntPtr.Zero)
        {
            Point a = bar.PointToScreen(new Point(0, 0)), b = bar.PointToScreen(new Point(bar.ActualWidth, bar.ActualHeight));
            var band = new Rect(a, b);
            EnumChildWindows(owner, (h, _) =>
            {
                if (!IsWindowVisible(h) || !GetWindowRect(h, out var r) || r.Right <= r.Left) return true;
                var rect = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                if (rect.Top < band.Top - 2 || rect.Bottom > band.Bottom + 2 || !rect.IntersectsWith(band)) return true;
                var text = new System.Text.StringBuilder(256); GetWindowText(h, text, 256);
                var cls = new System.Text.StringBuilder(64); GetClassName(h, cls, 64);
                items.Add(("win32:" + cls, text.ToString(), rect));
                return true;
            }, IntPtr.Zero);
        }
        return items;
    }

    static string? lastDump; static int dumps;
    /// <summary>Диагностика: состав строки параметров при каждом новом наборе надписей пишется в %TEMP%\BotPlusEom-optionbar.txt.</summary>
    static void Dump(FrameworkElement bar, List<(string Kind, string Text, Rect Bounds)> items, bool matched)
    {
        var signature = string.Join("|", items.Select(i => i.Text));
        if (signature == lastDump || dumps > 30) return;
        lastDump = signature; dumps++;
        try
        {
            var origin = bar.PointToScreen(new Point(0, 0));
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "BotPlusEom-optionbar.txt"),
                $"{DateTime.Now:HH:mm:ss} bar={origin} {bar.ActualWidth}x{bar.ActualHeight} matched={matched}\n" +
                string.Join("\n", items.Select(i => $"  {i.Kind} '{i.Text}' {i.Bounds}")) + "\n");
        }
        catch (IOException) { }
    }

    static void Tick()
    {
        var bar = Anchor();
        if (bar == null || !bar.IsVisible || bar.ActualWidth < 1) { Hide(); return; }
        var items = Items(bar);
        bool matched = items.Any(i => IsRouteTool(i.Text));
        Dump(bar, items, matched);
        if (!matched) { Hide(); return; }
        // Правый край штатных полей; растянутые на всю строку контейнеры не учитываем.
        var origin = bar.PointToScreen(new Point(0, 0)); var far = bar.PointToScreen(new Point(bar.ActualWidth, bar.ActualHeight));
        double width = far.X - origin.X, right = origin.X;
        foreach (var (_, _, r) in items) if (r.Width < width * 0.8 && r.Right <= far.X + 1) right = Math.Max(right, r.Right);
        Show(origin, far.Y - origin.Y, right + 12);
    }

    static void Create()
    {
        combo = new ComboBox
        {
            ItemsSource = WiringSymbols.Methods.Prepend(WiringSymbols.Auto).Append(WiringSymbols.None).ToList(),
            SelectedItem = RouteService.Current, Width = 230, Margin = new Thickness(4, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Способ прокладки по ГОСТ 21.210: обозначение проводки, которое ставится вдоль новой трассы на плане."
        };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s) { RouteService.Current = s; } };
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text = "Прокладка (ГОСТ 21.210):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) });
        content.Children.Add(combo);
        window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Background = SystemColors.ControlBrush, Foreground = SystemColors.ControlTextBrush, Content = content,
            SizeToContent = SizeToContent.Width, Height = 25, FontSize = 12, Topmost = false
        };
        new WindowInteropHelper(window) { Owner = owner };
        BotPlusTheme.Apply(window);
    }

    /// <summary>Положение в экранных пикселях: верх строки параметров и левый край полосы.</summary>
    static void Show(Point origin, double height, double left)
    {
        if (window == null) Create();
        Sync();
        if (!window!.IsVisible) window.Show();
        if (PresentationSource.FromVisual(window)?.CompositionTarget is not { } target) return;
        var dip = target.TransformFromDevice.Transform(new Point(left, origin.Y));
        window.Left = dip.X; window.Top = dip.Y; window.Height = target.TransformFromDevice.Transform(new Vector(0, height)).Y;
    }

    static void Hide() { if (window?.IsVisible == true) window.Hide(); }
}
