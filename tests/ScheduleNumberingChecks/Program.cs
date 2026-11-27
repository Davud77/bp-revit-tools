using BotPlus.RT;

void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
NumberingTarget Target(long id, bool writable = true, bool selected = true) => new(id, writable, selected);
NumberingRow Row(params NumberingTarget[] targets) => new(targets);
var rows = new[] { Row(Target(100)), Row(Target(5, false)), Row(Target(70)), Row(Target(10)) };
var plan = ScheduleNumberingPolicy.Build(rows, 8, false);
Check(plan.Groups.SelectMany(g => g.ElementIds).SequenceEqual(new long[] { 100, 70, 10 }), "Must preserve schedule order, not ID order");
Check(plan.Groups.Select(g => g.Number).SequenceEqual(new[] { 8, 9, 10 }) && plan.Skipped == 1, "Read-only row must not consume a number");
Console.WriteLine("PASS native row order and inaccessible parameter skip without numbering gaps");

plan = ScheduleNumberingPolicy.Build([Row(Target(20), Target(21)), Row(Target(30, selected: false)), Row(Target(40))], 1, true);
Check(plan.Groups.Count == 2 && plan.Groups[0].ElementIds.SequenceEqual(new long[] { 20, 21 }) && plan.Groups[1].Number == 2, "Grouped row and selected subset");
plan = ScheduleNumberingPolicy.Build([Row(Target(1, selected: false)), Row(Target(2, false))], 1, true);
Check(plan.Groups.Count == 0 && plan.Skipped == 1, "No eligible rows must not allocate numbers");
Console.WriteLine("PASS grouped row shares a number; selected-only preserves unrelated rows");

try { ScheduleNumberingPolicy.Build([Row(Target(1)), Row(Target(1))], 1, false); throw new Exception("Duplicate must fail"); }
catch (InvalidOperationException) { }
try { ScheduleNumberingPolicy.Build([Row(Target(1)), Row(Target(2))], int.MaxValue, false); throw new Exception("Overflow must fail"); }
catch (OverflowException) { }
Check(ScheduleNumberingPolicy.Build([Row(Target(1))], int.MaxValue, false).Groups[0].Number == int.MaxValue, "Maximum single value");
Console.WriteLine("PASS ambiguous row identities and integer overflow fail before writes");

long[] ordered = [100, 80, 42, 15, 9, 70];
var split = ScheduleNumberingPolicy.SplitRows(ordered, [2, 2, 1, 1]);
Check(split != null && split.Select(row => string.Join(",", row)).SequenceEqual(["100,80", "42,15", "9", "70"]),
    "Consecutive multi-element native rows retain order and membership");
Check(ScheduleNumberingPolicy.SplitRows(ordered, [2, 2, 1]) == null, "Missing element must fail");
Check(ScheduleNumberingPolicy.SplitRows(ordered, [2, 2, 3]) == null, "Excess element count must fail");
Check(ScheduleNumberingPolicy.SplitRows(ordered, [2, 0, 4]) == null, "An empty group must fail");
Check(ScheduleNumberingPolicy.SplitRows(ordered, [2, int.MaxValue]) == null, "Malformatted counts cannot overflow");
Console.WriteLine("PASS native row counts and incomplete or invalid grouping rejected");

IReadOnlyList<IReadOnlyList<long>> sortRows = [new long[] { 100, 101 }, new long[] { 70 }, new long[] { 10, 11 }];
var sorted = ScheduleNumberingPolicy.Sort(sortRows, [new(1, 10, ""), new(1, 2, ""), new(1, 2, "")]);
Check(sorted.SelectMany(r => r).SequenceEqual(new long[] { 70, 10, 11, 100, 101 }), "Sort by the row total, keeping groups and stable ties");
sorted = ScheduleNumberingPolicy.Sort(sortRows, [new(2, 0, "шт."), new(2, 0, "м"), new(0, 0, "")]);
Check(sorted.SelectMany(r => r).SequenceEqual(new long[] { 10, 11, 70, 100, 101 }), "Sort by native row text with blank first");
Console.WriteLine("PASS sorting by row values, stable ties and grouped row membership");
