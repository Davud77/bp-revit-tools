using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeatLossViewer.Mep;

/// <summary>One component of the composed name: prefix + value + suffix. Empty values drop the whole component.</summary>
public sealed class NamePart
{
    public string Parameter { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
}

public enum QuantityUnit { Piece, Meter, Millimeter, SquareMeter }
public enum QuantityRounding { Up, Nearest }
/// <summary>Physical kind of the source value after conversion from Revit internal units.</summary>
public enum SourceKind { Count, Length, Area, Number }

/// <summary>
/// Rule for one category and, optionally, one type. <see cref="TypeKey"/> = null is the «Без типа» rule:
/// it applies to every element of the category whose type has no rule of its own.
/// </summary>
public sealed class EngineeringRule
{
    public string Category { get; set; } = "";
    public string? TypeKey { get; set; }
    public string TypeName { get; set; } = "";
    public bool NameEnabled { get; set; }
    public string NameTarget { get; set; } = EngineeringRulesPolicy.DefaultNameTarget;
    public List<NamePart> Parts { get; set; } = [];
    public bool QuantityEnabled { get; set; }
    public string QuantityTarget { get; set; } = EngineeringRulesPolicy.DefaultQuantityTarget;
    public string QuantitySource { get; set; } = EngineeringRulesPolicy.CountSource;
    public double Reserve { get; set; } = 1.0;
    [JsonConverter(typeof(JsonStringEnumConverter))] public QuantityUnit Unit { get; set; } = QuantityUnit.Piece;
    public int Decimals { get; set; } = 2;
    [JsonConverter(typeof(JsonStringEnumConverter))] public QuantityRounding Rounding { get; set; } = QuantityRounding.Up;

    [JsonIgnore] public bool IsActive => NameEnabled || QuantityEnabled;

    public EngineeringRule Clone() => JsonSerializer.Deserialize<EngineeringRule>(JsonSerializer.Serialize(this))!;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Category)) throw new InvalidOperationException("Не указана категория правила.");
        if (NameEnabled)
        {
            if (string.IsNullOrWhiteSpace(NameTarget)) throw new InvalidOperationException("Укажите параметр для наименования.");
            if (Parts.Count == 0) throw new InvalidOperationException("Добавьте хотя бы один компонент наименования.");
            if (Parts.Any(p => string.IsNullOrWhiteSpace(p.Parameter))) throw new InvalidOperationException("У компонента наименования не выбран параметр.");
        }
        if (QuantityEnabled)
        {
            if (string.IsNullOrWhiteSpace(QuantityTarget)) throw new InvalidOperationException("Укажите параметр для количества.");
            if (string.IsNullOrWhiteSpace(QuantitySource)) throw new InvalidOperationException("Выберите исходный параметр количества.");
            if (!double.IsFinite(Reserve) || Reserve < 1 || Reserve > 10) throw new InvalidOperationException("Коэффициент запаса должен быть от 1 до 10.");
            if (Decimals is < 0 or > 6) throw new InvalidOperationException("Округление — от 0 до 6 знаков после запятой.");
        }
    }
}

public sealed class EngineeringRuleSet
{
    public int Version { get; set; } = 1;
    public List<EngineeringRule> Rules { get; set; } = [];

    /// <summary>Exact type rule wins over the category «Без типа» rule; inactive rules are ignored.</summary>
    public EngineeringRule? Resolve(string category, string? typeKey)
    {
        EngineeringRule? fallback = null;
        foreach (var rule in Rules)
        {
            if (rule.Category != category || !rule.IsActive) continue;
            if (typeKey != null && rule.TypeKey == typeKey) return rule;
            if (rule.TypeKey == null) fallback = rule;
        }
        return fallback;
    }

    public EngineeringRule? Find(string category, string? typeKey) =>
        Rules.FirstOrDefault(r => r.Category == category && r.TypeKey == typeKey);

    /// <summary>Replaces or removes the rule with the same scope. Inactive rules are not stored.</summary>
    public void Put(EngineeringRule rule)
    {
        // Validate first: an invalid edit must not drop the rule that is already stored.
        if (rule.IsActive) rule.Validate();
        Rules.RemoveAll(r => r.Category == rule.Category && r.TypeKey == rule.TypeKey);
        if (rule.IsActive) Rules.Add(rule);
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static EngineeringRuleSet FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new EngineeringRuleSet();
        var set = JsonSerializer.Deserialize<EngineeringRuleSet>(json) ?? throw new InvalidOperationException("Повреждены правила апдейтера.");
        if (set.Version != 1) throw new InvalidOperationException($"Неизвестная версия правил апдейтера: {set.Version}.");
        set.Rules ??= [];
        return set;
    }
}

public static class EngineeringRulesPolicy
{
    public const string DefaultNameTarget = "ADSK_Наименование";
    public const string DefaultQuantityTarget = "ADSK_Количество";
    /// <summary>Virtual sources. Real Revit parameter names never start with '@'.</summary>
    public const string CountSource = "@Штук";
    public const string SortedSizeSource = "@Размер";
    public const string TypeNameSource = "@Тип";
    public const string FamilyNameSource = "@Семейство";
    public static readonly string[] VirtualNameSources = [TypeNameSource, FamilyNameSource, SortedSizeSource];

    public static string ComposeName(IEnumerable<NamePart> parts, Func<string, string?> value)
    {
        var text = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            string? v = value(part.Parameter)?.Trim();
            if (string.IsNullOrEmpty(v)) continue;
            text.Append(part.Prefix).Append(v).Append(part.Suffix);
        }
        return text.ToString().Trim();
    }

    /// <summary>Units the user may choose for a source of the given kind.</summary>
    public static QuantityUnit[] UnitsFor(SourceKind kind) => kind switch
    {
        SourceKind.Count => [QuantityUnit.Piece],
        SourceKind.Length => [QuantityUnit.Meter, QuantityUnit.Millimeter],
        SourceKind.Area => [QuantityUnit.SquareMeter],
        _ => [QuantityUnit.Piece]
    };

    public static string UnitLabel(QuantityUnit unit) => unit switch
    {
        QuantityUnit.Meter => "м", QuantityUnit.Millimeter => "мм", QuantityUnit.SquareMeter => "м²", _ => "шт"
    };

    /// <summary>
    /// <paramref name="value"/> is in SI (m, m²) for length/area, raw for count/number.
    /// Returns null with a reason when the unit does not match the source kind.
    /// </summary>
    public static double? Quantity(EngineeringRule rule, SourceKind kind, double value, out string? error)
    {
        error = null;
        if (!double.IsFinite(value)) { error = "исходное значение не число"; return null; }
        if (kind != SourceKind.Number && !UnitsFor(kind).Contains(rule.Unit))
        { error = $"единица «{UnitLabel(rule.Unit)}» не подходит к источнику"; return null; }
        double converted = rule.Unit == QuantityUnit.Millimeter ? value * 1000 : value;
        return Round(converted * rule.Reserve, rule.Decimals, rule.Rounding);
    }

    public static double Round(double value, int decimals, QuantityRounding mode)
    {
        double scale = Math.Pow(10, decimals);
        double scaled = value * scale;
        // Floating products such as 1.1 * 10 land just above an integer; do not bump them a whole step.
        double nearest = Math.Round(scaled, MidpointRounding.AwayFromZero);
        if (mode == QuantityRounding.Nearest || Math.Abs(scaled - nearest) < 1e-7) return nearest / scale;
        return Math.Ceiling(scaled) / scale;
    }

    /// <summary>For a rectangular section the larger side goes first: 500×300, never 300×500.</summary>
    public static string SortedSize(double widthMm, double heightMm)
    {
        double a = Math.Max(widthMm, heightMm), b = Math.Min(widthMm, heightMm);
        return Format(a, 0) + "×" + Format(b, 0);
    }

    public static string Format(double value, int decimals) =>
        Math.Round(value, decimals).ToString(decimals <= 0 ? "0" : "0." + new string('#', decimals), CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>True when the stored value already equals what would be written — keeps the updater idempotent.</summary>
    public static bool SameNumber(double? current, double next) => current.HasValue && Math.Abs(current.Value - next) < 1e-9;
}
