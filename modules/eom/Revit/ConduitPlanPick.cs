using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Point = System.Windows.Point;
using Rectangle = Autodesk.Revit.DB.Rectangle;

namespace BotPlus.Eom;

public sealed partial class ConduitThroughLightsCommand
{
    // PickPoint принимает щелчок и Esc через Revit. Отдельное окно только рисует подсветку и не перехватывает мышь.
    sealed class PlanBodyPick : IDisposable
    {
        sealed record Body(Element Element, Rect Footprint, Port[] Ports, XYZ? BoxPoint);

        static readonly ElementMulticategoryFilter Categories = new(new List<BuiltInCategory>
        {
            BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingDevices,
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting
        });

        readonly UIView viewport;
        readonly XYZ right, up;
        readonly Body[] bodies;
        readonly Func<Element, XYZ, int?, HoverLeg?>? route;
        readonly IntPtr owner;
        readonly Window window;
        readonly Overlay overlay = new();
        readonly DispatcherTimer timer;
        Rectangle? screen;
        Rect zoom;
        (ElementId Element, int? Nearest, int? Hit)? shown;
        HoverLeg? leg;

        PlanBodyPick(UIDocument ui, ViewPlan view, UIView viewport, Predicate<Element> eligible, double? diameter,
            Func<Element, XYZ, int?, HoverLeg?>? route)
        {
            this.viewport = viewport;
            this.route = route;
            right = view.RightDirection;
            up = view.UpDirection;
            owner = ui.Application.MainWindowHandle;
            bodies = new FilteredElementCollector(ui.Document, view.Id).WherePasses(Categories)
                .WhereElementIsNotElementType().Where(e => eligible(e) && !e.IsHidden(view))
                .Select(e =>
                {
                    using var box = e.get_BoundingBox(view);
                    if (box == null) return null;
                    var bounds = Bounds(Corners(box));
                    var ports = FreePorts(e, diameter);
                    var boxPoint = ports.Length == 0 && IsLight(e) && Ports(e).Length == 0
                        ? (e.Location as LocationPoint)?.Point : null;
                    return new Body(e, bounds, ports, boxPoint);
                })
                .OfType<Body>().ToArray();

            window = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                Background = Brushes.Transparent, ShowActivated = false, ShowInTaskbar = false,
                SizeToContent = SizeToContent.Manual, Content = overlay, Focusable = false, IsHitTestVisible = false,
                Width = 1, Height = 1
            };
            var helper = new WindowInteropHelper(window) { Owner = owner };
            window.SourceInitialized += (_, _) =>
            {
                var handle = helper.Handle;
                const int extendedStyle = -20;
                const int transparentToolWindow = 0x20 | 0x80 | 0x08000000;
                SetWindowLong(handle, extendedStyle, GetWindowLong(handle, extendedStyle) | transparentToolWindow);
            };
            timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (_, _) => Paint();
        }

        internal static Pick? TryPick(UIDocument ui, Predicate<Element> eligible, double? diameter, string prompt,
            Func<Element, XYZ, int?, HoverLeg?>? route)
        {
            if (ui.ActiveView is not ViewPlan plan) return null;
            var openViews = ui.GetOpenUIViews();
            var viewport = openViews.FirstOrDefault(view => view.ViewId == plan.Id);
            try
            {
                if (viewport == null) return null;
                using var picker = new PlanBodyPick(ui, plan, viewport, eligible, diameter, route);
                if (picker.bodies.Length == 0 || !picker.ReadViewport()) return null;
                picker.window.Show();
                picker.screen = null;
                picker.Paint();
                picker.timer.Start();
                while (true)
                {
                    XYZ click;
                    try { click = ui.Selection.PickPoint(ObjectSnapTypes.None, prompt); }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
                    if (picker.ReadViewport())
                    {
                        var projected = picker.Project(click);
                        if (picker.At(projected) is { } body)
                            return new(body.Element, click, picker.PortAt(body, projected)?.Id);
                    }
                    prompt = "Щёлкните внутри корпуса светильника, выключателя или короба. Esc — завершить.";
                }
            }
            finally
            {
                foreach (var view in openViews) view.Dispose();
            }
        }

        Point Project(XYZ point) => new(point.DotProduct(right), point.DotProduct(up));
        Rect Bounds(IEnumerable<XYZ> points)
        {
            var bounds = Rect.Empty;
            foreach (var point in points) bounds.Union(Project(point));
            return bounds;
        }

        static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            var min = box.Min;
            var max = box.Max;
            for (int i = 0; i < 8; i++)
                yield return box.Transform.OfPoint(new XYZ((i & 1) == 0 ? min.X : max.X,
                    (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
        }

        bool ReadViewport()
        {
            var rect = viewport.GetWindowRectangle();
            zoom = Bounds(viewport.GetZoomCorners());
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top || zoom.Width <= 0 || zoom.Height <= 0) return false;
            if (screen == null || rect.Left != screen.Left || rect.Top != screen.Top
                || rect.Right != screen.Right || rect.Bottom != screen.Bottom)
            {
                screen = rect;
                if (window.IsVisible)
                    SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, rect.Left, rect.Top,
                        rect.Right - rect.Left, rect.Bottom - rect.Top, 0x0014);
            }
            return true;
        }

        Body? At(Point point)
        {
            double margin = 11 * zoom.Width / (screen!.Right - screen.Left);
            Body? best = null;
            double bestScore = double.PositiveInfinity;
            foreach (var body in bodies)
            {
                var box = body.Footprint;
                if (point.X < box.Left - margin || point.X > box.Right + margin
                    || point.Y < box.Top - margin || point.Y > box.Bottom + margin) continue;
                var center = new Point(box.Left + box.Width / 2, box.Top + box.Height / 2);
                var penalty = box.Contains(point) ? 0 : margin;
                double score = (point - center).Length + penalty;
                if (score >= bestScore) continue;
                best = body;
                bestScore = score;
            }
            return best;
        }

        Port? PortAt(Body body, Point point)
        {
            double scaleX = (screen!.Right - screen.Left) / zoom.Width;
            double scaleY = (screen.Bottom - screen.Top) / zoom.Height;
            Port? best = null;
            double bestDistance = 11 * 11;
            foreach (var port in body.Ports)
            {
                var delta = Project(port.Point) - point;
                var distance = new Vector(delta.X * scaleX, delta.Y * scaleY).LengthSquared;
                if (distance >= bestDistance) continue;
                best = port;
                bestDistance = distance;
            }
            return best;
        }

        void Paint()
        {
            try
            {
                if (GetForegroundWindow() != owner || !ReadViewport() || !GetCursorPos(out var cursor)
                    || cursor.X < screen!.Left || cursor.X >= screen.Right || cursor.Y < screen.Top || cursor.Y >= screen.Bottom)
                {
                    overlay.Draw(Rect.Empty, [], null, []);
                    return;
                }

                var pixelX = (double)(cursor.X - screen.Left) / (screen.Right - screen.Left);
                var pixelY = (double)(cursor.Y - screen.Top) / (screen.Bottom - screen.Top);
                var projected = new Point(zoom.Left + zoom.Width * pixelX, zoom.Bottom - zoom.Height * pixelY);
                if (At(projected) is not { } body)
                {
                    overlay.Draw(Rect.Empty, [], null, []);
                    return;
                }

                var hit = right * projected.X + up * projected.Y;
                var port = Nearest(body.Ports, hit, true).FirstOrDefault();
                var marked = PortAt(body, projected);
                var key = (body.Element.Id, port?.Id, marked?.Id);
                if (key != shown)
                {
                    shown = key;
                    leg = null;
                    try { leg = route?.Invoke(body.Element, hit, marked?.Id); }
                    catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
                    { System.Diagnostics.Trace.WriteLine(error.Message); }
                }

                Point ToScreen(Point point)
                {
                    var dpi = VisualTreeHelper.GetDpi(overlay);
                    return new((point.X - zoom.Left) / zoom.Width * (screen.Right - screen.Left) / dpi.DpiScaleX,
                        (zoom.Bottom - point.Y) / zoom.Height * (screen.Bottom - screen.Top) / dpi.DpiScaleY);
                }

                var frame = new Rect(ToScreen(body.Footprint.TopLeft), ToScreen(body.Footprint.BottomRight));
                frame.Inflate(3, 3);
                var marker = marked?.Point ?? leg?.ConnectorPoint ?? port?.Point ?? body.BoxPoint;
                overlay.Draw(frame, body.Ports.Select(port => ToScreen(Project(port.Point))).ToArray(),
                    marker != null ? ToScreen(Project(marker)) : null,
                    leg?.Points.Select(point => ToScreen(Project(point))).ToArray() ?? []);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                overlay.Draw(Rect.Empty, [], null, []);
            }
        }

        public void Dispose()
        {
            timer.Stop();
            window.Close();
        }

        sealed class Overlay : FrameworkElement
        {
            static readonly Pen Highlight = new(Brushes.DeepSkyBlue, 2);
            static readonly Pen Available = new(Brushes.DeepSkyBlue, 1.5);
            static readonly Pen ConnectorMarker = new(Brushes.LawnGreen, 2);
            Rect frame = Rect.Empty;
            Point[] available = [];
            Point? marker;
            Point[] path = [];

            internal void Draw(Rect frame, Point[] available, Point? marker, Point[] path)
            {
                this.frame = frame;
                this.available = available;
                this.marker = marker;
                this.path = path;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawing)
            {
                if (!frame.IsEmpty) drawing.DrawRoundedRectangle(null, Highlight, frame, 3, 3);
                for (int i = 1; i < path.Length; i++) drawing.DrawLine(Highlight, path[i - 1], path[i]);
                foreach (var markerPoint in available) drawing.DrawEllipse(Brushes.Black, Available, markerPoint, 4, 4);
                if (marker is { } point) drawing.DrawEllipse(Brushes.Black, ConnectorMarker, point, 5, 5);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ScreenPoint { internal int X, Y; }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetCursorPos(out ScreenPoint point);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
