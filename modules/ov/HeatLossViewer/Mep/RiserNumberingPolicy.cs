namespace HeatLossViewer.Mep;

/// <summary>Вертикальная труба: ID, середина в плане (мм), низ (мм), длина (мм) и текущее значение параметра номера.</summary>
public sealed record RiserPipe(long Id, double X, double Y, double Bottom, double LengthMm, string Value, string SystemType = "");

/// <summary>Стояк — вертикальные трубы с одной точкой в плане (в пределах допуска).</summary>
public sealed class Riser
{
    public List<RiserPipe> Pipes { get; } = [];
    public double X => Pipes[0].X;
    public double Y => Pipes[0].Y;
    public double LengthMm => Pipes.Sum(p => p.LengthMm);
}

/// <summary>Итог раскопирования заданных номеров.</summary>
public sealed class RiserCopyPlan
{
    /// <summary>Труба → номер её стояка (только трубы, у которых значение меняется).</summary>
    public List<(long Id, string Value)> Writes { get; } = [];
    /// <summary>Стояки без номера ни в одном сегменте.</summary>
    public List<Riser> Empty { get; } = [];
    /// <summary>Стояки с разными номерами в сегментах: не меняются.</summary>
    public List<(Riser Riser, string[] Values)> Conflicts { get; } = [];
    public int Risers { get; set; }
}

/// <summary>
/// Нумерация трубопроводных стояков по ADSK (скрипты Dynamo «Нумерация стояков автоматическая» и «по заданному значению»):
/// стояк — вертикальные трубы с одной точкой в плане; короче минимальной длины — подъёмы и опуски, не нумеруются.
/// Порядок: сверху вниз по плану (Y по убыванию), в одном ряду — слева направо (X по возрастанию).
/// </summary>
public static class RiserNumberingPolicy
{
    public const string DefaultParameter = "ADSK_Номер стояка";
    public const double DefaultMinLengthMm = 1500;
    /// <summary>Допуск совпадения точек в плане и вертикальности, мм.</summary>
    public const double ToleranceMm = 1;
    public const double DefaultNeighborDistanceMm = 300;
    public const double NeighborOverlapMm = 1000;

    /// <summary>Труба вертикальна: концы совпадают в плане, высота больше допуска.</summary>
    public static bool IsVertical(double dxMm, double dyMm, double dzMm) =>
        Math.Sqrt(dxMm * dxMm + dyMm * dyMm) <= ToleranceMm && Math.Abs(dzMm) > ToleranceMm;

    /// <summary>
    /// Стояки по порядку нумерации. Длина сравнивается для всего стояка, поэтому короткие сегменты между фитингами
    /// получают номер своего стояка; отдельный подъём или опуск короче <paramref name="minLengthMm"/> отбрасывается.
    /// </summary>
    public static List<Riser> Group(IEnumerable<RiserPipe> pipes, double minLengthMm, bool considerSystem = true)
    {
        var risers = new List<Riser>();
        foreach (var pipe in pipes.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Bottom).ThenBy(p => p.SystemType, StringComparer.Ordinal).ThenBy(p => p.Id))
        {
            var riser = risers.FirstOrDefault(r => (!considerSystem || r.Pipes[0].SystemType == pipe.SystemType) && Math.Abs(r.X - pipe.X) <= ToleranceMm && Math.Abs(r.Y - pipe.Y) <= ToleranceMm);
            if (riser is null) risers.Add(riser = new Riser());
            riser.Pipes.Add(pipe);
        }
        foreach (var r in risers) r.Pipes.Sort((a, b) => a.Bottom.CompareTo(b.Bottom));
        var kept = risers.Where(r => r.LengthMm >= minLengthMm - ToleranceMm).OrderByDescending(r => r.Y).ToList();
        // Ряды по Y (в пределах допуска), внутри ряда — слева направо.
        var ordered = new List<Riser>();
        for (int i = 0; i < kept.Count;)
        {
            int j = i;
            while (j < kept.Count && kept[i].Y - kept[j].Y <= ToleranceMm) j++;
            ordered.AddRange(kept.Skip(i).Take(j - i).OrderBy(r => r.X));
            i = j;
        }
        return ordered;
    }

    /// <summary>Автоматическая нумерация: стояки по порядку получают start, start+1, …; все трубы стояка — один номер.</summary>
    public static List<(long Id, int Number)> Auto(IReadOnlyList<Riser> risers, int start, bool considerSystem = true)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        var result = new List<(long, int)>();
        var next = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < risers.Count; i++)
        {
            var systems = risers[i].Pipes.Select(p => p.SystemType).Distinct(StringComparer.Ordinal).ToList();
            long number = considerSystem ? systems.Select(s => next.GetValueOrDefault(s, start)).Max() : (long)start + i;
            int value = checked((int)number);
            foreach (var p in risers[i].Pipes) result.Add((p.Id, value));
            foreach (var system in systems) next[system] = number + 1;
        }
        return result;
    }

    /// <summary>Complete-link bundles: different known system types, all axes close and each pair overlaps in height.</summary>
    public static List<Riser> Neighbors(IReadOnlyList<Riser> risers, double distanceMm)
    {
        if (!double.IsFinite(distanceMm) || distanceMm <= 0) throw new ArgumentOutOfRangeException(nameof(distanceMm));
        var bundles = new List<List<Riser>>();
        foreach (var riser in risers)
        {
            bool Fits(Riser other)
            {
                var a = riser.Pipes.Select(p => p.SystemType).Distinct().ToList();
                var b = other.Pipes.Select(p => p.SystemType).Distinct().ToList();
                if (a.Contains("") || b.Contains("") || a.Intersect(b).Any()) return false;
                double dx = riser.X - other.X, dy = riser.Y - other.Y;
                return dx * dx + dy * dy <= distanceMm * distanceMm && Overlap(riser, other) >= NeighborOverlapMm;
            }
            var bundle = bundles.FirstOrDefault(b => b.All(Fits));
            if (bundle == null) bundles.Add(bundle = []);
            bundle.Add(riser);
        }
        return bundles.Select(b =>
        {
            var combined = new Riser();
            combined.Pipes.AddRange(b.SelectMany(r => r.Pipes));
            return combined;
        }).ToList();
    }

    static double Overlap(Riser a, Riser b)
    {
        List<(double Low, double High)> Intervals(Riser r)
        {
            var result = new List<(double Low, double High)>();
            foreach (var p in r.Pipes.OrderBy(p => p.Bottom))
            {
                double top = p.Bottom + p.LengthMm;
                if (result.Count > 0 && p.Bottom <= result[^1].High)
                    result[^1] = (result[^1].Low, Math.Max(result[^1].High, top));
                else result.Add((p.Bottom, top));
            }
            return result;
        }
        var aa = Intervals(a); var bb = Intervals(b);
        int i = 0, j = 0; double overlap = 0;
        while (i < aa.Count && j < bb.Count)
        {
            overlap += Math.Max(0, Math.Min(aa[i].High, bb[j].High) - Math.Max(aa[i].Low, bb[j].Low));
            if (aa[i].High < bb[j].High) i++; else j++;
        }
        return overlap;
    }

    /// <summary>
    /// По заданному значению: номер, заполненный хотя бы у одного сегмента, копируется в остальные сегменты стояка.
    /// Стояк без номера и стояк с разными номерами не меняются и возвращаются для отчёта.
    /// </summary>
    public static RiserCopyPlan Copy(IReadOnlyList<Riser> risers)
    {
        var plan = new RiserCopyPlan { Risers = risers.Count };
        foreach (var riser in risers)
        {
            var values = riser.Pipes.Select(p => p.Value.Trim()).Where(v => v.Length > 0).Distinct().ToArray();
            if (values.Length == 0) { plan.Empty.Add(riser); continue; }
            if (values.Length > 1) { plan.Conflicts.Add((riser, values)); continue; }
            foreach (var p in riser.Pipes)
                if (p.Value != values[0]) plan.Writes.Add((p.Id, values[0]));
        }
        return plan;
    }

    /// <summary>Минимальная длина стояка из поля окна, мм; null — неверное значение.</summary>
    public static double? ParseLength(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mm)
            && double.IsFinite(mm) && mm >= 0 && mm <= 1_000_000 ? mm : null;
}
