using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Collections;
using HeatLossViewer.Export;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TabControl = System.Windows.Controls.TabControl;
using Panel = System.Windows.Controls.Panel;

namespace HeatLossViewer;

public sealed class ProjectCalculationEditor : TabControl
{
    private ProjectCalculationInputs draft;
    private readonly Dictionary<string, TextBox> building = [];
    private readonly List<ScopeRow> floors = [], rooms = [];
    private readonly List<DataGrid> scopeGrids = [];
    private readonly CheckBox reviewed = new() { Content = "Исходные данные проверены", Margin = new Thickness(0, 8, 0, 8) };
    private readonly CheckBox orientation = new() { Content = "Добавки по ориентации Poteri", Margin = new Thickness(0, 8, 0, 8) };
    private static readonly Dictionary<string, string> Climate = new()
    { ["indoor"] = "IndoorTemperature", ["outdoor"] = "OutdoorTemperature", ["windSpeed"] = "WindSpeed", ["buildingHeight"] = "BuildingHeight" };

    public ProjectCalculationEditor(ProjectCalculationInputs value, List<(string Key, string Label, string LevelKey, string LevelName)> catalog,
        Action<string, double> importClimate, Func<string, double?> readClimate)
    {
        draft = JsonSerializer.Deserialize<ProjectCalculationInputs>(JsonSerializer.Serialize(value))!;
        var general = new StackPanel { Margin = new Thickness(10) };
        general.Children.Add(new TextBlock { Text = "Температуры, ветер и высота — на вкладке «Проект». Пустое поле использует значение по умолчанию; этаж и помещение наследуют параметры здания. U = 1 / R.", TextWrapping = TextWrapping.Wrap });
        general.Children.Add(Form(draft.Building, building, false));
        reviewed.IsChecked = draft.Reviewed; orientation.IsChecked = draft.OrientationAdditions;
        general.Children.Add(orientation); general.Children.Add(reviewed);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var import = new Button { Content = "Импорт исходных данных JSON", Padding = new Thickness(8) };
        var export = new Button { Content = "Сохранить исходные данные JSON", Padding = new Thickness(8), Margin = new Thickness(8, 0, 0, 0) };
        actions.Children.Add(import); actions.Children.Add(export); general.Children.Add(actions);
        Add("Здание", general);
        AddScope("Этажи", catalog.GroupBy(r => r.LevelKey).Select(g => (g.Key, g.First().LevelName)).ToList(), draft.FloorOverrides, floors);
        AddScope("Помещения", catalog.Select(r => (r.Key, r.Label)).ToList(), draft.RoomOverrides, rooms);
        import.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Исходные данные JSON|*.json" };
            if (dialog.ShowDialog() != true) return;
            try
            {
                foreach(var grid in scopeGrids)
                    if(!grid.CommitEdit(DataGridEditingUnit.Cell,true)||!grid.CommitEdit(DataGridEditingUnit.Row,true))throw new ArgumentException("Завершите редактирование ячейки.");
                using var json = JsonDocument.Parse(File.ReadAllText(dialog.FileName));
                var settings = json.RootElement.GetProperty("settings");
                var next = new ProjectCalculationInputs();
                foreach (var field in ProjectCalculationInputs.Fields)
                    if (settings.TryGetProperty(field.Key, out var item) && item.TryGetDouble(out var number)) next.Building[field.Key] = number;
                if (settings.TryGetProperty("floorOverrides", out var f)) next.FloorOverrides = f.Deserialize<Dictionary<string, Dictionary<string, double>>>() ?? [];
                if (settings.TryGetProperty("roomOverrides", out var r)) next.RoomOverrides = r.Deserialize<Dictionary<string, Dictionary<string, double>>>() ?? [];
                // Legacy ventilation choices are intentionally not imported.
                foreach (var scope in next.FloorOverrides.Values.Concat(next.RoomOverrides.Values))
                    foreach (var key in scope.Keys.Where(k => !ProjectCalculationInputs.RoomKeys.Contains(k)).ToList()) scope.Remove(key);
                next.OrientationAdditions = !settings.TryGetProperty("orientationAdditions", out var o) || o.GetBoolean();
                next.Validate();
                foreach (var (key, property) in Climate)
                    if (next.Building.Remove(key, out var n)) importClimate(property, n);
                draft.Building = next.Building;
                draft.FloorOverrides.Clear(); foreach (var entry in next.FloorOverrides) draft.FloorOverrides.Add(entry.Key, entry.Value);
                draft.RoomOverrides.Clear(); foreach (var entry in next.RoomOverrides) draft.RoomOverrides.Add(entry.Key, entry.Value);
                foreach (var (key, input) in building) input.Text = next.Building.TryGetValue(key, out var n) ? n.ToString(CultureInfo.CurrentCulture) : "";
                foreach (var row in floors) row.Fill(next.FloorOverrides.GetValueOrDefault(row.Key));
                foreach (var row in rooms) row.Fill(next.RoomOverrides.GetValueOrDefault(row.Key));
                foreach (var grid in scopeGrids) grid.Items.Refresh();
                reviewed.IsChecked = false; orientation.IsChecked = next.OrientationAdditions;
            }
            catch (Exception ex) { System.Windows.MessageBox.Show("Не удалось загрузить исходные данные: " + ex.Message); }
        };
        export.Click += (_, _) =>
        {
            try
            {
                var data = Collect();
                var settings = data.Building.ToDictionary(p => p.Key, p => (object)p.Value);
                foreach (var (key, property) in Climate) if (readClimate(property) is double n) settings[key] = n;
                settings["method"] = "poteri"; settings["reviewed"] = data.Reviewed; settings["orientationAdditions"] = data.OrientationAdditions;
                settings["floorOverrides"] = data.FloorOverrides; settings["roomOverrides"] = data.RoomOverrides;
                var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Исходные данные JSON|*.json", FileName = "ov-inputs.json" };
                if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { settings }, BuildingExportWriter.JsonOptions));
            }
            catch (Exception ex) { System.Windows.MessageBox.Show("Не удалось сохранить исходные данные: " + ex.Message); }
        };
    }

    private void Add(string name, UIElement content) => Items.Add(new TabItem { Header = name, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
    public sealed class ScopeRow
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public Dictionary<string, string> Values { get; } = [];
        public void Fill(Dictionary<string, double>? values)
        { foreach (string key in ProjectCalculationInputs.RoomKeys) Values[key] = values?.TryGetValue(key, out var n) == true ? n.ToString(CultureInfo.CurrentCulture) : ""; }
    }
    private void AddScope(string name, List<(string Key, string Label)> choices, Dictionary<string, Dictionary<string, double>> source, List<ScopeRow> rows)
    {
        foreach (var choice in choices) { var row=new ScopeRow {Key=choice.Key,Label=choice.Label};row.Fill(source.GetValueOrDefault(choice.Key));rows.Add(row); }
        var panel = new DockPanel { Margin = new Thickness(10) };
        var note = new TextBlock { Text = "Пустая ячейка — наследовать параметры здания / этажа. Параметры не меняют геометрию Revit. Заголовки сортируют строки.", TextWrapping = TextWrapping.Wrap, Margin=new Thickness(0,0,0,8) };
        DockPanel.SetDock(note,Dock.Top);panel.Children.Add(note);
        var grid = new DataGrid { ItemsSource=rows,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Single,FrozenColumnCount=1,EnableRowVirtualization=true };
        grid.Columns.Add(new DataGridTextColumn { Header=name=="Этажи"?"Этаж":"Помещение / этаж",Binding=new System.Windows.Data.Binding("Label"),IsReadOnly=true,Width=280,SortMemberPath="Label" });
        foreach (var field in ProjectCalculationInputs.Fields.Where(f=>ProjectCalculationInputs.RoomKeys.Contains(f.Key)))
            grid.Columns.Add(new DataGridTextColumn { Header=field.Label,Binding=new System.Windows.Data.Binding("Values["+field.Key+"]") {UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},SortMemberPath=field.Key,Width=150 });
        grid.Sorting += (_,e)=>
        {
            if(!grid.CommitEdit(DataGridEditingUnit.Cell,true)||!grid.CommitEdit(DataGridEditingUnit.Row,true)){e.Handled=true;return;}
            e.Handled=true;var direction=e.Column.SortDirection==System.ComponentModel.ListSortDirection.Ascending?System.ComponentModel.ListSortDirection.Descending:System.ComponentModel.ListSortDirection.Ascending;
            foreach(var column in grid.Columns)column.SortDirection=null;e.Column.SortDirection=direction;
            ((ListCollectionView)CollectionViewSource.GetDefaultView(rows)).CustomSort=new ScopeComparer(e.Column.SortMemberPath,direction==System.ComponentModel.ListSortDirection.Ascending?1:-1);
        };
        scopeGrids.Add(grid);panel.Children.Add(grid);Items.Add(new TabItem {Header=name,Content=panel});
    }
    private sealed class ScopeComparer(string key,int direction) : IComparer
    {
        public int Compare(object? x,object? y)
        {
            var a=(ScopeRow)x!;var b=(ScopeRow)y!;
            if(key=="Label")return direction*StringComparer.CurrentCultureIgnoreCase.Compare(a.Label,b.Label);
            bool an=double.TryParse((a.Values[key]??"").Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var av);
            bool bn=double.TryParse((b.Values[key]??"").Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var bv);
            return an&&bn?direction*av.CompareTo(bv):an?-1:bn?1:0;
        }
    }
    private static UIElement FormFromExisting(Dictionary<string, TextBox> fields)
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        foreach (var (key, input) in fields)
        {
            if (input.Parent is Panel parent) parent.Children.Remove(input);
            var group = new StackPanel { Margin = new Thickness(0, 5, 12, 8) };
            group.Children.Add(new TextBlock { Text = ProjectCalculationInputs.Fields.First(f => f.Key == key).Label, FontSize = 12 });
            group.Children.Add(input); grid.Children.Add(group);
            if (new[] { "wall", "window", "door", "roof", "floor" }.Contains(key))
            {
                var resistance = new TextBox { Padding = new Thickness(4), MinHeight = 27, ToolTip = "R, м²·К/Вт; при вводе U = 1 / R" };
                if (double.TryParse(input.Text, out var u) && u > 0) resistance.Text = (1 / u).ToString(CultureInfo.CurrentCulture);
                group.Children.Add(new TextBlock { Text = "или R, м²·К/Вт", FontSize = 11 }); group.Children.Add(resistance);
                System.Windows.Automation.AutomationProperties.SetName(resistance, "Сопротивление R: " + key);
                resistance.TextChanged += (_, _) =>
                {
                    if (double.TryParse(resistance.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && double.IsFinite(r) && r > 0)
                        input.Text = (1 / r).ToString(CultureInfo.CurrentCulture);
                };
                resistance.LostFocus += (_, _) =>
                {
                    if (string.IsNullOrWhiteSpace(resistance.Text)) return;
                    if (double.TryParse(resistance.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && double.IsFinite(r) && r > 0)
                        input.Text = (1 / r).ToString(CultureInfo.CurrentCulture);
                    else System.Windows.MessageBox.Show("Сопротивление R должно быть больше нуля.");
                };
            }
        }
        return grid;
    }
    private static UIElement Form(Dictionary<string, double> values, Dictionary<string, TextBox> fields, bool scope)
    {
        foreach (var field in ProjectCalculationInputs.Fields.Where(f => scope ? ProjectCalculationInputs.RoomKeys.Contains(f.Key) : !Climate.ContainsKey(f.Key)))
        {
            var input = new TextBox { Text = values.TryGetValue(field.Key, out var n) ? n.ToString(CultureInfo.CurrentCulture) : "", Padding = new Thickness(4), MinHeight = 27,
                ToolTip = scope ? "Пусто — наследовать" : "По умолчанию: " + field.Default.ToString(CultureInfo.CurrentCulture) };
            System.Windows.Automation.AutomationProperties.SetName(input, field.Label); fields.Add(field.Key, input);
        }
        return FormFromExisting(fields);
    }
    private static void Fill(Dictionary<string, TextBox> fields, Dictionary<string, double>? values)
    { foreach (var (key, input) in fields) input.Text = values?.TryGetValue(key, out var n) == true ? n.ToString(CultureInfo.CurrentCulture) : ""; }
    public ProjectCalculationInputs Collect()
    {
        Dictionary<string, double> Read(Dictionary<string, TextBox> fields)
        {
            var result = new Dictionary<string, double>();
            foreach (var (key, input) in fields)
            {
                if (string.IsNullOrWhiteSpace(input.Text)) continue;
                if (!double.TryParse(input.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n))
                    throw new ArgumentException("Введите число: " + ProjectCalculationInputs.Fields.First(f => f.Key == key).Label);
                result[key] = n;
            }
            return result;
        }
        var data = JsonSerializer.Deserialize<ProjectCalculationInputs>(JsonSerializer.Serialize(draft))!;
        data.Building = Read(building);
        foreach(var grid in scopeGrids)
            if(!grid.CommitEdit(DataGridEditingUnit.Cell,true)||!grid.CommitEdit(DataGridEditingUnit.Row,true))throw new ArgumentException("Завершите редактирование ячейки.");
        Dictionary<string,double> ReadRow(ScopeRow row)
        {
            var result=new Dictionary<string,double>();
            foreach(var (key,text) in row.Values)
            {
                if(string.IsNullOrWhiteSpace(text))continue;
                if(!double.TryParse(text.Trim().Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)||!double.IsFinite(n))throw new ArgumentException(row.Label+": введите число — "+ProjectCalculationInputs.Fields.First(f=>f.Key==key).Label);
                result[key]=n;
            }
            return result;
        }
        foreach(var row in floors)data.FloorOverrides[row.Key]=ReadRow(row);
        foreach(var row in rooms)data.RoomOverrides[row.Key]=ReadRow(row);
        data.Reviewed = reviewed.IsChecked == true; data.OrientationAdditions = orientation.IsChecked == true;
        data.Validate(); return data;
    }
}
