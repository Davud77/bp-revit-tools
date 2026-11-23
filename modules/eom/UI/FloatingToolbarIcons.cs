using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace BotPlus.Eom;

internal static class FloatingToolbarIcons
{
    internal const string Emergency = "M4,18 L10,3 L16,18 M6.3,12.3 L13.7,12.3";
    internal const string ReverseWiring = "M2,6 H18 M2,6 L6,2 M2,6 L6,10 M18,14 H2 M18,14 L14,10 M18,14 L14,18";
    internal const string CreateSimilar = "M2,2 H12 V5 M2,2 V13 H5 M7,7 H18 V18 H7 Z M12.5,9.5 V15.5 M9.5,12.5 H15.5";
    internal const string Close = "M3,3 L17,17 M17,3 L3,17";

    internal static FrameworkElement Draw(string geometry, double size = 18)
    {
        var path = new Path
        {
            Data = Geometry.Parse(geometry), StrokeThickness = 1.65,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ButtonBase), 1)
        });
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 20, Height = 20, Children = { path } }, IsHitTestVisible = false };
    }
}
