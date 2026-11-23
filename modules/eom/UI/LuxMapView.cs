using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotPlus.Eom.Core;

namespace BotPlus.Eom;

internal sealed class LuxMapView : FrameworkElement
{
    PointLightMap? map;
    List<LuxContour> contours = [];
    List<LightVector> sources = [];
    Func<LuxPoint, Point>? screen;
    long lastTooltip;
    public bool Colors { get; set; } = true;
    public bool Lines { get; set; } = true;
    internal DrawingGroup CreateDrawing()
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) OnRender(context);
        return drawing;
    }
    internal RenderTargetBitmap CaptureBitmap()
    {
        // Рисуем карту в отдельный visual: PNG не зависит от отсечения окна или масштаба экрана.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawDrawing(CreateDrawing());
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(ActualWidth)), Math.Max(1, (int)Math.Ceiling(ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var corner = new byte[4]; bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), corner, 4, 0);
        if (corner[3] == 0) throw new InvalidOperationException("WPF не смог сформировать изображение. Повторите экспорт PNG в активном сеансе рабочего стола. Расчёт и экспорт CSV доступны.");
        return bitmap;
    }
    public void Set(PointLightMap? value, List<LuxContour> lines, List<LightVector> fixtures)
    {
        map = value; contours = lines; sources = fixtures; InvalidateVisual();
        ToolTip = null;
    }
    static FormattedText Text(string text, double size = 12) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), size, Brushes.Black, 1);
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (map == null || screen == null || Environment.TickCount64 - lastTooltip < 100) return;
        lastTooltip = Environment.TickCount64;
        var mouse = e.GetPosition(this);
        double Distance(LuxPoint p) { var v = screen(p) - mouse; return v.LengthSquared; }
        var point = map.Points.MinBy(Distance);
        ToolTip = Distance(point) < 400 ? $"Узел: X {point.X:0.###}, Y {point.Y:0.###} м\nОсвещённость {point.Lux:0.00} лк" : null;
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (map == null) { dc.DrawText(Text("Карта освещённости появится после расчёта."), new Point(20, 20)); return; }
        double minX = map.Points.Min(p => p.X), maxX = map.Points.Max(p => p.X), minY = map.Points.Min(p => p.Y), maxY = map.Points.Max(p => p.Y);
        double scale = Math.Max(1e-6, Math.Min(Math.Max(1, ActualWidth - 80) / (maxX - minX), Math.Max(1, ActualHeight - 80) / (maxY - minY)));
        double ox = (ActualWidth - (maxX - minX) * scale) / 2, oy = 45;
        Point P(LuxPoint p) => new(ox + (p.X - minX) * scale, oy + (maxY - p.Y) * scale);
        screen = P;
        double min = map.Min, max = map.Max;
        Brush Color(double value)
        {
            double t = max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0.5;
            // Синий → голубой → зелёный → жёлтый → красный.
            double r = Math.Clamp(4 * t - 2, 0, 1), b = Math.Clamp(2 - 4 * t, 0, 1), g = Math.Clamp(Math.Min(4 * t, 4 - 4 * t), 0, 1);
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(255 * r), (byte)(255 * g), (byte)(255 * b)));
        }
        foreach (var t in map.Triangles)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open()) { context.BeginFigure(P(t.A), true, true); context.LineTo(P(t.B), true, false); context.LineTo(P(t.C), true, false); }
            var fill = Colors ? Color((t.A.Lux + t.B.Lux + t.C.Lux) / 3) : Brushes.WhiteSmoke;
            dc.DrawGeometry(fill, new Pen(fill, 0.5), geometry);
        }
        if (Lines)
        {
            var pen = new Pen(Brushes.Black, 1);
            foreach (var c in contours) dc.DrawLine(pen, P(c.A), P(c.B));
            foreach (var group in contours.GroupBy(c => c.Level))
            {
                var c = group.First(); var text = Text($"{c.Level:0.##}"); var p = P(c.A);
                dc.DrawRectangle(Brushes.White, null, new Rect(p, new Size(text.Width + 4, text.Height)));
                dc.DrawText(text, p);
            }
        }
        foreach (var s in sources) dc.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1.5), P(new(s.X, s.Y, 0)), 4, 4);
        dc.DrawText(Text($"Прямой свет · Emin {min:0.0} / Eср {map.Average:0.0} / Emax {max:0.0} лк · U₀ {map.Uniformity:0.000}"), new Point(12, 8));
        for (int i = 0; i < 100; i++) dc.DrawRectangle(Color(min + (max - min) * i / 99), null, new Rect(12 + i * 2, ActualHeight - 25, 2, 10));
        dc.DrawText(Text($"{min:0.#} → {max:0.#} лк   +Y ↑   +X →   ○ светильник"), new Point(222, ActualHeight - 30));
    }
}
