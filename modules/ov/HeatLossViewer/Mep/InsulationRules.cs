using System.Text.Json;

namespace HeatLossViewer.Mep;

/// <summary>
/// Изоляция труб одной строки окна: система (пусто — все системы) и ADSK_Наименование трубы.
/// <see cref="InsulationType"/> = null — изоляция этой строкой не управляется.
/// </summary>
public sealed class InsulationRule
{
    public string System { get; set; } = "";
    public string Name { get; set; } = "";
    public string? InsulationType { get; set; }
    public double ThicknessMm { get; set; } = InsulationPolicy.DefaultThicknessMm;
}

/// <summary>Настройки изоляции проекта: хранятся в RVT вместе с правилами апдейтера.</summary>
public sealed class InsulationSettings
{
    public int Version { get; set; } = 1;
    /// <summary>Строить и обновлять изоляцию новых и изменённых труб автоматически.</summary>
    public bool Auto { get; set; } = true;
    /// <summary>Фитинги и арматура получают изоляцию соединённой трубы.</summary>
    public bool Fittings { get; set; } = true;
    public List<InsulationRule> Rules { get; set; } = [];

    public bool IsEmpty => !Rules.Any(r => r.InsulationType != null);

    /// <summary>Правило системы, иначе правило «Все системы»; строка без типа изоляции ничего не задаёт.</summary>
    public InsulationRule? Resolve(string? system, string name)
    {
        InsulationRule? Find(string s) => Rules.FirstOrDefault(r => r.InsulationType != null && Same(r.System, s) && Same(r.Name, name));
        return (system is { Length: > 0 } ? Find(system) : null) ?? Find("");
    }

    public InsulationRule? Get(string system, string name) => Rules.FirstOrDefault(r => Same(r.System, system) && Same(r.Name, name));

    /// <summary>Записать строку; без типа изоляции строка удаляется.</summary>
    public void Put(string system, string name, string? type, double thicknessMm)
    {
        Rules.RemoveAll(r => Same(r.System, system) && Same(r.Name, name));
        if (type is { Length: > 0 }) Rules.Add(new InsulationRule { System = system.Trim(), Name = name.Trim(), InsulationType = type, ThicknessMm = thicknessMm });
    }

    public void Validate()
    {
        foreach (var r in Rules.Where(r => r.InsulationType != null))
            if (InsulationPolicy.ThicknessError(r.ThicknessMm) is { } error)
                throw new InvalidOperationException($"{(r.System.Length == 0 ? InsulationPolicy.AllSystemsLabel : r.System)}, «{r.Name}»: {error}");
    }

    public InsulationSettings Clone() => FromJson(ToJson());

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static InsulationSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new InsulationSettings();
        try { return JsonSerializer.Deserialize<InsulationSettings>(json) ?? new InsulationSettings(); }
        catch (JsonException) { throw new InvalidOperationException("Повреждены настройки изоляции проекта."); }
    }

    static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);
}

public enum InsulationAction { None, Create, Update, Keep }

public static class InsulationPolicy
{
    public const double DefaultThicknessMm = 13;
    public const string AllSystemsLabel = "Все системы";
    public const string NoInsulation = "— не задано";
    /// <summary>Отмечено несколько систем с разными правилами строки; без правки строка их не меняет.</summary>
    public const string Mixed = "— разные";

    public static string? ThicknessError(double mm) =>
        !double.IsFinite(mm) || mm <= 0 ? "толщина изоляции должна быть больше нуля"
        : mm > 300 ? "толщина изоляции больше 300 мм" : null;

    /// <summary>Что сделать с изоляцией элемента: построить, изменить тип или толщину, оставить.</summary>
    public static InsulationAction Decide(InsulationRule? rule, string? currentType, double? currentMm)
    {
        if (rule?.InsulationType is null) return InsulationAction.None;
        if (currentType is null) return InsulationAction.Create;
        bool sameType = string.Equals(currentType, rule.InsulationType, StringComparison.CurrentCultureIgnoreCase);
        return sameType && currentMm is { } mm && Math.Abs(mm - rule.ThicknessMm) < 0.05 ? InsulationAction.Keep : InsulationAction.Update;
    }

    /// <summary>Диапазон диаметров строки: «15», «15–50».</summary>
    public static string Sizes(IEnumerable<double> mm)
    {
        var list = mm.Where(d => d > 0).Select(d => Math.Round(d, 1)).Distinct().Order().ToList();
        string F(double d) => d.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        return list.Count switch { 0 => "", 1 => F(list[0]), _ => F(list[0]) + "–" + F(list[^1]) };
    }

    public static bool TryParseThickness(string? text, out double mm) =>
        double.TryParse((text ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mm) && ThicknessError(mm) is null;
}

/// <summary>Трубы проекта одной строки окна: наименование, количество, диаметры, сколько уже изолировано.</summary>
public sealed record PipeGroup(string Name, int Count, string Sizes, int Insulated);

/// <summary>Данные проекта для окна изоляции (в проверках — подставные).</summary>
public interface IInsulationModel
{
    IReadOnlyList<string> Systems();
    /// <summary>Трубы отмеченных систем, сгруппированные по ADSK_Наименование; «» среди систем — все трубы проекта.</summary>
    IReadOnlyList<PipeGroup> Pipes(IReadOnlyCollection<string> systems);
    IReadOnlyList<string> InsulationTypes();
}
