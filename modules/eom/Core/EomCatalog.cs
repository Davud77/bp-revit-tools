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

public sealed class LuminaireItem
{
    public string Name { get; set; } = "";
    public double Power { get; set; }
    public double Flux { get; set; }
    /// <summary>Файл фотометрии .ies/.ldt (пока не используется в расчёте).</summary>
    public string Photometry { get; set; } = "";
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
            new() { Name = "Панель LED 595х595, 36 Вт, 4000 лм", Power = 36, Flux = 4000 },
            new() { Name = "Даунлайт LED, 18 Вт, 1800 лм", Power = 18, Flux = 1800 },
            new() { Name = "Линейный LED 1200 мм, 40 Вт, 4400 лм", Power = 40, Flux = 4400 },
            new() { Name = "Промышленный LED, 100 Вт, 13000 лм", Power = 100, Flux = 13000 }]);
        return catalog;
    }
}
