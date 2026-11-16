using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BotPlus.Shared;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace HeatLossViewer.Mep;

/// <summary>«Задать расстояние»: значение (история последних), между осями или гранями, по горизонтали или вертикали, изоляция.</summary>
internal sealed class AlignDistanceWindow : Window
{
    readonly TextBox distance = new() { MinWidth = 100 };
    readonly RadioButton axes = new() { Content = "Между осями", Margin = new Thickness(0, 0, 16, 0) }, faces = new() { Content = "Между гранями" };
    readonly RadioButton horizontal = new() { Content = "По горизонтали", Margin = new Thickness(0, 0, 16, 0), GroupName = "dir" }, vertical = new() { Content = "По вертикали", GroupName = "dir" };
    readonly CheckBox insulation = new() { Content = "Учитывать изоляцию", Margin = new Thickness(0, 8, 0, 0) };
    readonly MepToolSettings settings;
    internal double DistanceMm { get; private set; }
    internal string DistanceText => distance.Text.Trim();
    internal bool Horizontal => horizontal.IsChecked == true;
    internal bool Faces => faces.IsChecked == true;
    internal bool Insulation => insulation.IsChecked == true;

    internal AlignDistanceWindow(MepToolSettings settings)
    {
        this.settings = settings;
        Title = "Задать расстояние"; Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(14) }; Content = root;
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = "Расстояние, мм", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        line.Children.Add(distance);
        root.Children.Add(line);
        var history = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var h in settings.DistanceHistory)
        {
            var b = new Button { Content = h, MinWidth = 44, Margin = new Thickness(0, 0, 4, 4), ToolTip = "Последние значения" };
            b.Click += (_, _) => distance.Text = h;
            history.Children.Add(b);
        }
        root.Children.Add(history);
        root.Children.Add(Row(axes, faces));
        root.Children.Add(Row(horizontal, vertical));
        root.Children.Add(insulation);
        root.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 10, 0, 0),
            Text = "Затем выберите базовый элемент и выравниваемые (или выделите их заранее). Элемент остаётся со своей стороны от базового. Esc — завершить." });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 85, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok); buttons.Children.Add(cancel); root.Children.Add(buttons);
        distance.Text = settings.DistanceHistory.FirstOrDefault() ?? "100";
        (settings.DistanceFaces ? faces : axes).IsChecked = true;
        (settings.DistanceHorizontal ? horizontal : vertical).IsChecked = true;
        insulation.IsChecked = settings.DistanceInsulation;
        insulation.IsEnabled = Faces;
        faces.Checked += (_, _) => insulation.IsEnabled = true; axes.Checked += (_, _) => insulation.IsEnabled = false;
        BotPlusTheme.Apply(this);
    }

    static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var i in items) row.Children.Add(i);
        return row;
    }

    void Accept()
    {
        if (!double.TryParse(DistanceText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double mm) || !double.IsFinite(mm) || mm < 0 || mm > 100000)
        { MessageBox.Show(this, "Введите расстояние в мм: от 0 до 100000.", Title); return; }
        DistanceMm = mm;
        settings.DistanceFaces = Faces; settings.DistanceHorizontal = Horizontal; settings.DistanceInsulation = Insulation;
        DialogResult = true;
    }
}

/// <summary>«Мультипостроение»: произвольный угол или ближайший из фиксированных, объединение отмены.</summary>
internal sealed class MultiRouteWindow : Window
{
    readonly RadioButton free = new() { Content = "Произвольный угол (10–90°)" }, fixedAngles = new() { Content = "Ближайший из фиксированных:", Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBox angles = new() { MinWidth = 200, Margin = new Thickness(22, 4, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
    readonly CheckBox singleUndo = new() { Content = "Одна отмена (Ctrl+Z) на всё построение", Margin = new Thickness(0, 10, 0, 0) };
    readonly MepToolSettings settings;

    internal MultiRouteWindow(MepToolSettings settings)
    {
        this.settings = settings;
        Title = "Мультипостроение"; Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(14) }; Content = root;
        root.Children.Add(new TextBlock { Text = "Угол поворота трасс", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(free); root.Children.Add(fixedAngles); root.Children.Add(angles);
        root.Children.Add(singleUndo);
        root.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 10, 0, 0),
            Text = "Выберите параллельные трассы (или выделите заранее), затем базовую — ближе к концу, от которого продолжать. Далее указывайте на плане точки: " +
                "каждая точка добавляет поворот с отводами или, если она прямо по ходу, удлиняет трассы. Концы выравниваются, уклон сохраняется. Esc — завершить." });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 85, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 85 };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok); buttons.Children.Add(cancel); root.Children.Add(buttons);
        (settings.MultiFixedAngles ? fixedAngles : free).IsChecked = true;
        angles.Text = settings.MultiAngles; angles.IsEnabled = settings.MultiFixedAngles;
        fixedAngles.Checked += (_, _) => angles.IsEnabled = true; free.Checked += (_, _) => angles.IsEnabled = false;
        singleUndo.IsChecked = settings.MultiSingleUndo;
        BotPlusTheme.Apply(this);
    }

    void Accept()
    {
        if (fixedAngles.IsChecked == true)
        {
            try { MepAlignGeometry.ParseAngles(angles.Text); }
            catch (InvalidOperationException e) { MessageBox.Show(this, e.Message, Title); return; }
        }
        settings.MultiFixedAngles = fixedAngles.IsChecked == true; settings.MultiAngles = angles.Text.Trim(); settings.MultiSingleUndo = singleUndo.IsChecked == true;
        DialogResult = true;
    }
}
