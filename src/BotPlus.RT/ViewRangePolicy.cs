namespace BotPlus.RT;

internal enum RangePlane { Top, Cut, Bottom, Depth }
/// <summary>How a plane refers to a level: as in Revit's «Секущий диапазон» dialog.</summary>
internal enum LevelKind { Associated, Above, Below, Unlimited, Level }

internal sealed record LevelInfo(long Id, string Name, double ElevationMm);

internal sealed record LevelChoice(LevelKind Kind, long LevelId, string Label)
{
    public override string ToString() => Label;
}

internal sealed class RangeRow
{
    public RangePlane Plane { get; init; }
    public LevelChoice Level { get; set; } = new(LevelKind.Associated, 0, "");
    public double OffsetMm { get; set; }
    public RangeRow Clone() => new() { Plane = Plane, Level = Level, OffsetMm = OffsetMm };
}

/// <summary>Revit-free rules of the view range window: level choices, resolved elevations, plane order, slider limits.</summary>
internal static class ViewRangePolicy
{
    internal static readonly RangePlane[] Planes = [RangePlane.Top, RangePlane.Cut, RangePlane.Bottom, RangePlane.Depth];

    internal static string Title(RangePlane plane) => plane switch
    {
        RangePlane.Top => "Верхняя граница:",
        RangePlane.Cut => "Секущая плоскость:",
        RangePlane.Bottom => "Нижняя граница:",
        _ => "Глубина проецирования:"
    };

    static List<LevelInfo> Sorted(IEnumerable<LevelInfo> levels) => levels.OrderBy(l => l.ElevationMm).ThenBy(l => l.Name).ToList();

    internal static LevelInfo? Above(IReadOnlyList<LevelInfo> levels, LevelInfo associated) =>
        Sorted(levels).FirstOrDefault(l => l.Id != associated.Id && l.ElevationMm > associated.ElevationMm + 1e-6);

    internal static LevelInfo? Below(IReadOnlyList<LevelInfo> levels, LevelInfo associated) =>
        Sorted(levels).LastOrDefault(l => l.Id != associated.Id && l.ElevationMm < associated.ElevationMm - 1e-6);

    /// <summary>Choices for a plane. The cut plane always refers to the associated level; «Неограниченно» — only top and depth.</summary>
    internal static List<LevelChoice> Choices(RangePlane plane, IReadOnlyList<LevelInfo> levels, LevelInfo associated)
    {
        var list = new List<LevelChoice> { new(LevelKind.Associated, associated.Id, $"Связанный уровень ({associated.Name})") };
        if (plane == RangePlane.Cut) return list;
        var above = Above(levels, associated); var below = Below(levels, associated);
        list.Add(new(LevelKind.Above, above?.Id ?? 0, above is null ? "Уровень выше (нет)" : $"Уровень выше ({above.Name})"));
        list.Add(new(LevelKind.Below, below?.Id ?? 0, below is null ? "Уровень ниже (нет)" : $"Уровень ниже ({below.Name})"));
        if (plane is RangePlane.Top or RangePlane.Depth) list.Add(new(LevelKind.Unlimited, 0, "Неограниченно"));
        list.AddRange(Sorted(levels).Select(l => new LevelChoice(LevelKind.Level, l.Id, l.Name)));
        return list;
    }

    /// <summary>Elevation of the plane's level, mm; null — unlimited or a missing level above/below.</summary>
    internal static double? LevelElevation(LevelChoice choice, IReadOnlyList<LevelInfo> levels, LevelInfo associated) => choice.Kind switch
    {
        LevelKind.Associated => associated.ElevationMm,
        LevelKind.Above => Above(levels, associated)?.ElevationMm,
        LevelKind.Below => Below(levels, associated)?.ElevationMm,
        LevelKind.Level => levels.FirstOrDefault(l => l.Id == choice.LevelId)?.ElevationMm,
        _ => null
    };

    /// <summary>Elevation of the plane: level + offset; unlimited top is +∞, unlimited depth is −∞.</summary>
    internal static double Elevation(RangeRow row, IReadOnlyList<LevelInfo> levels, LevelInfo associated, out string? error)
    {
        error = null;
        var level = LevelElevation(row.Level, levels, associated);
        if (level is { } e) return e + row.OffsetMm;
        if (row.Level.Kind == LevelKind.Unlimited)
            return row.Plane == RangePlane.Top ? double.PositiveInfinity : double.NegativeInfinity;
        error = row.Level.Kind == LevelKind.Above ? "уровня выше нет" : row.Level.Kind == LevelKind.Below ? "уровня ниже нет" : "уровень не найден";
        return double.NaN;
    }

    /// <summary>Revit order: top ≥ cut ≥ bottom ≥ depth. Returns the plane at fault and a reason, or null when valid.</summary>
    internal static (RangePlane Plane, string Message)? Validate(IReadOnlyList<RangeRow> rows, IReadOnlyList<LevelInfo> levels, LevelInfo associated)
    {
        var elevations = new Dictionary<RangePlane, double>();
        foreach (var row in rows)
        {
            double e = Elevation(row, levels, associated, out string? error);
            if (error != null) return (row.Plane, $"{Title(row.Plane).TrimEnd(':')}: {error}.");
            elevations[row.Plane] = e;
        }
        string Mm(double v) => double.IsInfinity(v) ? "∞" : Math.Round(v).ToString("0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " мм";
        if (elevations[RangePlane.Cut] > elevations[RangePlane.Top] + 1e-6)
            return (RangePlane.Cut, $"Секущая плоскость ({Mm(elevations[RangePlane.Cut])}) выше верхней границы ({Mm(elevations[RangePlane.Top])}).");
        if (elevations[RangePlane.Bottom] > elevations[RangePlane.Cut] + 1e-6)
            return (RangePlane.Bottom, $"Нижняя граница ({Mm(elevations[RangePlane.Bottom])}) выше секущей плоскости ({Mm(elevations[RangePlane.Cut])}).");
        if (elevations[RangePlane.Depth] > elevations[RangePlane.Bottom] + 1e-6)
            return (RangePlane.Depth, $"Глубина проецирования ({Mm(elevations[RangePlane.Depth])}) выше нижней границы ({Mm(elevations[RangePlane.Bottom])}).");
        return null;
    }

    /// <summary>
    /// Slider limits for an offset from the plane's level, mm: from 1 m below the next lower level to 1 m above the next higher one
    /// (6 m when there is none), so the usual positions are reachable without typing. Always includes the current value.
    /// </summary>
    internal static (double Min, double Max) SliderRange(LevelChoice choice, double current, IReadOnlyList<LevelInfo> levels, LevelInfo associated)
    {
        double e = LevelElevation(choice, levels, associated) ?? associated.ElevationMm;
        var sorted = Sorted(levels);
        double lower = sorted.LastOrDefault(l => l.ElevationMm < e - 1e-6)?.ElevationMm ?? e - 6000;
        double upper = sorted.FirstOrDefault(l => l.ElevationMm > e + 1e-6)?.ElevationMm ?? e + 6000;
        double min = Snap(Math.Min(lower - e - 1000, current)), max = Snap(Math.Max(upper - e + 1000, current));
        return (min, max);
    }

    internal static double Snap(double mm) => Math.Round(mm / 10, MidpointRounding.AwayFromZero) * 10;

    internal static bool TryParseOffset(string text, out double mm) =>
        double.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mm)
        && double.IsFinite(mm) && Math.Abs(mm) <= 1_000_000;
}
