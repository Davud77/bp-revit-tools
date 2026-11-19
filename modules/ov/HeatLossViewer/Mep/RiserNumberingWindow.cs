using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BotPlus.Shared;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace HeatLossViewer.Mep;

/// <summary>
/// «Нумерация стояков»: автоматически (1, 2, 3… сверху вниз и слева направо по плану) или раскопировать номер, заданный у одного
/// сегмента, в остальные трубы стояка. Параметр, минимальная длина стояка и область — трубы активного вида или выделенные.
/// </summary>
internal sealed class RiserNumberingWindow : Window
{
    // Последние значения — до перезапуска Revit.
    static bool lastAuto = true;
    static bool lastSystem = true, lastNeighbors;
    static string lastDistance = "300";
    static string lastParameter = RiserNumberingPolicy.DefaultParameter, lastStart = "1", lastLength = "1500";

    readonly RadioButton auto = new() { Content = "Автоматически: 1, 2, 3… сверху вниз и слева направо по плану", GroupName = "mode" };
    readonly RadioButton copy = new() { Content = "По заданному значению: номер одного сегмента копируется во все трубы стояка", GroupName = "mode", Margin = new Thickness(0, 6, 0, 0) };
    readonly ComboBox parameter = new() { MinWidth = 260 };
    readonly TextBox start = new() { MinWidth = 80 }, length = new() { MinWidth = 80 };
    readonly CheckBox selectedOnly = new() { Content = "Только выделенные трубы", Margin = new Thickness(0, 10, 0, 0) };
    readonly CheckBox system = new() { Content = "Учитывать тип системы", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    readonly CheckBox neighbors = new() { Content = "Учитывать соседей — один номер для группы стояков", Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBox distance = new() { MinWidth = 80 };
    internal Action<string>? Warn { get; set; }

    internal bool Auto => auto.IsChecked == true;
    internal string ParameterName => (parameter.SelectedItem as string ?? "").Trim();
    internal int Start { get; private set; }
    internal double MinLengthMm { get; private set; }
    internal bool SelectedOnly => selectedOnly.IsChecked == true;
    internal bool ConsiderSystem => system.IsChecked == true;
    internal bool ConsiderNeighbors => neighbors.IsChecked == true;
    internal double NeighborDistanceMm { get; private set; } = RiserNumberingPolicy.DefaultNeighborDistanceMm;

    internal RiserNumberingWindow(IReadOnlyList<string> parameters, bool hasSelection)
    {
        Title = "Нумерация стояков"; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(14) }; Content = root;
        root.Children.Add(auto); root.Children.Add(copy);

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Line(string label, FrameworkElement input, string? hint = null)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            input.Margin = new Thickness(0, 4, 0, 4); input.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            if (hint != null) input.ToolTip = hint;
            Grid.SetRow(text, row); Grid.SetRow(input, row); Grid.SetColumn(input, 1);
            grid.Children.Add(text); grid.Children.Add(input);
        }
        Line("Параметр номера", parameter, "Параметр экземпляра труб, текст или целое. По умолчанию ADSK_Номер стояка.");
        Line("Начальный номер", start);
        Line("Мин. длина стояка, мм", length, "Вертикальные трубы одной точки плана короче этой длины в сумме — подъёмы и опуски, они не нумеруются.");
        root.Children.Add(grid);
        root.Children.Add(system); root.Children.Add(neighbors);
        var neighborLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 6, 0, 0) };
        neighborLine.Children.Add(new TextBlock { Text = "Между осями не больше, мм", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        neighborLine.Children.Add(distance); root.Children.Add(neighborLine);
        system.ToolTip = "Отдельный счётчик номеров для каждого типа системы. Без типа системы — отдельная группа. В режиме копирования разделяет совпавшие оси разных систем.";
        neighbors.ToolTip = "Разные типы систем: все оси группы в пределах заданного расстояния и каждая пара имеет не менее 1 м общего участка по высоте. Неопределённые типы систем не объединяются.";
        root.Children.Add(selectedOnly);
        root.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 10, 0, 0),
            Text = "Соседи определяются геометрически: близкие оси и общий участок по высоте от 1 м. У соседей один номер; счётчики их систем согласуются, возможны пропуски номеров. Без выделения — трубы активного вида." });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 85, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 };
        ok.Click += (_, _) => { if (Accept()) DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); root.Children.Add(buttons);

        // ADSK_Номер стояка всегда в списке: если у труб его нет, отчёт покажет пропущенные трубы.
        foreach (var p in parameters.Append(RiserNumberingPolicy.DefaultParameter).Distinct()) parameter.Items.Add(p);
        parameter.SelectedItem = parameter.Items.Contains(lastParameter) ? lastParameter : RiserNumberingPolicy.DefaultParameter;
        start.Text = lastStart; length.Text = lastLength;
        system.IsChecked = lastSystem; neighbors.IsChecked = lastNeighbors; distance.Text = lastDistance;
        distance.IsEnabled = ConsiderNeighbors;
        neighbors.Checked += (_, _) => distance.IsEnabled = true; neighbors.Unchecked += (_, _) => distance.IsEnabled = false;
        (lastAuto ? auto : copy).IsChecked = true;
        start.IsEnabled = Auto;
        auto.Checked += (_, _) => start.IsEnabled = true; copy.Checked += (_, _) => start.IsEnabled = false;
        selectedOnly.IsEnabled = hasSelection; selectedOnly.IsChecked = hasSelection;
        BotPlusTheme.Apply(this);
    }

    internal void SetMode(bool automatic) => (automatic ? auto : copy).IsChecked = true;
    internal void SetValues(string parameterName, string startText, string lengthText) { parameter.SelectedItem = parameterName; start.Text = startText; length.Text = lengthText; }
    internal void SetGrouping(bool bySystem, bool withNeighbors, string distanceText) { system.IsChecked = bySystem; neighbors.IsChecked = withNeighbors; distance.Text = distanceText; }

    internal bool Accept()
    {
        string? error = null;
        if (ParameterName.Length == 0) error = "Укажите параметр для номера стояка.";
        else if (Auto && (!int.TryParse(start.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) || s < 0)) error = "Начальный номер — целое число от 0.";
        else if (RiserNumberingPolicy.ParseLength(length.Text) is not { } mm) error = "Минимальная длина стояка — число в мм от 0.";
        else if (ConsiderNeighbors && (RiserNumberingPolicy.ParseLength(distance.Text) is not { } d || d <= 0)) error = "Расстояние между соседями — положительное число в мм до 1000000.";
        else
        {
            MinLengthMm = mm;
            Start = Auto ? int.Parse(start.Text.Trim(), CultureInfo.InvariantCulture) : 0;
            NeighborDistanceMm = ConsiderNeighbors ? RiserNumberingPolicy.ParseLength(distance.Text)!.Value : RiserNumberingPolicy.DefaultNeighborDistanceMm;
        }
        if (error != null) { if (Warn != null) Warn(error); else MessageBox.Show(this, error, Title); return false; }
        lastAuto = Auto; lastParameter = ParameterName; lastStart = start.Text.Trim(); lastLength = length.Text.Trim();
        lastSystem = ConsiderSystem; lastNeighbors = ConsiderNeighbors; lastDistance = distance.Text.Trim();
        return true;
    }
}
