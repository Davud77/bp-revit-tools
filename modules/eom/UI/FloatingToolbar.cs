using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BotPlus.Eom;

internal static class FloatingToolbar
{
    internal const string Title = "Floating Toolbar";

    internal static Border Layout(StackPanel controls, Action dismiss)
    {
        var title = new TextBlock
        {
            Text = Title, FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 5, 0)
        }.Themed(TextBlock.ForegroundProperty, "BP.Muted");
        const string closeName = "Закрыть панель";
        var close = new Button
        {
            Content = FloatingToolbarIcons.Draw(FloatingToolbarIcons.Close, 10),
            Width = 16, Height = 16, MinHeight = 0, Padding = new Thickness(0), ToolTip = closeName
        };
        AutomationProperties.SetName(close, closeName);
        close.Click += (_, _) => dismiss();

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);
        return new Border
        {
            Child = new StackPanel { Children = { header, controls } },
            Padding = new Thickness(3), BorderThickness = new Thickness(1)
        }.Themed(Border.BorderBrushProperty, "BP.Border");
    }
}
