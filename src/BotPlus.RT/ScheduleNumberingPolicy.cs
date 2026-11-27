namespace BotPlus.RT;

internal sealed record NumberingTarget(long Id, bool Writable, bool Selected);
internal sealed record NumberingRow(IReadOnlyList<NumberingTarget> Targets);
internal sealed record NumberingGroup(int Number, IReadOnlyList<long> ElementIds);
internal sealed record NumberingPlan(IReadOnlyList<NumberingGroup> Groups, int Skipped);

internal sealed record NumberingSortValue(int Kind, double Number, string Text) : IComparable<NumberingSortValue>
{
    public int CompareTo(NumberingSortValue? other)
    {
        if (other == null) return 1;
        int kind = Kind.CompareTo(other.Kind);
        return kind != 0 ? kind : Kind == 1 ? Number.CompareTo(other.Number)
            : StringComparer.CurrentCultureIgnoreCase.Compare(Text, other.Text);
    }
}

internal static class ScheduleNumberingPolicy
{
    internal static IReadOnlyList<IReadOnlyList<long>>? SplitRows(long[] itemized, IReadOnlyList<int> counts)
    {
        var rows = new List<IReadOnlyList<long>>(counts.Count);
        int offset = 0;
        foreach (int count in counts)
        {
            if (count <= 0 || count > itemized.Length - offset) return null;
            rows.Add(new ArraySegment<long>(itemized, offset, count));
            offset += count;
        }
        return offset == itemized.Length ? rows : null;
    }

    internal static IReadOnlyList<IReadOnlyList<long>> Sort(IReadOnlyList<IReadOnlyList<long>> rows, IReadOnlyList<NumberingSortValue> values)
    {
        if (values.Count != rows.Count)
            throw new InvalidOperationException("Не удалось сопоставить значения сортировки со строками спецификации.");
        // LINQ OrderBy is stable: equal values keep the schedule's original row order.
        return rows.Zip(values, (row, key) => (row, key)).OrderBy(x => x.key).Select(x => x.row).ToArray();
    }

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
