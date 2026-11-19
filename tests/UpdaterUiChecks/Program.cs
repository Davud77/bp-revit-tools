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
            var rules = new EngineeringRuleSet { PipeNameDefaultsVersion = 1, PipeQuantityDefaultsVersion = 1, EnabledDefaultsVersion = 1 };
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
            var scope = All<ListBox>(window).First(c => c.ItemsSource is IEnumerable<ScopeChoice>);
            var labels = ((IEnumerable<ScopeChoice>)scope.ItemsSource).Select(s => s.ToString()).ToList();
            Check(labels.Contains("● ADSK_Наименование\nТрубы") && labels.Contains("● ADSK_Количество\nТрубы") && labels.Contains("● ADSK_Количество\nСоединительные детали воздуховодов"), "Target above category, two rules per category without mark duplicates");
            Check(!labels.Any(l => l.Contains("игнорировать")), "«игнорировать» is not offered");
            var compactItem = (ListBoxItem)scope.ItemContainerGenerator.ContainerFromItem(scope.Items.Cast<ScopeChoice>().First(s => s.Category == "OST_PipeCurves"));
            Check(compactItem.ActualHeight <= 40, "Compact two-line rule row fits within 40 px");
            Check(scope.Items.Cast<ScopeChoice>().All(s => s.Icon.Width > 0 && s.Icon.Height > 0), "Every category has a visible icon");
            Check(!ReferenceEquals(RuleCategoryIcons.Get("OST_PipeCurves"), RuleCategoryIcons.Get("OST_DuctCurves")) && !ReferenceEquals(RuleCategoryIcons.Get("OST_Conduit"), RuleCategoryIcons.Get("OST_CableTray")), "Pipe, duct, conduit and tray icons differ");
            var texts = All<TextBlock>(window).Select(t => t.Text).ToList();
            Check(texts.Contains("Труба стальная электросварная ГОСТ 10704-91 ø57x3,5"), "Name example");
            Check(texts.Contains("2,2 м (запас 10 %)"), "Quantity example uses selected formula reserve, ignoring legacy size table");
            Check(!buttons0(window).Any(b => b?.StartsWith("Запас по размеру") == true), "No size reserve button");
            var buttons = All<Button>(window).Select(b => b.Content as string).ToList();
            Check(buttons.Contains("Сохранить и обновить") && !buttons.Contains("Сохранить настройки и закрыть") && buttons.Contains("Отменить"), "Only save/update and cancel actions");
            Check(!buttons.Contains("Импорт…") && !buttons.Contains("Экспорт…") && buttons.Contains("Толщина стенки…") && !buttons.Contains("Имена систем…"), "Service buttons: no import/export, system names in the dropdown");
            var lists = All<ListBox>(window).ToList();
            Check(lists.Count == 4 && lists.Any(l => l.Items.OfType<ListBoxItem>().Any(i => (string)i.Content == "Толщина стенки")), "Built-in parameters in the list");
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
            Check(All<DataGrid>(window).Count(g => g.IsVisible) == 1, "Only the selected name rule editor is visible");
            All<CheckBox>(window).First(c => c.IsVisible && c.Content as string == "Заполнять").IsChecked = false;
            window.ShowScope(s => s.Category == "OST_PipeCurves" && s.IsQuantity);
            window.UpdateLayout();
            Check(window.Rules.ConditionalGroups.First(g => g.Category == "OST_PipeCurves" && !g.IsQuantity).Conditions[0].Formula.NameEnabled == false && window.Rules.ConditionalGroups.First(g => g.Category == "OST_PipeCurves" && g.IsQuantity).Conditions[0].Formula.QuantityEnabled, "Name enable flag is independent from quantity");
            Check(All<DataGrid>(window).Count(g => g.IsVisible) == 1 && All<ComboBox>(window).Any(c => c.IsVisible && c.SelectedItem as string == "ADSK_Количество"), "Quantity has its own editor");
            var quantityEditor = All<DataGrid>(window).Single(g => g.IsVisible);
            Check(quantityEditor.Columns[1] is DataGridTextColumn { Header: "Запас, %" }, "Reserve is a text field, not a dropdown");
            quantityEditor.CurrentCell = new DataGridCellInfo(quantityEditor.Items[0], quantityEditor.Columns[1]);
            quantityEditor.BeginEdit(); window.UpdateLayout();
            var reserveInput = All<TextBox>(quantityEditor).Single(t => t.IsVisible);
            reserveInput.Text = "7,5"; reserveInput.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            var typedReserve = (QuantityRow)quantityEditor.Items[0];
            Check(Math.Abs(typedReserve.Reserve - 1.075) < 1e-12 && All<TextBlock>(window).Any(t => t.Text.StartsWith("2,15 м (запас 7,5 %)")), "Typed percentage with comma immediately recalculates quantity");
            foreach (var invalid in new[] { "", "abc", "-1", "901", "NaN", "Infinity" })
            { typedReserve.ReservePercent = invalid; Check(!typedReserve.TryReserve(out _), "Invalid reserve rejected: " + invalid); }
            typedReserve.ReservePercent = "0"; Check(typedReserve.Reserve == 1, "Zero percent means no reserve");
            typedReserve.ReservePercent = "900"; Check(typedReserve.Reserve == 10, "Maximum supported percent");
            typedReserve.ReservePercent = "7.5"; Check(Math.Abs(typedReserve.Reserve - 1.075) < 1e-12, "Decimal point supported");
            reserveInput.Text = "7,5"; reserveInput.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            quantityEditor.CommitEdit(DataGridEditingUnit.Cell, true); quantityEditor.CommitEdit(DataGridEditingUnit.Row, true);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Check(quantityEditor.Columns[1].GetCellContent(quantityEditor.Items[0]) is TextBlock { Text: "7,5" }, "Typed percent remains visible after leaving edit mode");
            if (args.Length > 0)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(args[0], dark ? "quantity-dark.png" : "quantity-light.png")); png.Save(output);
            }
            window.ShowScope(s => s.Category == "OST_PipeCurves" && !s.IsQuantity);
            window.UpdateLayout();
            Check(Math.Abs(window.Rules.ConditionalGroups.First(g => g.Category == "OST_PipeCurves" && g.IsQuantity).Conditions[0].Formula.Reserve - 1.075) < 1e-12, "Typed reserve saved when switching rule");
            Check(All<CheckBox>(window).First(c => c.IsVisible && c.Content as string == "Заполнять").IsChecked == false, "Name state restored after visiting quantity");
            Check(((IEnumerable<ScopeChoice>)scope.ItemsSource).Where(s => s.Category == "OST_PipeCurves").All(s => s.Configured == s.IsQuantity), "Independent configured markers");
            // Switching to a fitting scope loads the BIM2B defaults: ADSK_Размер_Площадь in m².
            scope.SelectedItem = ((IEnumerable<ScopeChoice>)scope.ItemsSource).First(s => s.Category == "OST_DuctFitting" && s.IsQuantity);
            window.UpdateLayout();
            Check(All<TextBlock>(window).Any(t => t.Text.StartsWith("0,47 м² (запас 10 %)")), "Fitting quantity from ADSK_Размер_Площадь with the default 10 %");
            Check(window.Rules.ConditionalGroups.First(g => g.Category == "OST_PipeCurves" && g.IsQuantity).IsActive, "Rule kept when switching category");
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
            var systemsRules = new EngineeringRuleSet(); systemsRules.SystemNames.Enabled = true;
            var systems = new EngineeringRulesWindow(new FakeModel(), systemsRules, "OST_PipeCurves", null) { ShowActivated = false, ShowInTaskbar = false };
            systems.Show(); systems.UpdateLayout();
            Check(!systems.SystemsShown && All<ListBox>(systems).First(c => c.ItemsSource is IEnumerable<ScopeChoice>).Items.Cast<object>().Any(s => s.ToString()!.Contains(EngineeringRulesPolicy.DefaultSystemTarget)), "System names are in the alphabetically sorted rule list");
            Check(!All<Button>(systems).Any(b => b.Content as string == "Имена систем…"), "No separate system names window");
            systems.ShowScope(s => s.IsSystems); systems.UpdateLayout();
            var panel = All<SystemNamesPanel>(systems).Single();
            Check(systems.SystemsShown && panel.Visibility == Visibility.Visible, "System names shown in place of the formulas");
            var rows = panel.PreviewRows;
            Check(rows.First(r => r.Type == "ADSK_Водопровод_Т3").Name == rows.First(r => r.Type == "ADSK_Водопровод_Т4").Name, "Preview: Т3/Т4 share the name");
            Check(rows.First(r => r.Type == "Прочие трубопроводы").Name.StartsWith("—") && rows.First(r => r.Type == "Приточный воздух").Name.StartsWith("—"), "Preview shows uncovered systems");
            if (args.Length > 0)
            {
                var image = new RenderTargetBitmap((int)Math.Ceiling(systems.ActualWidth), (int)Math.Ceiling(systems.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(systems);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(args[0], dark ? "systems-dark.png" : "systems-light.png")); encoder.Save(stream);
            }
            systems.ShowScope(s => s.Category == "OST_PipeCurves"); systems.UpdateLayout();
            Check(!systems.SystemsShown && panel.Visibility == Visibility.Collapsed && systems.Rules.SystemNames.Enabled && systems.Rules.SystemNames.Rules.Count > 5, "Back to formulas; system rules kept");
            systems.Close();
        }
        foreach (bool dark in new[] { false, true })
        {
            BotPlusTheme.Override = dark;
            var conditionalRules = new EngineeringRuleSet { PipeNameDefaultsVersion = 1, PipeQuantityDefaultsVersion = 1, EnabledDefaultsVersion = 1 };
            EngineeringRule Formula(string parameter) => new() { Category = "OST_PipeCurves", NameEnabled = true, Parts = [new() { Parameter = parameter }] };
            var group = new ConditionalRuleGroup
            {
                Category = "OST_PipeCurves", Target = "ADSK_Наименование",
                Conditions = [new() { Parameter = "Комментарии к типоразмеру", Value = "Труба стальная электросварная ГОСТ 10704-91", Formula = Formula("Внешний диаметр") },
                    new() { Parameter = "Комментарии к типоразмеру", Comparison = ConditionComparison.Contains, Value = "водогазопроводная", Formula = Formula("@Тип") }],
                Otherwise = Formula("@Размер")
            };
            conditionalRules.ConditionalGroups.Add(group);
            var editor = new EngineeringRulesWindow(new FakeModel(), conditionalRules, "OST_PipeCurves", null) { ShowActivated = false, ShowInTaskbar = false };
            editor.Show(); editor.UpdateLayout();
            var criterion = All<ComboBox>(editor).First(c => c.IsVisible && c.Items.Contains("Маркировка типоразмера"));
            Check(criterion.SelectedItem as string == "Комментарии к типоразмеру", "Condition parameter visible and selected");
            Check(All<TextBox>(editor).Any(t => t.IsVisible && t.Text == "Труба стальная электросварная ГОСТ 10704-91"), "Type comment equality value editable");
            Check(All<TextBlock>(editor).Any(t => t.Text == "57"), "Example uses first matching conditional formula");
            if (args.Length > 0)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(editor.ActualWidth), (int)Math.Ceiling(editor.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(editor);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(args[0], dark ? "conditions-dark.png" : "conditions-light.png")); png.Save(output);
            }
            var formulaGrid = All<DataGrid>(editor).Single(g => g.IsVisible);
            ((PartRow)formulaGrid.Items[0]).Prefix = "Днар ";
            editor.ShowCondition(1); editor.UpdateLayout();
            Check(editor.Rules.ConditionalGroups[0].Conditions[0].Formula.Parts[0].Prefix == "Днар ", "Branch formula edit saved when switching conditions");
            Check(All<TextBlock>(editor).Any(t => t.Text.StartsWith("Нет элементов для выбранного условия")), "Unmatched condition does not show another branch's sample");
            editor.ShowCondition(-1); editor.UpdateLayout();
            Check(!criterion.IsVisible && ((PartRow)formulaGrid.Items[0]).Parameter == "@Размер", "Fallback has its own formula and no criterion");
            void ClickConditionButton(string label) => All<Button>(editor).First(b => b.IsVisible && b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ClickConditionButton("+ Условие"); editor.UpdateLayout();
            Check(editor.Rules.ConditionalGroups[0].Conditions.Count == 3 && criterion.IsVisible, "Add condition from selected formula");
            criterion.SelectedItem = "Маркировка типоразмера";
            All<TextBox>(editor).First(t => t.IsVisible && t.MinWidth == 180).Text = "Днар x Стенка";
            ClickConditionButton("↑"); editor.UpdateLayout();
            Check(editor.Rules.ConditionalGroups[0].Conditions[1].Parameter == "Маркировка типоразмера", "Move changes condition priority");
            ClickConditionButton("Удалить"); editor.UpdateLayout();
            Check(editor.Rules.ConditionalGroups[0].Conditions.Count == 2, "Delete selected condition");
            editor.ShowScope(s => s.Category == "OST_PipeCurves" && s.IsQuantity); editor.UpdateLayout();
            editor.ShowScope(s => s.Category == "OST_PipeCurves" && !s.IsQuantity); editor.UpdateLayout();
            Check(((PartRow)formulaGrid.Items[0]).Prefix == "Днар ", "Condition formulas survive switching category rule");
            editor.Width = editor.MinWidth; editor.Height = editor.MinHeight; editor.UpdateLayout();
            Check(((FrameworkElement)editor.Content).DesiredSize.Width <= editor.ActualWidth && ((FrameworkElement)editor.Content).DesiredSize.Height <= editor.ActualHeight, "Conditional editor fits minimum window size");
            editor.Close();
            Check(group.Conditions.Count == 2 && group.Conditions[0].Formula.Parts[0].Prefix == "", "Cancel keeps original conditional rules unchanged");
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
            Check(insulation.Rows[0].InsulationName == InsulationPolicy.DefaultNameTemplate && insulation.Title == "Менеджер изоляций", "Default insulation name template, window title");
            insulation.Rows[0].InsulationName = "Изоляция K-Flex ø{Ø}";
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
            Check(insulation.Rows.Count == 0 && !All<CheckBox>(insulation).Any(b => b.Tag as string == "ADSK_Канализация_К1") && All<CheckBox>(insulation).Count(b => b.Tag is string) == 3, "Systems without pipes are not listed");
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
                && saved.Resolve("ADSK_Водопровод_Т3", "Труба PP-R PN20 Ø25")!.ThicknessMm == 9
                && saved.Resolve("ADSK_Водопровод_В1", "Труба PP-R PN20 Ø25")!.NameTemplate == InsulationPolicy.DefaultNameTemplate, "OK saves rules of every visited system");
            Check(stored.Rules.Count == 1 && stored.Rules[0].InsulationType == "K-Flex ST 13", "Window works on a copy");
            CheckBox Box(string tag) => All<CheckBox>(insulation).First(b => b.Tag as string == tag);
            Box("ADSK_Водопровод_В1").IsChecked = true; Box("ADSK_Водопровод_Т3").IsChecked = true;
            Check(insulation.Selected.SequenceEqual(["ADSK_Водопровод_В1", "ADSK_Водопровод_Т3"]) && Box("").IsChecked == false, "Checking systems replaces «all systems»");
            Box("").IsChecked = true;
            Check(insulation.Selected.SequenceEqual([""]) && Box("ADSK_Водопровод_В1").IsChecked == false, "«All systems» clears the others");
            insulation.Close();
        }
        foreach (bool dark in new[] { false, true })
        {
            BotPlusTheme.Override = dark;
            var riser = new RiserNumberingWindow(["ADSK_Номер стояка", "Комментарии"], false) { ShowActivated = false, ShowInTaskbar = false };
            var riserWarnings = new List<string>(); riser.Warn = riserWarnings.Add;
            riser.Show(); riser.UpdateLayout();
            Check(riser.Title == "Нумерация стояков" && riser.ParameterName == "ADSK_Номер стояка" && !riser.SelectedOnly, "Riser window: title, ADSK_Номер стояка by default, no selection option without selection");
            Check(riser.ConsiderSystem, "System type checkbox enabled by default");
            riser.SetGrouping(true, true, "300"); riser.UpdateLayout();
            if (args.Length > 0)
            {
                var image = new RenderTargetBitmap((int)Math.Ceiling(riser.ActualWidth), (int)Math.Ceiling(riser.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(riser);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(args[0], dark ? "risers-dark.png" : "risers-light.png")); encoder.Save(stream);
            }
            riser.SetMode(true); riser.SetValues("ADSK_Номер стояка", "abc", "1500");
            Check(!riser.Accept() && riserWarnings.Count == 1, "Riser window: invalid start refused");
            riser.SetValues("ADSK_Номер стояка", "5", "1,2");
            Check(riser.Accept() && riser.Start == 5 && riser.MinLengthMm == 1.2 && riser.Auto, "Riser window: auto mode values");
            riser.SetMode(false); riser.SetValues("ADSK_Номер стояка", "abc", "1500");
            Check(riser.Accept() && !riser.Auto && riser.MinLengthMm == 1500, "Riser window: copy mode ignores the start number");
            riserWarnings.Clear(); riser.SetGrouping(false, true, "0");
            Check(!riser.Accept() && riserWarnings.Count == 1, "Neighbors require a positive distance");
            riser.SetGrouping(false, true, "250,5");
            Check(riser.Accept() && !riser.ConsiderSystem && riser.ConsiderNeighbors && riser.NeighborDistanceMm == 250.5, "Grouping controls accept Russian decimal distance");
            riser.SetGrouping(true, false, "not a number");
            Check(riser.Accept() && riser.ConsiderSystem && !riser.ConsiderNeighbors, "Disabled neighbor distance ignored");
            riser.SetGrouping(true, false, "300"); riser.Accept();
            riser.Close();
        }
        foreach (bool dark in new[] { false, true })
        {
            BotPlusTheme.Override = dark;
            var source = new EngineeringRuleSet { Rules = EngineeringRuleSet.DefaultRules() };
            var manager = new EngineeringRulesWindow(new FakeModel(), source, "OST_PipeCurves", null) { ShowActivated = false, ShowInTaskbar = false };
            manager.Show(); manager.UpdateLayout();
            var navigation = All<ListBox>(manager).Single(l => l.ItemsSource is IEnumerable<ScopeChoice>);
            void CheckOrder()
            {
                var entries = navigation.Items.Cast<ScopeChoice>().ToList();
                Check(entries.SequenceEqual(entries.OrderBy(s => s.Target, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.CategoryLabel, StringComparer.CurrentCultureIgnoreCase)), "Rules sorted by target then category, independent of configured dot: " + string.Join(" | ", entries.Select(s => s.Target + ":" + s.CategoryLabel)));
            }
            CheckOrder();
            Check(new[] { "OST_DuctFitting", "OST_PipeFitting" }.All(c => navigation.Items.Cast<ScopeChoice>().Count(s => s.Category == c) == 2), "Both fitting categories offered even without model samples");
            Check(All<ComboBox>(manager).Any(c => c.IsVisible && c.SelectedItem as string == "Комментарии к типоразмеру"), "Requested pipe criterion configured on opening");
            var pipeParts = All<DataGrid>(manager).Single(g => g.IsVisible).Items.Cast<PartRow>().ToList();
            Check(pipeParts.Select(p => p.Parameter).SequenceEqual(new[] { "Комментарии к типоразмеру", "Внешний диаметр", "@Толщина стенки" }), "Steel name formula loaded");
            manager.ShowCondition(-1); manager.UpdateLayout();
            Check(All<DataGrid>(manager).Single(g => g.IsVisible).Items.Cast<PartRow>().ElementAt(1).Parameter == "Диаметр", "Fallback name formula loaded");
            manager.ShowCondition(0); manager.UpdateLayout();
            if (args.Length > 0)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(manager.ActualWidth), (int)Math.Ceiling(manager.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(manager);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(args[0], dark ? "rules-manager-dark.png" : "rules-manager-light.png")); png.Save(output);
            }
            manager.ShowScope(s => s.Category == "OST_PipeCurves" && s.IsQuantity); manager.UpdateLayout();
            Check(manager.Rules.ConditionalGroups.Single(g => g.Category == "OST_PipeCurves" && g.IsQuantity).Conditions.Count == 4, "Four default pipe quantity conditions in editor");
            var defaultQuantityRow = (QuantityRow)All<DataGrid>(manager).Single(g => g.IsVisible).Items[0];
            Check(defaultQuantityRow.ReservePercent == "10" && defaultQuantityRow.Parameter == "Длина" && defaultQuantityRow.Unit.Value == QuantityUnit.Meter && defaultQuantityRow.Rounding.Label == "Нет", "Special pipes: 10 percent, length, meters, no rounding");
            if (args.Length > 0)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(manager.ActualWidth), (int)Math.Ceiling(manager.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(manager);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(args[0], dark ? "pipe-reserve-defaults-dark.png" : "pipe-reserve-defaults-light.png")); png.Save(output);
            }
            manager.ShowCondition(-1); manager.UpdateLayout();
            Check(((QuantityRow)All<DataGrid>(manager).Single(g => g.IsVisible).Items[0]).ReservePercent == "30", "Other pipes show 30 percent in manual field");
            manager.ShowScope(s => s.Category == "OST_PipeCurves" && !s.IsQuantity); manager.UpdateLayout();
            var create = new RuleCreationWindow(manager.Rules.ConditionalGroups, (_, _) => ["Комментарии", "ADSK_Наименование", "ADSK_Количество"], "OST_PipeCurves") { ShowActivated = false, ShowInTaskbar = false };
            var warnings = new List<string>(); create.Warn = warnings.Add;
            create.Show(); create.UpdateLayout();
            Check(!create.Accept() && warnings.Count == 1, "Duplicate rule rejected in creation dialog");
            create.Select("OST_PipeCurves", false, "Комментарии");
            create.UpdateLayout();
            Check(create.Accept(), "Custom target rule accepted");
            if (args.Length > 0)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(create.ActualWidth), (int)Math.Ceiling(create.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(create);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(args[0], dark ? "rule-create-dark.png" : "rule-create-light.png")); png.Save(output);
            }
            create.Close();
            Check(manager.CreateRule("OST_PipeCurves", false, "Комментарии"), "Create rule from dialog choices"); manager.UpdateLayout(); CheckOrder();
            Check(!manager.CreateRule("OST_PipeCurves", false, "Комментарии"), "Duplicate creation cannot alter rule list");
            manager.DeleteRule(); manager.UpdateLayout(); CheckOrder();
            Check(!manager.Rules.ConditionalGroups.Any(g => g.Target == "Комментарии"), "Delete selected rule");
            manager.ShowScope(s => s.Category == "OST_PipeFitting" && !s.IsQuantity); manager.DeleteRule();
            var saved = EngineeringRuleSet.FromJson(manager.Rules.ToJson()); manager.Close();
            var reopened = new EngineeringRulesWindow(new FakeModel(), saved, "OST_PipeCurves", null) { ShowActivated = false, ShowInTaskbar = false };
            reopened.Show(); reopened.UpdateLayout();
            Check(!reopened.Rules.ConditionalGroups.Any(g => g.Category == "OST_PipeFitting" && !g.IsQuantity), "Deleted fitting rule not recreated on reopen");
            while (reopened.Rules.ConditionalGroups.Count > 0)
            {
                reopened.ShowScope(s => !s.IsSystems); reopened.DeleteRule();
            }
            var emptySaved = EngineeringRuleSet.FromJson(reopened.Rules.ToJson()); reopened.Close();
            var empty = new EngineeringRulesWindow(new FakeModel(), emptySaved, null, null) { ShowActivated = false, ShowInTaskbar = false };
            empty.Show(); empty.UpdateLayout();
            Check(empty.Rules.ConditionalGroups.Count == 0 && empty.SystemsShown, "Deleting all rules does not restore defaults");
            Check(empty.CreateRule("OST_DuctFitting", false, "ADSK_Наименование"), "Rules can be created from empty state");
            empty.Close();
            Check(source.ConditionalGroups.Count == 0 && !source.RuleListInitialized, "Rule management works on a copy");
        }
        if (args.Length > 0)
            foreach (int size in new[] { 32, 16 })
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 32, 32)); Check(BotPlus.RT.MepIcons.Draw(dc, "OVMepInsulation"), "Insulation icon"); }
                using (var dc2 = new DrawingVisual().RenderOpen()) Check(BotPlus.RT.MepIcons.Draw(dc2, "OVMepRiserNumbering"), "Riser numbering icon");
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
