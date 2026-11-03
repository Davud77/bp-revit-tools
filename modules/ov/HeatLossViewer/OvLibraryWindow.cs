using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using HeatLossViewer.Export;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Binding = System.Windows.Data.Binding;
using DataGrid = System.Windows.Controls.DataGrid;
using TabControl = System.Windows.Controls.TabControl;
using Window = System.Windows.Window;

namespace HeatLossViewer;

public sealed class OvLibraryWindow : Window
{
    public OvLibraryData Data { get; }
    private readonly TabControl tabs = new();
    private readonly List<DataGrid> grids = [];
    private readonly List<ListCollectionView> views = [];
    private readonly List<Action> detailRefresh = [];
    private readonly NativeRadiatorPane radiators;
    private readonly TextBlock status = new() { Margin = new Thickness(0, 6, 0, 8), TextWrapping = TextWrapping.Wrap };
    private bool busy;
    public OvLibraryWindow(OvLibraryData data, BuildingData building, bool showRadiators = false)
    {
        Data = data; Title = "ОВ — библиотека проекта"; Width = 1180; Height = 790; MinWidth = 800; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var bottom = new DockPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var save = new Button { Content = "Сохранить в модели", Padding = new Thickness(14, 7, 14, 7), IsDefault = true };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        buttons.Children.Add(save); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); bottom.Children.Add(buttons); bottom.Children.Add(status);
        status.Text = "Данные из текущей модели и загруженных связей. Материалы основной модели сохраняют штатные теплотехнические свойства Revit; для связей сохраняются переопределения в этом проекте. После записи сохраните RVT.";
        root.Children.Add(tabs);
        AddMaterials(); AddTypes("Стены", "wall"); AddTypes("Перекрытия", "floor"); AddTypes("Полы по грунту", "floor", true); AddTypes("Окна", "window"); AddTypes("Двери", "door");
        radiators = new NativeRadiatorPane(building); var radiatorTab = new TabItem { Header = "Радиаторы", Content = radiators }; tabs.Items.Add(radiatorTab);
        tabs.SelectionChanged += async (sender, e) =>
        {
            if (e.Source != tabs) return;
            try { Commit(); foreach (var view in views) view.Refresh(); foreach(var refresh in detailRefresh)refresh(); }
            catch (Exception ex) { status.Text = ex.Message; return; }
            if (tabs.SelectedItem == radiatorTab)
                try { Commit(); Data.Validate(); await radiators.UpdateLibraryAsync(Data); }
                catch (Exception ex) { status.Text = ex.Message; }
        };
        save.Click += async (_, _) =>
        {
            try
            {
                busy = true; save.IsEnabled = cancel.IsEnabled = false;
                Commit(); Data.Validate(); Data.Radiators = await radiators.CollectAsync(); Data.Validate(); DialogResult = true;
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; save.IsEnabled = cancel.IsEnabled = true; }
        };
        Closing += (_, e) => { if (busy && DialogResult != true) e.Cancel = true; };
        Closed += (_, _) => radiators.Dispose();
        if (showRadiators) tabs.SelectedItem = radiatorTab;
    }
    private void Commit()
    {
        foreach (var grid in grids)
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true) || Errors(grid))
                throw new ArgumentException("Проверьте числовые поля библиотеки; допустимы точка и запятая.");
    }
    private static bool Errors(DependencyObject item)
    {
        if (Validation.GetHasError(item)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) if (Errors(VisualTreeHelper.GetChild(item, i))) return true;
        return false;
    }
    private DataGrid GridFor(IList source)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(0, 8, 0, 0), IsSynchronizedWithCurrentItem = false };
        var view = new ListCollectionView(source); views.Add(view); grid.ItemsSource = view; grids.Add(grid); return grid;
    }
    private static void Text(DataGrid grid, string title, string path, bool readOnly = false, double width = 110)
    {
        grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true, ConverterCulture = CultureInfo.CurrentCulture, StringFormat = "{0:0.######}" }, IsReadOnly = readOnly, Width = width });
    }
    private static void Check(DataGrid grid, string title, string path, bool readOnly = false)
    { grid.Columns.Add(new DataGridCheckBoxColumn { Header = title, Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, IsReadOnly = readOnly, Width = 90 }); }
    private void AddMaterials()
    {
        var grid = GridFor(Data.Materials); Text(grid, "ElementId", "ElementId", true, 85); Text(grid, "Материал", "Name", true, 320);
        Text(grid, "λ, Вт/(м·К)", "Lambda"); Text(grid, "ρ, кг/м³", "Density"); Text(grid, "c, Дж/(кг·К)", "SpecificHeat", false, 130); Text(grid, "Типов стен", "WallTypes", true, 90); Check(grid, "В связи", "ReadOnly", true);
        AddTab("Материалы", grid, "Материалы стен и слоёв перекрытий. Пустое поле — свойство не задано. λ участвует в послойном R конструкций.");
    }
    private void AddTypes(string label, string kind, bool ground = false)
    {
        var rows = Data.Constructions.Where(t => t.Kind == kind).ToList(); var grid = GridFor(rows);
        var view = (ListCollectionView)grid.ItemsSource;
        view.Filter = item => !ground || ((OvConstruction)item).GroundFloor;
        Text(grid, "ElementId", "ElementId", true, 85); Text(grid, "Тип Revit", "Name", true, 310); Text(grid, "Экземпляров", "Instances", true, 90);
        Check(grid, "В расчёт", "UseInCalculation");
        if (kind is "wall" or "floor") { Check(grid, "По слоям", "UseLayers"); Text(grid, "Однородность r", "Homogeneity"); }
        Text(grid, "R₀, м²·К/Вт", "R0"); Text(grid, "n", "N", false, 65);
        if (kind == "floor") Check(grid, "Пол по грунту", "GroundFloor");
        if (kind is "window" or "door") { Text(grid, "Gn, кг/(м²·ч)", "Gn"); Text(grid, "f", "F", false, 65); Text(grid, "ai", "Ai", false, 65); }
        Check(grid, "В связи", "ReadOnly", true);
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        void RefreshDetails()
        {
            if (grid.SelectedItem is not OvConstruction row) { details.Text = ""; return; }
            double? r = Data.Resistance(row);
            details.Text = (row.Layers.Count == 0 ? "Нет послойной структуры." : string.Join("\n", row.Layers.Select(l => $"{l.Function}: {l.MaterialName}, {l.Thickness:0.###} м"))) + "\nR по выбранной схеме: " + (r?.ToString("0.###") ?? "не определено: заполните λ материалов или задайте R₀ вручную") + " м²·К/Вт";
        }
        detailRefresh.Add(RefreshDetails); grid.SelectionChanged += (_, _) => RefreshDetails();
        grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshDetails));
        AddTab(label, grid, ground ? "Здесь только типы, отмеченные «Пол по грунту» на вкладке «Перекрытия». Классификация задаётся явно." : "«В расчёт» включает параметры типа в Poteri. «По слоям» использует толщины Revit и λ материалов; иначе используется R₀. Геометрические толщины здесь не изменяются.", details);
    }
    private void AddTab(string name, DataGrid grid, string description, UIElement? details = null)
    {
        var panel = new DockPanel { Margin = new Thickness(8) };
        var text = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }; DockPanel.SetDock(text, Dock.Top); panel.Children.Add(text);
        var search = new TextBox { Margin = new Thickness(0, 8, 0, 0), ToolTip = "Поиск по названию или ElementId" }; DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        var view = (ListCollectionView)grid.ItemsSource; var original = view.Filter;
        search.TextChanged += (_, _) => { string query = search.Text.Trim(); view.Filter = item => (original?.Invoke(item) ?? true) && (item is OvMaterial m ? (m.Name + " " + m.ElementId).Contains(query, StringComparison.CurrentCultureIgnoreCase) : item is OvConstruction c && (c.Name + " " + c.ElementId).Contains(query, StringComparison.CurrentCultureIgnoreCase)); };
        if (details is not null) { DockPanel.SetDock(details, Dock.Bottom); panel.Children.Add(details); }
        panel.Children.Add(grid); tabs.Items.Add(new TabItem { Header = name, Content = panel });
    }
}
