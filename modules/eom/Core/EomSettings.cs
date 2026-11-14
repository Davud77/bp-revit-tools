using System.Text.Json;

namespace BotPlus.Eom.Core;

/// <summary>Нормы и правила проекта ЭОМ. Хранятся в модели (Extensible Storage), чтобы ехать вместе с RVT.</summary>
public sealed record EomSettings
{
    public static readonly string[] LightingNorms = ["СП 52.13330.2016", "СП 256.1325800.2016"];
    public static readonly string[] LoadNorms = ["СП 256.1325800.2016", "СП 31-110-2003", "РТМ 36.18.32.4-92"];

    public string LoadNorm { get; init; } = LoadNorms[0];
    public string LightingNorm { get; init; } = LightingNorms[0];
    /// <summary>Запас длины кабеля на монтаж и разделку, %.</summary>
    public double CableReserve { get; init; } = 10;
    /// <summary>Допустимая потеря напряжения в групповой линии, %.</summary>
    public double VoltageDropLimit { get; init; } = 4;
    public double PhaseVoltage { get; init; } = 230;
    public double LineVoltage { get; init; } = 400;
    /// <summary>Маска номера группы: {Щит} — имя щита, {N} — номер группы.</summary>
    public string GroupMask { get; init; } = "{Щит}.Гр{N}";
    /// <summary>Маска марки кабеля.</summary>
    public string CableMask { get; init; } = "{Щит}-{N}";
    public bool LinesCreated { get; init; }

    public void Validate()
    {
        if (CableReserve is < 0 or > 50) throw new InvalidOperationException("Запас длины кабеля: от 0 до 50 %.");
        if (VoltageDropLimit is <= 0 or > 15) throw new InvalidOperationException("Допустимая потеря напряжения: от 0 до 15 %.");
        if (PhaseVoltage <= 0 || LineVoltage <= 0) throw new InvalidOperationException("Напряжение должно быть больше нуля.");
        if (!GroupMask.Contains("{N}") || !CableMask.Contains("{N}")) throw new InvalidOperationException("Маски маркировки должны содержать {N}.");
    }

    public string ToJson() => JsonSerializer.Serialize(this);
    public static EomSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { var value = JsonSerializer.Deserialize<EomSettings>(json) ?? new(); value.Validate(); return value; }
        catch { return new(); }
    }

    /// <summary>Подставляет имя щита и номер группы в маску.</summary>
    public static string Mark(string mask, string panel, string number) =>
        mask.Replace("{Щит}", panel.Trim()).Replace("{N}", number.Trim());
}
