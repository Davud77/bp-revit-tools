using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.Revit.DB;
using BotPlus.Shared;
using Binding = System.Windows.Data.Binding;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using GroupBox = System.Windows.Controls.GroupBox;
using DockPanel = System.Windows.Controls.DockPanel;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;
using Dock = System.Windows.Controls.Dock;

namespace HeatLossViewer.Mep;

internal sealed record TypeChoice(string? Key, string Name) { public override string ToString() => Name; }
internal sealed record CategoryChoice(string Key, string Label) { public override string ToString() => Label; }
internal sealed record Choice<T>(T Value, string Label) { public override string ToString() => Label; }

internal sealed class PartRow : INotifyPropertyChanged
{
    string parameter = "", prefix = "", suffix = "", value = "";
    public string Parameter { get => parameter; set { parameter = value ?? ""; Changed(nameof(Parameter)); } }
    public string Prefix { get => prefix; set { prefix = value ?? ""; Changed(nameof(Prefix)); } }
    public string Suffix { get => suffix; set { suffix = value ?? ""; Changed(nameof(Suffix)); } }
    public string Value { get => value; set { this.value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action? Edited;
    void Changed(string name) { PropertyChanged?.Invoke(this, new(name)); Edited?.Invoke(); }
}

/// <summary>Rules editor. Works on a copy: Cancel discards every change; the caller commits <see cref="Rules"/>.</summary>
internal sealed class EngineeringRulesWindow : Window
{
    readonly Document doc;
    readonly ComboBox category = new() { MinWidth = 260 }, type = new() { MinWidth = 260 };
    readonly CheckBox nameOn = new() { Content = "Заполнять наименование" }, quantityOn = new() { Content = "Заполнять количество" };
    readonly TextBox nameTarget = new() { MinWidth = 200 }, quantityTarget = new() { MinWidth = 200 };
    readonly ObservableCollection<PartRow> parts = [];
    readonly DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, SelectionMode = DataGridSelectionMode.Single, Height = 190, HeadersVisibility = DataGridHeadersVisibility.Column };
    readonly DataGridComboBoxColumn partParameter = new() { Header = "Имя параметра", Width = new DataGridLength(1, DataGridLengthUnitType.Star) };
    readonly ComboBox source = new() { MinWidth = 200 }, unit = new() { MinWidth = 70 }, rounding = new() { MinWidth = 140 };
    readonly TextBox reserve = new() { MinWidth = 70 }, decimals = new() { MinWidth = 50 };
    readonly TextBlock namePreview = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) },
        quantityPreview = new() { Margin = new Thickness(0, 6, 0, 0) }, scopeHint = new() { Margin = new Thickness(0, 6, 0, 0), Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    CategoryChoice? currentCategory; TypeChoice? currentType;
    bool loading;
    Element? sample;

    internal EngineeringRuleSet Rules { get; }
    /// <summary>Scope to refresh after saving, or null for «Сохранить и закрыть».</summary>
    internal (string Category, string? TypeKey)? UpdateScope { get; private set; }

    internal EngineeringRulesWindow(Document doc, EngineeringRuleSet rules, string? initialCategory, string? initialType)
    {
        this.doc = doc;
        Rules = EngineeringRuleSet.FromJson(rules.ToJson());
        Title = "Апдейтер инженерных параметров"; Width = 760; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanResizeWithGrip; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(14) }; Content = root;

        var scope = new Grid();
        scope.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scope.ColumnDefinitions.Add(new ColumnDefinition());
        scope.RowDefinitions.Add(new RowDefinition()); scope.RowDefinitions.Add(new RowDefinition());
        Cell(scope, new TextBlock { Text = "Категория", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 4) }, 0, 0);
        Cell(scope, category, 0, 1); Cell(scope, new TextBlock { Text = "Тип", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 0) }, 1, 0);
        Cell(scope, type, 1, 1); type.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(scope); root.Children.Add(scopeHint);

        // Name
        var name = new StackPanel();
        name.Children.Add(Line(nameOn, new TextBlock { Text = "Параметр:", Margin = new Thickness(18, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }, nameTarget));
        grid.ItemsSource = parts;
        grid.Columns.Add(partParameter);
        partParameter.SelectedItemBinding = new Binding(nameof(PartRow.Parameter)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        grid.Columns.Add(new DataGridTextColumn { Header = "Префикс", Width = 90, Binding = new Binding(nameof(PartRow.Prefix)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
        grid.Columns.Add(new DataGridTextColumn { Header = "Значение", Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true, Binding = new Binding(nameof(PartRow.Value)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Суффикс", Width = 90, Binding = new Binding(nameof(PartRow.Suffix)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
        var tools = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        tools.Children.Add(Tool("Добавить", () => { var row = Row(new NamePart { Parameter = EngineeringRulesPolicy.TypeNameSource }); parts.Add(row); grid.SelectedItem = row; Refresh(); }));
        tools.Children.Add(Tool("Удалить", () => { if (grid.SelectedItem is PartRow r) { parts.Remove(r); Refresh(); } }));
        tools.Children.Add(Tool("Вверх", () => Move(-1)));
        tools.Children.Add(Tool("Вниз", () => Move(1)));
        var table = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(tools, Dock.Right); table.Children.Add(tools); table.Children.Add(grid);
        name.Children.Add(table); name.Children.Add(namePreview);
        root.Children.Add(new GroupBox { Header = "Наименование", Content = name, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(8) });

        // Quantity
        var quantity = new StackPanel();
        quantity.Children.Add(Line(quantityOn, new TextBlock { Text = "Параметр:", Margin = new Thickness(18, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }, quantityTarget));
        quantity.Children.Add(Line(Label("Исходный параметр"), source, Label("Запас, коэфф.", 14), reserve));
        quantity.Children.Add(Line(Label("Единицы"), unit, Label("Знаков", 14), decimals, Label("Округление", 14), rounding));
        quantity.Children.Add(quantityPreview);
        root.Children.Add(new GroupBox { Header = "Количество", Content = quantity, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(8) });
        rounding.ItemsSource = new[] { new Choice<QuantityRounding>(QuantityRounding.Up, "вверх"), new Choice<QuantityRounding>(QuantityRounding.Nearest, "математически") };

        root.Children.Add(new TextBlock { Text = "Правила хранятся в проекте. После сохранения значения обновляются автоматически при создании и изменении элементов. " +
            "«Без типа» — правило категории для типов без собственного правила.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 10, 0, 0) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var update = new Button { Content = "Сохранить и обновить", MinWidth = 150, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var save = new Button { Content = "Сохранить и закрыть", MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", MinWidth = 85, IsCancel = true };
        buttons.Children.Add(update); buttons.Children.Add(save); buttons.Children.Add(cancel); root.Children.Add(buttons);
        update.ToolTip = "Сохранить правила и обновить все элементы выбранной категории и типа.";
        update.Click += (_, _) => Finish(true);
        save.Click += (_, _) => Finish(false);

        foreach (var c in new Control[] { nameOn, quantityOn, nameTarget, quantityTarget, source, unit, reserve, decimals, rounding })
            switch (c)
            {
                case CheckBox b: b.Checked += (_, _) => Refresh(); b.Unchecked += (_, _) => Refresh(); break;
                case TextBox t: t.TextChanged += (_, _) => Refresh(); break;
                case ComboBox x: x.SelectionChanged += (_, _) => { if (x == source) Units(); Refresh(); }; break;
            }

        var categories = EngineeringEngine.Categories.Select(c => new CategoryChoice(c.Category.ToString(), c.Label)).ToList();
        category.ItemsSource = categories;
        category.SelectionChanged += (_, _) => { if (!loading) SwitchScope(true); };
        type.SelectionChanged += (_, _) => { if (!loading) SwitchScope(false); };
        loading = true;
        category.SelectedItem = categories.FirstOrDefault(c => c.Key == initialCategory) ?? categories[0];
        FillTypes(initialType);
        loading = false;
        Load();
        BotPlusTheme.Apply(this);
    }

    static void Cell(Grid g, UIElement e, int row, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); g.Children.Add(e); }
    static TextBlock Label(string text, double left = 0) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 6, 0) };
    static StackPanel Line(params UIElement[] items)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var i in items) line.Children.Add(i);
        return line;
    }
    static Button Tool(string text, Action click) { var b = new Button { Content = text, MinWidth = 85, Margin = new Thickness(0, 0, 0, 6) }; b.Click += (_, _) => click(); return b; }

    PartRow Row(NamePart part)
    {
        var row = new PartRow { Parameter = part.Parameter, Prefix = part.Prefix, Suffix = part.Suffix };
        row.Edited += Refresh;
        return row;
    }

    void Move(int delta)
    {
        if (grid.SelectedItem is not PartRow row) return;
        int i = parts.IndexOf(row), j = i + delta;
        if (j < 0 || j >= parts.Count) return;
        parts.Move(i, j); grid.SelectedItem = row; Refresh();
    }

    void FillTypes(string? selectKey)
    {
        var c = (CategoryChoice)category.SelectedItem;
        var bic = Enum.Parse<BuiltInCategory>(c.Key);
        var types = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType().Cast<ElementType>()
            .Select(t => new TypeChoice(t.UniqueId, string.IsNullOrEmpty(t.FamilyName) ? t.Name : t.FamilyName + ": " + t.Name))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        // Fittings and other loadable families: list the types actually used, they can be numerous otherwise.
        types.Insert(0, new TypeChoice(null, "Без типа (все типы категории)"));
        type.ItemsSource = types;
        type.SelectedItem = types.FirstOrDefault(t => t.Key == selectKey) ?? types[0];
    }

    void SwitchScope(bool categoryChanged)
    {
        if (!Commit(out string? error))
        {
            MessageBox.Show(this, error, Title);
            loading = true;
            category.SelectedItem = currentCategory;
            if (categoryChanged) FillTypes(currentType?.Key); else type.SelectedItem = currentType;
            loading = false;
            return;
        }
        if (categoryChanged) { loading = true; FillTypes(null); loading = false; }
        Load();
    }

    void Load()
    {
        loading = true;
        currentCategory = (CategoryChoice)category.SelectedItem;
        currentType = (TypeChoice)type.SelectedItem;
        var all = EngineeringEngine.Instances(doc, currentCategory.Key, currentType.Key).Take(50).ToList();
        sample = all.FirstOrDefault();
        var rule = Rules.Find(currentCategory.Key, currentType.Key)?.Clone() ?? Default(currentCategory.Key, currentType);
        var names = EngineeringRulesPolicy.VirtualNameSources.Concat(EngineeringEngine.ParameterNames(all.Count > 0 ? all
            : EngineeringEngine.Instances(doc, currentCategory.Key, null).Take(20))).ToList();
        partParameter.ItemsSource = names;
        var quantitySources = new List<string> { EngineeringRulesPolicy.CountSource };
        quantitySources.AddRange(names.Where(n => !n.StartsWith('@') && (sample == null || EngineeringEngine.Kind(sample, n) is SourceKind.Length or SourceKind.Area or SourceKind.Number)));
        if (!quantitySources.Contains(rule.QuantitySource)) quantitySources.Add(rule.QuantitySource);
        source.ItemsSource = quantitySources;
        nameOn.IsChecked = rule.NameEnabled; nameTarget.Text = rule.NameTarget;
        parts.Clear(); foreach (var p in rule.Parts) parts.Add(Row(p));
        quantityOn.IsChecked = rule.QuantityEnabled; quantityTarget.Text = rule.QuantityTarget;
        source.SelectedItem = rule.QuantitySource;
        Units(); unit.SelectedItem = ((IEnumerable<Choice<QuantityUnit>>)unit.ItemsSource).FirstOrDefault(u => u.Value == rule.Unit) ?? unit.Items[0];
        reserve.Text = rule.Reserve.ToString(CultureInfo.CurrentCulture);
        decimals.Text = rule.Decimals.ToString(CultureInfo.InvariantCulture);
        rounding.SelectedItem = ((IEnumerable<Choice<QuantityRounding>>)rounding.ItemsSource).First(r => r.Value == rule.Rounding);
        scopeHint.Text = (all.Count == 0 ? "В проекте нет элементов этой области — пример недоступен. " : $"Пример: элемент {sample!.Id}. ") +
            (currentType.Key != null && Rules.Find(currentCategory.Key, currentType.Key) == null && Rules.Find(currentCategory.Key, null) is { IsActive: true }
                ? "Сейчас для типа действует правило «Без типа»." : "");
        loading = false;
        Refresh();
    }

    static EngineeringRule Default(string category, TypeChoice type)
    {
        bool curve = category is "OST_PipeCurves" or "OST_FlexPipeCurves" or "OST_DuctCurves" or "OST_FlexDuctCurves" or "OST_CableTray" or "OST_Conduit";
        return new EngineeringRule
        {
            Category = category, TypeKey = type.Key, TypeName = type.Name,
            Parts = [new NamePart { Parameter = EngineeringRulesPolicy.TypeNameSource }, new NamePart { Parameter = EngineeringRulesPolicy.SortedSizeSource, Prefix = " " }],
            QuantitySource = curve ? "Длина" : EngineeringRulesPolicy.CountSource,
            Unit = curve ? QuantityUnit.Meter : QuantityUnit.Piece,
            Reserve = 1.0, Decimals = curve ? 2 : 0
        };
    }

    void Units()
    {
        var kind = sample != null && source.SelectedItem is string s ? EngineeringEngine.Kind(sample, s) : null;
        var units = (kind is { } k && k != SourceKind.Number ? EngineeringRulesPolicy.UnitsFor(k) : Enum.GetValues<QuantityUnit>())
            .Select(u => new Choice<QuantityUnit>(u, EngineeringRulesPolicy.UnitLabel(u))).ToList();
        var previous = (unit.SelectedItem as Choice<QuantityUnit>)?.Value;
        unit.ItemsSource = units;
        unit.SelectedItem = units.FirstOrDefault(u => u.Value == previous) ?? units[0];
    }

    EngineeringRule Editor()
    {
        double.TryParse(reserve.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double r);
        int.TryParse(decimals.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int d);
        return new EngineeringRule
        {
            Category = currentCategory!.Key, TypeKey = currentType!.Key, TypeName = currentType.Name,
            NameEnabled = nameOn.IsChecked == true, NameTarget = nameTarget.Text.Trim(),
            Parts = parts.Select(p => new NamePart { Parameter = p.Parameter, Prefix = p.Prefix, Suffix = p.Suffix }).ToList(),
            QuantityEnabled = quantityOn.IsChecked == true, QuantityTarget = quantityTarget.Text.Trim(),
            QuantitySource = source.SelectedItem as string ?? "", Reserve = r, Decimals = d,
            Unit = (unit.SelectedItem as Choice<QuantityUnit>)?.Value ?? QuantityUnit.Piece,
            Rounding = (rounding.SelectedItem as Choice<QuantityRounding>)?.Value ?? QuantityRounding.Up
        };
    }

    void Refresh()
    {
        if (loading || currentCategory == null) return;
        foreach (var row in parts) row.Value = sample == null ? "" : EngineeringEngine.Text(sample, row.Parameter) ?? "";
        var rule = Editor();
        namePreview.Text = sample == null ? "" : "Результат: " + EngineeringRulesPolicy.ComposeName(rule.Parts, p => EngineeringEngine.Text(sample, p));
        string q = "";
        if (sample != null && EngineeringEngine.Source(sample, rule.QuantitySource) is { } s)
        {
            var value = EngineeringRulesPolicy.Quantity(rule, s.Kind, s.Value, out string? error);
            q = value is { } v ? $"Результат: {EngineeringRulesPolicy.Format(v, rule.Decimals)} {EngineeringRulesPolicy.UnitLabel(rule.Unit)}" : "Ошибка: " + error;
        }
        quantityPreview.Text = q;
    }

    bool Commit(out string? error)
    {
        error = null;
        if (currentCategory == null) return true;
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        try { Rules.Put(Editor()); return true; }
        catch (InvalidOperationException e) { error = e.Message; return false; }
    }

    void Finish(bool update)
    {
        if (!Commit(out string? error)) { MessageBox.Show(this, error, Title); return; }
        UpdateScope = update ? (currentCategory!.Key, currentType!.Key) : null;
        DialogResult = true;
    }
}
