using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

namespace BotPlus.Eom;

internal static class RiserWindow
{
    internal static RiserPlacement.Settings? Edit(UIDocument ui, bool tray, Level[] levels,
        Level baseLevel, ElementType[] types, RiserPlacement.Size defaults)
    {
        var window = EomUi.Window("Высота стояка", 540, 360);
        window.ResizeMode = ResizeMode.NoResize;
        window.SizeToContent = SizeToContent.Height;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        new WindowInteropHelper(window).Owner = ui.Application.MainWindowHandle;

        var type = new ComboBox { Margin = new Thickness(0, 4, 0, 8) };
        foreach (var item in types)
        {
            var option = new ComboBoxItem { Content = item.Name, Tag = item.Id };
            type.Items.Add(option);
            if (item.Id == defaults.Type) type.SelectedItem = option;
        }
        if (type.SelectedIndex < 0) type.SelectedIndex = 0;
        var width = EomUi.Number(RiserPlacement.Mm(defaults.Width));
        var height = EomUi.Number(RiserPlacement.Mm(defaults.Height));
        var dimensions = new StackPanel();
        dimensions.Children.Add(Field(tray ? "Ширина" : "Диаметр", width));
        if (tray) dimensions.Children.Add(Field("Высота", height));

        var bottomLevel = LevelPicker(levels, baseLevel);
        var topLevel = LevelPicker(levels, baseLevel);
        var bottomOffset = EomUi.Number(0);
        var topOffset = EomUi.Number(2500);
        var absolute = new CheckBox { Content = "Абсолютные отметки от начала координат проекта", Margin = new Thickness(0, 7, 0, 3) };
        var split = new CheckBox { Content = "Разбить вертикальный участок по уровням", IsChecked = true };
        var bottomLabel = FieldLabel("Смещение");
        var topLabel = FieldLabel("Смещение");
        var endpoints = new Grid { Margin = new Thickness(0, 6, 0, 3) };
        endpoints.ColumnDefinitions.Add(new ColumnDefinition());
        endpoints.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        endpoints.ColumnDefinitions.Add(new ColumnDefinition());
        endpoints.Children.Add(End("Нижняя точка", bottomLevel, bottomLabel, bottomOffset));
        var top = End("Верхняя точка", topLevel, topLabel, topOffset);
        Grid.SetColumn(top, 2);
        endpoints.Children.Add(top);

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        error.Themed(TextBlock.ForegroundProperty, "BP.Error");
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "Тип трассы" });
        content.Children.Add(type);
        content.Children.Add(dimensions);
        content.Children.Add(endpoints);
        content.Children.Add(absolute);
        content.Children.Add(split);
        content.Children.Add(error);

        absolute.Click += (_, _) =>
        {
            var useAbsolute = absolute.IsChecked == true;
            ChangeMode(bottomLevel, bottomOffset, useAbsolute);
            ChangeMode(topLevel, topOffset, useAbsolute);
            bottomLabel.Text = topLabel.Text = useAbsolute ? "Отметка" : "Смещение";
        };

        RiserPlacement.Settings? result = null;
        var ok = EomUi.Button("Указать точку", () =>
        {
            try
            {
                var w = Read(width, tray ? "Ширина" : "Диаметр");
                var h = tray ? Read(height, "Высота") : 0;
                if (w <= 0 || tray && h <= 0) throw new InvalidOperationException("Размеры должны быть больше нуля.");
                var relative = absolute.IsChecked != true;
                var low = Read(bottomOffset, "Нижняя точка") + (relative ? Elevation(bottomLevel) : 0);
                var high = Read(topOffset, "Верхняя точка") + (relative ? Elevation(topLevel) : 0);
                if (high <= low) throw new InvalidOperationException("Верхняя точка должна быть выше нижней.");
                var typeId = (ElementId)((ComboBoxItem)type.SelectedItem).Tag;
                result = new(new(typeId, w, h), low, high, split.IsChecked == true);
                window.DialogResult = true;
            }
            catch (InvalidOperationException ex) { error.Text = ex.Message; }
        }, true);
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90, Padding = new Thickness(14, 5, 14, 5) };
        EomUi.Layout(window, content, bottom: EomUi.Buttons(cancel, ok));
        return window.ShowDialog() == true ? result : null;
    }

    static ComboBox LevelPicker(Level[] levels, Level selected)
    {
        var picker = new ComboBox { Margin = new Thickness(0, 3, 0, 6) };
        foreach (var level in levels)
        {
            var option = new ComboBoxItem { Content = level.Name, Tag = level.ProjectElevation };
            picker.Items.Add(option);
            if (level.Id == selected.Id) picker.SelectedItem = option;
        }
        if (picker.SelectedIndex < 0) picker.SelectedIndex = 0;
        return picker;
    }

    static GroupBox End(string title, ComboBox levels, TextBlock label, TextBox offset)
    {
        var content = new StackPanel { Margin = new Thickness(7) };
        content.Children.Add(new TextBlock { Text = "Уровень" });
        content.Children.Add(levels);
        content.Children.Add(Field(label, offset));
        return new GroupBox { Header = title, Content = content };
    }

    static TextBlock FieldLabel(string text) => new() { Text = text, Width = 92, VerticalAlignment = VerticalAlignment.Center };
    static StackPanel Field(string label, TextBox value) => Field(FieldLabel(label), value);

    static StackPanel Field(TextBlock label, TextBox value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(label);
        row.Children.Add(value);
        row.Children.Add(new TextBlock { Text = "мм", Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    static double Elevation(ComboBox levels) => (double)((ComboBoxItem)levels.SelectedItem).Tag;

    static void ChangeMode(ComboBox levels, TextBox offset, bool absolute)
    {
        if (TryRead(offset, out var value))
        {
            var z = value + (absolute ? 1 : -1) * Elevation(levels);
            offset.Text = RiserPlacement.Mm(z).ToString("0.###", CultureInfo.CurrentCulture);
        }
        levels.IsEnabled = !absolute;
    }

    static bool TryRead(TextBox box, out double value)
    {
        value = 0;
        if (!double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mm)
            || !double.IsFinite(mm) || Math.Abs(mm) > 10_000_000)
            return false;
        value = RiserPlacement.Feet(mm);
        return true;
    }

    static double Read(TextBox box, string field) => TryRead(box, out var value) ? value :
        throw new InvalidOperationException($"Проверьте значение поля «{field}» в миллиметрах.");
}
