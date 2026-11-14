using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Binding = System.Windows.Data.Binding;
using Grid = System.Windows.Controls.Grid;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace BotPlus.Eom;

/// <summary>Общие кирпичики окон ЭОМ: тема плагина, заголовки, подсказки, строки «подпись — поле», ряд кнопок.</summary>
internal static class EomUi
{
    internal static Window Window(string title, double width, double height)
    {
        var window = new Window { Title = "BotPlus ЭОМ — " + title, Width = width, Height = height, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen, MinWidth = 420, MinHeight = 260 };
        BotPlusTheme.Apply(window);
        return window;
    }
    internal static TextBlock Heading(string text) => new() { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
    internal static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 6) };
    internal static UIElement Row(string label, FrameworkElement input, string unit = "")
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = 280, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(input);
        if (unit.Length > 0) row.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(6, 0, 0, 0) });
        return row;
    }
    internal static TextBox Number(double value, double width = 90) => new() { Text = value.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture), Width = width };
    internal static double Parse(TextBox box, string name)
    {
        if (double.TryParse(box.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
        throw new InvalidOperationException($"«{name}»: введите число.");
    }
    internal static Button Button(string text, Action action, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), MinWidth = 90, IsDefault = primary };
        b.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(System.Windows.Window.GetWindow(b), e.Message, "BotPlus ЭОМ"); } };
        return b;
    }
    internal static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in buttons) panel.Children.Add(b);
        return panel;
    }
    /// <summary>Окно: содержимое растягивается, кнопки прижаты к низу.</summary>
    internal static void Layout(Window window, UIElement body, UIElement? top = null, UIElement? bottom = null)
    {
        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (top != null) grid.Children.Add(top);
        Grid.SetRow(body, 1); grid.Children.Add(body);
        if (bottom != null) { Grid.SetRow(bottom, 2); grid.Children.Add(bottom); }
        window.Content = grid;
    }

    /// <summary>Выделяет элемент в Revit и показывает его на виде. Только в контексте API (модальное окно команды или ExternalEvent).</summary>
    internal static void Show(UIDocument ui, ElementId id)
    {
        ui.Selection.SetElementIds([id]);
        try { ui.ShowElements(id); } catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
    }
}

/// <summary>Строка таблицы: ячейки и элемент Revit, который показывается по двойному щелчку.</summary>
internal sealed class TableRow(ElementId? id, params string[] cells)
{
    public string[] Cells { get; } = cells;
    public ElementId? Id { get; } = id;
}

/// <summary>Универсальная таблица результата: расчёты, подбор, проверки. Двойной щелчок — показать элемент в модели.</summary>
internal static class TableWindow
{
    internal static DataGrid Grid(string[] headers, IEnumerable<TableRow> rows, UIDocument? ui)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, ItemsSource = rows.ToList() };
        for (int i = 0; i < headers.Length; i++)
            grid.Columns.Add(new DataGridTextColumn { Header = headers[i], Binding = new Binding($"Cells[{i}]"), Width = i == headers.Length - 1 ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto });
        if (ui != null) grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is TableRow { Id: { } id }) EomUi.Show(ui, id); };
        return grid;
    }

    internal static bool? Show(string title, string hint, string[] headers, IEnumerable<TableRow> rows, UIDocument? ui, params (string Text, Action<Window> Action)[] actions)
    {
        var window = EomUi.Window(title, 980, 560);
        var buttons = actions.Select(a => EomUi.Button(a.Text, () => a.Action(window), true)).Append(EomUi.Button("Закрыть", window.Close)).ToArray();
        EomUi.Layout(window, Grid(headers, rows, ui), EomUi.Hint(hint), EomUi.Buttons(buttons));
        return window.ShowDialog();
    }
}
