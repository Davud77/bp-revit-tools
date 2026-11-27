using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotPlus.Eom;
using BotPlus.Eom.Core;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var probe = new DrawingVisual();
        using (var dc = probe.RenderOpen()) dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        var probeBitmap = new RenderTargetBitmap(10, 10, 96, 96, PixelFormats.Pbgra32); probeBitmap.Render(probe);
        var probePixels = new byte[400]; probeBitmap.CopyPixels(probePixels, 40, 0);
        bool rasterAvailable = probePixels[2] == 255 && probePixels[3] == 255;
        if (!rasterAvailable) Console.WriteLine("SKIP raster pixel checks: WPF returned a transparent bitmap for an independent red rectangle. Vector drawing checks remain enabled.");
        string output = args.Length > 0 ? args[0] : throw new InvalidOperationException("Pass the validation output directory.");
        Directory.CreateDirectory(output);
        var ies = IesPhotometry.Parse("TILT=NONE\n1 -1 1 4 1 1 2 0 0 0 1 1 10\n0 30 60 90\n0\n400 350 150 0");
        var sources = new[] { new LightVector(1.5, 1.5, 2.8), new LightVector(4.5, 1.5, 2.8), new LightVector(1.5, 4.5, 2.8) };
        var map = PointLightMap.Calculate(0, 0, 6, 6, 0.8, 0.15, 1.4,
            sources.Select(p => new PointLightSource(p, new(1, 0, 0), new(0, 1, 0), new(0, 0, -1), ies)).ToList(),
            (x, y) => (x < 3 || y < 3) && !(x > 0.8 && x < 1.2 && y > 3.8 && y < 4.2));
        var contours = map.Contours([20, 50, 75, 100]);
        if (contours.Count == 0) throw new Exception("No contour lines in the sample scene.");
        var view = new LuxMapView();
        var size = new Size(1000, 700);
        foreach (string mode in new[] { "map", "isolines", "colors", "empty" })
        {
            view.Colors = mode != "isolines"; view.Lines = mode != "colors";
            view.Set(mode == "empty" ? null : map, contours, sources.ToList());
            view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            static IEnumerable<Drawing> Leaves(Drawing drawing)
            {
                if (drawing is DrawingGroup group) { foreach (var child in group.Children) foreach (var leaf in Leaves(child)) yield return leaf; }
                else yield return drawing;
            }
            var drawing = view.CreateDrawing();
            var geometry = Leaves(drawing).OfType<GeometryDrawing>().ToList();
            int colorFills = geometry.Count(g => g.Brush is SolidColorBrush b && (b.Color.R != b.Color.G || b.Color.G != b.Color.B));
            int blackStrokes = geometry.Count(g => g.Pen?.Brush is SolidColorBrush b && b.Color == System.Windows.Media.Colors.Black);
            if ((mode is "map" or "colors") && colorFills < 1000) throw new Exception("Missing heat map triangles.");
            if (mode == "isolines" && (colorFills != 100 || blackStrokes < contours.Count)) throw new Exception("Missing isolines or stale colored triangles.");
            if (mode == "empty" && (colorFills != 0 || blackStrokes != 0)) throw new Exception("Stale drawing after invalidation.");
            if (mode == "colors" && blackStrokes != sources.Length) throw new Exception("Isoline visibility toggle failed.");
            if (!Leaves(drawing).OfType<GlyphRunDrawing>().Any()) throw new Exception("Missing legend text.");
            if (!rasterAvailable)
            {
                try { view.CaptureBitmap(); } catch (InvalidOperationException) { continue; }
                throw new Exception("PNG export did not report the unavailable rasterizer.");
            }
            var bitmap = view.CaptureBitmap();
            var pixels = new byte[1000 * 700 * 4]; bitmap.CopyPixels(pixels, 4000, 0);
            int colored = 0, dark = 0;
            for (int p = 0; p < pixels.Length; p += 4)
            {
                if (pixels[p] != pixels[p + 1] || pixels[p + 1] != pixels[p + 2]) colored++;
                if (pixels[p] < 80 && pixels[p + 1] < 80 && pixels[p + 2] < 80) dark++;
            }
            Console.WriteLine($"{mode}: colored={colored}, dark={dark}, size={view.ActualWidth}x{view.ActualHeight}");
            if ((mode is "map" or "colors") && colored < 10000) throw new Exception("Missing heat map colors.");
            if (mode == "isolines" && (colored > 2500 || dark < 1000)) throw new Exception("Isolines-only mode did not render correctly (legend remains colored).");
            if (mode == "empty" && colored > 0) throw new Exception("Stale results after invalidation.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, mode + ".png")); encoder.Save(file);
        }
        Console.WriteLine("PASS WPF lighting vector drawing: concave room, hole, sources, legend, isolines, color toggle and invalidation" + (rasterAvailable ? "; PNG snapshots saved" : "; raster checks skipped, unavailable renderer reported"));
        app.Shutdown();
    }
}
