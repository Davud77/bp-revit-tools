using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BotPlus.Shared;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DockPanel = System.Windows.Controls.DockPanel;
using Dock = System.Windows.Controls.Dock;
using Grid = System.Windows.Controls.Grid;
using GroupBox = System.Windows.Controls.GroupBox;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using Media = System.Windows.Media;
using Point = System.Windows.Point;

namespace HeatLossViewer.Mep;

internal sealed record Choice<T>(T Value, string Label) { public override string ToString() => Label; }

/// <summary>Entry of the scope list: category and «Маркировка типоразмера» (or «Без типа»), as in BIM2B.</summary>
internal sealed class ScopeChoice(string category, string categoryLabel, string? mark, List<string> types)
{
    public string Category { get; } = category;
    public string CategoryLabel { get; } = categoryLabel;
    public string? Mark { get; } = mark;
    public List<string> Types { get; } = types;
    public bool Configured { get; set; }
    /// <summary>Пункт «Имена систем» в том же списке: вместо формул показываются правила ADSK_Система_Имя.</summary>
    public bool IsSystems { get; init; }
    public bool IsQuantity { get; init; }
    internal const string SystemsLabel = "Имена систем (ADSK_Система_Имя)";
    public string Target { get; set; } = "";
    public Media.ImageSource Icon => RuleCategoryIcons.Get(IsSystems ? "Systems" : Category);
    public override string ToString() => $"{(Configured ? "● " : "")}{(IsSystems ? Target + "\nВсе категории систем" : $"{Target}\n{CategoryLabel}")}";

}

internal sealed class PartRow : INotifyPropertyChanged
{
    string prefix = "", suffix = "", value = "";
    public string Parameter { get; init; } = "";
    public string Label => EngineeringRulesWindow.Display(Parameter);
    public string Prefix { get => prefix; set { prefix = value ?? ""; Changed(nameof(Prefix)); } }
    public string Suffix { get => suffix; set { suffix = value ?? ""; Changed(nameof(Suffix)); } }
    public string Value { get => value; set { this.value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action? Edited;
    void Changed(string name) { PropertyChanged?.Invoke(this, new(name)); Edited?.Invoke(); }
}

internal sealed class QuantityRow : INotifyPropertyChanged
{
    string reservePercent; Choice<QuantityUnit> unit; Choice<(int, QuantityRounding)> rounding;
    public QuantityRow(double reserve, Choice<QuantityUnit> unit, Choice<(int, QuantityRounding)> rounding) { reservePercent = ((reserve - 1) * 100).ToString("G15", CultureInfo.CurrentCulture); this.unit = unit; this.rounding = rounding; }
    public string Parameter { get; init; } = "";
    public string Label => EngineeringRulesWindow.Display(Parameter);
    public string ReservePercent { get => reservePercent; set { reservePercent = value ?? ""; Changed(); } }
    public double Reserve => TryReserve(out double value) ? value : double.NaN;
    public bool TryReserve(out double value)
    {
        value = double.NaN;
        if (!double.TryParse(ReservePercent.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)
            || !double.IsFinite(percent) || percent < 0 || percent > 900) return false;
        value = 1 + percent / 100; return true;
    }
    public Choice<QuantityUnit> Unit { get => unit; set { unit = value; Changed(); } }
    public Choice<(int, QuantityRounding)> Rounding { get => rounding; set { rounding = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action? Edited;
    void Changed() { PropertyChanged?.Invoke(this, new(null)); Edited?.Invoke(); }
}

/// <summary>
/// «Настройки апдейтера»: слева заполняемые параметры по категориям, справа условия со своими формулами,
/// справа редактор выбранного правила, пример и кнопки сохранения. Работает на копии: «Отменить»
/// не меняет ни правила, ни модель.
/// </summary>
internal sealed class EngineeringRulesWindow : Window
{
    static readonly Choice<QuantityUnit>[] Units =
        [new(QuantityUnit.Meter, "Метры"), new(QuantityUnit.Millimeter, "Миллиметры"), new(QuantityUnit.SquareMeter, "Кв. метры"), new(QuantityUnit.Piece, "Штуки")];
    static readonly Choice<(int, QuantityRounding)>[] Roundings =
    [
        new((6, QuantityRounding.Nearest), "Нет"),
        new((0, QuantityRounding.Up), "До целого вверх"), new((1, QuantityRounding.Up), "До 0,1 вверх"), new((2, QuantityRounding.Up), "До 0,01 вверх"),
        new((0, QuantityRounding.Nearest), "До целого"), new((1, QuantityRounding.Nearest), "До 0,1"), new((2, QuantityRounding.Nearest), "До 0,01")
    ];

    readonly IUpdaterModel model;
    readonly Button deleteRuleButton = new() { Content = "Удалить правило", Margin = new Thickness(6, 0, 0, 0), MinHeight = 26 };
    readonly ListBox scope = new() { FontSize = 12, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
    readonly TextBlock types = new() { Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 4, 0, 0) };
    readonly CheckBox nameOn = new() { Content = "Заполнять", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    readonly CheckBox quantityOn = new() { Content = "Заполнять", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    readonly ComboBox nameTarget = new() { MinWidth = 240, FontSize = 13, FontWeight = FontWeights.SemiBold }, quantityTarget = new() { MinWidth = 240, FontSize = 13, FontWeight = FontWeights.SemiBold };
    readonly ListBox nameList = new(), quantityList = new();
    readonly ObservableCollection<PartRow> parts = [];
    readonly ObservableCollection<QuantityRow> quantity = [];
    List<ReserveRow> reserveRows = [];
    readonly DataGrid partsGrid = Grid0(), quantityGrid = Grid0();
    readonly TextBlock namePreview = new() { TextWrapping = TextWrapping.Wrap }, quantityPreview = new() { TextWrapping = TextWrapping.Wrap };
    ScopeChoice? current;
    /// <summary>Выбранный пункт списка, включая «Имена систем» (тогда <see cref="current"/> = null).</summary>
    ScopeChoice? shown;
    readonly SystemNamesPanel systemsPanel;
    readonly List<UIElement> ruleArea = [];
    readonly List<UIElement> quantityArea = [];
    readonly ListBox branches = new() { Height = 110, DisplayMemberPath = "Label" };
    readonly ComboBox conditionParameter = new() { MinWidth = 240 };
    readonly ComboBox conditionComparison = new() { MinWidth = 120, ItemsSource = new Choice<ConditionComparison>[] {
        new(ConditionComparison.Equals, "равно"), new(ConditionComparison.NotEquals, "не равно"),
        new(ConditionComparison.Contains, "содержит"), new(ConditionComparison.StartsWith, "начинается с"),
        new(ConditionComparison.Empty, "пусто"), new(ConditionComparison.NotEmpty, "не пусто") } };
    readonly TextBox conditionValue = new() { MinWidth = 180 };
    readonly Grid conditionLine = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel conditionsPanel = new() { Margin = new Thickness(0, 8, 0, 0) };
    ConditionalRuleGroup? activeGroup;
    EngineeringRule? currentFormula;
    int branchIndex = -1;
    string? sampleMark;
    bool sample;
    bool loading;

    internal EngineeringRuleSet Rules { get; private set; }
    internal bool SystemsShown => shown?.IsSystems == true;

    internal static string Percent(double reserve) => EngineeringRulesPolicy.Format((reserve - 1) * 100, 1) + " %";

    internal static string Display(string parameter) => parameter.StartsWith('@') ? parameter[1..] : parameter;

    static DataGrid Grid0() => new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, CanUserReorderColumns = false,
        SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 150
    };

    internal EngineeringRulesWindow(IUpdaterModel model, EngineeringRuleSet rules, string? initialCategory, string? initialMark)
    {
        this.model = model;
        Rules = EngineeringRuleSet.FromJson(rules.ToJson());
        Rules.PrepareDefaults();
        Title = "Настройки апдейтера"; Width = 1400; Height = 760; MinWidth = 1240; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { Margin = new Thickness(12) }; Content = layout;
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 250 });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        ScrollViewer.SetHorizontalScrollBarVisibility(scope, ScrollBarVisibility.Disabled);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding("."));
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        label.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 1, 3, 1));
        label.SetValue(TextBlock.LineHeightProperty, 14.0);
        var entry = new FrameworkElementFactory(typeof(DockPanel));
        var icon = new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
        icon.SetBinding(System.Windows.Controls.Image.SourceProperty, new Binding(nameof(ScopeChoice.Icon)));
        icon.SetValue(FrameworkElement.WidthProperty, 22.0); icon.SetValue(FrameworkElement.HeightProperty, 22.0);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 5, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        entry.AppendChild(icon); entry.AppendChild(label);
        scope.ItemTemplate = new DataTemplate { VisualTree = entry };
        var navigation = new DockPanel();
        var ruleTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        ruleTools.Children.Add(Small("+ Правило", AddRule, "Создать правило для параметра и категории"));
        deleteRuleButton.Click += (_, _) => DeleteRule(); ruleTools.Children.Add(deleteRuleButton);
        DockPanel.SetDock(ruleTools, Dock.Top); navigation.Children.Add(ruleTools); navigation.Children.Add(scope);
        layout.Children.Add(Section("Правила", navigation));
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetColumn(splitter, 1); layout.Children.Add(splitter);
        var root = new Grid(); Grid.SetColumn(root, 2); layout.Children.Add(root);
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        int row = 0;
        void Add(UIElement e) { Grid.SetRow(e, row++); root.Children.Add(e); }

        // Заголовок редактора и сервисные кнопки.
        var top = new DockPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        tools.Children.Add(Small("Толщина стенки…", EditWalls, "Таблица толщины стенки воздуховодов по форме сечения, размеру и огнезащите; запись в параметр."));
        DockPanel.SetDock(tools, Dock.Right); top.Children.Add(tools);
        top.Children.Add(new TextBlock { Text = "Настройка правила", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Add(top);
        Add(types);
        var conditionTools = new StackPanel { Orientation = Orientation.Horizontal };
        conditionTools.Children.Add(new TextBlock { Text = "Условия (первое совпадение сверху)", VerticalAlignment = VerticalAlignment.Center });
        conditionTools.Children.Add(Small("+ Условие", AddCondition, "Добавить условие со своей формулой"));
        conditionTools.Children.Add(Small("Удалить", RemoveCondition, "Удалить выбранное условие; вариант «В остальных случаях» остаётся"));
        conditionTools.Children.Add(Small("↑", () => MoveCondition(-1), "Повысить приоритет условия"));
        conditionTools.Children.Add(Small("↓", () => MoveCondition(1), "Понизить приоритет условия"));
        conditionsPanel.Children.Add(conditionTools); conditionsPanel.Children.Add(branches);
        conditionLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        conditionLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        conditionLine.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(conditionComparison, 1); Grid.SetColumn(conditionValue, 2);
        conditionParameter.ToolTip = "Параметр экземпляра или типа для проверки условия.";
        conditionValue.ToolTip = "Текст для сравнения. Регистр и пробелы по краям не учитываются.";
        conditionLine.Children.Add(conditionParameter); conditionLine.Children.Add(conditionComparison); conditionLine.Children.Add(conditionValue);
        conditionsPanel.Children.Add(conditionLine); Add(conditionsPanel);
        branches.SelectionChanged += (_, _) => SwitchCondition();
        conditionParameter.SelectionChanged += (_, _) => ConditionEdited();
        conditionComparison.SelectionChanged += (_, _) => ConditionEdited();
        conditionValue.TextChanged += (_, _) => ConditionEdited();
        conditionValue.LostKeyboardFocus += (_, _) => PreviewCondition();
        conditionParameter.LostKeyboardFocus += (_, _) => PreviewCondition();

        // Редакторы занимают одну область; виден только выбранный в списке.
        int firstRuleRow = row;
        void Rule(UIElement e) { ruleArea.Add(e); Add(e); }
        Rule(Header(nameOn, nameTarget));
        Rule(Body(nameList, partsGrid, AddPart, RemovePart, true));
        Rule(Example(namePreview));

        // Количество.
        row = firstRuleRow;
        void QuantityRule(UIElement e) { quantityArea.Add(e); Add(e); }
        QuantityRule(Header(quantityOn, quantityTarget));
        QuantityRule(Body(quantityList, quantityGrid, AddQuantity, () => { quantity.Clear(); Refresh(); }, false));
        QuantityRule(Example(quantityPreview));
        systemsPanel = new SystemNamesPanel(Rules.SystemNames, model.SystemTypes()) { Visibility = Visibility.Collapsed };
        Grid.SetRow(systemsPanel, firstRuleRow - 1); Grid.SetRowSpan(systemsPanel, row - firstRuleRow + 1); root.Children.Add(systemsPanel);

        var buttons = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var update = Wide("Сохранить и обновить", true);
        var cancel = Wide("Отменить", false); cancel.IsCancel = true;
        buttons.Children.Add(update); Grid.SetColumn(cancel, 2); buttons.Children.Add(cancel);
        update.Click += (_, _) => Finish(); Add(buttons);

        // Формула наименования.
        partsGrid.ItemsSource = parts;
        partsGrid.Columns.Add(new DataGridTextColumn { Header = "Имя параметра", Binding = new Binding(nameof(PartRow.Label)), IsReadOnly = true, Width = 190 });
        partsGrid.Columns.Add(new DataGridTextColumn { Header = "Префикс", Binding = new Binding(nameof(PartRow.Prefix)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 90 });
        partsGrid.Columns.Add(new DataGridTextColumn { Header = "Значение", Binding = new Binding(nameof(PartRow.Value)), IsReadOnly = true, Width = 210 });
        partsGrid.Columns.Add(new DataGridTextColumn { Header = "Суффикс", Binding = new Binding(nameof(PartRow.Suffix)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 80 });
        // Формула количества.
        quantityGrid.ItemsSource = quantity;
        quantityGrid.Columns.Add(new DataGridTextColumn { Header = "Имя параметра", Binding = new Binding(nameof(QuantityRow.Label)), IsReadOnly = true, Width = 190 });
        quantityGrid.Columns.Add(new DataGridTextColumn { Header = "Запас, %", Binding = new Binding(nameof(QuantityRow.ReservePercent)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 140 });
        quantityGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Единицы", ItemsSource = Units, SelectedItemBinding = new Binding(nameof(QuantityRow.Unit)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 110 });
        quantityGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Округление", ItemsSource = Roundings, SelectedItemBinding = new Binding(nameof(QuantityRow.Rounding)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 130 });

        nameList.MouseDoubleClick += (_, _) => AddPart();
        quantityList.MouseDoubleClick += (_, _) => AddQuantity();
        foreach (var c in new[] { nameOn, quantityOn })
        {
            c.ToolTip = "Заполнять параметр по формуле выбранного условия. При выключении совпавшие элементы остаются без изменений.";
            c.Checked += (_, _) => Refresh(); c.Unchecked += (_, _) => Refresh();
        }
        foreach (var c in new[] { nameTarget, quantityTarget }) c.SelectionChanged += (_, _) => Refresh();
        scope.SelectionChanged += (_, _) => { if (!loading) SwitchScope(); };

        FillScopes(initialCategory, initialMark);
        BotPlusTheme.Apply(this);
    }

    Button Small(string text, Action click, string tip)
    {
        var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), ToolTip = tip, MinHeight = 26 };
        b.Click += (_, _) => click();
        return b;
    }

    static Button Wide(string text, bool primary)
    {
        var b = new Button { Content = text, MinHeight = 32, FontSize = 13, FontWeight = FontWeights.SemiBold };
        if (primary) { b.IsDefault = true; b.Themed(BackgroundProperty, "BP.Accent"); b.Foreground = System.Windows.Media.Brushes.White; }
        return b;
    }

    static UIElement Header(CheckBox on, ComboBox target)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 6) };
        line.Children.Add(target); line.Children.Add(on);
        target.ToolTip = "Заполняемый параметр экземпляра. Можно выбрать другой параметр подходящего типа.";
        return line;
    }

    static UIElement Example(TextBlock text)
    {
        var line = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var label = new TextBlock { Text = "Пример:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) };
        line.Children.Add(label); line.Children.Add(text);
        var border = new Border { Child = line, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(2) };
        border.Themed(Border.BackgroundProperty, "BP.Panel");
        return border;
    }

    UIElement Body(ListBox list, DataGrid formula, Action add, Action remove, bool movable, UIElement? footer = null)
    {
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star), MinWidth = 220 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        body.Children.Add(Section("Список параметров:", list));
        var arrows = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        arrows.Children.Add(Arrow("→", add, "Добавить в формулу (или двойной щелчок)"));
        arrows.Children.Add(Arrow("←", remove, "Убрать из формулы"));
        Grid.SetColumn(arrows, 1); body.Children.Add(arrows);
        var right = new DockPanel();
        if (movable)
        {
            var move = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            move.Children.Add(Arrow("↑", () => Move(-1), "Выше")); move.Children.Add(Arrow("↓", () => Move(1), "Ниже"));
            DockPanel.SetDock(move, Dock.Bottom); right.Children.Add(move);
        }
        if (footer != null) { DockPanel.SetDock(footer, Dock.Bottom); right.Children.Add(footer); }
        right.Children.Add(formula);
        var group = Section("Формула", right);
        Grid.SetColumn(group, 2); body.Children.Add(group);
        return body;
    }

    static UIElement Section(string header, UIElement content)
    {
        var panel = new DockPanel();
        var title = new TextBlock { Text = header, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        panel.Children.Add(content);
        return panel;
    }

    static Button Arrow(string text, Action click, string tip)
    {
        var b = new Button { Content = text, Width = 34, Height = 28, FontSize = 16, FontWeight = FontWeights.Bold, Padding = new Thickness(0), Margin = new Thickness(0, 2, 4, 2), ToolTip = tip };
        b.Themed(ForegroundProperty, "BP.Accent");
        b.Click += (_, _) => click();
        return b;
    }

    void RebuildConditions()
    {
        if (activeGroup == null) return;
        bool wasLoading = loading; loading = true;
        branches.ItemsSource = activeGroup.Conditions.Select((c, i) => new Choice<int>(i,
            $"Условие {i + 1}: {c.Parameter} {ComparisonLabel(c.Comparison)} «{c.Value}»"))
            .Append(new Choice<int>(-1, "В остальных случаях")).ToList();
        branches.SelectedItem = branches.Items.Cast<Choice<int>>().First(x => x.Value == branchIndex);
        loading = wasLoading;
    }

    string ComparisonLabel(ConditionComparison value) => conditionComparison.Items.Cast<Choice<ConditionComparison>>().First(x => x.Value == value).Label;

    void StoreFormula()
    {
        if (activeGroup == null || current == null) return;
        if (current.IsQuantity && quantity.Any(q => !q.TryReserve(out _))) return;
        var formula = Editor();
        // Each branch has its own formula; the target belongs to the category rule.
        activeGroup.Target = (current.IsQuantity ? quantityTarget : nameTarget).SelectedItem as string ?? "";
        formula.NameTarget = formula.QuantityTarget = activeGroup.Target;
        current.Target = activeGroup.Target;
        formula.TypeMark = null;
        if (branchIndex < 0) activeGroup.Otherwise = formula;
        else
        {
            var c = activeGroup.Conditions[branchIndex]; c.Formula = formula;
            c.Parameter = conditionParameter.Text.Trim(); c.Value = conditionValue.Text;
            c.Comparison = (conditionComparison.SelectedItem as Choice<ConditionComparison>)?.Value ?? ConditionComparison.Equals;
        }
        currentFormula = formula;
    }

    void ConditionEdited()
    {
        if (loading || activeGroup == null || branchIndex < 0) return;
        StoreFormula(); RebuildConditions();
        conditionValue.IsEnabled = activeGroup.Conditions[branchIndex].Comparison is not (ConditionComparison.Empty or ConditionComparison.NotEmpty);
        sample = false; // Never show an example from the previously edited criterion.
        Refresh();
    }

    void PreviewCondition()
    {
        if (loading || activeGroup == null) return;
        if (current?.IsQuantity == true && quantity.Any(q => !q.TryReserve(out _))) return;
        StoreFormula(); LoadFormula();
    }

    bool CanCommit()
    {
        if (Commit(out var error)) return true;
        MessageBox.Show(this, error, Title); return false;
    }

    void SwitchCondition()
    {
        if (loading || branches.SelectedItem is not Choice<int> selected) return;
        int next = selected.Value;
        if (!Commit(out var error)) { MessageBox.Show(this, error, Title); RebuildConditions(); return; }
        branchIndex = next; LoadFormula();
    }

    internal void ShowCondition(int index)
    {
        branches.SelectedItem = branches.Items.Cast<Choice<int>>().First(x => x.Value == index);
    }

    void AddCondition()
    {
        if (activeGroup == null || !CanCommit()) return;
        activeGroup.Conditions.Add(new FormulaCondition { Formula = currentFormula!.Clone() });
        branchIndex = activeGroup.Conditions.Count - 1;
        RebuildConditions(); LoadFormula();
    }

    void RemoveCondition()
    {
        if (activeGroup == null || branchIndex < 0) return;
        // The deleted branch need not be valid.
        activeGroup.Conditions.RemoveAt(branchIndex);
        branchIndex = Math.Min(branchIndex, activeGroup.Conditions.Count - 1);
        RebuildConditions(); LoadFormula();
    }

    void MoveCondition(int delta)
    {
        if (activeGroup == null || branchIndex < 0 || !CanCommit()) return;
        int next = branchIndex + delta;
        if (next < 0 || next >= activeGroup.Conditions.Count) return;
        var condition = activeGroup.Conditions[branchIndex]; activeGroup.Conditions.RemoveAt(branchIndex);
        activeGroup.Conditions.Insert(next, condition); branchIndex = next;
        RebuildConditions(); LoadFormula();
    }

    // ---------- Области ----------

    static ConditionalRuleGroup NewGroup(string category, bool quantityRule, string target) => new()
    {
        Category = category, IsQuantity = quantityRule, Target = target, Otherwise = EngineeringRulesPolicy.DefaultFormula(category)
    };

    void AddRule()
    {
        if (!CanCommit()) return;
        IReadOnlyList<string> TargetsFor(string category, bool quantityRule) => model.Marks(category)
            .SelectMany(m => quantityRule ? model.NumberTargets(category, m.Mark) : model.TextTargets(category, m.Mark)).Distinct().ToList();
        var dialog = new RuleCreationWindow(Rules.ConditionalGroups, TargetsFor, current?.Category) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        CreateRule(dialog.Category, dialog.IsQuantity, dialog.Target);
    }

    internal bool CreateRule(string category, bool quantityRule, string target)
    {
        if (!CanCommit()) return false;
        if (!EngineeringRulesPolicy.Categories.Any(c => c.Key == category) || string.IsNullOrWhiteSpace(target) ||
            Rules.ConditionalGroups.Any(g => g.Category == category && g.Target == target.Trim())) return false;
        Rules.ConditionalGroups.Add(NewGroup(category, quantityRule, target.Trim()));
        FillScopes(category, null);
        ShowScope(s => s.Category == category && s.Target == target.Trim());
        return true;
    }

    internal void DeleteRule()
    {
        if (current == null || activeGroup == null) return;
        string category = current.Category;
        Rules.ConditionalGroups.Remove(activeGroup);
        // Clear the deleted draft before selecting another item; it must never be written back.
        current = null; currentFormula = null; activeGroup = null; shown = null;
        FillScopes(category, null);
    }

    void FillScopes(string? category, string? mark)
    {
        var list = new List<ScopeChoice> { new("", "", null, []) { IsSystems = true } };
        foreach (var group in Rules.ConditionalGroups)
        {
            var names = model.Marks(group.Category).SelectMany(m => m.Types).Distinct().ToList();
            list.Add(new ScopeChoice(group.Category, EngineeringRulesPolicy.CategoryLabel(group.Category), null, names) { IsQuantity = group.IsQuantity, Target = group.Target });
        }
        list[0].Target = Rules.SystemNames.Target;
        list = list.OrderBy(s => s.Target, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.CategoryLabel, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var s in list) SetConfigured(s);
        loading = true;
        scope.ItemsSource = list;
        SortScopes();
        var rules = list.Where(s => !s.IsSystems).ToList();
        scope.SelectedItem = rules.FirstOrDefault(s => s.Category == category && !s.IsQuantity)
            ?? rules.FirstOrDefault(s => s.Category == category) ?? rules.FirstOrDefault() ?? list[0];
        loading = false;
        Load();
    }

    void SwitchScope()
    {
        if (!Commit(out string? error))
        {
            MessageBox.Show(this, error, Title);
            loading = true; scope.SelectedItem = shown; loading = false;
            return;
        }
        foreach (ScopeChoice s in scope.Items) SetConfigured(s);
        SortScopes();
        scope.Items.Refresh();
        Load();
    }

    void SortScopes()
    {
        if (CollectionViewSource.GetDefaultView(scope.ItemsSource) is ListCollectionView view)
            view.CustomSort = new ScopeOrder();
    }

    sealed class ScopeOrder : System.Collections.IComparer
    {
        public int Compare(object? a, object? b)
        {
            if (a is not ScopeChoice x || b is not ScopeChoice y) return 0;
            int parameter = StringComparer.CurrentCultureIgnoreCase.Compare(x.Target, y.Target);
            return parameter != 0 ? parameter : StringComparer.CurrentCultureIgnoreCase.Compare(x.CategoryLabel, y.CategoryLabel);
        }
    }

    internal void ShowScope(Func<ScopeChoice, bool> pick) => scope.SelectedItem = scope.Items.Cast<ScopeChoice>().First(pick);

    void SetConfigured(ScopeChoice s)
    {
        var group = Rules.ConditionalGroups.FirstOrDefault(g => g.Category == s.Category && g.IsQuantity == s.IsQuantity && g.Target == s.Target);
        s.Configured = s.IsSystems ? Rules.SystemNames.Enabled : group?.IsActive == true;
        if (group != null) s.Target = group.Target;
    }

    void Load()
    {
        shown = scope.SelectedItem as ScopeChoice;
        bool systems = shown?.IsSystems == true;
        deleteRuleButton.IsEnabled = !systems && shown != null;
        systemsPanel.Visibility = systems ? Visibility.Visible : Visibility.Collapsed;
        foreach (var e in ruleArea) e.Visibility = !systems && shown?.IsQuantity == false ? Visibility.Visible : Visibility.Collapsed;
        foreach (var e in quantityArea) e.Visibility = !systems && shown?.IsQuantity == true ? Visibility.Visible : Visibility.Collapsed;
        conditionsPanel.Visibility = systems ? Visibility.Collapsed : Visibility.Visible;
        if (systems)
        {
            activeGroup = null; currentFormula = null;
            current = null;
            types.Text = "Правила ADSK_Система_Имя по типу системы действуют для всех категорий; «Сохранить и обновить» заполняет весь проект.";
            types.ToolTip = null;
            return;
        }
        current = shown;
        if (current == null) { IsEnabled = true; types.Text = "В проекте нет элементов поддерживаемых категорий."; return; }
        activeGroup = Rules.ConditionalGroups.First(g => g.Category == current.Category && g.IsQuantity == current.IsQuantity && g.Target == current.Target);
        branchIndex = activeGroup.Conditions.Count > 0 ? 0 : -1;
        RebuildConditions();
        LoadFormula();
    }

    void LoadFormula()
    {
        if (current == null || activeGroup == null) return;
        loading = true;
        currentFormula = branchIndex < 0 ? activeGroup.Otherwise : activeGroup.Conditions[branchIndex].Formula;
        var rule = currentFormula.Clone();
        rule.NameTarget = rule.QuantityTarget = activeGroup.Target;
        var preview = model.SelectPreview(current.Category, activeGroup, currentFormula, Rules.WallThickness);
        sampleMark = preview.Mark; sample = preview.Exists;
        types.Text = sample ? "Пример: элемент, соответствующий выбранному условию." : "Нет элементов для выбранного условия. Формулу можно настроить и сохранить.";
        types.ToolTip = string.Join("\n", current.Types);
        var names = model.Marks(current.Category).SelectMany(m => model.Parameters(current.Category, m.Mark)).Distinct().Order().ToList();
        nameList.Items.Clear();
        foreach (var v in EngineeringRulesPolicy.VirtualNameSources) nameList.Items.Add(Item(v, true));
        foreach (var n in names) nameList.Items.Add(Item(n, false));
        quantityList.Items.Clear();
        foreach (var v in EngineeringRulesPolicy.VirtualQuantitySources) quantityList.Items.Add(Item(v, true));
        foreach (var n in names.Where(n => !sample || Kind(n) is SourceKind.Length or SourceKind.Area or SourceKind.Number)) quantityList.Items.Add(Item(n, false));
        if (current.Category == "OST_DuctFitting" && !names.Contains(EngineeringRulesPolicy.FittingAreaParameter))
            quantityList.Items.Add(Item(EngineeringRulesPolicy.FittingAreaParameter, false));
        conditionParameter.ItemsSource = names.Concat(EngineeringRulesPolicy.VirtualNameSources).Append("Маркировка типоразмера")
            .Append(branchIndex < 0 ? "Комментарии к типоразмеру" : activeGroup.Conditions[branchIndex].Parameter).Distinct().Order().ToList();
        conditionLine.Visibility = branchIndex < 0 ? Visibility.Collapsed : Visibility.Visible;
        if (branchIndex >= 0)
        {
            var c = activeGroup.Conditions[branchIndex]; conditionParameter.SelectedItem = c.Parameter;
            conditionComparison.SelectedItem = conditionComparison.Items.Cast<Choice<ConditionComparison>>().First(x => x.Value == c.Comparison);
            conditionValue.Text = c.Value;
            conditionValue.IsEnabled = c.Comparison is not (ConditionComparison.Empty or ConditionComparison.NotEmpty);
        }
        reserveRows = rule.ReserveBySize;
        Targets(nameTarget, model.TextTargets(current.Category, sampleMark), rule.NameTarget);
        Targets(quantityTarget, model.NumberTargets(current.Category, sampleMark), rule.QuantityTarget);
        nameOn.IsChecked = rule.NameEnabled;
        parts.Clear(); foreach (var p in rule.Parts) parts.Add(Part(p.Parameter, p.Prefix, p.Suffix));
        quantityOn.IsChecked = rule.QuantityEnabled;
        quantity.Clear(); quantity.Add(Quantity(rule.QuantitySource, rule.Reserve, rule.Unit, rule.Decimals, rule.Rounding));
        loading = false;
        Refresh();
    }

    /// <summary>Target choices of the scope; the stored target stays selectable even when the example element lacks it.</summary>
    static void Targets(ComboBox combo, IReadOnlyList<string> names, string selected)
    {
        var list = names.ToList();
        if (selected.Length > 0 && !list.Contains(selected)) list.Insert(0, selected);
        combo.ItemsSource = list;
        combo.SelectedItem = selected.Length > 0 ? selected : list.FirstOrDefault();
    }

    ListBoxItem Item(string name, bool builtIn)
    {
        var item = new ListBoxItem { Content = Display(name), Tag = name, Padding = new Thickness(4, 2, 4, 2) };
        if (builtIn)
        {
            // Как в BIM2B: встроенные параметры апдейтера выделены цветом.
            item.Themed(ForegroundProperty, "BP.Accent");
            item.ToolTip = name switch
            {
                EngineeringRulesPolicy.WallThicknessSource => "Встроенный: трубы — (Dнар − Dвн)/2; воздуховоды — по таблице «Толщина стенки…» с учётом огнезащиты.",
                EngineeringRulesPolicy.FittingSizeSource => "Встроенный: размер детали по коннекторам — отвод Ø250, переход 500×300-400×200, тройник Ø250-Ø160.",
                EngineeringRulesPolicy.SortedSizeSource => "Встроенный: размер сегмента, у прямоугольного большая сторона первой.",
                EngineeringRulesPolicy.HostLengthSource => "Встроенный: длина основы изоляции (трубы).",
                EngineeringRulesPolicy.HostAreaSource => "Встроенный: фактическая площадь основы изоляции (воздуховода).",
                EngineeringRulesPolicy.CountSource => "Встроенный: 1 шт на элемент.",
                _ => "Встроенный параметр апдейтера."
            };
        }
        return item;
    }

    PartRow Part(string parameter, string prefix = "", string suffix = "")
    {
        var row = new PartRow { Parameter = parameter, Prefix = prefix, Suffix = suffix };
        row.Edited += Refresh;
        return row;
    }

    QuantityRow Quantity(string source, double reserve, QuantityUnit unit, int decimals, QuantityRounding mode)
    {
        var round = Roundings.FirstOrDefault(x => x.Value == (decimals, mode)) ?? Roundings[0];
        var row = new QuantityRow(reserve, Units.First(u => u.Value == unit), round) { Parameter = source };
        row.Edited += Refresh;
        return row;
    }


    string? Text(string parameter) => model.Text(current!.Category, sampleMark, parameter, Rules.WallThickness);
    SourceKind? Kind(string parameter) => model.Source(current!.Category, sampleMark, parameter)?.Kind;

    // ---------- Формулы ----------

    void AddPart()
    {
        if (nameList.SelectedItem is not ListBoxItem { Tag: string name }) return;
        var row = Part(name); parts.Add(row); partsGrid.SelectedItem = row;
        nameOn.IsChecked = true; Refresh();
    }

    void RemovePart()
    {
        if (partsGrid.SelectedItem is PartRow r) { parts.Remove(r); Refresh(); }
    }

    void AddQuantity()
    {
        if (quantityList.SelectedItem is not ListBoxItem { Tag: string name }) return;
        var old = quantity.FirstOrDefault();
        var kind = sample ? Kind(name) : null;
        var unit = kind switch { SourceKind.Length => QuantityUnit.Meter, SourceKind.Area => QuantityUnit.SquareMeter, SourceKind.Count => QuantityUnit.Piece, _ => old?.Unit.Value ?? QuantityUnit.Piece };
        if (name == EngineeringRulesPolicy.HostAreaSource || name == EngineeringRulesPolicy.FittingAreaParameter) unit = QuantityUnit.SquareMeter;
        if (name == EngineeringRulesPolicy.HostLengthSource) unit = QuantityUnit.Meter;
        var (dec, mode) = old?.Rounding.Value ?? (2, QuantityRounding.Up);
        quantity.Clear();
        var replacement = Quantity(name, old != null && old.TryReserve(out _) ? old.Reserve : EngineeringRulesPolicy.DefaultReserve, unit, dec, mode);
        if (old != null) replacement.ReservePercent = old.ReservePercent;
        quantity.Add(replacement);
        quantityOn.IsChecked = true; Refresh();
    }

    void Move(int delta)
    {
        if (partsGrid.SelectedItem is not PartRow row) return;
        int i = parts.IndexOf(row), j = i + delta;
        if (j < 0 || j >= parts.Count) return;
        parts.Move(i, j); partsGrid.SelectedItem = row; Refresh();
    }

    EngineeringRule Editor()
    {
        var q = quantity.FirstOrDefault();
        return new EngineeringRule
        {
            Category = current!.Category,
            NameEnabled = nameOn.IsChecked == true, NameTarget = nameTarget.SelectedItem as string ?? "",
            Parts = parts.Select(p => new NamePart { Parameter = p.Parameter, Prefix = p.Prefix, Suffix = p.Suffix }).ToList(),
            QuantityEnabled = quantityOn.IsChecked == true && q != null, QuantityTarget = quantityTarget.SelectedItem as string ?? "",
            QuantitySource = q?.Parameter ?? EngineeringRulesPolicy.CountSource, Reserve = q?.Reserve ?? 1,
            Unit = q?.Unit.Value ?? QuantityUnit.Piece, Decimals = q?.Rounding.Value.Item1 ?? 2, Rounding = q?.Rounding.Value.Item2 ?? QuantityRounding.Up,
            ReserveBySize = reserveRows.Select(r => new ReserveRow { Systems = [.. r.Systems], MaxSizeMm = r.MaxSizeMm, Reserve = r.Reserve }).ToList()
        };
    }

    void Refresh()
    {
        if (loading || current == null) return;
        foreach (var row in parts) row.Value = sample ? Text(row.Parameter) ?? "" : "";
        var rule = Editor();
        string off = " (заполнение выключено)";
        namePreview.Text = !sample ? "нет элементов для выбранного условия"
            : EngineeringRulesPolicy.ComposeName(rule.Parts, Text) + (rule.NameEnabled ? "" : off);
        if (!sample) { quantityPreview.Text = "нет элементов для выбранного условия"; return; }
        if (quantity.FirstOrDefault() is not { } q) { quantityPreview.Text = "формула пуста"; return; }
        if (!q.TryReserve(out _)) { quantityPreview.Text = "Запас: введите число от 0 до 900 % (например, 7,5)."; return; }
        if (model.Source(current.Category, sampleMark, q.Parameter) is not { } s)
        {
            quantityPreview.Text = q.Parameter == EngineeringRulesPolicy.FittingAreaParameter
                ? $"у семейства нет «{EngineeringRulesPolicy.FittingAreaParameter}» — элемент будет проигнорирован" : $"нет значения «{Display(q.Parameter)}»";
            return;
        }
        double reserve = rule.Reserve;
        var value = EngineeringRulesPolicy.Quantity(rule, s.Kind, s.Value, out string? error, reserve);
        quantityPreview.Text = (value is { } v ? $"{EngineeringRulesPolicy.Format(v, rule.Decimals)} {EngineeringRulesPolicy.UnitLabel(rule.Unit)}" +
                $" (запас {Percent(reserve)})" : "ошибка: " + error)
            + (rule.QuantityEnabled ? "" : off);
    }

    bool Commit(out string? error)
    {
        error = null;
        if (shown?.IsSystems == true)
        {
            if (!systemsPanel.TryRead(out var names, out error)) return false;
            Rules.SystemNames = names;
        }
        if (current == null) return true;
        partsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        quantityGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (current.IsQuantity && quantity.FirstOrDefault() is { } edited && !edited.TryReserve(out _))
        { error = "Запас: введите число от 0 до 900 % (например, 7,5)."; return false; }
        try
        {
            StoreFormula();
            Rules.ValidateConditionalGroups();
            return true;
        }
        catch (InvalidOperationException e) { error = e.Message; return false; }
    }

    void Finish()
    {
        if (!Commit(out string? error)) { MessageBox.Show(this, error, Title); return; }
        DialogResult = true;
    }

    void EditWalls()
    {
        var w = new WallThicknessWindow(Rules.WallThickness) { Owner = this };
        if (w.ShowDialog() == true) { Rules.WallThickness = w.Result!; Refresh(); }
    }

}

internal static class RuleCategoryIcons
{
    static readonly Dictionary<string, Media.ImageSource> cache = [];
    internal static Media.ImageSource Get(string category)
    {
        if (cache.TryGetValue(category, out var saved)) return saved;
        var drawing = new Media.DrawingGroup();
        using (var dc = drawing.Open())
        {
            var blue = Media.Brushes.DeepSkyBlue;
            var edge = new Media.Pen(Media.Brushes.SlateGray, 1.2);
            var tube = new Media.Pen(Media.Brushes.LightSteelBlue, 6) { StartLineCap = Media.PenLineCap.Round, EndLineCap = Media.PenLineCap.Round, LineJoin = Media.PenLineJoin.Round };
            void Path(string data, Media.Pen pen) => dc.DrawGeometry(null, pen, Media.Geometry.Parse(data));
            void Box(double x, double y, double width, double height) => dc.DrawRectangle(Media.Brushes.LightSteelBlue, edge, new Rect(x, y, width, height));
            if (category == "OST_CableTray")
            {
                Path("M3,2 L3,21 M19,2 L19,21 M3,5 L19,5 M3,11 L19,11 M3,17 L19,17", new Media.Pen(blue, 2));
            }
            else if (category == "OST_Conduit")
            {
                Path("M3,19 L3,8 Q3,3 8,3 L20,3 M9,21 L9,12 Q9,9 12,9 L21,9", new Media.Pen(Media.Brushes.Goldenrod, 3));
            }
            else if (category.Contains("Insulations"))
            {
                dc.DrawEllipse(Media.Brushes.Goldenrod, edge, new Point(11, 11), 9, 9);
                dc.DrawEllipse(Media.Brushes.LightSteelBlue, edge, new Point(11, 11), 6, 6);
                dc.DrawEllipse(blue, null, new Point(11, 11), 3, 3);
                if (category == "OST_DuctInsulations") dc.DrawRectangle(null, edge, new Rect(3, 3, 16, 16));
            }
            else if (category.Contains("Duct"))
            {
                if (category.Contains("Fitting")) { Box(1, 8, 20, 7); Box(8, 1, 7, 10); }
                else if (category.Contains("Flex")) { Path("M2,17 C2,4 20,18 20,4", tube); Path("M3,10 L7,13 M8,8 L12,12 M13,8 L17,11", new Media.Pen(blue, 1.5)); }
                else { Box(1, 6, 20, 10); Path("M4,6 L4,16 M18,6 L18,16", new Media.Pen(blue, 2)); }
            }
            else if (category.Contains("Pipe"))
            {
                string path = category.Contains("Fitting") ? "M3,19 L3,8 Q3,3 8,3 L19,3" : category.Contains("Flex") ? "M2,18 C2,2 20,20 20,3" : "M3,18 L19,4";
                Path(path, tube); Path(path, new Media.Pen(blue, 2));
                dc.DrawEllipse(Media.Brushes.LightSteelBlue, edge, new Point(3, 18), 3, 3);
            }
            else
            {
                Path("M4,5 L17,5 L17,17 L4,17 Z M4,5 L17,17", new Media.Pen(blue, 2));
                foreach (var p in new[] { new Point(4, 5), new Point(17, 5), new Point(4, 17), new Point(17, 17) })
                    dc.DrawEllipse(Media.Brushes.LightSteelBlue, edge, p, 2.5, 2.5);
            }
        }
        drawing.Freeze(); var source = new Media.DrawingImage(drawing); source.Freeze();
        cache[category] = source; return source;
    }
}

internal sealed class RuleCreationWindow : Window
{
    readonly ComboBox category = new() { MinHeight = 28, DisplayMemberPath = "Label" };
    readonly ComboBox kind = new() { MinHeight = 28, ItemsSource = new[] { "Наименование (текст)", "Количество (число)" }, SelectedIndex = 0 };
    readonly ComboBox target = new() { MinHeight = 28 };
    readonly IEnumerable<ConditionalRuleGroup> existing;
    readonly Func<string, bool, IReadOnlyList<string>> targets;
    internal Action<string> Warn { get; set; }
    internal string Category => (category.SelectedItem as Choice<string>)?.Value ?? "";
    internal bool IsQuantity => kind.SelectedIndex == 1;
    internal string Target => target.SelectedItem as string ?? "";

    internal RuleCreationWindow(IEnumerable<ConditionalRuleGroup> existing, Func<string, bool, IReadOnlyList<string>> targets, string? initialCategory)
    {
        this.existing = existing; this.targets = targets;
        Warn = message => MessageBox.Show(this, message, Title);
        Title = "Создать правило"; Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) }; Content = panel;
        void Field(string label, UIElement input)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(input);
            ((FrameworkElement)input).Margin = new Thickness(0, 0, 0, 12);
        }
        Field("Категория", category); Field("Тип формулы", kind); Field("Заполняемый параметр", target);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var create = new Button { Content = "Создать", IsDefault = true, MinWidth = 110, MinHeight = 30 };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 110, MinHeight = 30, Margin = new Thickness(8, 0, 0, 0) };
        create.Click += (_, _) => { if (Accept()) DialogResult = true; };
        buttons.Children.Add(create); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        category.ItemsSource = EngineeringRulesPolicy.Categories.OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new Choice<string>(c.Key, c.Label)).ToList();
        category.SelectionChanged += (_, _) => FillTargets(); kind.SelectionChanged += (_, _) => FillTargets();
        category.SelectedItem = category.Items.Cast<Choice<string>>().FirstOrDefault(c => c.Value == initialCategory) ?? category.Items[0];
        BotPlusTheme.Apply(this);
    }

    void FillTargets()
    {
        if (Category.Length == 0) return;
        string standard = IsQuantity ? EngineeringRulesPolicy.DefaultQuantityTarget : EngineeringRulesPolicy.DefaultNameTarget;
        target.ItemsSource = targets(Category, IsQuantity).Append(standard).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        target.SelectedItem = standard;
    }

    internal void Select(string categoryKey, bool quantityRule, string parameter)
    {
        category.SelectedItem = category.Items.Cast<Choice<string>>().First(c => c.Value == categoryKey);
        kind.SelectedIndex = quantityRule ? 1 : 0; target.SelectedItem = parameter;
    }

    internal bool Accept()
    {
        if (Category.Length == 0 || Target.Length == 0) { Warn("Выберите категорию и заполняемый параметр."); return false; }
        if (existing.Any(g => g.Category == Category && g.Target == Target))
        { Warn("В категории уже есть правило для этого параметра. Выберите его в списке слева."); return false; }
        return true;
    }
}

internal sealed class WallRow
{
    public Choice<SectionShape> Shape { get; set; } = WallThicknessWindow.Shapes[0];
    public bool FireProtected { get; set; }
    public double MaxSizeMm { get; set; }
    public double ThicknessMm { get; set; }
}

/// <summary>Duct wall table: shape, fire protection, size limit and thickness; rows apply from the smaller size up.</summary>
internal sealed class WallThicknessWindow : Window
{
    internal static readonly Choice<SectionShape>[] Shapes = [new(SectionShape.Round, "Круглое"), new(SectionShape.Rectangular, "Прямоугольное/овальное")];
    readonly ObservableCollection<WallRow> rows = [];
    readonly TextBox marker = new() { MinWidth = 160 }, target = new() { MinWidth = 220 };
    readonly DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column };
    internal WallThicknessSettings? Result { get; private set; }

    internal WallThicknessWindow(WallThicknessSettings settings)
    {
        Title = "Толщина стенки"; Width = 640; Height = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Text =
            "Трубы: (наружный − внутренний диаметр)/2. Воздуховоды: строки своей формы применяются от меньшего размера к большему — " +
            "берётся первая строка, предел которой не меньше диаметра или большей стороны. Для огнезащищённых воздуховодов — строки с отметкой, " +
            "при их отсутствии — обычные. Значения по умолчанию проверьте по нормам проекта." };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var options = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        options.Children.Add(Line("Огнезащита — если в имени типа изоляции есть:", marker));
        options.Children.Add(Line("Записывать в параметр экземпляра (пусто — не записывать):", target));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Btn("Добавить", () => rows.Add(new WallRow { MaxSizeMm = 1000, ThicknessMm = 0.7 })));
        buttons.Children.Add(Btn("Удалить", () => { if (grid.SelectedItem is WallRow r) rows.Remove(r); }));
        buttons.Children.Add(Btn("По умолчанию", () => Fill(WallThicknessSettings.Defaults())));
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 85, Margin = new Thickness(40, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        options.Children.Add(buttons);
        DockPanel.SetDock(options, Dock.Bottom); root.Children.Add(options);
        grid.ItemsSource = rows;
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "Сечение", ItemsSource = Shapes, SelectedItemBinding = new Binding(nameof(WallRow.Shape)), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Огнезащита", Binding = new Binding(nameof(WallRow.FireProtected)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "До размера, мм", Binding = new Binding(nameof(WallRow.MaxSizeMm)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Толщина, мм", Binding = new Binding(nameof(WallRow.ThicknessMm)), Width = 100 });
        root.Children.Add(grid);
        marker.Text = settings.FireMarker; target.Text = settings.Target;
        Fill(settings.Rows);
        BotPlusTheme.Apply(this);
    }

    static StackPanel Line(string label, UIElement input)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        line.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        line.Children.Add(input);
        return line;
    }

    static Button Btn(string text, Action click) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) }; b.Click += (_, _) => click(); return b; }

    void Fill(IEnumerable<WallThicknessRow> source)
    {
        rows.Clear();
        foreach (var r in source.OrderBy(r => r.Shape).ThenBy(r => r.FireProtected).ThenBy(r => r.MaxSizeMm))
            rows.Add(new WallRow { Shape = Shapes.First(s => s.Value == r.Shape), FireProtected = r.FireProtected, MaxSizeMm = r.MaxSizeMm, ThicknessMm = r.ThicknessMm });
    }

    void Accept()
    {
        if (!grid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var settings = new WallThicknessSettings
        {
            FireMarker = marker.Text.Trim(), Target = target.Text.Trim(),
            Rows = rows.Select(r => new WallThicknessRow { Shape = r.Shape.Value, FireProtected = r.FireProtected, MaxSizeMm = r.MaxSizeMm, ThicknessMm = r.ThicknessMm }).ToList()
        };
        try { settings.Validate(); }
        catch (InvalidOperationException e) { MessageBox.Show(this, e.Message, Title); return; }
        Result = settings; DialogResult = true;
    }
}
