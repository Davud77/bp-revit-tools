using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BotPlus.Shared;

namespace BotPlus.RT;

/// <summary>
/// «Секущий диапазон»: four planes — level, offset and a slider — applied to the view right away through <see cref="Apply"/>
/// (the caller wraps the changes in a transaction group). «Принять» keeps them, «Отмена» or closing rolls them back.
/// </summary>
internal sealed class ViewRangeWindow : Window
{
    sealed class Line
    {
        public required RangeRow Row;
        public required ComboBox Level;
        public required TextBox Offset;
        public required Slider Slider;
        public required Border Frame;
    }

    readonly List<Line> lines = [];
    readonly IReadOnlyList<LevelInfo> levels;
    readonly LevelInfo associated;
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MinHeight = 18 };
    readonly DispatcherTimer delay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    bool loading;
    string? lastApplied;

    /// <summary>Applies the rows to the view; returns an error text or null.</summary>
    internal Func<IReadOnlyList<RangeRow>, string?>? Apply;
    internal IReadOnlyList<RangeRow> Rows => lines.Select(l => l.Row).ToList();
    internal int Applied { get; private set; }

    internal ViewRangeWindow(IReadOnlyList<RangeRow> rows, IReadOnlyList<LevelInfo> levels, LevelInfo associated, string? note)
    {
        this.levels = levels; this.associated = associated;
        Title = "Секущий диапазон"; Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(14) }; Content = root;
        if (note != null) root.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 8) });
        loading = true;
        foreach (var source in rows)
        {
            var row = source.Clone();
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            var title = new TextBlock { Text = ViewRangePolicy.Title(row.Plane), VerticalAlignment = VerticalAlignment.Center };
            var choices = ViewRangePolicy.Choices(row.Plane, levels, associated);
            var level = new ComboBox { ItemsSource = choices, IsEnabled = row.Plane != RangePlane.Cut, Margin = new Thickness(0, 0, 10, 0) };
            level.SelectedItem = choices.FirstOrDefault(c => c.Kind == row.Level.Kind && (c.Kind != LevelKind.Level || c.LevelId == row.Level.LevelId)) ?? choices[0];
            row.Level = (LevelChoice)level.SelectedItem;
            var offsetLabel = new TextBlock { Text = "Смещение:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var offset = new TextBox { Text = Format(row.OffsetMm), VerticalContentAlignment = VerticalAlignment.Center };
            Grid.SetColumn(level, 1); Grid.SetColumn(offsetLabel, 2); Grid.SetColumn(offset, 3);
            grid.Children.Add(title); grid.Children.Add(level); grid.Children.Add(offsetLabel); grid.Children.Add(offset);
            var slider = new Slider { TickFrequency = 10, IsSnapToTickEnabled = true, SmallChange = 10, LargeChange = 100, Margin = new Thickness(0, 6, 0, 0) };
            var block = new StackPanel(); block.Children.Add(grid); block.Children.Add(slider);
            var frame = new Border { Child = block, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 0, 0, 4), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
            frame.BorderBrush = System.Windows.Media.Brushes.Transparent;
            var line = new Line { Row = row, Level = level, Offset = offset, Slider = slider, Frame = frame };
            lines.Add(line);
            root.Children.Add(frame);
            Range(line);
            level.SelectionChanged += (_, _) => { if (loading) return; row.Level = (LevelChoice)level.SelectedItem; Range(line); Changed(); };
            slider.ValueChanged += (_, _) =>
            {
                if (loading) return;
                row.OffsetMm = ViewRangePolicy.Snap(slider.Value);
                loading = true; offset.Text = Format(row.OffsetMm); loading = false;
                Changed();
            };
            offset.TextChanged += (_, _) =>
            {
                if (loading) return;
                if (!ViewRangePolicy.TryParseOffset(offset.Text, out double mm)) { Show(line.Row.Plane, "Смещение — число в мм."); return; }
                row.OffsetMm = mm;
                loading = true; Range(line); loading = false;
                Changed();
            };
        }
        loading = false;
        root.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "Принять", IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 10, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 100 };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        delay.Tick += (_, _) => { delay.Stop(); Push(); };
        lastApplied = Key();
        Validate();
        BotPlusTheme.Apply(this);
    }

    static string Format(double mm) => mm.ToString("0.##", CultureInfo.CurrentCulture);

    void Range(Line line)
    {
        bool unlimited = line.Row.Level.Kind == LevelKind.Unlimited;
        line.Offset.IsEnabled = !unlimited; line.Slider.IsEnabled = !unlimited;
        var (min, max) = ViewRangePolicy.SliderRange(line.Row.Level, line.Row.OffsetMm, levels, associated);
        bool was = loading; loading = true;
        line.Slider.Minimum = min; line.Slider.Maximum = max; line.Slider.Value = line.Row.OffsetMm;
        loading = was;
    }

    string Key() => string.Join("|", lines.Select(l => $"{l.Row.Level.Kind}:{l.Row.Level.LevelId}:{l.Row.OffsetMm}"));

    bool Validate()
    {
        foreach (var l in lines) l.Frame.BorderBrush = System.Windows.Media.Brushes.Transparent;
        var problem = ViewRangePolicy.Validate(Rows, levels, associated);
        if (problem is { } p) { Show(p.Plane, p.Message); return false; }
        status.Text = ""; return true;
    }

    void Show(RangePlane plane, string message)
    {
        var line = lines.First(l => l.Row.Plane == plane);
        line.Frame.SetResourceReference(Border.BorderBrushProperty, "BP.Error");
        status.SetResourceReference(TextBlock.ForegroundProperty, "BP.Error");
        status.Text = message + " Значение не применено.";
    }

    void Changed()
    {
        if (!Validate()) { delay.Stop(); return; }
        delay.Stop(); delay.Start();
    }

    /// <summary>Applies the current rows if they changed and are valid. Public for the checks.</summary>
    internal bool Push()
    {
        if (!Validate()) return false;
        string key = Key();
        if (key == lastApplied) return true;
        string? error = Apply?.Invoke(Rows);
        if (error != null)
        {
            status.SetResourceReference(TextBlock.ForegroundProperty, "BP.Error");
            status.Text = error;
            return false;
        }
        lastApplied = key; Applied++;
        status.Text = "";
        return true;
    }

    void Accept()
    {
        delay.Stop();
        if (!Push()) return;
        DialogResult = true;
    }
}
