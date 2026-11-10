using System.Windows;
using System.Windows.Media;

namespace BP.Revit.AI;

internal static class RibbonAssets
{
    public static DrawingImage Icon(int pixels)
    {
        var group = new DrawingGroup { Transform = new ScaleTransform(pixels / 32d, pixels / 32d) };
        using (var drawing = group.Open())
        {
            var outline = new Pen(new SolidColorBrush(Color.FromRgb(76, 96, 117)), 1.4);
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(233, 242, 248)), outline,
                new Rect(7, 7, 18, 18), 2, 2);
            for (var pin = 11; pin <= 21; pin += 5)
            {
                drawing.DrawLine(outline, new Point(pin, 3), new Point(pin, 7));
                drawing.DrawLine(outline, new Point(pin, 25), new Point(pin, 29));
                drawing.DrawLine(outline, new Point(3, pin), new Point(7, pin));
                drawing.DrawLine(outline, new Point(25, pin), new Point(29, pin));
            }
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(51, 145, 210)), null, new Point(16, 16), 4, 4);
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    public static DrawingImage AutoConfirmIcon(int pixels)
    {
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(15, 91, 121)), pixels == 16 ? 1.2 : 1.5);
        var tile = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(19, 124, 154)), outline,
            new RectangleGeometry(new Rect(1, 1, 30, 30), 6, 6));
        var check = new GeometryDrawing(null,
            new Pen(Brushes.White, 3.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round },
            Geometry.Parse("M 7,16 L 13,22 L 25,10"));
        var group = new DrawingGroup();
        group.Children.Add(tile);
        group.Children.Add(check);
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
