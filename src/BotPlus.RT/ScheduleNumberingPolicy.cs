namespace BotPlus.RT;

internal sealed record NumberingTarget(long Id, bool Writable, bool Selected);
internal sealed record NumberingRow(IReadOnlyList<NumberingTarget> Targets);
internal sealed record NumberingGroup(int Number, IReadOnlyList<long> ElementIds);
internal sealed record NumberingPlan(IReadOnlyList<NumberingGroup> Groups, int Skipped);

internal static class ScheduleNumberingPolicy
{
    internal static NumberingPlan Build(IReadOnlyList<NumberingRow> rows, int start, bool selectedOnly)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        var groups = new List<NumberingGroup>();
        var seen = new HashSet<long>();
        int skipped = 0;
        foreach (var row in rows)
        {
            var ids = new List<long>();
            foreach (var target in row.Targets)
            {
                if (!seen.Add(target.Id)) throw new InvalidOperationException("Один элемент показан в нескольких строках. Нумерация неоднозначна.");
                if (selectedOnly && !target.Selected) continue;
                if (!target.Writable) { skipped++; continue; }
                ids.Add(target.Id);
            }
            if (ids.Count > 0) groups.Add(new NumberingGroup(checked(start + groups.Count), ids));
        }
        return new NumberingPlan(groups, skipped);
    }

    /// <summary>
    /// Matches Revit's collapsed rows to our ordered groups. <paramref name="rows"/>: group index shown in the row, or null when the
    /// marker is blank because Revit merged several neighbouring groups into it. Returns the groups of each table row, or null with
    /// the reason when the match is not unambiguous (a group split over rows, two merged rows in a row, a group missing).
    /// </summary>
    public static List<int[]>? Reconcile(IReadOnlyList<int?> rows, int groupCount, out string? failure)
    {
        failure = null;
        var result = new List<int[]>();
        int next = 0;
        for (int k = 0; k < rows.Count; k++)
        {
            if (rows[k] is int g)
            {
                if (g != next) { failure = g < next ? "группа разделена на несколько строк" : "группа не найдена в таблице"; return null; }
                result.Add([g]); next++;
                continue;
            }
            // Blank marker: the groups up to the next shown one.
            int until = groupCount;
            for (int j = k + 1; j < rows.Count; j++) if (rows[j] is int h) { until = h; break; } else { failure = "несколько объединённых строк подряд"; return null; }
            if (until - next < 2) { failure = "строка без идентификатора"; return null; }
            result.Add(Enumerable.Range(next, until - next).ToArray()); next = until;
        }
        if (next != groupCount) { failure = "не все группы найдены в таблице"; return null; }
        return result;
    }
}
