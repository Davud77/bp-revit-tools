namespace BotPlus.Eom.Core;

internal static class PanelCalculation
{
    internal sealed record Feeder(double Installed, double Demand, double Apparent, double Current, double Leakage,
        double Length, BreakerItem? Breaker, CableItem? Cable, double? Drop, string Note, int CableCount = 0);

    internal static BreakerItem? SelectBreaker(double current, int poles, EomCatalog catalog) =>
        catalog.Breakers.Where(b => b.Kind == "Автомат" && b.Poles == poles && b.Rating >= 1.25 * current).MinBy(b => b.Rating);

    internal static double Round(double value)
    {
        double fraction = value - Math.Truncate(value);
        return Math.Round(value, fraction is > 0 and < 0.001 ? 4 : 3);
    }

    internal static Feeder Calculate(CircuitInput input, double voltage, double demandFactor, EomSettings settings, EomCatalog catalog, bool feedsPanel = false)
    {
        double installed = input.ActivePowerW / 1000;
        double demand = Round(installed * demandFactor);
        double length = input.LengthM * (1 + settings.CableReserve / 100);
        if (voltage <= 0 || input.Poles is not (1 or 3) || input.PowerFactor is <= 0 or > 1 || installed < 0)
            return new(installed, demand, 0, 0, 0, length, null, null, null, "Проверьте U, cos φ и число полюсов (1 или 3)");
        double cos = input.PowerFactor, multiplier = input.Poles == 3 ? Math.Sqrt(3) : 1;
        double apparent = Round(demand / cos);
        double current = Round(1000 * (feedsPanel ? demand : installed) / (multiplier * voltage * cos));
        double leakage = Round(0.4 * current + 0.01 * length);
        var breaker = SelectBreaker(current, input.Poles, catalog);
        if (breaker == null || installed == 0)
            return new(installed, demand, apparent, current, leakage, length, null, null, null, installed == 0 ? "Нет нагрузки" : "Нет автомата в каталоге");
        if (length <= 0) return new(installed, demand, apparent, current, 0, length, breaker, null, null, "Нет длины трассы");
        int cores = input.Poles == 3 ? 5 : 3;
        var cables = catalog.Cables.Where(c => c.Cores == cores && c.AllowedCurrent > 0 && c.Section > 0).OrderBy(c => c.Section).ToList();
        foreach (var cable in cables)
        {
            double drop = VoltageDrop(current, voltage, cos, length, cable.Section, input.Poles);
            if (cable.AllowedCurrent >= breaker.Rating && drop <= settings.VoltageDropLimit)
                return new(installed, demand, apparent, current, leakage, length, breaker, cable, drop, "", 1);
        }
        var largest = cables.LastOrDefault();
        if (largest != null && settings.VoltageDropLimit > 0)
        {
            int count = (int)Math.Clamp(Math.Ceiling(Math.Max(breaker.Rating / largest.AllowedCurrent,
                VoltageDrop(current, voltage, cos, length, largest.Section, input.Poles) / settings.VoltageDropLimit)), 2, 32);
            double drop = VoltageDrop(current / count, voltage, cos, length, largest.Section, input.Poles);
            if (count * largest.AllowedCurrent >= breaker.Rating && drop <= settings.VoltageDropLimit)
                return new(installed, demand, apparent, current, leakage, length, breaker, largest, drop, "Параллельные линии, проверьте условия прокладки", count);
        }
        return new(installed, demand, apparent, current, leakage, length, breaker, null, null, "Нет подходящего медного кабеля в каталоге");
    }

    internal static double VoltageDrop(double current, double voltage, double cos, double length, double section, int poles)
        => Round(100 * (poles == 3 ? Math.Sqrt(3) : 2) * current * (22.5 / section * cos + 0.08 * Math.Sqrt(1 - cos * cos)) * length / (1000 * voltage));

    /// <summary>Поочерёдное назначение фаз и перенос нагрузки, ближайшей к половине перекоса.</summary>
    internal static (int[] Assignment, double[] Totals) Balance(IReadOnlyList<double> singlePhase, double perPhaseBase)
    {
        var assignment = new int[singlePhase.Count];
        double[] totals = [perPhaseBase, perPhaseBase, perPhaseBase];
        for (int i = 0; i < singlePhase.Count; i++) totals[assignment[i] = i % 3] += singlePhase[i];
        if (singlePhase.Count <= 3) return (assignment, totals);
        for (int pass = 0; pass < singlePhase.Count * 3; pass++)
        {
            int high = Array.IndexOf(totals, totals.Max()), low = Array.IndexOf(totals, totals.Min());
            double imbalance = totals[high] - totals[low];
            int best = -1;
            double distance = imbalance / 2;
            for (int i = 0; i < singlePhase.Count; i++)
            {
                if (assignment[i] != high) continue;
                double difference = Math.Abs(imbalance / 2 - singlePhase[i]);
                if (difference >= distance - 1e-9) continue;
                best = i;
                distance = difference;
            }
            if (best < 0) break;
            double current = singlePhase[best];
            totals[high] -= current;
            totals[low] += current;
            assignment[best] = low;
        }
        return (assignment, totals);
    }
}
