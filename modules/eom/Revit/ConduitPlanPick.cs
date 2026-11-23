using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using FormattedText = System.Windows.Media.FormattedText;
using Point = System.Windows.Point;
using Rectangle = Autodesk.Revit.DB.Rectangle;

namespace BotPlus.Eom;

public sealed partial class ConduitThroughLightsCommand
{
    // PickPoint принимает щелчок и Esc через Revit. Отдельное окно только рисует подсветку и не перехватывает мышь.
    sealed class PlanBodyPick : IDisposable
    {
        sealed record Body(Element Element, Rect Footprint, Port[] Ports, XYZ? BoxPoint, Line? Axis);

        static readonly ElementMulticategoryFilter Categories = new(Network.DeviceCategories
            .Concat([BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting]).ToList());

        readonly UIView viewport;
        readonly XYZ right, up;
        readonly Body[] bodies;
        readonly Func<Element, XYZ, int?, HoverLeg?>? route;
        readonly Func<XYZ, FreeSketch>? freePath;
        readonly double? riserElevation;
        readonly IntPtr owner;
        readonly Window window;
        readonly Overlay overlay = new();
        readonly DispatcherTimer timer;
        Rectangle? screen;
        Rect zoom;
        (ElementId Element, int? Nearest, int? Hit, Point? Tap)? shown;
        HoverLeg? leg;

        PlanBodyPick(UIDocument ui, ViewPlan view, UIView viewport, Predicate<Element> eligible,
            Func<Element, XYZ, int?, HoverLeg?>? route, Func<XYZ, FreeSketch>? freePath, double? riserElevation)
        {
            this.viewport = viewport;
            this.route = route;
            this.freePath = freePath;
            this.riserElevation = riserElevation;
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
                    var axis = e is Conduit && e.Location is LocationCurve location ? location.Curve as Line : null;
                    var ports = axis != null && IsVertical(axis.Direction) ? [] : FreePorts(e, null);
                    var boxPoint = ports.Length == 0 && IsLight(e) && Ports(e).Length == 0
                        ? (e.Location as LocationPoint)?.Point : null;
                    return new Body(e, bounds, ports, boxPoint, axis);
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

        internal static Pick? TryPick(UIDocument ui, Predicate<Element> eligible, string prompt,
            Func<Element, XYZ, int?, HoverLeg?>? route, Func<XYZ, FreeSketch>? freePath, bool allowFree,
            double? riserElevation, Predicate<Element>? excluded)
        {
            if (ui.ActiveView is not ViewPlan plan) return null;
            var openViews = ui.GetOpenUIViews();
            var viewport = openViews.FirstOrDefault(view => view.ViewId == plan.Id);
            try
            {
                if (viewport == null) return null;
                using var picker = new PlanBodyPick(ui, plan, viewport,
                    excluded == null ? eligible : e => eligible(e) || excluded(e), route, freePath, riserElevation);
                if ((!allowFree && picker.bodies.Length == 0) || !picker.ReadViewport()) return null;
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
                        var body = picker.At(projected);
                        if (body != null && eligible(body.Element))
                            return new(body.Element, click, picker.PortAt(body, projected)?.Id);
                        if (allowFree && body == null) return new(null, click);
                    }
                    prompt = "Щёлкните по корпусу прибора или по линии короба. Esc — завершить.";
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
            var hit = right * point.X + up * point.Y;
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
                if (body.Axis is { } axis)
                {
                    var distance = (Project(AxisPoint(axis, hit, true)) - point).Length;
                    if (distance > margin) continue;
                    score = distance + margin * 0.25;
                    if (IsVertical(axis.Direction) && riserElevation is { } height)
                    {
                        var atHeight = AxisPoint(axis, hit, true, height);
                        score += Math.Min(Math.Abs(atHeight.Z - height), margin * 4);
                    }
                }
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
                Point ToScreen(Point point)
                {
                    var dpi = VisualTreeHelper.GetDpi(overlay);
                    return new((point.X - zoom.Left) / zoom.Width * (screen.Right - screen.Left) / dpi.DpiScaleX,
                        (zoom.Bottom - point.Y) / zoom.Height * (screen.Bottom - screen.Top) / dpi.DpiScaleY);
                }

                var hit = right * projected.X + up * projected.Y;
                if (At(projected) is not { } body)
                {
                    var sketch = freePath?.Invoke(hit);
                    overlay.Draw(Rect.Empty, [], null, sketch?.Points.Select(point => ToScreen(Project(point))).ToArray() ?? [],
                        hintAt: ToScreen(projected), hint: sketch?.Hint, invalid: sketch?.Invalid == true);
                    return;
                }

                var port = Nearest(body.Ports, hit, true).FirstOrDefault();
                var marked = PortAt(body, projected);
                var tap = marked == null && body.Axis is { } axis ? AxisPoint(axis, hit, true, riserElevation) : null;
                var key = (body.Element.Id, port?.Id, marked?.Id, tap == null ? (Point?)null : Project(tap));
                if (key != shown)
                {
                    shown = key;
                    leg = null;
                    try { leg = route?.Invoke(body.Element, hit, marked?.Id); }
                    catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
                    { System.Diagnostics.Trace.WriteLine(error.Message); }
                }

                var frame = new Rect(ToScreen(body.Footprint.TopLeft), ToScreen(body.Footprint.BottomRight));
                frame.Inflate(3, 3);
                var marker = marked?.Point ?? tap ?? leg?.ConnectorPoint ?? port?.Point ?? body.BoxPoint;
                var conduitAxis = body.Axis is { } line
                    ? new[] { ToScreen(Project(line.GetEndPoint(0))), ToScreen(Project(line.GetEndPoint(1))) } : null;
                overlay.Draw(frame, body.Ports.Select(port => ToScreen(Project(port.Point))).ToArray(),
                    marker != null ? ToScreen(Project(marker)) : null,
                    leg?.Points.Select(point => ToScreen(Project(point))).ToArray() ?? [], conduitAxis,
                    ToScreen(projected), leg?.Hint, leg?.Invalid == true);
            }
            catch (Exception error) when (error is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
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
            static readonly Pen Available = new(Brushes.DeepSkyBlue, 1.5);
            static readonly Pen ConnectorMarker = new(Brushes.LawnGreen, 2);
            static readonly Pen NormalRoute = RoutePen(false), EmergencyRoute = RoutePen(true);

            static Pen RoutePen(bool emergency)
            {
                var (r, g, b) = RouteService.SystemColor(emergency);
                var pen = new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)), 2);
                pen.Freeze();
                return pen;
            }
            Rect frame = Rect.Empty;
            Point[] available = [];
            Point? marker;
            Point[] path = [];
            Point[]? axis;
            Point hintAt;
            string? hint;
            bool invalid;

            internal void Draw(Rect frame, Point[] available, Point? marker, Point[] path, Point[]? axis = null,
                Point hintAt = default, string? hint = null, bool invalid = false)
            {
                this.frame = frame;
                this.available = available;
                this.marker = marker;
                this.path = path;
                this.axis = axis;
                this.hintAt = hintAt;
                this.hint = hint;
                this.invalid = invalid;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawing)
            {
                var route = RouteTools.IsEmergency ? EmergencyRoute : NormalRoute;
                if (axis is { } line) drawing.DrawLine(route, line[0], line[1]);
                else if (!frame.IsEmpty) drawing.DrawRoundedRectangle(null, route, frame, 3, 3);
                for (int i = 1; i < path.Length; i++) drawing.DrawLine(route, path[i - 1], path[i]);
                foreach (var markerPoint in available) drawing.DrawEllipse(Brushes.Black, Available, markerPoint, 4, 4);
                if (marker is { } point) drawing.DrawEllipse(Brushes.Black, ConnectorMarker, point, 5, 5);
                if (hint == null) return;
                var label = new FormattedText(hint, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    12, invalid ? Brushes.Orange : Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var x = Math.Clamp(hintAt.X + 14, 3, Math.Max(3, ActualWidth - label.Width - 14));
                var y = Math.Clamp(hintAt.Y + 16, 3, Math.Max(3, ActualHeight - label.Height - 10));
                drawing.DrawRoundedRectangle(Brushes.Black, null, new Rect(x, y, label.Width + 12, label.Height + 6), 4, 4);
                drawing.DrawText(label, new Point(x + 6, y + 3));
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
