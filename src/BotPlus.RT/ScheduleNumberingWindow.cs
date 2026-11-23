using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BotPlus.Shared;

namespace BotPlus.RT;

internal sealed class ScheduleNumberingWindow : Window
{
    readonly ComboBox parameters;
    readonly ComboBox sorting;
    readonly TextBox initial = new() { Text = "1", MinWidth = 90 };
    readonly CheckBox selected;
    internal NumberingParameter? Parameter => parameters.SelectedItem as NumberingParameter;
    internal NumberingSortParameter? SortParameter => sorting.SelectedItem as NumberingSortParameter;
    internal bool Clear { get; private set; }
    internal int Start { get; private set; }
    internal bool SelectedOnly => selected.IsChecked == true;

    internal ScheduleNumberingWindow(IReadOnlyList<NumberingParameter> choices, bool hasSelection, IReadOnlyList<NumberingSortParameter>? sortChoices = null)
    {
        Title = "Нумерация"; Width = 450; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(16) }; Content = root;
        root.Children.Add(new TextBlock { Text = "Параметр для записи", Margin = new Thickness(0, 0, 0, 6) });
        parameters = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(NumberingParameter.Label), MinWidth = 360 };
        parameters.SelectedItem = choices.FirstOrDefault(p => p.Name == "ADSK_Позиция") ?? choices[0];
        root.Children.Add(parameters);
        root.Children.Add(new TextBlock { Text = "Сортировка перед нумерацией (по возрастанию)", Margin = new Thickness(0, 12, 0, 6) });
        sorting = new ComboBox { DisplayMemberPath = nameof(NumberingSortParameter.Label), MinWidth = 360 };
        sorting.Items.Add("В порядке строк спецификации");
        foreach (var item in sortChoices ?? []) sorting.Items.Add(item);
        sorting.SelectedItem = sortChoices?.FirstOrDefault(p => p.Name == "ADSK_Единица измерения")
            ?? (object?)sortChoices?.FirstOrDefault(p => p.Name == "Единица измерения") ?? sorting.Items[0];
        root.Children.Add(sorting);
        root.Children.Add(new TextBlock { Text = "Начальное значение", Margin = new Thickness(0, 12, 0, 6) });
        root.Children.Add(initial);
        selected = new CheckBox { Content = "Для выделенных элементов", IsEnabled = hasSelection, Margin = new Thickness(0, 12, 0, 8) };
        root.Children.Add(selected);
        root.Children.Add(new TextBlock { Text = "Одинаковые значения сортировки сохраняют порядок строк. В сгруппированной строке элементы получают общий номер. «Очистить» делает пустым выбранный параметр только у выделенных строк.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12), Opacity = 0.7 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 85, Margin = new Thickness(0, 0, 8, 0) };
        var clear = new Button { Content = "Очистить", IsEnabled = hasSelection, MinWidth = 85, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 };
        buttons.Children.Add(clear); buttons.Children.Add(ok); buttons.Children.Add(cancel); root.Children.Add(buttons);
        clear.Click += (_, _) => { if (Parameter == null) return; Clear = true; DialogResult = true; };
        ok.Click += (_, _) =>
        {
            if (Parameter == null || !int.TryParse(initial.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            { MessageBox.Show(this, "Введите целое неотрицательное начальное значение.", "Нумерация"); return; }
            Start = value; DialogResult = true;
        };
        BotPlusTheme.Apply(this);
    }
}
