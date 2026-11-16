using System.Text.Json;
using System.Text.Encodings.Web;

namespace BotPlus.Eom.Core;

public sealed class CableItem
{
    public string Mark { get; set; } = "ВВГнг(А)-LS";
    public int Cores { get; set; } = 3;
    /// <summary>Сечение жилы, мм².</summary>
    public double Section { get; set; }
    /// <summary>Длительно допустимый ток при прокладке в воздухе, А.</summary>
    public double AllowedCurrent { get; set; }
    /// <summary>Активное сопротивление жилы при 20 °C, Ом/км.</summary>
    public double Resistance { get; set; }
    public string Name => $"{Mark} {Cores}х{Section.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))}";
}

public sealed class BreakerItem
{
    public string Series { get; set; } = "ВА47-29";
    /// <summary>Автомат, УЗО или Дифавтомат.</summary>
    public string Kind { get; set; } = "Автомат";
    public string Curve { get; set; } = "C";
    public double Rating { get; set; }
    public int Poles { get; set; } = 1;
    /// <summary>Номинальный отключающий дифференциальный ток, мА (0 — нет).</summary>
    public double Leakage { get; set; }
    public string Name => Kind == "Автомат" ? $"{Series} {Poles}P {Curve}{Rating:0}" : $"{Series} {Poles}P {Rating:0} А {Leakage:0} мА";
}

public sealed class TrayItem
{
    public string Name { get; set; } = "";
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>Светильник БД оборудования: технические данные и коэффициенты использования для расчёта методом Ки.</summary>
public sealed class LuminaireItem
{
    public string Name { get; set; } = "";
    /// <summary>Раздел и серия — уровни дерева БД.</summary>
    public string Section { get; set; } = "Офисно-административное освещение";
    public string Series { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    /// <summary>Номинальная мощность светильника, Вт.</summary>
    public double Power { get; set; }
    /// <summary>Световой поток всех ламп светильника, лм (для LED — поток светильника при КПД = 1).</summary>
    public double Flux { get; set; }
    /// <summary>Тип ламп («LED», «ЛЛ»...) и их количество.</summary>
    public string LampType { get; set; } = "LED";
    public int LampCount { get; set; } = 1;
    /// <summary>Световой КПД светильника, о.е.: для типовой таблицы Ки = ηп·КПД.</summary>
    public double Efficiency { get; set; } = 1;
    public double PowerFactor { get; set; } = 0.95;
    public string Mounting { get; set; } = "Встраиваемый";
    public string IP { get; set; } = "IP20";
    public double LengthMm { get; set; }
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }
    /// <summary>Описание в спецификации, код оборудования, нормативный документ, напряжение, масса.</summary>
    public string Description { get; set; } = "";
    public string Code { get; set; } = "";
    public string Document { get; set; } = "";
    public double Voltage { get; set; } = 220;
    public double MassKg { get; set; }
    /// <summary>Файл фотометрии .ies/.ldt (справочно).</summary>
    public string Photometry { get; set; } = "";
    /// <summary>
    /// Связанное семейство Revit («Семейство: Тип»). При расстановке в этом семействе создаётся тип с именем светильника,
    /// в который записываются данные БД.
    /// </summary>
    public string RevitType { get; set; } = "";
    /// <summary>Собственная таблица Ки из паспорта; null — типовая таблица КСС Д × КПД.</summary>
    public UtilizationTable? Utilization { get; set; }
    public double LuminaireFlux => Flux;
    public override string ToString() => Name;
}

/// <summary>Каталог оборудования. Хранится вне модели, в %APPDATA%\BotPlus RT\eom-catalog.json; при первом запуске — демонстрационный набор.</summary>
public sealed class EomCatalog
{
    public List<CableItem> Cables { get; set; } = [];
    public List<BreakerItem> Breakers { get; set; } = [];
    public List<TrayItem> Trays { get; set; } = [];
    public List<LuminaireItem> Luminaires { get; set; } = [];

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BotPlus RT", "eom-catalog.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static EomCatalog Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<EomCatalog>(File.ReadAllText(path)) is { } catalog && catalog.Cables.Count > 0) return catalog;
        }
        catch { }
        return Demo();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Демонстрационный каталог: медные кабели (ПУЭ табл. 1.3.6, в воздухе), автоматы ВА47-29/ВА88, лотки, светильники.</summary>
    public static EomCatalog Demo()
    {
        var catalog = new EomCatalog();
        (double s, double i, double r)[] copper = [(1.5, 19, 12.1), (2.5, 25, 7.41), (4, 35, 4.61), (6, 42, 3.08), (10, 55, 1.83), (16, 75, 1.15),
            (25, 95, 0.727), (35, 120, 0.524), (50, 145, 0.387), (70, 180, 0.268), (95, 220, 0.193), (120, 260, 0.153)];
        foreach (int cores in new[] { 3, 5 })
            foreach (var (s, i, r) in copper) catalog.Cables.Add(new() { Cores = cores, Section = s, AllowedCurrent = i, Resistance = r });
        foreach (int poles in new[] { 1, 3 })
            foreach (double rating in new double[] { 6, 10, 16, 20, 25, 32, 40, 50, 63 })
                catalog.Breakers.Add(new() { Rating = rating, Poles = poles });
        foreach (double rating in new double[] { 80, 100, 125, 160, 200, 250 })
            catalog.Breakers.Add(new() { Series = "ВА88-32", Rating = rating, Poles = 3 });
        foreach (double rating in new double[] { 16, 25, 40 })
            catalog.Breakers.Add(new() { Series = "АД12", Kind = "Дифавтомат", Rating = rating, Poles = 1, Leakage = 30 });
        foreach (double rating in new double[] { 25, 40, 63 })
            catalog.Breakers.Add(new() { Series = "ВД1-63", Kind = "УЗО", Rating = rating, Poles = 2, Leakage = 30, Curve = "" });
        foreach (var (w, h) in new[] { (100, 50), (200, 50), (300, 50), (200, 100), (400, 100) })
            catalog.Trays.Add(new() { Name = $"Лоток перфорированный {w}х{h}", Width = w, Height = h });
        catalog.Luminaires.AddRange([
            new() { Name = "Панель LED 595х595, 36 Вт, 4000 лм", Series = "Панели LED", Power = 36, Flux = 4000, LengthMm = 595, WidthMm = 595 },
            new() { Name = "Даунлайт LED, 18 Вт, 1800 лм", Series = "Даунлайты LED", Power = 18, Flux = 1800, LengthMm = 200, WidthMm = 200 },
            new() { Name = "Линейный LED 1200 мм, 40 Вт, 4400 лм", Series = "Линейные LED", Mounting = "Накладной", Power = 40, Flux = 4400, LengthMm = 1200, WidthMm = 70 },
            new() { Name = "Пылевлагозащищённый LED, 36 Вт, 3600 лм", Section = "Технические помещения", Series = "Линейные IP65", Mounting = "Накладной", IP = "IP65", Power = 36, Flux = 3600, LengthMm = 1270, WidthMm = 90 },
            new() { Name = "ЛЛ 4х18 Вт 595х595 (зеркальная решётка)", Series = "Растровые ЛЛ", LampType = "ЛЛ", LampCount = 4, Power = 72, Flux = 4 * 1300, Efficiency = 0.66, PowerFactor = 0.85, LengthMm = 595, WidthMm = 595 },
            new() { Name = "Промышленный LED, 100 Вт, 13000 лм", Section = "Промышленное освещение", Series = "Колокола LED", Mounting = "Подвесной", IP = "IP65", Power = 100, Flux = 13000 }]);
        return catalog;
    }
}
