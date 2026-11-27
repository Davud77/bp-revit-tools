using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using BotPlus.Shared;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using Grid = System.Windows.Controls.Grid;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace HeatLossViewer.Mep;

internal sealed class InsulationRow : INotifyPropertyChanged
{
    string type = InsulationPolicy.NoInsulation, thickness = "", insulationName = InsulationPolicy.DefaultNameTemplate;
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public string Sizes { get; init; } = "";
    public string Insulated { get; init; } = "";
    public string Type { get => type; set { type = value ?? InsulationPolicy.NoInsulation; Edited = true; Changed(); } }
    public string Thickness { get => thickness; set { thickness = value ?? ""; Edited = true; Changed(); } }
    public string InsulationName { get => insulationName; set { insulationName = value ?? ""; Edited = true; Changed(); } }
    public bool Active => Type != InsulationPolicy.NoInsulation && Type != InsulationPolicy.Mixed;
    /// <summary>Строка изменена пользователем: только такие строки записываются в правила (строка «— разные» без правки не трогает системы).</summary>
    internal bool Edited { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed() => PropertyChanged?.Invoke(this, new(null));
}

/// <summary>
/// Изоляция труб: слева — системы (можно отметить несколько или «Все системы»), справа — все уникальные трубы отмеченных
/// систем по ADSK_Наименование, у каждой свой тип и толщина изоляции. Правило строки записывается в каждую отмеченную систему.
/// «OK» сохраняет правила в проект и сразу строит изоляцию; новые трубы получают её автоматически.
/// </summary>
internal sealed class InsulationWindow : Window
{
    readonly IInsulationModel model;
    readonly InsulationSettings settings;
    readonly StackPanel systemList = new();
    readonly List<(string System, CheckBox Box)> systemBoxes = [];
    readonly ComboBox bulkType = new() { MinWidth = 220 };
    readonly TextBox bulkThickness = new() { Width = 60, Text = InsulationPolicy.DefaultThicknessMm.ToString(), VerticalContentAlignment = VerticalAlignment.Center };
    readonly CheckBox auto = new() { Content = "Автоматически изолировать новые и изменённые элементы", Margin = new Thickness(0, 0, 0, 4) };
    readonly CheckBox preserve = new() { Content = "Не восстанавливать изоляцию, снятую вручную", Margin = new Thickness(0, 4, 0, 0) };
    readonly List<(InsulationCategories Category, MenuItem Item)> categoryChoices = [];
    readonly Dictionary<DataGridColumn, Action<InsulationRow, InsulationRow>> fillColumns = [];
    readonly DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock caption = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ObservableCollection<InsulationRow> rows = [];
    readonly List<string> typeChoices;
    List<string> current = [];
    bool switching;
    internal InsulationSettings? Result { get; private set; }
    internal bool DeleteAllRequested { get; private set; }
    internal IReadOnlyList<InsulationRow> Rows => rows;
    internal IReadOnlyList<string> Selected => current;
    /// <summary>Сообщение об ошибке ввода; проверки подменяют, чтобы не открывать модальное окно.</summary>
    internal Action<string> Warn;

    internal InsulationWindow(IInsulationModel model, InsulationSettings settings, IReadOnlyCollection<string>? initialSystems, int insulationCount = 0)
    {
        this.model = model; this.settings = settings;
        Warn = text => MessageBox.Show(this, text, Title);
        Title = "Менеджер изоляций"; Width = 1440; Height = 660; MinWidth = 1200; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        typeChoices = [InsulationPolicy.NoInsulation, InsulationPolicy.Mixed, .. model.InsulationTypes()];
        foreach (var r in settings.Rules.Where(r => r.InsulationType != null && !typeChoices.Contains(r.InsulationType!, StringComparer.CurrentCultureIgnoreCase)))
            typeChoices.Add(r.InsulationType!);

        var root = new Grid { Margin = new Thickness(12) }; Content = root;
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        int line = 0;
        void Add(UIElement e) { Grid.SetRow(e, line++); Grid.SetColumn(e, 1); root.Children.Add(e); }

        // Системы: «Все системы» исключает остальные, остальные можно отмечать вместе.
        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var leftTitle = new TextBlock { Text = "Системы:", Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(leftTitle, Dock.Top); left.Children.Add(leftTitle);
        var leftTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var none = new Button { Content = "Снять все", Padding = new Thickness(8, 1, 8, 1) };
        none.Click += (_, _) => SelectSystems();
        leftTools.Children.Add(none);
        DockPanel.SetDock(leftTools, Dock.Bottom); left.Children.Add(leftTools);
        var frame = new Border { BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = systemList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6, 4, 6, 4) } };
        frame.Themed(Border.BorderBrushProperty, "BP.Border");
        left.Children.Add(frame);
        Grid.SetRowSpan(left, 4); root.Children.Add(left);

        var head = new DockPanel();
        var hint = new TextBlock { Text = "«Все системы» действуют там, где для системы тип не задан.", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(hint, Dock.Right); head.Children.Add(hint);
        head.Children.Add(caption);
        Add(head);

        var bulk = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        bulk.Children.Add(new TextBlock { Text = "Всем трубам:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        bulk.Children.Add(bulkType);
        bulk.Children.Add(new TextBlock { Text = "толщина, мм:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
        bulk.Children.Add(bulkThickness);
        var all = new Button { Content = "Задать всем", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
        all.Click += (_, _) => SetAll(bulkType.SelectedItem as string, bulkThickness.Text, grid.SelectedItems.Count > 1 ? grid.SelectedItems.OfType<InsulationRow>().ToList() : null);
        bulk.Children.Add(all);
        bulk.Children.Add(new TextBlock { Text = "(или выделенным строкам)", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        Add(bulk);
        bulkType.ItemsSource = typeChoices.Skip(2).ToList();
        bulkType.SelectedIndex = 0;

        grid.ItemsSource = rows;
        grid.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = InsulationEngineNames.Name }, Binding = new Binding(nameof(InsulationRow.Name)), IsReadOnly = true, Width = 330 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Труб", Binding = new Binding(nameof(InsulationRow.Count)), IsReadOnly = true, Width = 52 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Ø, мм", Binding = new Binding(nameof(InsulationRow.Sizes)), IsReadOnly = true, Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Изолировано", Binding = new Binding(nameof(InsulationRow.Insulated)), IsReadOnly = true, Width = 92 });
        void EditColumn(DataGridColumn column, Action<InsulationRow, InsulationRow> copy)
        {
            grid.Columns.Add(column);
            fillColumns.Add(column, copy);
        }
        var typeBox = new FrameworkElementFactory(typeof(ComboBox));
        typeBox.SetValue(ItemsControl.ItemsSourceProperty, typeChoices);
        typeBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Stretch);
        typeBox.SetValue(ComboBox.IsSynchronizedWithCurrentItemProperty, false);
        typeBox.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(InsulationRow.Type))
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        EditColumn(new DataGridTemplateColumn
        {
            Header = "Тип изоляции", Width = 230, IsReadOnly = true, CellTemplate = new DataTemplate { VisualTree = typeBox }
        }, (source, target) => { if (source.Type != InsulationPolicy.Mixed) target.Type = source.Type; });
        EditColumn(new DataGridTextColumn { Header = "Толщина, мм", Binding = new Binding(nameof(InsulationRow.Thickness))
            { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 96 }, (source, target) => target.Thickness = source.Thickness);
        EditColumn(new DataGridTextColumn { Header = new TextBlock { Text = InsulationEngineNames.Name + " изоляции" },
            Binding = new Binding(nameof(InsulationRow.InsulationName)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 270 },
            (source, target) => target.InsulationName = source.InsulationName);
        grid.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject part || FillHandle.Ancestor<ComboBox>(part) == null
                || FillHandle.Ancestor<DataGridCell>(part) is not { DataContext: InsulationRow row } cell) return;
            if (Keyboard.Modifiers == ModifierKeys.None && !grid.SelectedItems.Contains(row)) grid.SelectedItem = row;
            grid.CurrentCell = new DataGridCellInfo(row, cell.Column);
        };
        new FillHandle(grid, fillColumns.ContainsKey, (column, source, targets) =>
            Fill(column, (InsulationRow)source, targets.OfType<InsulationRow>())).Attach();
        Add(grid);

        var options = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        options.Children.Add(new TextBlock { Text = "В наименовании изоляции «" + InsulationPolicy.DiameterToken + "» заменяется диаметром трубы; пустое поле — наименование не заполняется.", Opacity = 0.7, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
        options.Children.Add(CategoriesField());
        options.Children.Add(auto);
        options.Children.Add(preserve);
        Grid.SetRow(options, 3); Grid.SetColumn(options, 1); root.Children.Add(options); line++;
        var bottom = new StackPanel();
        bottom.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 85, Margin = new Thickness(0, 0, 8, 0) };
        ok.Themed(BackgroundProperty, "BP.Accent"); ok.Foreground = System.Windows.Media.Brushes.White;
        ok.ToolTip = "Сохранить правила и построить изоляцию для выбранных категорий.";
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 };
        var delete = new Button { Content = "Удалить всю изоляцию…", IsEnabled = insulationCount > 0, Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(10, 2, 10, 2) };
        delete.ToolTip = "Удалить изоляцию труб, фитингов и арматуры во всём проекте";
        delete.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(this,
                $"Удалить всю изоляцию труб, фитингов и арматуры в проекте? Экземпляров для удаления: {insulationCount}. "
                + "Зависимые марки тоже могут удалиться. Автопостроение будет выключено, сохранённые правила останутся в проекте.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
            DeleteAllRequested = true;
            DialogResult = true;
        };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(delete); buttons.Children.Add(ok); buttons.Children.Add(cancel);
        bottom.Children.Add(buttons);
        Grid.SetRow(bottom, 4); Grid.SetColumnSpan(bottom, 2); root.Children.Add(bottom);

        auto.IsChecked = settings.Auto;
        preserve.IsChecked = settings.PreserveManualRemovals;
        preserve.ToolTip = "После сохранения настройки BotPlus запоминает изолированные элементы. "
            + "Если затем снять с них изоляцию вручную, она не восстановится при построении.";
        // Только системы, в которых есть трубы; число труб — после имени.
        foreach (var (name, count) in new[] { ("", model.Pipes([""]).Sum(g => g.Count)) }.Concat(model.Systems().Select(s => (s, model.Pipes([s]).Sum(g => g.Count)))))
        {
            if (name.Length > 0 && count == 0) continue;
            var label = new TextBlock { Text = name.Length == 0 ? InsulationPolicy.AllSystemsLabel : name };
            var number = new System.Windows.Documents.Run($"  {count}") { FontWeight = FontWeights.Normal };
            number.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "BP.Muted");
            label.Inlines.Add(number);
            var box = new CheckBox { Content = label, Tag = name, Margin = new Thickness(0, 2, 0, 2) };
            if (name.Length == 0) box.FontWeight = FontWeights.SemiBold;
            if (settings.Rules.Any(r => r.InsulationType != null && r.System == name)) box.ToolTip = "Есть правила изоляции";
            string key = name;
            box.Checked += (_, _) => Toggle(key, true);
            box.Unchecked += (_, _) => Toggle(key, false);
            systemBoxes.Add((name, box)); systemList.Children.Add(box);
        }
        var start = initialSystems?.Where(s => systemBoxes.Any(b => b.System == s)).ToList() is { Count: > 0 } picked ? picked
            : systemBoxes.Where(b => b.System.Length > 0 && settings.Rules.Any(r => r.InsulationType != null && r.System == b.System)).Select(b => b.System).Take(1).ToList() is { Count: > 0 } ruled ? ruled
            : [""];
        SelectSystems([.. start]);
        if (model.InsulationTypes().Count == 0)
            status.Text = "В проекте нет типов изоляции труб. Создайте их в Revit («Изоляция труб» в диспетчере проекта) или загрузите из шаблона.";
        BotPlusTheme.Apply(this);
    }

    FrameworkElement CategoriesField()
    {
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        var button = new Button { Content = "Что изолировать ▾", ContextMenu = menu, Padding = new Thickness(10, 2, 10, 2) };
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var (category, label) in new[]
        {
            (InsulationCategories.Pipes, "Трубы"),
            (InsulationCategories.Fittings, "Соединительные детали трубопроводов"),
            (InsulationCategories.Accessories, "Арматура трубопроводов")
        })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = settings.Categories.HasFlag(category), StaysOpenOnClick = true };
            item.Checked += (_, _) => Refresh();
            item.Unchecked += (_, _) => Refresh();
            categoryChoices.Add((category, item));
            menu.Items.Add(item);
        }
        void Refresh()
        {
            var labels = categoryChoices.Where(choice => choice.Item.IsChecked).Select(choice => choice.Item.Header).ToList();
            value.Text = labels.Count == 0 ? "Категории не выбраны" : string.Join(", ", labels);
        }
        Refresh();
        button.Click += (_, _) => { menu.PlacementTarget = button; menu.IsOpen = true; };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(button);
        row.Children.Add(value);
        return row;
    }

    internal void Fill(DataGridColumn column, InsulationRow source, IEnumerable<InsulationRow> targets)
    {
        foreach (var target in targets) fillColumns[column](source, target);
    }

    void Toggle(string system, bool on)
    {
        if (switching) return;
        var next = current.ToList();
        if (on)
        {
            if (system.Length == 0) next = [""];
            else { next.Remove(""); next.Add(system); }
        }
        else next.Remove(system);
        SelectSystems([.. next]);
    }

    /// <summary>Отметить системы: строки прежних систем записываются в правила, загружаются трубы новых.</summary>
    internal void SelectSystems(params string[] systems)
    {
        if (Store() is { } error) { Mark(current); Warn(error); return; }
        current = systems.Contains("") ? [""] : systemBoxes.Select(b => b.System).Where(s => s.Length > 0 && systems.Contains(s)).ToList();
        Mark(current);
        Load();
    }

    void Mark(IReadOnlyCollection<string> selected)
    {
        switching = true;
        foreach (var (system, box) in systemBoxes) box.IsChecked = selected.Contains(system);
        switching = false;
    }

    void Load()
    {
        rows.Clear();
        caption.Text = current.Count == 0 ? "Отметьте одну или несколько систем слева."
            : current[0].Length == 0 ? "Все трубы проекта"
            : current.Count == 1 ? "Трубы системы " + current[0] : $"Трубы систем ({current.Count}): " + string.Join(", ", current);
        if (current.Count == 0) return;
        foreach (var g in model.Pipes(current))
        {
            var found = current.Select(s => settings.Get(s, g.Name)).ToList();
            var first = found[0];
            bool same = found.All(r => r?.InsulationType == first?.InsulationType && (r is null || Math.Abs(r.ThicknessMm - first!.ThicknessMm) < 0.05 && r.NameTemplate == first.NameTemplate));
            var row = new InsulationRow
            {
                Name = g.Name, Count = g.Count, Sizes = g.Sizes, Insulated = g.Insulated == 0 ? "—" : $"{g.Insulated} из {g.Count}",
                Type = !same ? InsulationPolicy.Mixed : first?.InsulationType ?? InsulationPolicy.NoInsulation,
                Thickness = !same || first is null ? "" : first.ThicknessMm.ToString("0.#"),
                InsulationName = !same ? "" : first?.NameTemplate ?? InsulationPolicy.DefaultNameTemplate
            };
            row.Edited = false;
            rows.Add(row);
        }
        if (model.InsulationTypes().Count > 0)
            status.Text = rows.Count == 0 ? "В отмеченных системах нет труб. Правила можно задать позже — они применятся к новым трубам." : "";
    }

    internal void SelectSystem(string name) => SelectSystems(name);

    /// <summary>Задать тип и толщину всем строкам (или выделенным).</summary>
    internal void SetAll(string? type, string thicknessText, IReadOnlyList<InsulationRow>? only = null)
    {
        if (type is null) return;
        if (!InsulationPolicy.TryParseThickness(thicknessText, out double mm)) { Warn("Толщина: число больше 0 и не больше 300 мм."); return; }
        foreach (var row in only ?? rows) { row.Type = type; row.Thickness = mm.ToString("0.#"); }
    }

    /// <summary>Изменённые строки записываются в каждую отмеченную систему.</summary>
    string? Store()
    {
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        var edited = rows.Where(r => r.Edited && r.Type != InsulationPolicy.Mixed).ToList();
        var values = new List<(string Name, string? Type, double Mm, string Template)>();
        foreach (var row in edited)
        {
            if (!row.Active) { values.Add((row.Name, null, 0, "")); continue; }
            string text = row.Thickness.Trim().Length == 0 ? InsulationPolicy.DefaultThicknessMm.ToString() : row.Thickness;
            if (!InsulationPolicy.TryParseThickness(text, out double mm))
                return $"«{row.Name}»: толщина — число больше 0 и не больше 300 мм.";
            values.Add((row.Name, row.Type, mm, row.InsulationName));
        }
        foreach (var system in current)
            foreach (var (name, type, mm, template) in values) settings.Put(system, name, type, mm, template);
        foreach (var row in edited) row.Edited = false;
        return null;
    }

    internal bool Accept()
    {
        if (Store() is { } error) { Warn(error); return false; }
        settings.Auto = auto.IsChecked == true;
        settings.PreserveManualRemovals = preserve.IsChecked == true;
        settings.Categories = categoryChoices.Where(choice => choice.Item.IsChecked)
            .Aggregate(InsulationCategories.None, (categories, choice) => categories | choice.Category);
        try { settings.Validate(); }
        catch (InvalidOperationException e) { Warn(e.Message); return false; }
        Result = settings;
        if (IsVisible && System.Windows.Interop.ComponentDispatcher.IsThreadModal) DialogResult = true;
        return true;
    }
}

/// <summary>Заголовок колонки: TextBlock, чтобы «_» не стал клавишей доступа.</summary>
internal static class InsulationEngineNames { internal const string Name = "ADSK_Наименование"; }
