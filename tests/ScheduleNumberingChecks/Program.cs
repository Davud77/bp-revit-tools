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

// Grouped (non-itemized) table: Revit's collapsed rows matched to our groups.
var exact = ScheduleNumberingPolicy.Reconcile([0, 1, 2], 3, out _);
Check(exact!.Select(r => string.Join(",", r)).SequenceEqual(["0", "1", "2"]), "One row per group");
var merged = ScheduleNumberingPolicy.Reconcile([0, null, 3], 4, out _);
Check(merged!.Select(r => string.Join(",", r)).SequenceEqual(["0", "1,2", "3"]), "Blank marker merges neighbouring groups (Revit grouped them together)");
var mergedEnd = ScheduleNumberingPolicy.Reconcile([0, null], 3, out _);
Check(mergedEnd!.Last().SequenceEqual([1, 2]), "Merged row at the end");
Check(ScheduleNumberingPolicy.Reconcile([0, 0, 1], 2, out var f1) is null && f1!.Contains("разделена"), "Group split over rows is rejected");
Check(ScheduleNumberingPolicy.Reconcile([null, null], 4, out var f2) is null && f2!.Contains("подряд"), "Two merged rows in a row are ambiguous");
Check(ScheduleNumberingPolicy.Reconcile([0, 2], 3, out _) is null && ScheduleNumberingPolicy.Reconcile([0, null, 2], 3, out _) is null, "Missing group and a one-group blank row rejected");
Console.WriteLine("PASS grouped rows reconciled with Revit's collapsed table");

IReadOnlyList<IReadOnlyList<long>> sortRows = new IReadOnlyList<long>[] { new long[] { 100 }, new long[] { 70 }, new long[] { 10 } };
var values = new Dictionary<long, NumberingSortValue> { [100] = new(1, 10, ""), [70] = new(1, 2, ""), [10] = new(1, 2, "") };
var sorted = ScheduleNumberingPolicy.Sort(sortRows, id => values[id], null);
Check(sorted.SelectMany(r => r).SequenceEqual(new long[] { 70, 10, 100 }), "Numeric sorting and stable equal keys");
values[100] = new(2, 0, "шт."); values[70] = new(2, 0, "м"); values[10] = new(2, 0, "м");
sorted = ScheduleNumberingPolicy.Sort(sortRows, id => values[id], null);
Check(sorted.SelectMany(r => r).SequenceEqual(new long[] { 70, 10, 100 }), "Unit sorting before numbering");
try
{
    ScheduleNumberingPolicy.Sort([new long[] { 100, 70 }], id => values[id], null);
    throw new Exception("Mixed grouped sort keys must fail");
}
catch (InvalidOperationException) { }
Check(ScheduleNumberingPolicy.Sort([new long[] { 100, 70 }], id => values[id], new HashSet<long> { 70 }).Count == 1, "Selection restricts grouped sort keys");
Console.WriteLine("PASS sorting by units/numbers, stable ties and ambiguous grouped keys");
