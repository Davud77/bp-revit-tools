using System.Windows;
using System.Windows.Controls;

namespace BotPlus.Eom;

/// <summary>Менеджер БД: редактируемые таблицы каталога. Сохраняется в общий файл пользователя.</summary>
internal static class CatalogWindow
{
    internal static void Show()
    {
        var catalog = EomCatalog.Load();
        var prototypes = PrototypeLibrary.Load();
        var window = EomUi.Window("Менеджер БД", 960, 600);
        DataGrid Grid<T>(List<T> items) => new() { ItemsSource = items, AutoGenerateColumns = true, CanUserAddRows = true, CanUserDeleteRows = true, HeadersVisibility = DataGridHeadersVisibility.Column };
        var headers = new Dictionary<string, string>
        {
            ["Mark"] = "Марка", ["Cores"] = "Жил", ["Section"] = "Сечение, мм²", ["AllowedCurrent"] = "Iдоп, А", ["Resistance"] = "R, Ом/км", ["Name"] = "Наименование",
            ["Series"] = "Серия", ["Kind"] = "Вид", ["Curve"] = "Характеристика", ["Rating"] = "In, А", ["Poles"] = "Полюсов", ["Leakage"] = "IΔn, мА",
            ["Width"] = "Ширина, мм", ["Height"] = "Высота, мм", ["Power"] = "P, Вт", ["Flux"] = "Φ, лм", ["Photometry"] = "Файл IES/LDT", ["Manufacturer"] = "Производитель", ["LampType"] = "Тип ламп", ["LampCount"] = "Ламп", ["Efficiency"] = "КПД", ["PowerFactor"] = "cos φ",
            ["Mounting"] = "Установка", ["IP"] = "IP", ["LengthMm"] = "Длина, мм", ["WidthMm"] = "Ширина, мм", ["Group"] = "Каталог", ["VisualCategory"] = "Разряд",
            ["Lux"] = "Eн, лк", ["WorkPlaneM"] = "Раб. поверхность, м", ["Maintenance"] = "Kз", ["Ceiling"] = "ρп", ["Walls"] = "ρс", ["Floor"] = "ρр", ["Environment"] = "Среда", ["Keywords"] = "Ключевые слова",
            ["HeightMm"] = "Высота, мм", ["Description"] = "Описание в спецификации", ["Code"] = "Код оборудования", ["Document"] = "Нормативный документ", ["Voltage"] = "U, В", ["MassKg"] = "Масса, кг",
            ["Label"] = "Подпись", ["Temperature"] = "t, °C", ["RoomCategory"] = "Категория", ["ExplosionZone"] = "Взрывоопасная зона", ["FireZone"] = "Пожароопасная зона", ["RevitType"] = "Семейство Revit"
        };
        var tabs = new TabControl();
        foreach (var (title, grid) in new[] { ("Кабели", Grid(catalog.Cables)), ("Защитные аппараты", Grid(catalog.Breakers)), ("Лотки", Grid(catalog.Trays)), ("Светильники", Grid(catalog.Luminaires)), ("Прототипы помещений", Grid(prototypes.Items)) })
        {
            grid.AutoGeneratingColumn += (_, e) =>
            {
                if (e.PropertyName is "Utilization" or "LuminaireFlux" || title == "Прототипы помещений" && e.PropertyName is "Norm" or "Section") { e.Cancel = true; return; }
                if (headers.TryGetValue(e.PropertyName, out var h)) e.Column.Header = h;
                if (title == "Светильники" && e.PropertyName == "Section") e.Column.Header = "Раздел";
                if (e.PropertyName == "Name" && title != "Лотки" && title != "Светильники" && title != "Прототипы помещений") e.Column.IsReadOnly = true;
            };
            tabs.Items.Add(new TabItem { Header = title, Content = grid });
        }
        EomUi.Layout(window, tabs,
            EomUi.Hint($"Демонстрационный каталог: кабели ВВГнг(А)-LS (Iдоп по ПУЭ, табл. 1.3.6), автоматы, УЗО, дифавтоматы, лотки, светильники (Ки — типовая таблица КСС Д × КПД, если у светильника нет своей; «Семейство Revit» — связанное семейство). " +
                (prototypes.Source.Length > 0 ? $"Прототипы помещений импортированы из nanoCAD BIM Электро: {prototypes.Items.Count} шт. ({prototypes.Source}). " : "Прототипы помещений — сокращённый встроенный набор по СП 52.13330.2016; полную библиотеку можно импортировать из nanoCAD. ") +
                $"Файлы: {EomCatalog.DefaultPath}, {PrototypeLibrary.DefaultPath}"),
            EomUi.Buttons(
                EomUi.Button("Импорт прототипов nanoCAD…", () =>
                {
                    var found = NanoCadPrototypes.Find();
                    var dialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "Прототипы комнат nanoCAD BIM Электро", Filter = "Прототипы комнат (*.prt)|*.prt|Все файлы|*.*",
                        InitialDirectory = found != null ? Path.GetDirectoryName(found) : null, FileName = found != null ? Path.GetFileName(found) : ""
                    };
                    if (dialog.ShowDialog(window) != true) return;
                    int count = prototypes.Import(dialog.FileName);
                    prototypes.Save(); catalog.Save();
                    MessageBox.Show(window, $"Импортировано прототипов: {count}. Пользовательские прототипы сохранены.", "BotPlus ЭОМ");
                    window.Close(); Show();
                }),
                EomUi.Button("Открыть папку", () => { Directory.CreateDirectory(Path.GetDirectoryName(EomCatalog.DefaultPath)!); System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(EomCatalog.DefaultPath)!); }),
                EomUi.Button("Сбросить к демо", () => { if (MessageBox.Show(window, "Заменить каталог и библиотеку прототипов встроенными?", "BotPlus ЭОМ", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { EomCatalog.Demo().Save(); PrototypeLibrary.Builtin().Save(); window.Close(); Show(); } }),
                EomUi.Button("Сохранить", () => { catalog.Save(); prototypes.Save(); window.Close(); }, true),
                EomUi.Button("Отмена", window.Close)));
        window.ShowDialog();
    }
}
