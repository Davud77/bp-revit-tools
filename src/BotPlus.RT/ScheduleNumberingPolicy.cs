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
}
