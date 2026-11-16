using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotPlus.Shared;
using HeatLossViewer.Mep;

internal sealed class FakeModel : IUpdaterModel
{
    public IReadOnlyList<(string? Mark, List<string> Types)> Marks(string category) => category switch
    {
        "OST_PipeCurves" => [(null, ["Трубы: Стандартный"]), ("Днар x Стенка", ["Трубы: Сталь ГОСТ 10704", "Трубы: Сталь ГОСТ 3262"]), ("игнорировать", ["Трубы: Служебная"])],
        "OST_DuctFitting" => [("Фасонные", ["Отвод: Стандарт", "Тройник: Стандарт"])],
        _ => []
    };
    public bool HasSample(string category, string? mark) => category == "OST_PipeCurves" && mark == "Днар x Стенка" || category == "OST_DuctFitting";
    public IReadOnlyList<string> Parameters(string category, string? mark) => ["ADSK_Наименование", "ADSK_Количество", "Внешний диаметр", "Длина", "Комментарии к типоразмеру"];
    public IReadOnlyList<string> TextTargets(string category, string? mark) => ["ADSK_Наименование", "Комментарии"];
    public IReadOnlyList<string> NumberTargets(string category, string? mark) => ["ADSK_Количество"];
    public string? Text(string category, string? mark, string parameter, WallThicknessSettings walls) => parameter switch
    {
        "Комментарии к типоразмеру" => "Труба стальная электросварная ГОСТ 10704-91",
        "Внешний диаметр" => "57", "@Толщина стенки" => "3,5", "@Тип" => "Сталь ГОСТ 10704", "@Размер фитинга" => "500×300-400×200", _ => null
    };
    public (double? SizeMm, IReadOnlyList<string> Systems) ReserveContext(string category, string? mark) =>
        category == "OST_PipeCurves" ? (50, ["ADSK_Канализация_К1"]) : (null, []);
    public IReadOnlyList<(string Type, SystemDomain Domain)> SystemTypes() =>
        [("ADSK_Водопровод_В1", SystemDomain.Pipe), ("ADSK_Водопровод_Т3", SystemDomain.Pipe), ("ADSK_Водопровод_Т4", SystemDomain.Pipe),
         ("ADSK_Канализация_К1", SystemDomain.Pipe), ("ADSK_Пожаротушение водяное_В21", SystemDomain.Pipe), ("Прочие трубопроводы", SystemDomain.Pipe), ("Приточный воздух", SystemDomain.Duct)];
    public (SourceKind Kind, double Value)? Source(string category, string? mark, string parameter) => parameter switch
    {
        "Длина" => (SourceKind.Length, 2.0), "@Штук" => (SourceKind.Count, 1), "ADSK_Размер_Площадь" => (SourceKind.Area, 0.42), _ => null
    };
}

internal static class Program
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var x in All<T>(child)) yield return x;
        }
    }

    static List<string?> buttons0(Window w) => All<Button>(w).Select(b => b.Content as string).ToList();

    [STAThread] static void Main(string[] args)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (bool dark in new[] { false, true })
        {
            BotPlusTheme.Override = dark;
            var rules = new EngineeringRuleSet();
            rules.Put(new EngineeringRule
            {
                Category = "OST_PipeCurves", TypeMark = "Днар x Стенка", NameEnabled = true,
                Parts = [new() { Parameter = "Комментарии к типоразмеру" }, new() { Parameter = "Внешний диаметр", Prefix = " ø" }, new() { Parameter = "@Толщина стенки", Prefix = "x" }],
                QuantityEnabled = true, QuantitySource = "Длина", Unit = QuantityUnit.Meter, Reserve = 1.1, Decimals = 2,
                ReserveBySize = [new() { Systems = ["К1"], MaxSizeMm = 50, Reserve = 1.15 }, new() { Systems = ["К1"], MaxSizeMm = 110, Reserve = 1.1 }]
            });
            var window = new EngineeringRulesWindow(new FakeModel(), rules, "OST_PipeCurves", "Днар x Стенка") { ShowActivated = false, ShowInTaskbar = false };
            window.Show(); window.UpdateLayout();
            var combos = All<ComboBox>(window).ToList();
            var scope = combos.First(c => c.ItemsSource is IEnumerable<ScopeChoice>);
            var labels = ((IEnumerable<ScopeChoice>)scope.ItemsSource).Select(s => s.ToString()).ToList();
            Check(labels.Contains("● Трубы : Днар x Стенка") && labels.Contains("Трубы : Без типа") && labels.Contains("Соединительные детали воздуховодов : Фасонные"), "Scopes by category and type mark");
            Check(!labels.Any(l => l.Contains("игнорировать")), "«игнорировать» is not offered");
            var texts = All<TextBlock>(window).Select(t => t.Text).ToList();
            Check(texts.Contains("Труба стальная электросварная ГОСТ 10704-91 ø57x3,5"), "Name example");
            Check(texts.Contains("2,3 м (запас 15 %, размер 50 мм)"), "Quantity example uses the size/system reserve");
            Check(buttons0(window).Contains("Запас по размеру… (2)"), "Reserve button shows the number of rows");
            var buttons = All<Button>(window).Select(b => b.Content as string).ToList();
            Check(buttons.Contains("Сохранить настройки и обновить все элементы выбранного типа") && buttons.Contains("Сохранить настройки и закрыть") && buttons.Contains("Отменить и закрыть"), "Three BIM2B buttons");
            Check(buttons.Contains("Импорт…") && buttons.Contains("Экспорт…") && buttons.Contains("Толщина стенки…") && buttons.Contains("Имена систем…"), "Service buttons");
            var lists = All<ListBox>(window).ToList();
            Check(lists.Count == 2 && lists[0].Items.Cast<ListBoxItem>().Any(i => (string)i.Content == "Толщина стенки"), "Built-in parameters in the list");
            var root = (FrameworkElement)window.Content;
            Check(root.DesiredSize.Width <= window.ActualWidth + 1 && root.DesiredSize.Height <= window.ActualHeight + 1, "Clipped window");
            if (args.Length > 0)
            {
                Directory.CreateDirectory(args[0]);
                var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(args[0], dark ? "updater-dark.png" : "updater-light.png")); encoder.Save(stream);
            }
            // Switching to a fitting scope loads the BIM2B defaults: ADSK_Размер_Площадь in m².
            scope.SelectedItem = ((IEnumerable<ScopeChoice>)scope.ItemsSource).First(s => s.Category == "OST_DuctFitting");
            window.UpdateLayout();
            Check(All<TextBlock>(window).Any(t => t.Text.StartsWith("0,47 м² (запас 10 %)")), "Fitting quantity from ADSK_Размер_Площадь with the default 10 %");
            Check(window.Rules.Find("OST_PipeCurves", "Днар x Стенка") is { IsActive: true }, "Rule kept when switching scope");
            window.Close();
            Check(rules.Rules.Count == 1, "Window works on a copy");
        }
        foreach (bool dark in new[] { false, true })
        {
            BotPlusTheme.Override = dark;
            var reserve = new ReserveWindow([new() { Systems = ["К1", "К2"], MaxSizeMm = 50, Reserve = 1.15 }, new() { Systems = ["К1", "К2"], MaxSizeMm = 110, Reserve = 1.1 }], 1.1)
                { ShowActivated = false, ShowInTaskbar = false };
            reserve.Show(); reserve.UpdateLayout();
            Check(All<DataGrid>(reserve).Single().Items.Count == 2, "Reserve rows loaded");
            if (args.Length > 0)
            {
                var image = new RenderTargetBitmap((int)Math.Ceiling(reserve.ActualWidth), (int)Math.Ceiling(reserve.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(reserve);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(args[0], dark ? "reserve-dark.png" : "reserve-light.png")); encoder.Save(stream);
            }
            reserve.Close();
            var systems = new SystemNamesWindow(new SystemNameSettings { Enabled = true }, new FakeModel().SystemTypes()) { ShowActivated = false, ShowInTaskbar = false };
            systems.Show(); systems.UpdateLayout();
            var rows = systems.PreviewRows;
            Check(rows.First(r => r.Type == "ADSK_Водопровод_Т3").Name == rows.First(r => r.Type == "ADSK_Водопровод_Т4").Name, "Preview: Т3/Т4 share the name");
            Check(rows.First(r => r.Type == "Прочие трубопроводы").Name.StartsWith("—") && rows.First(r => r.Type == "Приточный воздух").Name.StartsWith("—"), "Preview shows uncovered systems");
            if (args.Length > 0)
            {
                var image = new RenderTargetBitmap((int)Math.Ceiling(systems.ActualWidth), (int)Math.Ceiling(systems.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(systems);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(args[0], dark ? "systems-dark.png" : "systems-light.png")); encoder.Save(stream);
            }
            systems.Close();
        }
        foreach (bool dark in new[] { false, true })
        {
            BotPlusTheme.Override = dark;
            var stored = new InsulationSettings();
            stored.Put("ADSK_Водопровод_Т3", "Труба стальная водогазопроводная Ø20х2,8", "K-Flex ST 13", 13);
            var insulation = new InsulationWindow(new FakeInsulationModel(), stored.Clone(), ["ADSK_Водопровод_Т3"]) { ShowActivated = false, ShowInTaskbar = false };
            var warnings = new List<string>(); insulation.Warn = warnings.Add;
            insulation.Show(); insulation.UpdateLayout();
            Check(insulation.Rows.Count == 3 && insulation.Rows[0].Type == "K-Flex ST 13" && insulation.Rows[0].Insulated == "4 из 12" && insulation.Rows[1].Type == InsulationPolicy.NoInsulation, "Pipes of the system with stored rule");
            Check(insulation.Selected.SequenceEqual(["ADSK_Водопровод_Т3"]), "Starts on the selected pipes' systems");
            insulation.SelectSystems("ADSK_Водопровод_Т3", "ADSK_Водопровод_В1"); insulation.UpdateLayout();
            Check(insulation.Rows.Count == 4 && insulation.Rows.First(r => r.Name.StartsWith("Труба PP-R PN20 Ø25")).Count == 25, "Several systems: pipes merged by name");
            Check(insulation.Rows.First(r => r.Name.Contains("Ø20х2,8")).Type == InsulationPolicy.Mixed, "Different rules across systems shown as mixed");
            if (args.Length > 0)
            {
                var image = new RenderTargetBitmap((int)Math.Ceiling(insulation.ActualWidth), (int)Math.Ceiling(insulation.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(insulation);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(args[0], dark ? "insulation-dark.png" : "insulation-light.png")); encoder.Save(stream);
            }
            insulation.SetAll("Energoflex Super 9", "9");
            Check(insulation.Rows.All(r => r.Type == "Energoflex Super 9" && r.Thickness == "9"), "«Задать всем» fills every pipe");
            insulation.SelectSystem("ADSK_Канализация_К1");
            Check(insulation.Rows.Count == 0, "System without pipes");
            insulation.SelectSystem("ADSK_Водопровод_В1");
            Check(insulation.Rows.Count == 2 && insulation.Rows.All(r => r.Type == "Energoflex Super 9"), "Rows of several systems written to each of them");
            insulation.Rows[0].Thickness = "abc";
            insulation.SelectSystem("");
            Check(insulation.Rows.Count == 2 && warnings.Count == 1 && insulation.Selected.SequenceEqual(["ADSK_Водопровод_В1"]), "Invalid thickness keeps the systems: switching refused");
            insulation.Rows[0].Thickness = "19";
            insulation.SelectSystem("");
            Check(insulation.Rows.Count == 4 && insulation.Selected.SequenceEqual([""]), "«Все системы» lists every pipe of the project");
            Check(insulation.Accept() && insulation.Result is { } saved
                && saved.Resolve("ADSK_Водопровод_Т3", "Труба стальная водогазопроводная Ø20х2,8")!.InsulationType == "Energoflex Super 9"
                && saved.Resolve("ADSK_Водопровод_В1", "Труба PP-R PN20 Ø25")!.ThicknessMm == 19
                && saved.Resolve("ADSK_Водопровод_Т3", "Труба PP-R PN20 Ø25")!.ThicknessMm == 9, "OK saves rules of every visited system");
            Check(stored.Rules.Count == 1 && stored.Rules[0].InsulationType == "K-Flex ST 13", "Window works on a copy");
            CheckBox Box(string text) => All<CheckBox>(insulation).First(b => (b.Content as TextBlock)?.Text == text);
            Box("ADSK_Водопровод_В1").IsChecked = true; Box("ADSK_Водопровод_Т3").IsChecked = true;
            Check(insulation.Selected.SequenceEqual(["ADSK_Водопровод_В1", "ADSK_Водопровод_Т3"]) && Box(InsulationPolicy.AllSystemsLabel).IsChecked == false, "Checking systems replaces «all systems»");
            Box(InsulationPolicy.AllSystemsLabel).IsChecked = true;
            Check(insulation.Selected.SequenceEqual([""]) && Box("ADSK_Водопровод_В1").IsChecked == false, "«All systems» clears the others");
            insulation.Close();
        }
        if (args.Length > 0)
            foreach (int size in new[] { 32, 16 })
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 32, 32)); Check(BotPlus.RT.MepIcons.Draw(dc, "OVMepInsulation"), "Insulation icon"); }
                var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32); bmp.Render(visual);
                BitmapSource src = size == 32 ? bmp : new TransformedBitmap(bmp, new ScaleTransform(0.5, 0.5));
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(src));
                using var s = File.Create(Path.Combine(args[0], $"insulation-icon-{size}.png")); enc.Save(s);
            }
        app.Shutdown();
        Console.WriteLine("PASS updater window: scopes by type mark, ignore mark hidden, name/quantity examples, built-in parameters, BIM2B buttons, system names preview, pipe insulation window, light/dark layout");
    }
}

internal sealed class FakeInsulationModel : IInsulationModel
{
    static readonly Dictionary<string, PipeGroup[]> Data = new()
    {
        ["ADSK_Водопровод_Т3"] = [new("Труба стальная водогазопроводная Ø20х2,8", 12, "20", 4), new("Труба стальная водогазопроводная Ø32х3,2", 5, "32", 0), new("Труба PP-R PN20 Ø25", 7, "25", 0)],
        ["ADSK_Водопровод_В1"] = [new("Труба PP-R PN20 Ø25", 18, "25", 0), new("Труба PP-R PN20 Ø32", 6, "32", 0)],
    };
    public IReadOnlyList<string> Systems() => ["ADSK_Водопровод_В1", "ADSK_Водопровод_Т3", "ADSK_Канализация_К1"];
    public IReadOnlyList<PipeGroup> Pipes(IReadOnlyCollection<string> systems) =>
        Data.Where(p => systems.Contains("") || systems.Contains(p.Key)).SelectMany(p => p.Value).GroupBy(g => g.Name)
            .Select(g => new PipeGroup(g.Key, g.Sum(x => x.Count), g.First().Sizes, g.Sum(x => x.Insulated))).OrderBy(g => g.Name).ToList();
    public IReadOnlyList<string> InsulationTypes() => ["Energoflex Super 9", "K-Flex ST 13", "K-Flex ST 19"];
}


namespace Autodesk.Revit.UI
{
    public enum UITheme { Light, Dark }
    public static class UIThemeManager { public static UITheme CurrentTheme => UITheme.Light; }
}
