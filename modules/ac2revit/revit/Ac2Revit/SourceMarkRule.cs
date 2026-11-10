namespace Ac2Revit;

/// <summary>Pure naming rule for repeated Archicad IDs written to Revit's «Марка» (no Revit API, unit-testable).</summary>
public static class SourceMarkRule
{
    /// <summary>The first free «mark», «mark (2)», «mark (3)» …</summary>
    public static string Unique(string mark, Func<string, bool> taken)
    {
        if (!taken(mark)) return mark;
        for (int n = 2; ; ++n)
        {
            string candidate = $"{mark} ({n})";
            if (!taken(candidate)) return candidate;
        }
    }

    /// <summary>True when <paramref name="current"/> is a value <see cref="Unique"/> could have produced for <paramref name="mark"/>.</summary>
    public static bool IsVariantOf(string current, string mark)
    {
        if (current == mark) return true;
        if (!current.StartsWith(mark + " (", StringComparison.Ordinal) || !current.EndsWith(")", StringComparison.Ordinal)) return false;
        var digits = current.Substring(mark.Length + 2, current.Length - mark.Length - 3);
        return digits.Length > 0 && digits.All(char.IsDigit);
    }
}
