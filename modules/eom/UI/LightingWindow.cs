using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.Revit.DB;
using Binding = System.Windows.Data.Binding;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace BotPlus.Eom;

/// <summary>Строка расчёта освещённости по помещению. Норма E редактируется в таблице, остальное пересчитывается.</summary>
internal sealed class LightingRow : INotifyPropertyChanged
{
    internal RoomRef Room { get; }
    internal LightingRow(RoomRef room) { Room = room; lux = Lighting.DefaultLux(room.Name); }
    public string Source => Room.Source;
    public string Number => Room.Number;
    public string Name => Room.Name;
    public string Area => Room.AreaM2.ToString("0.0");
    double lux;
    public double Lux { get => lux; set { lux = value; Changed?.Invoke(); } }
    public double Index { get; private set; }
    public double Eta { get; private set; }
    public int Count { get; private set; }
    public double Actual { get; private set; }
    public string IndexText => Index.ToString("0.00");
    public string EtaText => Eta.ToString("0.00");
    public string ActualText => Actual.ToString("0");
    internal Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Calculate(LuminaireItem lum, double workPlane, double mount, double kz)
    {
        double h = Math.Max(0.1, mount - workPlane);
        Index = Lighting.RoomIndex(Room.AreaM2, Room.PerimeterM, h);
        Eta = Lighting.Utilization(Index);
        Count = Lighting.Count(Lux, Room.AreaM2, lum.Flux, Eta, kz);
        Actual = Lighting.Illuminance(Count, Room.AreaM2, lum.Flux, Eta, kz);
        foreach (var p in new[] { nameof(IndexText), nameof(EtaText), nameof(Count), nameof(ActualText) }) PropertyChanged?.Invoke(this, new(p));
    }
}

/// <summary>Окно расчёта освещённости методом коэффициента использования и запуска авторасстановки.</summary>
internal static class LightingWindow
{
    internal sealed record Placement(List<LightingRow> Rows, ElementId Symbol, double MountM);

    internal static Placement? Show(List<RoomRef> rooms, List<(ElementId Id, string Name)> symbols)
    {
        var catalog = EomCatalog.Load();
        var window = EomUi.Window("Расчёт освещённости", 1080, 640);
        var rows = rooms.Select(r => new LightingRow(r)).ToList();
        var luminaire = new ComboBox { Width = 300, ItemsSource = catalog.Luminaires, SelectedIndex = 0 };
        TextBox work = EomUi.Number(0.8, 60), mount = EomUi.Number(3.0, 60), kz = EomUi.Number(1.4, 60);
        var family = new ComboBox { Width = 300, ItemsSource = symbols.Select(s => s.Name).ToList(), SelectedIndex = symbols.Count > 0 ? 0 : -1 };
        var total = new TextBlock { Margin = new Thickness(0, 6, 0, 0), Opacity = 0.8 };

        void Recalc()
        {
            if (luminaire.SelectedItem is not LuminaireItem lum) return;
            double w, m, k;
            try { w = EomUi.Parse(work, "Рабочая поверхность"); m = EomUi.Parse(mount, "Высота подвеса"); k = EomUi.Parse(kz, "Kз"); } catch { return; }
            foreach (var r in rows) r.Calculate(lum, w, m, k);
            total.Text = $"Всего светильников: {rows.Sum(r => r.Count)}, установленная мощность {rows.Sum(r => r.Count) * lum.Power / 1000:0.00} кВт.";
        }
        foreach (var r in rows) r.Changed = Recalc;
        luminaire.SelectionChanged += (_, _) => Recalc();
        foreach (var box in new[] { work, mount, kz }) box.TextChanged += (_, _) => Recalc();

        var grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = rows, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Extended };
        void Col(string header, string path, bool edit = false) => grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, IsReadOnly = !edit });
        Col("Источник", nameof(LightingRow.Source)); Col("№", nameof(LightingRow.Number)); Col("Помещение", nameof(LightingRow.Name)); Col("S, м²", nameof(LightingRow.Area));
        Col("Eн, лк", nameof(LightingRow.Lux), true); Col("i", nameof(LightingRow.IndexText)); Col("η", nameof(LightingRow.EtaText));
        Col("N, шт", nameof(LightingRow.Count)); Col("Eф, лк", nameof(LightingRow.ActualText));
        grid.Columns[2].Width = new DataGridLength(1, DataGridLengthUnitType.Star);

        var top = new StackPanel();
        top.Children.Add(EomUi.Hint("N = Eн·S·Kз·z / (Φ·η), z = 1,1. η — по индексу помещения i = 2S/(h·P) для отражений 70/50/20 % (демонстрационная таблица; расчёт по IES — в разработке). Нормы Eн подставлены по названию помещения — проверьте и исправьте."));
        var line1 = new WrapPanel();
        line1.Children.Add(EomUi.Row("Светильник из каталога", luminaire));
        top.Children.Add(line1);
        var line2 = new StackPanel { Orientation = Orientation.Horizontal };
        line2.Children.Add(new TextBlock { Text = "Рабочая поверхность, м ", VerticalAlignment = VerticalAlignment.Center }); line2.Children.Add(work);
        line2.Children.Add(new TextBlock { Text = "   Высота подвеса, м ", VerticalAlignment = VerticalAlignment.Center }); line2.Children.Add(mount);
        line2.Children.Add(new TextBlock { Text = "   Kз ", VerticalAlignment = VerticalAlignment.Center }); line2.Children.Add(kz);
        top.Children.Add(line2);
        top.Children.Add(EomUi.Row("Семейство для расстановки", family));
        top.Children.Add(new TextBlock { Height = 6 });

        var bottom = new DockPanel();
        DockPanel.SetDock(total, Dock.Left); bottom.Children.Add(total);
        Placement? result = null;
        bottom.Children.Add(EomUi.Buttons(
            EomUi.Button("Расставить в выбранных", () =>
            {
                var selected = grid.SelectedItems.Cast<LightingRow>().Where(r => r.Count > 0).ToList();
                if (selected.Count == 0) throw new InvalidOperationException("Выделите строки помещений (Ctrl/Shift) с ненулевым числом светильников.");
                if (family.SelectedIndex < 0) throw new InvalidOperationException("В проекте нет семейств осветительных приборов. Загрузите семейство через «База УГО».");
                result = new(selected, symbols[family.SelectedIndex].Id, EomUi.Parse(mount, "Высота подвеса"));
                window.DialogResult = true;
            }, true),
            EomUi.Button("Закрыть", () => window.DialogResult = false)));
        EomUi.Layout(window, grid, top, bottom);
        Recalc();
        return window.ShowDialog() == true ? result : null;
    }
}
