using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BotPlus.Shared;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using DataGrid = System.Windows.Controls.DataGrid;
using Grid = System.Windows.Controls.Grid;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace HeatLossViewer.Mep;

/// <summary>Preview line: WPF binds properties, not tuple fields.</summary>
internal sealed record SystemPreview(string Type, string Domain, string Name);

internal sealed class SystemRow : INotifyPropertyChanged
{
    string types = "", name = "";
    Choice<SystemDomain> domain = SystemNamesPanel.Domains[0];
    public string Types { get => types; set { types = value ?? ""; Changed(); } }
    public Choice<SystemDomain> Domain { get => domain; set { domain = value; Changed(); } }
    public string Name { get => name; set { name = value ?? ""; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action? Edited;
    void Changed() { PropertyChanged?.Invoke(this, new(null)); Edited?.Invoke(); }
}

/// <summary>
/// ADSK_Система_Имя по типу системы: правило — список типов систем (через запятую), сеть и имя. Внизу — что получат
/// типы систем текущего проекта, чтобы сразу увидеть непокрытые. Встраивается в окно апдейтера (пункт выпадающего списка).
/// </summary>
internal sealed class SystemNamesPanel : UserControl
{
    internal static readonly Choice<SystemDomain>[] Domains = [new(SystemDomain.Pipe, "Трубопроводы"), new(SystemDomain.Duct, "Воздуховоды"), new(SystemDomain.Any, "Любые")];
    readonly ObservableCollection<SystemRow> rows = [];
    readonly CheckBox enabled = new() { Content = "Заполнять", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly TextBox target = new() { MinWidth = 220 };
    readonly DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column };
    readonly DataGrid preview = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 120 };
    readonly IReadOnlyList<(string Type, SystemDomain Domain)> projectTypes;

    internal SystemNamesPanel(SystemNameSettings settings, IReadOnlyList<(string Type, SystemDomain Domain)> projectTypes)
    {
        this.projectTypes = projectTypes;
        var root = new Grid { Margin = new Thickness(0, 8, 0, 0) }; Content = root;
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1.4, GridUnitType.Star), GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        int r = 0;
        void Add(UIElement e) { Grid.SetRow(e, r++); root.Children.Add(e); }

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = "Параметр:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(target); head.Children.Add(enabled);
        Add(head);
        Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 8, 0, 8),
            Text = "Тип системы подходит к правилу, если его имя совпадает с записью или оканчивается на «_запись»: «В1» подходит к ADSK_Водопровод_В1. " +
                "Несколько типов через запятую получают общее имя (Т3, Т4). Срабатывает первое подходящее правило; без правила значение не меняется. " +
                "Заполняются трубы, воздуховоды, детали, арматура, изоляция, приборы, оборудование и спринклеры; у семейства с несколькими системами берётся первая (по порядку соединителей). Имя наследуется вложенными семействами."
        });
        grid.ItemsSource = rows;
        grid.Columns.Add(new DataGridTextColumn { Header = "Типы систем (через запятую)", Binding = new Binding(nameof(SystemRow.Types)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 210 });
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "Сети", ItemsSource = Domains, SelectedItemBinding = new Binding(nameof(SystemRow.Domain)), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = EngineeringRulesPolicy.DefaultSystemTarget }, Binding = new Binding(nameof(SystemRow.Name)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 500 });
        Add(grid);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        tools.Children.Add(Btn("Добавить", () => { var row = Row(new SystemNameRule { Types = [""], Name = "" }); rows.Add(row); grid.SelectedItem = row; }));
        tools.Children.Add(Btn("Удалить", () => { if (grid.SelectedItem is SystemRow x) { rows.Remove(x); Refresh(); } }));
        tools.Children.Add(Btn("Выше", () => Move(-1)));
        tools.Children.Add(Btn("Ниже", () => Move(1)));
        tools.Children.Add(Btn("По умолчанию", () => Fill(SystemNameSettings.Defaults())));
        Add(tools);
        Add(new TextBlock { Text = "Типы систем проекта:", Margin = new Thickness(0, 12, 0, 4) });
        preview.Columns.Add(new DataGridTextColumn { Header = "Тип системы", Binding = new Binding(nameof(SystemPreview.Type)), Width = 260 });
        preview.Columns.Add(new DataGridTextColumn { Header = "Сети", Binding = new Binding(nameof(SystemPreview.Domain)), Width = 110 });
        preview.Columns.Add(new DataGridTextColumn { Header = "Будет записано", Binding = new Binding(nameof(SystemPreview.Name)), Width = 470 });
        Add(preview);
        enabled.IsChecked = settings.Enabled; target.Text = settings.Target;
        enabled.Checked += (_, _) => Refresh(); enabled.Unchecked += (_, _) => Refresh();
        Fill(settings.Rules);
    }

    static Button Btn(string text, Action click) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) }; b.Click += (_, _) => click(); return b; }

    SystemRow Row(SystemNameRule rule)
    {
        var row = new SystemRow { Types = string.Join(", ", rule.Types), Domain = Domains.First(d => d.Value == rule.Domain), Name = rule.Name };
        row.Edited += Refresh;
        return row;
    }

    void Fill(IEnumerable<SystemNameRule> source) { rows.Clear(); foreach (var r in source) rows.Add(Row(r)); Refresh(); }

    void Move(int delta)
    {
        if (grid.SelectedItem is not SystemRow row) return;
        int i = rows.IndexOf(row), j = i + delta;
        if (j < 0 || j >= rows.Count) return;
        rows.Move(i, j); grid.SelectedItem = row; Refresh();
    }

    SystemNameSettings Editor() => new()
    {
        Enabled = enabled.IsChecked == true, Target = target.Text.Trim(),
        Rules = rows.Select(r => new SystemNameRule { Types = [.. r.Types.Split(',')], Domain = r.Domain.Value, Name = r.Name.Trim() }).ToList()
    };

    /// <summary>Preview rows: system type, network, resulting name.</summary>
    internal List<SystemPreview> PreviewRows
    {
        get
        {
            var settings = Editor();
            return projectTypes.Select(t => new SystemPreview(t.Type, Domains.First(d => d.Value == t.Domain).Label,
                EngineeringRulesPolicy.SystemName(settings, [t]) ?? "— нет правила, значение не меняется")).ToList();
        }
    }

    void Refresh() => preview.ItemsSource = PreviewRows;

    /// <summary>Текущие правила панели; ошибка — текст для пользователя.</summary>
    internal bool TryRead(out SystemNameSettings settings, out string? error)
    {
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        settings = Editor(); error = null;
        try { settings.Validate(); return true; }
        catch (InvalidOperationException e) { error = e.Message; return false; }
    }
}
