using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using HeatLossViewer.Export;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Window = System.Windows.Window;

namespace HeatLossViewer;

[Transaction(TransactionMode.Manual)]
public sealed class ProjectInformationCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument) return Result.Cancelled;
        try
        {
            var window = new ProjectInformationWindow(ProjectConditionsStore.Read(doc), InputCatalog(doc));
            new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
            if (window.ShowDialog() != true) return Result.Cancelled;
            using var transaction = new Transaction(doc, "ОВ — сведения о проекте");
            transaction.Start();
            ProjectConditionsStore.Write(doc, window.Value);
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit не сохранил сведения о проекте.");
            return Result.Succeeded;
        }
        catch (Exception ex) { message = "Сведения о проекте: " + ex.Message; return Result.Failed; }
    }

    private static List<(string Key, string Label, string LevelKey, string LevelName)> InputCatalog(Document host)
    {
        var result = new List<(string, string, string, string)>();
        void Read(Document doc, string source, HashSet<Document> parents, bool links)
        {
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Autodesk.Revit.DB.Architecture.Room>().Where(r => r.Area > 0).OrderBy(r => r.Number).ToList();
            foreach (var room in rooms)
            {
                var level = doc.GetElement(room.LevelId) as Level;
                result.Add((source + "/" + room.UniqueId, $"{level?.Name} · {room.Number} · {room.Name}", source + "/level/" + (level?.UniqueId ?? "none"), level?.Name ?? "Без уровня"));
            }
            if (!links && rooms.Count > 0) return;
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var linked = link.GetLinkDocument(); if (linked is null || parents.Contains(linked)) continue;
                parents.Add(linked); Read(linked, source + "/" + link.UniqueId, parents, true); parents.Remove(linked);
            }
        }
        Read(host, "host", [host], false); return result;
    }
}

public sealed class ProjectInformationWindow : Window
{
    public ProjectConditions Value { get; private set; }
    private readonly Dictionary<string, TextBox> fields = [];

    public ProjectInformationWindow(ProjectConditions data, List<(string Key, string Label, string LevelKey, string LevelName)>? catalog = null)
    {
        Value = data;
        Title = "ОВ — сведения о проекте";
        Width = 1040; Height = 680; MinWidth = 760; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(14) };
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var save = new Button { Content = "Сохранить в модели", IsDefault = true, Padding = new Thickness(14, 7, 14, 7) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(14, 7, 14, 7) };
        actions.Children.Add(save); actions.Children.Add(cancel);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var content = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        var tabs = new System.Windows.Controls.TabControl();
        tabs.Items.Add(new TabItem { Header = "Проект", Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        root.Children.Add(tabs);
        Content = root;
        Add(nameof(data.Name), "Название проекта", data.Name);
        Add(nameof(data.Number), "Номер проекта", data.Number);
        Add(nameof(data.Address), "Адрес / населённый пункт", data.Address);
        Add(nameof(data.ClimateRegion), "Климатический район / зона", data.ClimateRegion);
        Add(nameof(data.ClimateSubregion), "Климатический подрайон", data.ClimateSubregion);
        Add(nameof(data.OutdoorTemperature), "Расчётная наружная температура, °C", data.OutdoorTemperature);
        Add(nameof(data.IndoorTemperature), "Базовая внутренняя температура, °C", data.IndoorTemperature);
        Add(nameof(data.WindSpeed), "Расчётная скорость ветра, м/с", data.WindSpeed);
        Add(nameof(data.HeatingPeriodTemperature), "Средняя температура отопительного периода, °C", data.HeatingPeriodTemperature);
        Add(nameof(data.HeatingPeriodDays), "Продолжительность отопительного периода, суток", data.HeatingPeriodDays);
        Add(nameof(data.BuildingHeight), "Высота здания над землёй, м", data.BuildingHeight);
        Add(nameof(data.FirstFloorHeight), "От земли до первого этажа, м", data.FirstFloorHeight);
        Add(nameof(data.Notes), "Примечания / принятые условия", data.Notes);
        var calculation = new ProjectCalculationEditor(data.Calculation, catalog ?? [],
            (key, number) => fields[key].Text = number.ToString(CultureInfo.CurrentCulture),
            key => double.TryParse(fields[key].Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null);
        tabs.Items.Add(new TabItem { Header = "Исходные данные", Content = calculation });
        save.Click += (_, _) =>
        {
            try
            {
                var next = new ProjectConditions();
                foreach (var property in typeof(ProjectConditions).GetProperties())
                {
                    if (property.Name == nameof(ProjectConditions.Calculation)) continue;
                    string text = fields[property.Name].Text.Trim();
                    if (property.PropertyType == typeof(string)) property.SetValue(next, text);
                    else
                    {
                        double? number = null;
                        if (text.Length > 0)
                        {
                            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
                            { fields[property.Name].Focus(); throw new ArgumentException("Введите число (допустимы точка и запятая) или оставьте поле пустым."); }
                            number = parsed;
                        }
                        property.SetValue(next, number);
                    }
                }
                next.Calculation = calculation.Collect();
                next.Validate(); Value = next; DialogResult = true;
            }
            catch (ArgumentException ex) { System.Windows.MessageBox.Show(this, ex.Message, "Проверьте сведения", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };

        void Add(string key, string label, object? value)
        {
            var group = new StackPanel { Margin = new Thickness(0, 0, 12, 8) };
            group.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 3) });
            var field = new TextBox { Text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "", Padding = new Thickness(4), MinHeight = 27 };
            field.ToolTip = "Пустое поле — не задано. После изменения сохраните RVT.";
            System.Windows.Automation.AutomationProperties.SetName(field, label);
            fields.Add(key, field); group.Children.Add(field); content.Children.Add(group);
        }
    }
}
