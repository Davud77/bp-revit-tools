using System.Windows;
using System.Windows.Controls;

namespace BotPlus.Eom;

/// <summary>Менеджер БД: редактируемые таблицы каталога. Сохраняется в общий файл пользователя.</summary>
internal static class CatalogWindow
{
    internal static void Show()
    {
        var catalog = EomCatalog.Load();
        var window = EomUi.Window("Менеджер БД", 960, 600);
        DataGrid Grid<T>(List<T> items) => new() { ItemsSource = items, AutoGenerateColumns = true, CanUserAddRows = true, CanUserDeleteRows = true, HeadersVisibility = DataGridHeadersVisibility.Column };
        var headers = new Dictionary<string, string>
        {
            ["Mark"] = "Марка", ["Cores"] = "Жил", ["Section"] = "Сечение, мм²", ["AllowedCurrent"] = "Iдоп, А", ["Resistance"] = "R, Ом/км", ["Name"] = "Наименование",
            ["Series"] = "Серия", ["Kind"] = "Вид", ["Curve"] = "Характеристика", ["Rating"] = "In, А", ["Poles"] = "Полюсов", ["Leakage"] = "IΔn, мА",
            ["Width"] = "Ширина, мм", ["Height"] = "Высота, мм", ["Power"] = "P, Вт", ["Flux"] = "Φ, лм", ["Photometry"] = "Файл IES/LDT"
        };
        var tabs = new TabControl();
        foreach (var (title, grid) in new[] { ("Кабели", Grid(catalog.Cables)), ("Защитные аппараты", Grid(catalog.Breakers)), ("Лотки", Grid(catalog.Trays)), ("Светильники", Grid(catalog.Luminaires)) })
        {
            grid.AutoGeneratingColumn += (_, e) =>
            {
                if (headers.TryGetValue(e.PropertyName, out var h)) e.Column.Header = h;
                if (e.PropertyName == "Name" && title != "Лотки" && title != "Светильники") e.Column.IsReadOnly = true;
            };
            tabs.Items.Add(new TabItem { Header = title, Content = grid });
        }
        EomUi.Layout(window, tabs,
            EomUi.Hint($"Демонстрационный каталог: кабели ВВГнг(А)-LS (Iдоп по ПУЭ, табл. 1.3.6), автоматы, УЗО, дифавтоматы, лотки, светильники. Файл: {EomCatalog.DefaultPath}"),
            EomUi.Buttons(
                EomUi.Button("Открыть папку", () => { Directory.CreateDirectory(Path.GetDirectoryName(EomCatalog.DefaultPath)!); System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(EomCatalog.DefaultPath)!); }),
                EomUi.Button("Сбросить к демо", () => { if (MessageBox.Show(window, "Заменить каталог демонстрационным?", "BotPlus ЭОМ", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { EomCatalog.Demo().Save(); window.Close(); Show(); } }),
                EomUi.Button("Сохранить", () => { catalog.Save(); window.Close(); }, true),
                EomUi.Button("Отмена", window.Close)));
        window.ShowDialog();
    }
}
