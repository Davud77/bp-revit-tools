using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace BotPlus.Eom;

internal static class IsoluxWindow
{
    internal static void Show(List<RoomRef> rooms, RoomRef? selected, Dictionary<string, RoomLight> stored,
        Func<RoomRef, List<PhotometryAssignment>> assignments,
        Func<RoomRef, List<PhotometryAssignment>, double, double, double, IsoluxResult> calculate,
        Action<IsoluxResult, List<LuxContour>, bool> export)
    {
        var window = EomUi.Window("Точечный метод — карта и изолюксы", 1280, 900);
        var roomBox = new ComboBox { MinWidth = 440, ItemsSource = rooms.Select((r, i) => new { Text = $"{r.Source} / {r.Number} {r.Name}", Index = i }).ToList(), DisplayMemberPath = "Text" };
        var height = EomUi.Number(0.8); var step = EomUi.Number(0.25); var kz = EomUi.Number(1.4);
        var levels = new TextBox { Text = "50; 100; 150; 200; 300; 500", MinWidth = 280 };
        var color = new CheckBox { Content = "Цветовая карта", IsChecked = true, Margin = new Thickness(8) };
        var isolines = new CheckBox { Content = "Изолинии", IsChecked = true, Margin = new Thickness(8) };
        var summary = EomUi.Hint("Выберите помещение, задайте IES каждого типа и нажмите «Рассчитать».");
        var mapView = new LuxMapView { MinHeight = 240 };
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, Height = 155, SelectionMode = DataGridSelectionMode.Single };
        void Column(string title, string property, bool readOnly = false, double width = 100) => grid.Columns.Add(new DataGridTextColumn
        {
            Header = title, Binding = new Binding(property), IsReadOnly = readOnly, Width = new DataGridLength(width)
        });
        Column("Тип Revit", nameof(PhotometryAssignment.Type), true, 280);
        Column("Шт.", nameof(PhotometryAssignment.Count), true, 45);
        Column("Файл IES", nameof(PhotometryAssignment.File), false, 330);
        Column("ΔX, м", nameof(PhotometryAssignment.OffsetX), false, 75);
        Column("ΔY, м", nameof(PhotometryAssignment.OffsetY), false, 75);
        Column("ΔZ, м", nameof(PhotometryAssignment.OffsetZ), false, 75);
        Column("C0, °", nameof(PhotometryAssignment.Rotation), false, 75);
        IsoluxResult? result = null;
        List<PhotometryAssignment> current = [];
        void Invalidate()
        {
            result = null; mapView.Set(null, [], []);
            summary.Text = "Параметры изменены. Нажмите «Рассчитать».";
        }
        List<double> Levels()
        {
            var tokens = levels.Text.Split([';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length > 30) throw new InvalidOperationException("Задайте от 1 до 30 уровней изолиний через точку с запятой.");
            return tokens.Select(token =>
            {
                if (!double.TryParse(token.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v <= 0)
                    throw new InvalidOperationException("Уровни изолиний — положительные числа в люксах через точку с запятой.");
                return v;
            }).Distinct().Order().ToList();
        }
        void Redraw()
        {
            if (result == null) return;
            mapView.Colors = color.IsChecked == true; mapView.Lines = isolines.IsChecked == true;
            mapView.Set(result.Map, result.Map.Contours(Levels()), result.Sources);
        }
        IsoluxResult Ready() => result ?? throw new InvalidOperationException("Сначала выполните расчёт с текущими параметрами.");
        roomBox.SelectionChanged += (_, _) =>
        {
            Invalidate();
            if (roomBox.SelectedIndex < 0) return;
            var room = rooms[roomBox.SelectedIndex];
            current = assignments(room); grid.ItemsSource = current;
            var light = stored.GetValueOrDefault(room.Key) ?? new RoomLight();
            height.Text = light.WorkPlaneM.ToString("0.###"); kz.Text = light.Maintenance.ToString("0.###");
        };
        foreach (var box in new[] { height, step, kz }) box.TextChanged += (_, _) => Invalidate();
        grid.CellEditEnding += (_, _) => Invalidate();
        void RefreshView()
        {
            try { Redraw(); }
            catch (InvalidOperationException e) { MessageBox.Show(window, e.Message, "BotPlus ЭОМ"); }
        }
        color.Click += (_, _) => RefreshView(); isolines.Click += (_, _) => RefreshView();
        levels.LostFocus += (_, _) => RefreshView();
        var top = new StackPanel();
        top.Children.Add(EomUi.Row("Помещение", roomBox));
        var settings = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        void Input(string title, FrameworkElement control)
        {
            settings.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) });
            settings.Children.Add(control);
        }
        Input("Рабочая поверхность, м", height); Input("Шаг, м", step); Input("Kз", kz);
        settings.Children.Add(color); settings.Children.Add(isolines); top.Children.Add(settings);
        top.Children.Add(EomUi.Hint("Прямой свет: E = Σ I(C, γ)·cos(α)/(r²·Kз). Без отражений и затенения. Только верхние экземпляры светильников текущего проекта и фазы активного вида, находящиеся над контуром помещения. Светильники из связей не учитываются."));
        top.Children.Add(EomUi.Hint("IES Type C, TILT=NONE; канделы из файла, без масштабирования по потоку БД. γ0 направлена по локальной −Z семейства; C0 — по +X. Проверьте оси семейства и центр излучения: ΔX/ΔY/ΔZ в локальных осях, C0 — дополнительный поворот. Это точечная модель, без геометрии светящего тела."));
        top.Children.Add(grid);
        top.Children.Add(EomUi.Buttons(EomUi.Button("Выбрать IES…", () =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (grid.SelectedItem is not PhotometryAssignment a) throw new InvalidOperationException("Выберите строку типа светильника.");
            var dialog = new OpenFileDialog { Filter = "Фотометрия IES (*.ies)|*.ies", Title = a.Type };
            if (dialog.ShowDialog(window) != true) return;
            IesPhotometry.Parse(File.ReadAllText(dialog.FileName));
            a.File = dialog.FileName; grid.Items.Refresh(); Invalidate();
        })));
        top.Children.Add(EomUi.Row("Уровни изолиний, лк (через ;)", levels));
        top.Children.Add(summary);
        var buttons = EomUi.Buttons(
            EomUi.Button("Рассчитать", () =>
            {
                Invalidate();
                if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)
                    || Validation.GetHasError(grid)) throw new InvalidOperationException("Исправьте значения в таблице светильников.");
                if (current.Count == 0) throw new InvalidOperationException("Над контуром помещения нет светильников текущего проекта и фазы активного вида.");
                Levels();
                var room = rooms[roomBox.SelectedIndex];
                window.Cursor = System.Windows.Input.Cursors.Wait;
                try
                {
                    result = calculate(room, current, EomUi.Parse(height, "Рабочая поверхность"), EomUi.Parse(step, "Шаг"), EomUi.Parse(kz, "Kз"));
                    var map = result.Map;
                    summary.Text = $"Emin {map.Min:0.0} · Eср {map.Average:0.0} · Emax {map.Max:0.0} лк  |  Emin/Eср {map.Uniformity:0.000}  |  узлов {map.Points.Count}, источников {result.Sources.Count}. Синий — минимум, красный — максимум. Контур аппроксимирован сеткой; проверяйте сходимость уменьшением шага.";
                    Redraw();
                }
                finally { window.Cursor = null; }
            }, true),
            EomUi.Button("Обновить изолинии", Redraw),
            EomUi.Button("Изолинии на план", () => { var r = Ready(); export(r, r.Map.Contours(Levels()), false); }),
            EomUi.Button("Сетка на план", () => { var r = Ready(); export(r, [], true); }),
            EomUi.Button("CSV…", () =>
            {
                var r = Ready(); var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "Освещённость.csv" };
                if (dialog.ShowDialog(window) != true) return;
                var data = new StringBuilder("X_room_m;Y_room_m;Z_room_m;E_lux\r\n");
                foreach (var p in r.Map.Points) data.AppendLine(FormattableString.Invariant($"{p.X:G17};{p.Y:G17};{r.PlaneZ:G17};{p.Lux:G17}"));
                File.WriteAllText(dialog.FileName, data.ToString(), new UTF8Encoding(true));
            }),
            EomUi.Button("PNG…", () =>
            {
                Ready(); Redraw(); var dialog = new SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = "Изолюксы.png" };
                if (dialog.ShowDialog(window) != true) return;
                mapView.UpdateLayout();
                var bitmap = mapView.CaptureBitmap();
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(dialog.FileName); encoder.Save(stream);
            }),
            EomUi.Button("Закрыть", window.Close));
        EomUi.Layout(window, mapView, top, buttons);
        roomBox.SelectedIndex = selected == null ? 0 : rooms.IndexOf(selected);
        window.ShowDialog();
    }
}
