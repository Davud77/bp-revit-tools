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

public static class Lighting
{
    // Коэффициент использования для типового LED-светильника при ρп/ρс/ρр = 70/50/20 % (демонстрационная таблица).
    static readonly (double Index, double Eta)[] Table = [(0.6, 0.36), (0.8, 0.44), (1.0, 0.50), (1.25, 0.56), (1.5, 0.61), (2.0, 0.67), (2.5, 0.71), (3.0, 0.74), (4.0, 0.78), (5.0, 0.81)];

    /// <summary>Индекс помещения i = 2S/(h·P), где h — высота подвеса над рабочей поверхностью, P — периметр.</summary>
    public static double RoomIndex(double area, double perimeter, double height) =>
        area <= 0 || perimeter <= 0 || height <= 0 ? 0 : 2 * area / (height * perimeter);

    public static double Utilization(double index)
    {
        if (index <= Table[0].Index) return Table[0].Eta;
        for (int k = 1; k < Table.Length; k++)
            if (index <= Table[k].Index)
            {
                var (i0, e0) = Table[k - 1]; var (i1, e1) = Table[k];
                return e0 + (e1 - e0) * (index - i0) / (i1 - i0);
            }
        return Table[^1].Eta;
    }

    /// <summary>Метод коэффициента использования: N = E·S·Kз·z/(Φ·η).</summary>
    public static int Count(double lux, double area, double flux, double eta, double maintenance = 1.4, double z = 1.1) =>
        lux <= 0 || area <= 0 || flux <= 0 || eta <= 0 ? 0 : (int)Math.Ceiling(lux * area * maintenance * z / (flux * eta) - 1e-9);

    /// <summary>Фактическая освещённость при N светильниках.</summary>
    public static double Illuminance(int count, double area, double flux, double eta, double maintenance = 1.4, double z = 1.1) =>
        area <= 0 ? 0 : count * flux * eta / (area * maintenance * z);

    /// <summary>Равномерная сетка внутри прямоугольника: число рядов и колонок по соотношению сторон, отступ от стен — половина шага.</summary>
    public static List<(double X, double Y)> Grid(double minX, double minY, double maxX, double maxY, int count)
    {
        var points = new List<(double, double)>();
        double w = maxX - minX, l = maxY - minY;
        if (count <= 0 || w <= 0 || l <= 0) return points;
        int cols = Math.Max(1, (int)Math.Round(Math.Sqrt(count * w / l)));
        int rows = (int)Math.Ceiling(count / (double)cols);
        double dx = w / cols, dy = l / rows;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++) points.Add((minX + dx * (c + 0.5), minY + dy * (r + 0.5)));
        return points;
    }

    /// <summary>Норма освещённости по названию помещения (упрощённо, СП 52.13330), лк.</summary>
    public static double DefaultLux(string roomName)
    {
        string n = roomName.ToLowerInvariant();
        if (n.Contains("кабинет") || n.Contains("офис") || n.Contains("переговор")) return 300;
        if (n.Contains("класс") || n.Contains("чертеж") || n.Contains("чертёж")) return 500;
        if (n.Contains("коридор") || n.Contains("холл") || n.Contains("лестн")) return 100;
        if (n.Contains("с/у") || n.Contains("сану") || n.Contains("туалет") || n.Contains("душ")) return 75;
        if (n.Contains("тех") || n.Contains("венткам") || n.Contains("щитов") || n.Contains("электро")) return 200;
        if (n.Contains("склад") || n.Contains("кладов")) return 75;
        return 200;
    }
}
