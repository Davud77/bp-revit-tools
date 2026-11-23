using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Visibility = System.Windows.Visibility;

namespace BotPlus.Eom;

public sealed partial class ConduitThroughLightsCommand
{
    static Pick PickBody(UIDocument ui, Predicate<Element> eligible, double? diameter, string prompt,
        Func<Element, XYZ, int?, HoverLeg?>? route = null)
    {
        if (PlanBodyPick.TryPick(ui, eligible, diameter, prompt, route) is { } planPick) return planPick;
        using var filter = new BodyFilter(ui, eligible, diameter);
        var reference = ui.Selection.PickObject(ObjectType.PointOnElement, filter, prompt);
        var element = ui.Document.GetElement(reference.ElementId);
        var point = reference.GlobalPoint ?? filter.PositionOn(element.Id) ?? (element.Location switch
        {
            LocationPoint insertion => insertion.Point,
            LocationCurve curve => curve.Curve.Evaluate(0.5, true),
            _ => null
        });
        return new(element, point ?? throw new InvalidOperationException("Revit не определил точку на корпусе. Укажите грань или линию прибора."));
    }

    sealed class BodyFilter(UIDocument ui, Predicate<Element> eligible, double? diameter) : ISelectionFilter, IDisposable
    {
        readonly Document doc = ui.Document;
        readonly ElementId view = ui.ActiveView.Id;
        readonly bool plan = ui.ActiveView is ViewPlan;
        readonly Dictionary<ElementId, Port[]> known = [];
        TemporaryGraphicsManager? graphics;
        string? bitmap;
        int? control;
        (ElementId Element, int? Port)? displayed;
        (ElementId Element, XYZ Position)? hit;
        bool graphicsUnavailable;

        internal XYZ? PositionOn(ElementId element) => hit is { } last && last.Element == element ? last.Position : null;

        public bool AllowElement(Element element)
        {
            bool allowed = eligible(element);
            if (!allowed) Hide();
            return allowed;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            if (position == null) return true;
            hit = (reference.ElementId, position);
            if (graphicsUnavailable) return true;
            try
            {
                if (!known.TryGetValue(reference.ElementId, out var ports))
                {
                    ports = FreePorts(doc.GetElement(reference.ElementId), diameter);
                    known.Add(reference.ElementId, ports);
                }
                var selected = Nearest(ports, position, plan).FirstOrDefault();
                if (selected == null) { Hide(); return true; }
                var key = (reference.ElementId, selected.Id);
                if (displayed == key) return true;

                graphics ??= TemporaryGraphicsManager.GetTemporaryGraphicsManager(doc);
                if (bitmap == null)
                {
                    bitmap = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"BotPlus-Eom-{Guid.NewGuid():N}.bmp");
                    WriteMarker(bitmap);
                }
                using var data = new InCanvasControlData(bitmap, selected.Point);
                if (control is { } index)
                {
                    graphics.UpdateControl(index, data);
                    graphics.SetVisibility(index, true);
                }
                else control = graphics.AddControl(data, view);
                graphics.SetTooltip(control.Value, selected.ToString());
                displayed = key;
            }
            catch (Exception error) when (error is Autodesk.Revit.Exceptions.ApplicationException or IOException or UnauthorizedAccessException)
            {
                graphicsUnavailable = true;
                Hide();
                System.Diagnostics.Trace.WriteLine(error);
            }
            return true;
        }

        void Hide()
        {
            if (displayed == null || control is not { } index) return;
            displayed = null;
            try { graphics!.SetVisibility(index, false); }
            catch (Autodesk.Revit.Exceptions.ApplicationException error) { System.Diagnostics.Trace.WriteLine(error); }
        }

        public void Dispose()
        {
            try
            {
                if (control is { } index) graphics!.RemoveControl(index);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException error) { System.Diagnostics.Trace.WriteLine(error); }
            finally
            {
                graphics?.Dispose();
                if (bitmap != null)
                {
                    try { File.Delete(bitmap); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        System.Diagnostics.Trace.WriteLine(error);
                    }
                }
            }
        }

        static void WriteMarker(string path)
        {
            const int size = 17;
            var pixels = new byte[size * size * 3];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int offset = (y * size + x) * 3;
                    bool green = x is >= 2 and <= 14 && y is >= 2 and <= 14 && (x is 7 or 8 or 9 || y is 7 or 8 or 9);
                    pixels[offset] = green ? (byte)70 : (byte)25;
                    pixels[offset + 1] = green ? (byte)240 : (byte)30;
                    pixels[offset + 2] = green ? (byte)50 : (byte)25;
                }

            var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr24, null, pixels, size * 3);
            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(path);
            encoder.Save(file);
        }
    }

    sealed class ConduitRoutePreview : IDirectContext3DServer, IDisposable
    {
        readonly UIDocument ui;
        readonly Document doc;
        readonly ElementId viewId;
        readonly Guid id = Guid.NewGuid();
        readonly MultiServerService service;
        readonly double markerSize;
        (XYZ Start, XYZ End, ColorWithTransparency Color)[] lines = [];
        Outline? bounds;

        ConduitRoutePreview(UIDocument ui)
        {
            this.ui = ui;
            doc = ui.Document;
            viewId = ui.ActiveView.Id;
            markerSize = Feet(Math.Clamp(ui.ActiveView.Scale * 1.5, 25, 175));
            service = (MultiServerService)ExternalServiceRegistry.GetService(GetServiceId());
            service.AddServer(this);
            try
            {
                var active = service.GetActiveServerIds();
                active.Add(id);
                service.SetActiveServers(active);
            }
            catch
            {
                service.RemoveServer(id);
                throw;
            }
        }

        internal static Leg? Review(UIDocument ui, Port[] sources, Func<Port, Port[]> ends, Func<Port, Port, bool> canAlign,
            Func<Port, Port, bool, Leg> makeLeg, bool switchBox, bool branch)
        {
            using var preview = new ConduitRoutePreview(ui);
            var title = switchBox ? "Коробка над выключателем" : branch ? "Спуск к выключателю" : "Участок трассы";
            var window = EomUi.Window(title, 470, 270);
            new WindowInteropHelper(window).Owner = ui.Application.MainWindowHandle;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.SizeToContent = SizeToContent.Height;
            window.MinHeight = 0;
            var viewport = ui.GetOpenUIViews().FirstOrDefault(v => v.ViewId == ui.ActiveView.Id)?.GetWindowRectangle();
            if (viewport != null)
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
                window.Left = Math.Max(viewport.Left / dpi.DpiScaleX, viewport.Right / dpi.DpiScaleX - window.Width - 24);
                window.Top = viewport.Top / dpi.DpiScaleY + 24;
            }
            var status = EomUi.Hint("");
            var from = new System.Windows.Controls.ComboBox { ItemsSource = sources, SelectedIndex = 0 };
            var to = new System.Windows.Controls.ComboBox { ItemsSource = ends(sources[0]), SelectedIndex = 0 };
            var align = new System.Windows.Controls.CheckBox { Content = "Сдвинуть второй светильник на ось, сохранив отметку" };
            if (to.SelectedItem is Port initialEnd) align.IsChecked = canAlign(sources[0], initialEnd);
            var choices = new StackPanel();
            choices.Children.Add(EomUi.Hint("Номер · диаметр · направление вывода (X/Y/Z)"));
            choices.Children.Add(new TextBlock { Text = "Начальный вывод" });
            choices.Children.Add(from);
            choices.Children.Add(new TextBlock { Text = "Конечный вывод" });
            choices.Children.Add(to);
            var body = new StackPanel();
            body.Children.Add(status);
            body.Children.Add(align);
            body.Children.Add(new Expander
            {
                Header = "Сменить коннектор", Content = choices, IsExpanded = to.Items.Count > 1,
                Margin = new Thickness(0, 6, 0, 0)
            });

            Leg? candidate = null;
            bool updating = false;
            var apply = EomUi.Button(switchBox ? "Поставить и подключить" : "Построить",
                () => window.DialogResult = true, true);
            var cancel = EomUi.Button("Отмена", () => window.DialogResult = false);
            cancel.IsCancel = true;
            EomUi.Layout(window, body, bottom: EomUi.Buttons(cancel, apply));

            void Update()
            {
                if (updating) return;
                var start = (Port)from.SelectedItem;
                var end = to.SelectedItem as Port;
                candidate = null;
                align.Visibility = end != null && canAlign(start, end) ? Visibility.Visible : Visibility.Collapsed;
                try
                {
                    if (end == null) throw new InvalidOperationException("У прибора нет свободного вывода диаметра выбранного начала.");
                    candidate = makeLeg(start, end, align.Visibility == Visibility.Visible && align.IsChecked == true);
                    var route = candidate.Points.Length switch
                    {
                        2 => "Прямой участок",
                        3 => "Два участка с одним поворотом 90°",
                        4 => "Три участка с двумя поворотами 90°",
                        5 => "Четыре участка с тремя поворотами 90°",
                        int length => $"Участков: {length - 1}. Поворотов 90°: {length - 2}"
                    };
                    string shift = candidate.Shift.GetLength() > PointTolerance ? $" Сдвиг {Mm(candidate.Shift.GetLength()):0.##} мм." : "";
                    status.Text = route + "." + shift + (switchBox
                        ? " Спуск от коробки к выключателю будет построен автоматически."
                        : " Зелёные метки показывают выбранные выводы.");
                }
                catch (InvalidOperationException error) { status.Text = error.Message; }
                apply.IsEnabled = candidate != null;
                preview.Show(candidate?.Points ?? [], candidate is { } leg
                    ? [leg.Points[0], leg.Points[^1]]
                    : [start.Point, end?.Point ?? start.Point]);
            }

            from.SelectionChanged += (_, _) =>
            {
                if (from.SelectedItem is not Port selected) return;
                updating = true;
                var previous = (to.SelectedItem as Port)?.Id;
                var compatible = ends(selected);
                to.ItemsSource = compatible;
                to.SelectedItem = compatible.FirstOrDefault(p => p.Id == previous) ?? compatible.FirstOrDefault();
                align.IsChecked = false;
                updating = false;
                Update();
            };
            to.SelectionChanged += (_, _) =>
            {
                if (updating) return;
                align.IsChecked = false;
                Update();
            };
            align.Checked += (_, _) => Update();
            align.Unchecked += (_, _) => Update();
            window.Loaded += (_, _) => Update();
            return window.ShowDialog() == true ? candidate : null;
        }

        void Show(XYZ[] points, XYZ[] markers)
        {
            var cyan = new ColorWithTransparency(0, 175, 255, 0);
            var green = new ColorWithTransparency(70, 240, 65, 0);
            var segments = new List<(XYZ, XYZ, ColorWithTransparency)>();
            for (int i = 1; i < points.Length; i++)
                segments.Add((points[i - 1], points[i], cyan));
            foreach (var point in markers)
                foreach (var direction in new[] { XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ })
                    segments.Add((point - direction * markerSize, point + direction * markerSize, green));

            var all = segments.SelectMany(segment => new[] { segment.Item1, segment.Item2 }).ToArray();
            bounds?.Dispose();
            bounds = new Outline(new XYZ(all.Min(p => p.X), all.Min(p => p.Y), all.Min(p => p.Z)),
                new XYZ(all.Max(p => p.X), all.Max(p => p.Y), all.Max(p => p.Z)));
            lines = segments.ToArray();
            ui.RefreshActiveView();
        }

        public void Dispose()
        {
            var active = service.GetActiveServerIds();
            active.Remove(id);
            service.SetActiveServers(active);
            service.RemoveServer(id);
            bounds?.Dispose();
            ui.RefreshActiveView();
        }

        public Guid GetServerId() => id;
        public string GetVendorId() => "BTPL";
        public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
        public string GetName() => "BotPlus ЭОМ — просмотр трассы";
        public string GetDescription() => "Участки короба и выбранные соединители.";
        public string GetApplicationId() => "";
        public string GetSourceId() => "";
        public bool UsesHandles() => false;
        public bool CanExecute(View view) => view.Document.Equals(doc) && view.Id == viewId;
        public Outline? GetBoundingBox(View view) => bounds;
        public bool UseInTransparentPass(View view) => false;

        public void RenderScene(View view, DisplayStyle style)
        {
            var segments = lines;
            if (segments.Length == 0) return;
            int vertexCount = segments.Length * 2;
            int vertexSize = vertexCount * VertexPositionColored.GetSizeInFloats();
            int indexSize = segments.Length * IndexLine.GetSizeInShortInts();
            using var vertices = new VertexBuffer(vertexSize);
            using var indices = new IndexBuffer(indexSize);
            using var format = new VertexFormat(VertexFormatBits.PositionColored);
            using var effect = new EffectInstance(VertexFormatBits.PositionColored);
            vertices.Map(vertexSize);
            indices.Map(indexSize);
            var vertexStream = vertices.GetVertexStreamPositionColored();
            var indexStream = indices.GetIndexStreamLine();
            for (int i = 0; i < segments.Length; i++)
            {
                var (start, end, color) = segments[i];
                vertexStream.AddVertex(new VertexPositionColored(start, color));
                vertexStream.AddVertex(new VertexPositionColored(end, color));
                indexStream.AddLine(new IndexLine(i * 2, i * 2 + 1));
            }
            vertices.Unmap();
            indices.Unmap();
            DrawContext.FlushBuffer(vertices, vertexCount, indices, vertexCount, format, effect, PrimitiveType.LineList, 0, segments.Length);
        }
    }
}
