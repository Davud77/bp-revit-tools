using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using BotPlus.Shared;

namespace BotPlus.RT;

internal sealed class SettingsWindow : Window
{
    readonly RadioButton follow = new() { Content = "Следовать теме Revit (по умолчанию)", Margin = new Thickness(0, 4, 0, 4) };
    readonly RadioButton light = new() { Content = "Светлая", Margin = new Thickness(0, 4, 0, 4) };
    readonly RadioButton dark = new() { Content = "Тёмная", Margin = new Thickness(0, 4, 0, 4) };
    readonly Dictionary<string, CheckBox> panels = [];

    internal string ThemeMode => light.IsChecked == true ? BotPlusSettings.Light : dark.IsChecked == true ? BotPlusSettings.Dark : BotPlusSettings.Follow;
    internal IReadOnlyList<string> HiddenPanels => BotPlusSettings.Panels.Where(name => panels[name].IsChecked != true).ToArray();

    internal SettingsWindow()
    {
        Title = "BotPlus RT — Настройки"; Width = 560; Height = 470; MinWidth = 480; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        BotPlusTheme.Apply(this);
        var root = new DockPanel { Margin = new Thickness(14) }; Content = root;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "ОК", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90 };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var tabs = new TabControl(); root.Children.Add(tabs);
        ok.Click += (_, _) => DialogResult = true;

        TextBlock Heading(string text) => new() { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0) };

        // Тема.
        var theme = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
        theme.Children.Add(Heading("Тема окон модулей"));
        foreach (var option in new[] { follow, light, dark }) { option.GroupName = "theme"; theme.Children.Add(option); }
        (BotPlusSettings.ThemeMode switch { BotPlusSettings.Light => light, BotPlusSettings.Dark => dark, _ => follow }).IsChecked = true;
        theme.Children.Add(Hint("Тема действует во всех модулях: MCP, Ac2Revit, ОВ, Отверстия, Марки и в окне обновлений. Уже открытые окна изменятся при следующем открытии."));
        tabs.Items.Add(new TabItem { Header = "Тема", Content = theme });

        // Модули.
        var ribbon = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
        ribbon.Children.Add(Heading("Модули BotPlus RT"));
        ribbon.Children.Add(new TextBlock { Text = "Снимите флажок у модулей, которые не нужны.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var name in BotPlusSettings.Panels)
        {
            var box = new CheckBox { Content = name, IsChecked = !BotPlusSettings.HiddenPanels.Contains(name), Margin = new Thickness(0, 4, 0, 4) };
            panels[name] = box; ribbon.Children.Add(box);
        }
        ribbon.Children.Add(Hint("После нажатия «ОК» лента обновится. Перезапустите Revit, чтобы применить новый состав модулей. Панель «Настройки» доступна всегда."));
        tabs.Items.Add(new TabItem { Header = "Модули", Content = ribbon });

        // О плагине.
        var about = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        about.Children.Add(new TextBlock { Text = "BotPlus RT " + version?.ToString(3), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        about.Children.Add(new TextBlock { Text = "Autodesk Revit 2026" });
        about.Children.Add(new TextBlock { Text = "Модули: " + string.Join(", ", BotPlusSettings.Panels), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var link = new Hyperlink(new Run("github.com/Davud77/bp-revit-tools")) { NavigateUri = new Uri("https://github.com/Davud77/bp-revit-tools") };
        link.SetResourceReference(TextElement.ForegroundProperty, "BP.Accent");
        link.RequestNavigate += (_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception) { } };
        about.Children.Add(new TextBlock(link) { Margin = new Thickness(0, 8, 0, 0) });
        about.Children.Add(Hint("Файл настроек: " + BotPlusSettings.FilePath));
        tabs.Items.Add(new TabItem { Header = "О плагине", Content = about });
    }
}
