namespace BotPlus.Eom.Core;

/// <summary>Исходные данные группы для расчёта.</summary>
public sealed record CircuitInput(string Panel, string Number, string Name, double ActivePowerW, double PowerFactor, int Poles, double LengthM);

/// <summary>Результат расчёта и подбора по одной группе.</summary>
public sealed record CircuitResult(CircuitInput Input, double CurrentA, BreakerItem? Breaker, CableItem? Cable, double CableLengthM, double VoltageDrop, string Problem)
{
    public bool Ok => Problem.Length == 0;
}

public static class EomCalc
{
    const double Sqrt3 = 1.7320508075688772;

    /// <summary>Расчётный ток: 1ф — P/(Uф·cosφ), 3ф — P/(√3·Uл·cosφ).</summary>
    public static double Current(double powerW, double powerFactor, int poles, EomSettings settings)
    {
        double cos = powerFactor is > 0 and <= 1 ? powerFactor : 1;
        return poles >= 3 ? powerW / (Sqrt3 * settings.LineVoltage * cos) : powerW / (settings.PhaseVoltage * cos);
    }

    /// <summary>Потеря напряжения, % (активная составляющая): 1ф — 2·I·R·cosφ/Uф, 3ф — √3·I·R·cosφ/Uл.</summary>
    public static double VoltageDrop(double current, double lengthM, CableItem cable, double powerFactor, int poles, EomSettings settings)
    {
        double r = cable.Resistance / 1000 * lengthM, cos = powerFactor is > 0 and <= 1 ? powerFactor : 1;
        return poles >= 3 ? Sqrt3 * current * r * cos / settings.LineVoltage * 100 : 2 * current * r * cos / settings.PhaseVoltage * 100;
    }

    /// <summary>
    /// Подбор: ближайший больший автомат с In ≥ Iр, затем минимальное сечение с Iдоп ≥ In и ΔU ≤ допустимой.
    /// Длина кабеля увеличивается на запас из настроек.
    /// </summary>
    public static CircuitResult Select(CircuitInput input, EomSettings settings, EomCatalog catalog)
    {
        double current = Current(input.ActivePowerW, input.PowerFactor, input.Poles, settings);
        double length = input.LengthM * (1 + settings.CableReserve / 100);
        int poles = input.Poles >= 3 ? 3 : 1, cores = poles == 3 ? 5 : 3;
        var breaker = catalog.Breakers.Where(b => b.Kind == "Автомат" && b.Poles == poles && b.Rating >= current).MinBy(b => b.Rating);
        if (breaker == null) return new(input, current, null, null, length, 0, "Нет автомата на такой ток в каталоге");
        foreach (var cable in catalog.Cables.Where(c => c.Cores == cores && c.AllowedCurrent >= breaker.Rating).OrderBy(c => c.Section))
        {
            double drop = VoltageDrop(current, length, cable, input.PowerFactor, poles, settings);
            if (drop <= settings.VoltageDropLimit) return new(input, current, breaker, cable, length, drop, "");
        }
        var largest = catalog.Cables.Where(c => c.Cores == cores).MaxBy(c => c.Section);
        double worst = largest == null ? 0 : VoltageDrop(current, length, largest, input.PowerFactor, poles, settings);
        return new(input, current, breaker, largest, length, worst, $"ΔU {worst:0.##}% > {settings.VoltageDropLimit:0.##}% даже для максимального сечения");
    }

    /// <summary>Сумма по щиту: Pр = Кс·ΣP, Qр = Pр·tgφ, Sр = √(Pр²+Qр²).</summary>
    public static (double P, double Q, double S) PanelLoad(IEnumerable<CircuitInput> circuits, double demandFactor = 1)
    {
        double p = 0, q = 0;
        foreach (var c in circuits)
        {
            double cos = c.PowerFactor is > 0 and <= 1 ? c.PowerFactor : 1, pi = c.ActivePowerW * demandFactor;
            p += pi; q += pi * Math.Sqrt(1 - cos * cos) / cos;
        }
        return (p, q, Math.Sqrt(p * p + q * q));
    }
}
