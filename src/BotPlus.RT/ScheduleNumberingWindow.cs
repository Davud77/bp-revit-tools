using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BotPlus.Shared;

namespace BotPlus.RT;

internal sealed class ScheduleNumberingWindow : Window
{
    readonly ComboBox parameters;
    readonly TextBox initial = new() { Text = "1", MinWidth = 90 };
    readonly CheckBox selected;
    internal NumberingParameter? Parameter => parameters.SelectedItem as NumberingParameter;
    internal int Start { get; private set; }
    internal bool SelectedOnly => selected.IsChecked == true;

    internal ScheduleNumberingWindow(IReadOnlyList<NumberingParameter> choices, bool hasSelection)
    {
        Title = "Нумерация"; Width = 450; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(16) }; Content = root;
        root.Children.Add(new TextBlock { Text = "Параметр для записи", Margin = new Thickness(0, 0, 0, 6) });
        parameters = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(NumberingParameter.Label), MinWidth = 360 };
        parameters.SelectedItem = choices.FirstOrDefault(p => p.Name == "ADSK_Позиция") ?? choices[0];
        root.Children.Add(parameters);
        root.Children.Add(new TextBlock { Text = "Начальное значение", Margin = new Thickness(0, 12, 0, 6) });
        root.Children.Add(initial);
        selected = new CheckBox { Content = "Для выделенных элементов", IsEnabled = hasSelection, Margin = new Thickness(0, 12, 0, 8) };
        root.Children.Add(selected);
        root.Children.Add(new TextBlock { Text = "В порядке строк активной спецификации. В сгруппированной строке доступные элементы получают общий номер.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12), Opacity = 0.7 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 85, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); root.Children.Add(buttons);
        ok.Click += (_, _) =>
        {
            if (Parameter == null || !int.TryParse(initial.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            { MessageBox.Show(this, "Введите целое неотрицательное начальное значение.", "Нумерация"); return; }
            Start = value; DialogResult = true;
        };
        BotPlusTheme.Apply(this);
    }
}
