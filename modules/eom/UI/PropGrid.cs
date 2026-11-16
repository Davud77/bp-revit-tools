using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;

namespace BotPlus.Eom;

/// <summary>Таблица свойств в духе окон nanoCAD: группы с заголовком, строки «подпись — значение».</summary>
internal sealed class PropGrid
{
    internal Grid View { get; } = new();
    int row;

    internal PropGrid()
    {
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star), MinWidth = 150 });
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 110 });
    }

    UIElement Place(UIElement element, int column, int span = 1)
    {
        Grid.SetRow(element, row); Grid.SetColumn(element, column); Grid.SetColumnSpan(element, span);
        View.Children.Add(element);
        return element;
    }

    internal void Header(string text)
    {
        View.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var border = new Border { Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, row == 0 ? 0 : 6, 0, 0), Child = new TextBlock { Text = "▾  " + text, FontWeight = FontWeights.SemiBold } }
            .Themed(Border.BackgroundProperty, "BP.Panel");
        Place(border, 0, 2); row++;
    }

    internal T Row<T>(string label, T editor, string? tip = null) where T : FrameworkElement
    {
        View.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = label, Margin = new Thickness(14, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = tip ?? label };
        var cell = new Border { BorderThickness = new Thickness(0, 0, 0, 1), MinHeight = 24, Child = text }.Themed(Border.BorderBrushProperty, "BP.Panel");
        Place(cell, 0);
        editor.Margin = new Thickness(0, 1, 0, 1); editor.VerticalAlignment = VerticalAlignment.Center;
        Place(editor, 1); row++;
        return editor;
    }

    internal TextBlock Value(string label, string value = "", string? tip = null) =>
        Row(label, new TextBlock { Text = value, Margin = new Thickness(4, 0, 0, 0), TextWrapping = TextWrapping.Wrap }.Themed(TextBlock.ForegroundProperty, "BP.Muted"), tip);

    /// <summary>Поле, применяющее значение по Enter или при уходе фокуса.</summary>
    internal static TextBox Edit(Action<string> commit)
    {
        var box = new TextBox();
        string? shown = null;
        box.GotKeyboardFocus += (_, _) => shown = box.Text;
        void Commit() { if (shown != null && box.Text != shown) { shown = box.Text; commit(box.Text); } }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); box.SelectAll(); e.Handled = true; } };
        return box;
    }

    internal static FrameworkElement WithButton(FrameworkElement field, string text, Action click, string tip)
    {
        var dock = new DockPanel();
        var b = EomUi.Button(text, click); b.MinWidth = 0; b.Padding = new Thickness(6, 0, 6, 0); b.Margin = new Thickness(3, 0, 0, 0); b.ToolTip = tip;
        DockPanel.SetDock(b, Dock.Right); dock.Children.Add(b); dock.Children.Add(field);
        return dock;
    }

    internal static StackPanel Toolbar(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var b in buttons) { b.Margin = new Thickness(0, 0, 6, 0); b.MinWidth = 0; panel.Children.Add(b); }
        return panel;
    }

    internal static ComboBox Choice(IEnumerable<string> items) => new() { ItemsSource = items.ToList() };
}
