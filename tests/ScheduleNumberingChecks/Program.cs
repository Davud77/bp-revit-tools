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
