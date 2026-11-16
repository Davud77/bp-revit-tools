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

namespace HeatLossViewer.Mep;

internal sealed record Choice<T>(T Value, string Label) { public override string ToString() => Label; }

/// <summary>Entry of the scope list: category and «Маркировка типоразмера» (or «Без типа»), as in BIM2B.</summary>
internal sealed class ScopeChoice(string category, string categoryLabel, string? mark, List<string> types)
{
    public string Category { get; } = category;
    public string? Mark { get; } = mark;
    public List<string> Types { get; } = types;
    public bool Configured { get; set; }
    public override string ToString() => $"{(Configured ? "● " : "")}{categoryLabel} : {Mark ?? EngineeringRulesPolicy.NoTypeLabel}";
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
    Choice<double> reserve; Choice<QuantityUnit> unit; Choice<(int, QuantityRounding)> rounding;
    public QuantityRow(Choice<double> reserve, Choice<QuantityUnit> unit, Choice<(int, QuantityRounding)> rounding) { this.reserve = reserve; this.unit = unit; this.rounding = rounding; }
    public string Parameter { get; init; } = "";
    public string Label => EngineeringRulesWindow.Display(Parameter);
    public Choice<double> Reserve { get => reserve; set { reserve = value; Changed(); } }
    public Choice<QuantityUnit> Unit { get => unit; set { unit = value; Changed(); } }
    public Choice<(int, QuantityRounding)> Rounding { get => rounding; set { rounding = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action? Edited;
    void Changed() { PropertyChanged?.Invoke(this, new(null)); Edited?.Invoke(); }
}

/// <summary>
/// «Настройки апдейтера» в компоновке BIM2B: область «Категория : Маркировка», для наименования и количества —
/// список параметров, стрелки и формула, строка примера и три кнопки. Работает на копии: «Отменить и закрыть»
/// не меняет ни правила, ни модель.
/// </summary>
internal sealed class EngineeringRulesWindow : Window
{
    static readonly Choice<double>[] Reserves = [new(1, "Нет"), .. new[] { 1.05, 1.1, 1.15, 1.2, 1.25, 1.3, 1.5 }.Select(v => new Choice<double>(v, Percent(v)))];
    static readonly Choice<QuantityUnit>[] Units =
        [new(QuantityUnit.Meter, "Метры"), new(QuantityUnit.Millimeter, "Миллиметры"), new(QuantityUnit.SquareMeter, "Кв. метры"), new(QuantityUnit.Piece, "Штуки")];
    static readonly Choice<(int, QuantityRounding)>[] Roundings =
    [
        new((6, QuantityRounding.Nearest), "Нет"),
        new((0, QuantityRounding.Up), "До целого вверх"), new((1, QuantityRounding.Up), "До 0,1 вверх"), new((2, QuantityRounding.Up), "До 0,01 вверх"),
        new((0, QuantityRounding.Nearest), "До целого"), new((1, QuantityRounding.Nearest), "До 0,1"), new((2, QuantityRounding.Nearest), "До 0,01")
    ];

    readonly IUpdaterModel model;
    readonly ComboBox scope = new() { MinHeight = 26, FontSize = 13 };
    readonly TextBlock types = new() { Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 4, 0, 0) };
    readonly CheckBox nameOn = new() { Content = "Заполнять", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    readonly CheckBox quantityOn = new() { Content = "Заполнять", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    readonly ComboBox nameTarget = new() { MinWidth = 240, FontSize = 13, FontWeight = FontWeights.SemiBold }, quantityTarget = new() { MinWidth = 240, FontSize = 13, FontWeight = FontWeights.SemiBold };
    readonly ListBox nameList = new(), quantityList = new();
    readonly ObservableCollection<PartRow> parts = [];
    readonly ObservableCollection<QuantityRow> quantity = [];
    List<ReserveRow> reserveRows = [];
    readonly Button reserveButton = new() { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, MinHeight = 26,
        ToolTip = "Свой запас по диаметру (у прямоугольного — по большей стороне) и, при необходимости, по типам систем: например канализация Ø50 — 15 %, Ø110 — 10 %." };
    readonly DataGrid partsGrid = Grid0(), quantityGrid = Grid0();
    readonly TextBlock namePreview = new() { TextWrapping = TextWrapping.Wrap }, quantityPreview = new() { TextWrapping = TextWrapping.Wrap };
    ScopeChoice? current;
    bool sample;
    bool loading;

    internal EngineeringRuleSet Rules { get; private set; }
    /// <summary>Scope to refresh after saving, or null for «Сохранить настройки и закрыть».</summary>
    internal (string Category, string? Mark)? UpdateScope { get; private set; }

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
        Title = "Настройки апдейтера"; Width = 980; Height = 760; MinWidth = 820; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(12) }; Content = root;
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        int row = 0;
        void Add(UIElement e) { Grid.SetRow(e, row++); root.Children.Add(e); }

        // Область и сервисные кнопки.
        var top = new DockPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        tools.Children.Add(Small("Имена систем…", EditSystems, "ADSK_Система_Имя по типу системы: правила, сети и предпросмотр по типам систем проекта."));
        tools.Children.Add(Small("Толщина стенки…", EditWalls, "Таблица толщины стенки воздуховодов по форме сечения, размеру и огнезащите; запись в параметр."));
        tools.Children.Add(Small("Импорт…", Import, "Загрузить правила из JSON: выбранные правила сопоставляются по категории и маркировке типоразмера."));
        tools.Children.Add(Small("Экспорт…", Export, "Сохранить все правила и таблицу толщины стенки в JSON."));
        DockPanel.SetDock(tools, Dock.Right); top.Children.Add(tools); top.Children.Add(scope);
        Add(top);
        Add(types);

        // Наименование.
        Add(Header(nameOn, nameTarget));
        Add(Body(nameList, partsGrid, AddPart, RemovePart, true));
        Add(Example(namePreview));

        // Количество.
        Add(Header(quantityOn, quantityTarget));
        reserveButton.Click += (_, _) => EditReserve();
        Add(Body(quantityList, quantityGrid, AddQuantity, () => { quantity.Clear(); Refresh(); }, false, reserveButton));
        Add(Example(quantityPreview));

        // Кнопки, как в BIM2B: широкая основная и две под ней.
        var buttons = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.RowDefinitions.Add(new RowDefinition()); buttons.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) }); buttons.RowDefinitions.Add(new RowDefinition());
        var update = Wide("Сохранить настройки и обновить все элементы выбранного типа", true);
        var save = Wide("Сохранить настройки и закрыть", false);
        var cancel = Wide("Отменить и закрыть", false); cancel.IsCancel = true;
        Grid.SetColumnSpan(update, 3); buttons.Children.Add(update);
        Grid.SetRow(save, 2); buttons.Children.Add(save);
        Grid.SetRow(cancel, 2); Grid.SetColumn(cancel, 2); buttons.Children.Add(cancel);
        update.Click += (_, _) => Finish(true); save.Click += (_, _) => Finish(false);
        Add(buttons);

        // Формула наименования.
        partsGrid.ItemsSource = parts;
        partsGrid.Columns.Add(new DataGridTextColumn { Header = "Имя параметра", Binding = new Binding(nameof(PartRow.Label)), IsReadOnly = true, Width = 190 });
        partsGrid.Columns.Add(new DataGridTextColumn { Header = "Префикс", Binding = new Binding(nameof(PartRow.Prefix)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 90 });
        partsGrid.Columns.Add(new DataGridTextColumn { Header = "Значение", Binding = new Binding(nameof(PartRow.Value)), IsReadOnly = true, Width = 210 });
        partsGrid.Columns.Add(new DataGridTextColumn { Header = "Суффикс", Binding = new Binding(nameof(PartRow.Suffix)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 80 });
        // Формула количества.
        quantityGrid.ItemsSource = quantity;
        quantityGrid.Columns.Add(new DataGridTextColumn { Header = "Имя параметра", Binding = new Binding(nameof(QuantityRow.Label)), IsReadOnly = true, Width = 190 });
        quantityGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Запас (общий)", ItemsSource = Reserves, SelectedItemBinding = new Binding(nameof(QuantityRow.Reserve)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 140 });
        quantityGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Единицы", ItemsSource = Units, SelectedItemBinding = new Binding(nameof(QuantityRow.Unit)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 110 });
        quantityGrid.Columns.Add(new DataGridComboBoxColumn { Header = "Округление", ItemsSource = Roundings, SelectedItemBinding = new Binding(nameof(QuantityRow.Rounding)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 130 });

        nameList.MouseDoubleClick += (_, _) => AddPart();
        quantityList.MouseDoubleClick += (_, _) => AddQuantity();
        foreach (var c in new[] { nameOn, quantityOn }) { c.Checked += (_, _) => Refresh(); c.Unchecked += (_, _) => Refresh(); }
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

    // ---------- Области ----------

    void FillScopes(string? category, string? mark)
    {
        var list = new List<ScopeChoice>();
        foreach (var (key, label) in EngineeringRulesPolicy.Categories)
            foreach (var (m, names) in model.Marks(key))
                if (!EngineeringRulesPolicy.IsIgnored(m)) list.Add(new ScopeChoice(key, label, m, names));
        // Rules of marks no longer in the project stay editable (and removable).
        foreach (var r in Rules.Rules.Where(r => !list.Any(s => s.Category == r.Category && s.Mark == r.TypeMark)))
            list.Add(new ScopeChoice(r.Category, EngineeringRulesPolicy.CategoryLabel(r.Category), r.TypeMark, []));
        foreach (var s in list) s.Configured = Rules.Find(s.Category, s.Mark) is { IsActive: true };
        loading = true;
        scope.ItemsSource = list;
        scope.SelectedItem = list.FirstOrDefault(s => s.Category == category && s.Mark == mark)
            ?? list.FirstOrDefault(s => s.Category == category) ?? list.FirstOrDefault();
        loading = false;
        Load();
    }

    void SwitchScope()
    {
        if (!Commit(out string? error))
        {
            MessageBox.Show(this, error, Title);
            loading = true; scope.SelectedItem = current; loading = false;
            return;
        }
        if (current != null) current.Configured = Rules.Find(current.Category, current.Mark) is { IsActive: true };
        scope.Items.Refresh();
        Load();
    }

    void Load()
    {
        current = scope.SelectedItem as ScopeChoice;
        if (current == null) { IsEnabled = true; types.Text = "В проекте нет элементов поддерживаемых категорий."; return; }
        loading = true;
        types.Text = current.Types.Count == 0 ? "Типов с этой маркировкой в проекте нет." : "Типы: " + string.Join(", ", current.Types);
        types.ToolTip = string.Join("\n", current.Types);
        sample = model.HasSample(current.Category, current.Mark);
        var names = model.Parameters(current.Category, current.Mark);

        nameList.Items.Clear();
        foreach (var v in EngineeringRulesPolicy.VirtualNameSources) nameList.Items.Add(Item(v, true));
        foreach (var n in names) nameList.Items.Add(Item(n, false));
        quantityList.Items.Clear();
        foreach (var v in EngineeringRulesPolicy.VirtualQuantitySources) quantityList.Items.Add(Item(v, true));
        foreach (var n in names.Where(n => !sample || Kind(n) is SourceKind.Length or SourceKind.Area or SourceKind.Number))
            quantityList.Items.Add(Item(n, false));
        if (current.Category == "OST_DuctFitting" && !names.Contains(EngineeringRulesPolicy.FittingAreaParameter))
            quantityList.Items.Add(Item(EngineeringRulesPolicy.FittingAreaParameter, false));



        var rule = Rules.Find(current.Category, current.Mark)?.Clone() ?? Default(current.Category, current.Mark);
        reserveRows = rule.ReserveBySize;
        ReserveLabel();
        Targets(nameTarget, model.TextTargets(current.Category, current.Mark), rule.NameTarget);
        Targets(quantityTarget, model.NumberTargets(current.Category, current.Mark), rule.QuantityTarget);
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
        var r = Reserves.FirstOrDefault(x => Math.Abs(x.Value - reserve) < 1e-9) ?? new Choice<double>(reserve, reserve.ToString(CultureInfo.CurrentCulture));
        var round = Roundings.FirstOrDefault(x => x.Value == (decimals, mode)) ?? Roundings[0];
        var row = new QuantityRow(r, Units.First(u => u.Value == unit), round) { Parameter = source };
        row.Edited += Refresh;
        return row;
    }

    static EngineeringRule Default(string category, string? mark)
    {
        var (source, unit, decimals) = category switch
        {
            "OST_PipeCurves" or "OST_FlexPipeCurves" or "OST_DuctCurves" or "OST_FlexDuctCurves" or "OST_CableTray" or "OST_Conduit" => ("Длина", QuantityUnit.Meter, 2),
            "OST_DuctFitting" => (EngineeringRulesPolicy.FittingAreaParameter, QuantityUnit.SquareMeter, 2),
            "OST_PipeInsulations" => (EngineeringRulesPolicy.HostLengthSource, QuantityUnit.Meter, 2),
            "OST_DuctInsulations" => (EngineeringRulesPolicy.HostAreaSource, QuantityUnit.SquareMeter, 2),
            _ => (EngineeringRulesPolicy.CountSource, QuantityUnit.Piece, 0)
        };
        string size = category is "OST_DuctFitting" or "OST_PipeFitting" ? EngineeringRulesPolicy.FittingSizeSource : EngineeringRulesPolicy.SortedSizeSource;
        return new EngineeringRule
        {
            Category = category, TypeMark = mark,
            Parts = [new NamePart { Parameter = EngineeringRulesPolicy.TypeNameSource }, new NamePart { Parameter = size, Prefix = " " }],
            QuantitySource = source, Unit = unit, Decimals = decimals, Reserve = EngineeringRulesPolicy.DefaultReserve
        };
    }

    string? Text(string parameter) => model.Text(current!.Category, current.Mark, parameter, Rules.WallThickness);
    SourceKind? Kind(string parameter) => model.Source(current!.Category, current.Mark, parameter)?.Kind;

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
        quantity.Add(Quantity(name, old?.Reserve.Value ?? EngineeringRulesPolicy.DefaultReserve, unit, dec, mode));
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
            Category = current!.Category, TypeMark = current.Mark,
            NameEnabled = nameOn.IsChecked == true, NameTarget = nameTarget.SelectedItem as string ?? "",
            Parts = parts.Select(p => new NamePart { Parameter = p.Parameter, Prefix = p.Prefix, Suffix = p.Suffix }).ToList(),
            QuantityEnabled = quantityOn.IsChecked == true && q != null, QuantityTarget = quantityTarget.SelectedItem as string ?? "",
            QuantitySource = q?.Parameter ?? EngineeringRulesPolicy.CountSource, Reserve = q?.Reserve.Value ?? 1,
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
        namePreview.Text = !sample ? "нет элементов этой области в проекте"
            : EngineeringRulesPolicy.ComposeName(rule.Parts, Text) + (rule.NameEnabled ? "" : off);
        if (!sample) { quantityPreview.Text = "нет элементов этой области в проекте"; return; }
        if (quantity.FirstOrDefault() is not { } q) { quantityPreview.Text = "формула пуста"; return; }
        if (model.Source(current.Category, current.Mark, q.Parameter) is not { } s)
        {
            quantityPreview.Text = q.Parameter == EngineeringRulesPolicy.FittingAreaParameter
                ? $"у семейства нет «{EngineeringRulesPolicy.FittingAreaParameter}» — элемент будет проигнорирован" : $"нет значения «{Display(q.Parameter)}»";
            return;
        }
        var (size, systems) = model.ReserveContext(current.Category, current.Mark);
        double reserve = EngineeringRulesPolicy.ReserveFor(rule, size, systems);
        var value = EngineeringRulesPolicy.Quantity(rule, s.Kind, s.Value, out string? error, reserve);
        quantityPreview.Text = (value is { } v ? $"{EngineeringRulesPolicy.Format(v, rule.Decimals)} {EngineeringRulesPolicy.UnitLabel(rule.Unit)}" +
                $" (запас {Percent(reserve)}{(size is { } d && rule.ReserveBySize.Count > 0 ? $", размер {EngineeringRulesPolicy.Format(d, 1)} мм" : "")})" : "ошибка: " + error)
            + (rule.QuantityEnabled ? "" : off);
    }

    bool Commit(out string? error)
    {
        error = null;
        if (current == null) return true;
        partsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        quantityGrid.CommitEdit(DataGridEditingUnit.Row, true);
        try { Rules.Put(Editor()); return true; }
        catch (InvalidOperationException e) { error = e.Message; return false; }
    }

    void Finish(bool update)
    {
        if (!Commit(out string? error)) { MessageBox.Show(this, error, Title); return; }
        UpdateScope = update && current != null ? (current.Category, current.Mark) : null;
        DialogResult = true;
    }

    // ---------- Толщина стенки, импорт, экспорт ----------

    void ReserveLabel() => reserveButton.Content = reserveRows.Count == 0 ? "Запас по размеру…" : $"Запас по размеру… ({reserveRows.Count})";

    void EditReserve()
    {
        var w = new ReserveWindow(reserveRows, quantity.FirstOrDefault()?.Reserve.Value ?? EngineeringRulesPolicy.DefaultReserve) { Owner = this };
        if (w.ShowDialog() != true) return;
        reserveRows = w.Result!;
        ReserveLabel(); Refresh();
    }

    void EditWalls()
    {
        var w = new WallThicknessWindow(Rules.WallThickness) { Owner = this };
        if (w.ShowDialog() == true) { Rules.WallThickness = w.Result!; Refresh(); }
    }

    void EditSystems()
    {
        var w = new SystemNamesWindow(Rules.SystemNames, model.SystemTypes()) { Owner = this };
        if (w.ShowDialog() == true) Rules.SystemNames = w.Result!;
    }

    void Export()
    {
        if (!Commit(out string? error)) { MessageBox.Show(this, error, Title); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Настройки апдейтера (*.json)|*.json", FileName = "BotPlus-апдейтер.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { System.IO.File.WriteAllText(dialog.FileName, Rules.ToJson()); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { MessageBox.Show(this, "Не удалось сохранить файл: " + e.Message, Title); }
    }

    void Import()
    {
        if (!Commit(out string? error)) { MessageBox.Show(this, error, Title); return; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Настройки апдейтера (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        EngineeringRuleSet incoming;
        try { incoming = EngineeringRuleSet.FromJson(System.IO.File.ReadAllText(dialog.FileName)); }
        catch (Exception e) when (e is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "Не удалось прочитать настройки: " + e.Message, Title); return; }
        var pick = new ImportWindow(incoming) { Owner = this };
        if (pick.ShowDialog() != true) return;
        try
        {
            int n = Rules.Merge(incoming, pick.Selected.Contains, pick.Walls, pick.Systems);
            FillScopes(current?.Category, current?.Mark);
            MessageBox.Show(this, $"Импортировано правил: {n}." + (pick.Walls ? "\nТаблица толщины стенки заменена." : "") + (pick.Systems ? "\nПравила имён систем заменены." : ""), Title);
        }
        catch (InvalidOperationException e) { MessageBox.Show(this, e.Message, Title); }
    }
}

/// <summary>Import: each rule of the file with a checkbox, matched by category and «Маркировка типоразмера».</summary>
internal sealed class ImportWindow : Window
{
    readonly List<(CheckBox Box, EngineeringRule Rule)> boxes = [];
    readonly CheckBox walls = new() { Content = "Таблица толщины стенки и параметр записи", Margin = new Thickness(0, 8, 0, 0) };
    internal HashSet<EngineeringRule> Selected { get; } = [];
    internal bool Walls => walls.IsChecked == true;
    readonly CheckBox systems = new() { Content = "Правила ADSK_Система_Имя", Margin = new Thickness(0, 4, 0, 0) };
    internal bool Systems => systems.IsChecked == true;

    internal ImportWindow(EngineeringRuleSet incoming)
    {
        Title = "Импорт настроек апдейтера"; Width = 520; Height = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var hint = new TextBlock { Text = "Отметьте правила для импорта. Правило с той же категорией и маркировкой типоразмера будет заменено.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "Импортировать", IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 };
        ok.Click += (_, _) => { foreach (var (b, r) in boxes) if (b.IsChecked == true) Selected.Add(r); DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        DockPanel.SetDock(systems, Dock.Bottom); root.Children.Add(systems);
        DockPanel.SetDock(walls, Dock.Bottom); root.Children.Add(walls);
        var list = new StackPanel();
        foreach (var rule in incoming.Rules.OrderBy(r => r.Category).ThenBy(r => r.TypeMark))
        {
            string label = EngineeringRulesPolicy.CategoryLabel(rule.Category);
            var box = new CheckBox { Content = $"{label} : {rule.ScopeLabel}", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
            boxes.Add((box, rule)); list.Children.Add(box);
        }
        if (boxes.Count == 0) list.Children.Add(new TextBlock { Text = "В файле нет правил.", Opacity = 0.7 });
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        BotPlusTheme.Apply(this);
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
