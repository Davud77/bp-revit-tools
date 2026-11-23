using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BotPlus.Eom;

internal static class PanelDiagram
{
    internal static void Show(Window owner, string name, string feed,
        IReadOnlyList<(string Number, string Load, string Breaker, string Cable, string Current)> groups)
    {
        var window = EomUi.Window("Однолинейная схема · " + name, 1170, 485);
        window.Owner = owner;
        const double spacing = 185;
        var canvas = new Canvas { Width = Math.Max(500, 45 + groups.Count * spacing), Height = 310, Margin = new Thickness(12) };
        canvas.Themed(Panel.BackgroundProperty, "BP.Panel");
        void Wire(double x1, double y1, double x2, double y2, double weight = 1.3)
        {
            var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = weight };
            line.SetResourceReference(Shape.StrokeProperty, "BP.Text");
            canvas.Children.Add(line);
        }
        void Text(double x, double y, double width, string value, bool bold = false)
        {
            var label = new TextBlock { Text = value, Width = width, TextWrapping = TextWrapping.Wrap,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, ToolTip = value };
            Canvas.SetLeft(label, x); Canvas.SetTop(label, y); canvas.Children.Add(label);
        }
        Text(50, 10, canvas.Width - 60, feed, true);
        Wire(30, 12, 30, 70);
        Wire(30, 70, canvas.Width - 20, 70, 3);
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            double x = 95 + i * spacing;
            Wire(x, 70, x, 101);
            Wire(x, 101, x + 10, 122);
            Wire(x, 124, x, 156);
            Wire(x - 3, 150, x, 156); Wire(x + 3, 150, x, 156);
            Text(x - 43, 167, spacing - 15, group.Number, true);
            Text(x - 43, 188, spacing - 15, $"{group.Load}\n{group.Breaker}\n{group.Cable}\nIр {group.Current} А");
        }
        if (groups.Count == 0) Text(50, 95, 350, "У щита нет отходящих групп или резервов.");
        EomUi.Layout(window, new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, bottom: EomUi.Buttons(EomUi.Button("Закрыть", window.Close)));
        window.ShowDialog();
    }
}
