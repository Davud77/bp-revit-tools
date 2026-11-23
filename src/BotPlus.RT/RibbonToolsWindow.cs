using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.UI;
using BotPlus.Shared;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace BotPlus.RT;

internal sealed record RibbonTool(
    string Id, string Title, string Panel, string? Section, string? Module, string Description,
    string AssemblyPath, string ClassName, string? Availability, ImageSource? Image, ImageSource? LargeImage,
    PushButton? Button)
{
    public string Label => $"{Title}  ·  {(Section == null || Section == Panel ? Panel : Section + " / " + Panel)}";

    internal static RibbonTool FromButton(PushButton button, string panel, string? section = null, string? module = null) =>
        new(button.Name, string.Join(' ', button.ItemText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
            panel, section, module, button.ToolTip ?? "", button.AssemblyName, button.ClassName,
            button.AvailabilityClassName, button.Image, button.LargeImage, button);

    internal PushButtonData FavoriteButton() =>
        new("BotPlusFavorite_" + Id, Title, AssemblyPath, ClassName)
        {
            AvailabilityClassName = Availability,
            ToolTip = Description,
            Image = Image,
            LargeImage = LargeImage
        };
}

internal sealed class RibbonToolsWindow : Window
{
    readonly TextBox query = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly ListBox results = new() { DisplayMemberPath = nameof(RibbonTool.Label) };
    readonly CheckBox onlyFavorites = new() { Content = "Только избранное", Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 48, Margin = new Thickness(0, 10, 0, 8) };
    readonly Button favorite = new() { MinWidth = 155 };
    readonly Button run = new() { Content = "Запустить", IsDefault = true, MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
    readonly List<RibbonTool> catalog;
    readonly Func<string, bool> isFavorite;

    internal RibbonTool? Selected => results.SelectedItem as RibbonTool;

    internal RibbonToolsWindow(IEnumerable<RibbonTool> tools, string initialQuery, bool manageFavorites,
        Func<string, bool> isFavorite, Action<string> toggleFavorite)
    {
        this.isFavorite = isFavorite;
        catalog = tools.OrderBy(tool => tool.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        Title = manageFavorites ? "BotPlus RT — Избранное" : "BotPlus RT — Поиск инструментов";
        Width = 600;
        Height = 480;
        MinWidth = 470;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        BotPlusTheme.Apply(this);

        var root = new DockPanel { Margin = new Thickness(14) };
        Content = root;
        var filters = new StackPanel();
        filters.Children.Add(new Label { Content = "Поиск инструментов", Target = query, Padding = new Thickness(0) });
        filters.Children.Add(query);
        filters.Children.Add(onlyFavorites);
        DockPanel.SetDock(filters, Dock.Top);
        root.Children.Add(filters);

        var footer = new StackPanel();
        footer.Children.Add(description);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(favorite);
        buttons.Children.Add(run);
        buttons.Children.Add(new Button { Content = "Закрыть", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) });
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(results);

        query.Text = initialQuery;
        query.TextChanged += (_, _) => Filter();
        onlyFavorites.Checked += (_, _) => Filter();
        onlyFavorites.Unchecked += (_, _) => Filter();
        results.SelectionChanged += (_, _) => UpdateActions();
        results.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(results, source) is ListBoxItem)
                RunSelected();
        };
        favorite.Click += (_, _) =>
        {
            if (Selected is not { } tool) return;
            try { toggleFavorite(tool.Id); Filter(); }
            catch (Exception error) { TaskDialog.Show("Избранное", "Не удалось сохранить избранное. " + error.Message); }
        };
        run.Click += (_, _) => RunSelected();
        Loaded += (_, _) => { query.Focus(); query.CaretIndex = query.Text.Length; };
        Filter();
    }

    void Filter()
    {
        var selection = Selected;
        var tokens = Normalize(query.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var visible = catalog.Where(tool =>
        {
            if (onlyFavorites.IsChecked == true && !isFavorite(tool.Id)) return false;
            string searchable = Normalize($"{tool.Label} {tool.Description}");
            return tokens.All(token => searchable.Contains(token, StringComparison.Ordinal));
        }).ToList();
        results.ItemsSource = visible;
        results.SelectedItem = selection != null && visible.Contains(selection) ? selection : visible.FirstOrDefault();
        UpdateActions();
    }

    static string Normalize(string text) => text.ToLowerInvariant().Replace('ё', 'е');

    void UpdateActions()
    {
        var tool = Selected;
        description.Text = tool?.Description ?? (onlyFavorites.IsChecked == true ? "Подходящих команд в избранном нет." : "Инструменты не найдены.");
        favorite.IsEnabled = run.IsEnabled = tool != null;
        favorite.Content = tool != null && isFavorite(tool.Id) ? "Убрать из избранного" : "В избранное";
    }

    void RunSelected()
    {
        if (Selected != null) DialogResult = true;
    }
}
