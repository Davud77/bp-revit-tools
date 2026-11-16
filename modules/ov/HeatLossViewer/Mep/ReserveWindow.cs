using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BotPlus.Shared;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using DockPanel = System.Windows.Controls.DockPanel;
using Dock = System.Windows.Controls.Dock;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace HeatLossViewer.Mep;

internal sealed class ReserveEditRow
{
    public string Systems { get; set; } = "";
    public double MaxSizeMm { get; set; }
    public double Percent { get; set; } = 10;
}

/// <summary>
/// «Запас по размеру»: строки «Типы систем / До размера, мм / Запас, %». Строки своей системы проверяются раньше общих,
/// внутри — от меньшего размера к большему; без подходящей строки действует общий запас правила.
/// </summary>
internal sealed class ReserveWindow : Window
{
    readonly ObservableCollection<ReserveEditRow> rows = [];
    readonly DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column };
    internal List<ReserveRow>? Result { get; private set; }

    internal ReserveWindow(IEnumerable<ReserveRow> source, double general)
    {
        Title = "Запас по размеру"; Width = 640; Height = 480; MinWidth = 560; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "Размер — условный диаметр трубы, диаметр или большая сторона воздуховода; у деталей — наибольший коннектор, у изоляции — основа. " +
                "Строки с типами систем (как в «Имена систем»: «К1» подходит к ADSK_Канализация_К1) проверяются раньше строк без систем; " +
                $"внутри — от меньшего размера к большему. Если строка не подошла, действует общий запас правила: {EngineeringRulesWindow.Percent(general)}."
        };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Btn("Добавить", () => { var r = new ReserveEditRow { MaxSizeMm = 110 }; rows.Add(r); grid.SelectedItem = r; }));
        buttons.Children.Add(Btn("Удалить", () => { if (grid.SelectedItem is ReserveEditRow r) rows.Remove(r); }));
        buttons.Children.Add(Btn("Очистить", rows.Clear));
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 85, Margin = new Thickness(40, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        grid.ItemsSource = rows;
        grid.Columns.Add(new DataGridTextColumn { Header = "Типы систем (через запятую, пусто — любые)", Binding = new Binding(nameof(ReserveEditRow.Systems)), Width = 290 });
        grid.Columns.Add(new DataGridTextColumn { Header = "До размера, мм", Binding = new Binding(nameof(ReserveEditRow.MaxSizeMm)) { ConverterCulture = CultureInfo.CurrentCulture }, Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Запас, %", Binding = new Binding(nameof(ReserveEditRow.Percent)) { ConverterCulture = CultureInfo.CurrentCulture }, Width = 100 });
        root.Children.Add(grid);
        foreach (var r in source.OrderBy(r => r.Systems.Count == 0).ThenBy(r => string.Join(",", r.Systems)).ThenBy(r => r.MaxSizeMm))
            rows.Add(new ReserveEditRow { Systems = string.Join(", ", r.Systems), MaxSizeMm = r.MaxSizeMm, Percent = Math.Round((r.Reserve - 1) * 100, 3) });
        BotPlusTheme.Apply(this);
    }

    static Button Btn(string text, Action click) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) }; b.Click += (_, _) => click(); return b; }

    void Accept()
    {
        if (!grid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        var result = rows.Select(r => new ReserveRow { Systems = [.. r.Systems.Split(',')], MaxSizeMm = r.MaxSizeMm, Reserve = 1 + r.Percent / 100 }).ToList();
        // Same checks as on save, so the user sees the problem here rather than later.
        var probe = new EngineeringRule { Category = "probe", QuantityEnabled = true, QuantityTarget = "x", QuantitySource = "x", ReserveBySize = result };
        try { probe.Validate(); }
        catch (InvalidOperationException e) { MessageBox.Show(this, e.Message, Title); return; }
        Result = probe.ReserveBySize; DialogResult = true;
    }
}
