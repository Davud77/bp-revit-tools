using HeatLossViewer.Mep;

void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
bool Near(double a, double b, double tol = 1e-9) => Math.Abs(a - b) < tol;
bool NearV(V3 a, V3 b, double tol = 1e-9) => (a - b).Length < tol;
void Fails(Action action, string message) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception(message); }

// ---------- Updater rules ----------
var values = new Dictionary<string, string?> { ["@Тип"] = "Труба стальная", ["@Размер"] = "500×300", ["Материал"] = "", ["Ду"] = "25" };
var parts = new List<NamePart> { new() { Parameter = "@Тип" }, new() { Parameter = "Ду", Prefix = " Ø", Suffix = " мм" }, new() { Parameter = "Материал", Prefix = ", " } };
Check(EngineeringRulesPolicy.ComposeName(parts, p => values.GetValueOrDefault(p)) == "Труба стальная Ø25 мм", "Empty value drops prefix and suffix");
parts.Reverse();
Check(EngineeringRulesPolicy.ComposeName(parts, p => values.GetValueOrDefault(p)) == "Ø25 ммТруба стальная", "Order of components follows the list");
Check(EngineeringRulesPolicy.SortedSize(300, 500) == "500×300" && EngineeringRulesPolicy.SortedSize(500, 300) == "500×300", "Larger side first");
Console.WriteLine("PASS name composition: order, prefixes, empty values, larger side first");

var rule = new EngineeringRule { Category = "OST_PipeCurves", QuantityEnabled = true, QuantitySource = "Длина", Unit = QuantityUnit.Meter, Reserve = 1.1, Decimals = 2 };
Check(EngineeringRulesPolicy.Quantity(rule, SourceKind.Length, 2.0, out _) == 2.2, "1.1 × 2 m = 2.2, not bumped to 2.21 by float noise");
Check(EngineeringRulesPolicy.Quantity(rule, SourceKind.Length, 2.0001, out _) == 2.21, "Round up");
rule.Rounding = QuantityRounding.Nearest;
Check(EngineeringRulesPolicy.Quantity(rule, SourceKind.Length, 2.0001, out _) == 2.2, "Nearest rounding");
rule.Unit = QuantityUnit.Millimeter; rule.Decimals = 0; rule.Reserve = 1;
Check(EngineeringRulesPolicy.Quantity(rule, SourceKind.Length, 1.2345, out _) == 1235, "m → mm");
Check(EngineeringRulesPolicy.Quantity(rule, SourceKind.Area, 3, out var unitError) == null && unitError != null, "mm is not an area unit");
rule.Unit = QuantityUnit.Piece;
Check(EngineeringRulesPolicy.Quantity(rule, SourceKind.Count, 1, out _) == 1, "Pieces");
Check(EngineeringRulesPolicy.Quantity(rule, SourceKind.Number, double.NaN, out _) == null, "NaN rejected");
Check(EngineeringRulesPolicy.SameNumber(2.2, 2.2) && !EngineeringRulesPolicy.SameNumber(null, 2.2) && !EngineeringRulesPolicy.SameNumber(2.2, 2.21), "Idempotency check");
Console.WriteLine("PASS quantity: reserve, units, rounding up/nearest, incompatible unit, idempotent compare");

var set = new EngineeringRuleSet();
var noType = new EngineeringRule { Category = "OST_DuctCurves", NameEnabled = true, Parts = [new() { Parameter = "@Тип" }] };
var marked = new EngineeringRule { Category = "OST_DuctCurves", TypeMark = " Оцинк ", NameEnabled = true, Parts = [new() { Parameter = "@Размер" }] };
set.Put(noType); set.Put(marked);
Check(marked.TypeMark == "Оцинк", "Mark is trimmed");
Check(set.Resolve("OST_DuctCurves", "Оцинк") == marked, "Types with the same mark share the rule");
Check(set.Resolve("OST_DuctCurves", "") == noType && set.Resolve("OST_DuctCurves", null) == noType, "Empty mark is «Без типа»");
Check(set.Resolve("OST_DuctCurves", "Другая") == null, "A mark without its own rule is not processed (BIM2B grouping)");
Check(set.Resolve("OST_DuctCurves", "Игнорировать") == null && set.Resolve("OST_DuctCurves", " игнорировать ") == null, "«игнорировать» switches the type off");
Fails(() => set.Put(new EngineeringRule { Category = "OST_DuctCurves", TypeMark = "игнорировать", NameEnabled = true, Parts = [new() { Parameter = "@Тип" }] }), "No rule for the ignore mark");
Check(set.Resolve("OST_PipeCurves", "Оцинк") == null, "Category isolation");
set.Put(new EngineeringRule { Category = "OST_DuctCurves", TypeMark = "Оцинк" });
Check(set.Rules.Count == 1 && set.Resolve("OST_DuctCurves", "Оцинк") == null, "Both «Заполнять» off removes the rule");
Fails(() => set.Put(new EngineeringRule { Category = "OST_DuctCurves", NameEnabled = true }), "Name rule without components must fail");
Check(set.Rules.Count == 1, "Invalid edit keeps the stored rule");
Fails(() => set.Put(new EngineeringRule { Category = "OST_DuctCurves", QuantityEnabled = true, Reserve = 0.5 }), "Reserve below 1 must fail");
var copy = EngineeringRuleSet.FromJson(set.ToJson());
Check(copy.Version == EngineeringRuleSet.CurrentVersion && copy.Rules.Count == 1 && copy.Rules[0].Parts[0].Parameter == "@Тип" && copy.Resolve("OST_DuctCurves", null) != null, "JSON round trip");
Check(set.ToJson().Contains("ADSK_Наименование") && !set.ToJson().Contains("TypeKey"), "Readable Cyrillic JSON without legacy keys");
Check(EngineeringRuleSet.FromJson(null).Rules.Count == 0 && EngineeringRuleSet.FromJson("").Rules.Count == 0, "Empty storage");
Fails(() => EngineeringRuleSet.FromJson("{\"Version\":99,\"Rules\":[]}"), "Unknown version must fail");
Fails(() => EngineeringRuleSet.FromJson("{broken"), "Broken JSON must fail with a message");
Console.WriteLine("PASS rule scope: «Маркировка типоразмера», «Без типа», «игнорировать», removal, validation, storage round trip");

// Version 1 (rules by type UniqueId) → marks of those types; type rules win over the former category rule.
var v1 = EngineeringRuleSet.FromJson("{\"Version\":1,\"Rules\":[" +
    "{\"Category\":\"OST_PipeCurves\",\"TypeKey\":null,\"NameEnabled\":true,\"Parts\":[{\"Parameter\":\"@Тип\"}]}," +
    "{\"Category\":\"OST_PipeCurves\",\"TypeKey\":\"uid-steel\",\"NameEnabled\":true,\"Parts\":[{\"Parameter\":\"@Размер\"}]}," +
    "{\"Category\":\"OST_PipeCurves\",\"TypeKey\":\"uid-unmarked\",\"QuantityEnabled\":true,\"QuantitySource\":\"Длина\",\"Unit\":\"Meter\"}," +
    "{\"Category\":\"OST_PipeCurves\",\"TypeKey\":\"uid-ignored\",\"NameEnabled\":true,\"Parts\":[{\"Parameter\":\"@Тип\"}]}]}");
Check(v1.Version == EngineeringRuleSet.CurrentVersion, "v1 upgraded");
Check(v1.Migrate(k => k switch { "uid-steel" => "Сталь", "uid-ignored" => "игнорировать", _ => null }), "Migration ran");
Check(v1.Resolve("OST_PipeCurves", "Сталь")!.Parts[0].Parameter == "@Размер", "Type rule → its mark");
Check(v1.Resolve("OST_PipeCurves", null)!.QuantityEnabled, "Unmarked type rule wins over the former category rule");
Check(v1.Rules.Count == 2 && !v1.Migrate(_ => null), "Ignored type dropped; second migration is a no-op");
Console.WriteLine("PASS version 1 rules migrate to type marks");

// Category rules: first matching condition, fallback, independent name/quantity and migration.
var conditional = new EngineeringRuleSet();
EngineeringRule NameFormula(string source) => new() { Category = "OST_PipeCurves", NameEnabled = true, Parts = [new() { Parameter = source }] };
var nameGroup = new ConditionalRuleGroup
{
    Category = "OST_PipeCurves", Target = "ADSK_Наименование",
    Conditions = [new() { Parameter = "Комментарии к типоразмеру", Value = "Труба стальная электросварная прямошовная", Formula = NameFormula("Внешний диаметр") },
        new() { Parameter = "Комментарии к типоразмеру", Comparison = ConditionComparison.Contains, Value = "стальная", Formula = NameFormula("Диаметр") }],
    Otherwise = NameFormula("@Тип")
};
conditional.ConditionalGroups.Add(nameGroup);
var quantityGroup = new ConditionalRuleGroup
{
    Category = "OST_PipeCurves", IsQuantity = true, Target = "ADSK_Количество",
    Otherwise = new EngineeringRule { Category = "OST_PipeCurves", QuantityEnabled = true, QuantitySource = "Длина", Unit = QuantityUnit.Meter, Reserve = 1.15 }
};
conditional.ConditionalGroups.Add(quantityGroup);
foreach (var g in conditional.ConditionalGroups) g.Validate();
IReadOnlyList<EngineeringRule> Selected(string? comment) => conditional.ResolveConditionals("OST_PipeCurves", null, _ => comment);
Check(Selected(" ТРУБА СТАЛЬНАЯ ЭЛЕКТРОСВАРНАЯ ПРЯМОШОВНАЯ ").First(r => r.NameEnabled).Parts[0].Parameter == "Внешний диаметр", "Exact type comment, trim/case and first-match priority");
Check(Selected("Труба стальная водогазопроводная").First(r => r.NameEnabled).Parts[0].Parameter == "Диаметр", "Second condition has a different formula");
Check(Selected("Полипропилен").First(r => r.NameEnabled).Parts[0].Parameter == "@Тип" && Selected(null).First(r => r.NameEnabled).Parts[0].Parameter == "@Тип", "Fallback on unmatched or missing parameter");
Check(Selected("Полипропилен").First(r => r.QuantityEnabled).Reserve == 1.15, "Quantity conditions independent from name");
var firstCondition = nameGroup.Conditions[0]; nameGroup.Conditions.Reverse();
Check(Selected(firstCondition.Value).First(r => r.NameEnabled).Parts[0].Parameter == "Диаметр", "Changing condition order changes priority");
nameGroup.Conditions.Reverse();
firstCondition.Formula.NameEnabled = false;
Check(!Selected(firstCondition.Value).Any(r => r.NameEnabled), "Disabled matched formula skips write rather than using fallback");
firstCondition.Formula.NameEnabled = true;
Check(conditional.ResolveConditionals("OST_PipeCurves", "игнорировать", _ => firstCondition.Value).Count == 0, "Ignore mark retained");
Check(conditional.ResolveConditionals("OST_DuctCurves", null, _ => firstCondition.Value).Count == 0, "Conditions isolated by category");
var comparison = new FormulaCondition { Parameter = "x", Comparison = ConditionComparison.Empty };
Check(comparison.Matches(_ => "") && !comparison.Matches(_ => null), "Empty existing parameter differs from missing parameter");
comparison.Comparison = ConditionComparison.NotEmpty;
Check(comparison.Matches(_ => "a") && !comparison.Matches(_ => " "), "Not empty");
comparison.Comparison = ConditionComparison.NotEquals; comparison.Value = "a";
Check(comparison.Matches(_ => "b") && !comparison.Matches(_ => null), "Missing value never satisfies not equals");
comparison.Comparison = ConditionComparison.StartsWith; comparison.Value = "труба";
Check(comparison.Matches(_ => "Труба стальная"), "Starts with");
var roundTrip = EngineeringRuleSet.FromJson(conditional.ToJson());
Check(roundTrip.Version == 3 && roundTrip.ConditionalGroups.Count == 2 && roundTrip.ResolveConditionals("OST_PipeCurves", null, _ => firstCondition.Value).First(r => r.NameEnabled).Parts[0].Parameter == "Внешний диаметр", "Conditions and fallback survive JSON round trip");
var migratedGroups = new EngineeringRuleSet { Rules = EngineeringRuleSet.DefaultRules() };
migratedGroups.EnsureConditionalGroups();
foreach (var old in migratedGroups.Rules)
{
    var resolved = migratedGroups.ResolveConditionals(old.Category, old.TypeMark, _ => null);
    if (old.NameEnabled)
        Check(resolved.First(r => r.NameEnabled).Parts.Select(p => p.Parameter).SequenceEqual(old.Parts.Select(p => p.Parameter)), "Legacy name formula retained");
    Check(resolved.First(r => r.QuantityEnabled).QuantitySource == old.QuantitySource, "Legacy quantity formula retained");
}
Check(migratedGroups.ResolveConditionals("OST_PipeCurves", "Неизвестная", _ => null).Count == 0, "Migration preserves coverage; unmatched old marks stay untouched");
int convertedCount = migratedGroups.ConditionalGroups.Count;
migratedGroups.ConditionalGroups[0].Conditions.Clear(); migratedGroups.EnsureConditionalGroups();
Check(migratedGroups.ConditionalGroups.Count == convertedCount && migratedGroups.ConditionalGroups[0].Conditions.Count == 0, "Migration does not restore deleted conditions");
var differentTargets = new EngineeringRuleSet();
differentTargets.Put(NameFormula("@Тип"));
var customTarget = NameFormula("Диаметр"); customTarget.TypeMark = "Другая"; customTarget.NameTarget = "Комментарии"; differentTargets.Put(customTarget);
differentTargets.EnsureConditionalGroups();
Check(differentTargets.ResolveConditionals("OST_PipeCurves", "Другая", _ => null).Single().NameTarget == "Комментарии", "Legacy custom target not lost during conversion");
var invalidGroup = new ConditionalRuleGroup { Category = "OST_PipeCurves", Conditions = [new() { Parameter = "", Formula = NameFormula("@Тип") }] };
Fails(invalidGroup.Validate, "Condition without a parameter rejected");
Console.WriteLine("PASS conditional rules: type comments, priority, fallback, independent quantity, comparisons, JSON and legacy migration");

var requestedPipes = new EngineeringRuleSet { Rules = EngineeringRuleSet.DefaultRules() };
requestedPipes.EnsureConditionalGroups(); requestedPipes.ApplyPipeNameDefaults();
var pipeNames = requestedPipes.ConditionalGroups.Single(g => g.Category == "OST_PipeCurves" && !g.IsQuantity && g.Target == "ADSK_Наименование");
Check(pipeNames.Conditions.Count == 1 && pipeNames.Conditions[0].Value == "Труба стальная электросварная прямошовная", "Requested exact type comment");
string PipeName(string comment, string outer, string nominal, string thickness) => EngineeringRulesPolicy.ComposeName(
    requestedPipes.ResolveConditionals("OST_PipeCurves", null, p => p == "Комментарии к типоразмеру" ? comment : null).Single(r => r.NameEnabled).Parts,
    p => p switch { "Комментарии к типоразмеру" => comment, "Внешний диаметр" => outer, "Диаметр" => nominal, "@Толщина стенки" => thickness, _ => null });
Check(PipeName("Труба стальная электросварная прямошовная", "133", "125", "4") == "Труба стальная электросварная прямошовная ø133x4", "Steel formula uses outer diameter, like screenshot");
Check(PipeName("Труба полипропиленовая PN20", "25", "20", "3.4") == "Труба полипропиленовая PN20 ø20x3.4", "Fallback uses nominal diameter, like screenshot");
pipeNames.Otherwise.Parts[1].Prefix = " Изменено "; requestedPipes.ApplyPipeNameDefaults();
Check(pipeNames.Otherwise.Parts[1].Prefix == " Изменено ", "Pipe defaults do not overwrite later edits");
requestedPipes.ConditionalGroups.RemoveAll(g => g.Category == "OST_PipeCurves");
var afterDelete = EngineeringRuleSet.FromJson(requestedPipes.ToJson());
afterDelete.EnsureConditionalGroups(); afterDelete.ApplyPipeNameDefaults();
Check(!afterDelete.ConditionalGroups.Any(g => g.Category == "OST_PipeCurves") && afterDelete.ResolveConditionals("OST_PipeCurves", null, _ => "").Count == 0, "Deleted rules stay deleted after JSON/reopen; legacy rules never revive");
Console.WriteLine("PASS requested pipe defaults and persistent rule deletion");

var enabledDefaults = new EngineeringRuleSet(); enabledDefaults.PrepareDefaults();
Check(enabledDefaults.ConditionalGroups.Count == 19 && enabledDefaults.ConditionalGroups.All(g => g.IsActive)
    && enabledDefaults.ConditionalGroups.Where(g => g.Category == "OST_PipeInsulations").Single().IsQuantity
    && enabledDefaults.SystemNames.Enabled, "Defaults enable all quantity categories, supported names and system names");
enabledDefaults.ValidateConditionalGroups();
foreach (var group in enabledDefaults.ConditionalGroups)
    Check(group.Conditions.Select(c => c.Formula).Append(group.Otherwise).All(f => group.IsQuantity ? f.QuantityEnabled : f.NameEnabled), "Every default condition and fallback enabled");
var pipeQuantity = enabledDefaults.ConditionalGroups.First(g => g.Category == "OST_PipeCurves" && g.IsQuantity);
Check(pipeQuantity.Otherwise.QuantitySource == "Длина" && pipeQuantity.Otherwise.Unit == QuantityUnit.Meter, "Previously empty pipe fallback gets length, not 1 piece");
pipeQuantity.Otherwise.QuantityEnabled = false;
enabledDefaults.SystemNames.Enabled = false;
enabledDefaults.ConditionalGroups.RemoveAll(g => g.Category == "OST_CableTray");
var enabledRoundTrip = EngineeringRuleSet.FromJson(enabledDefaults.ToJson()); enabledRoundTrip.PrepareDefaults();
Check(!enabledRoundTrip.SystemNames.Enabled && !enabledRoundTrip.ConditionalGroups.First(g => g.Category == "OST_PipeCurves" && g.IsQuantity).Otherwise.QuantityEnabled && !enabledRoundTrip.ConditionalGroups.Any(g => g.Category == "OST_CableTray"), "Later user disabling and deletion survive default preparation and reopening");
var userCustom = new EngineeringRuleSet { ConditionalGroups = [new() { Category = "OST_PipeCurves", Target = "Комментарии", Otherwise = new() { Category = "OST_PipeCurves" } }] };
userCustom.PrepareDefaults();
Check(!userCustom.ConditionalGroups.First(g => g.Target == "Комментарии").IsActive, "Enabling standard defaults does not enable custom target rules");
Console.WriteLine("PASS default preparation: all enabled, valid fallbacks, persistence of later changes and custom rule isolation");

var pipeReserves = new EngineeringRuleSet(); pipeReserves.PrepareDefaults();
var pipeReserveGroup = pipeReserves.ConditionalGroups.Single(g => g.Category == "OST_PipeCurves" && g.IsQuantity);
Check(pipeReserveGroup.Conditions.Count == 4, "Four exact type comments for 10 percent pipe reserve");
foreach (string comment in new[] { "Труба стальная водогазопроводная", "Труба стальная водогазопроводная оцинкованная", "Труба стальная электросварная прямошовная", "Труба чугунная SML" })
{
    var selected = pipeReserves.ResolveConditionals("OST_PipeCurves", null, _ => " " + comment.ToUpperInvariant() + " ").Single(r => r.QuantityEnabled);
    Check(selected.Reserve == 1.1 && selected.QuantitySource == "Длина" && selected.Unit == QuantityUnit.Meter && selected.Decimals == 6 && selected.Rounding == QuantityRounding.Nearest, "10 percent, length in meters, no rounding preset: " + comment);
    Check(Near(EngineeringRulesPolicy.Quantity(selected, SourceKind.Length, 2, out _)!.Value, 2.2), "2 m becomes 2.2 m for special pipe");
}
foreach (string? comment in new[] { "Труба полипропиленовая PN20", "Труба стальная водогазопроводная ГОСТ", "", null })
{
    var selected = pipeReserves.ResolveConditionals("OST_PipeCurves", null, _ => comment).Single(r => r.QuantityEnabled);
    Check(selected.Reserve == 1.3 && Near(EngineeringRulesPolicy.Quantity(selected, SourceKind.Length, 2, out _)!.Value, 2.6), "All other/missing type comments get 30 percent");
}
pipeReserveGroup.Conditions[0].Formula.Reserve = 1.075; pipeReserveGroup.Otherwise.QuantityEnabled = false;
var reserveReload = EngineeringRuleSet.FromJson(pipeReserves.ToJson()); reserveReload.PrepareDefaults();
Check(reserveReload.ConditionalGroups.Single(g => g.Category == "OST_PipeCurves" && g.IsQuantity).Conditions[0].Formula.Reserve == 1.075 && !reserveReload.ConditionalGroups.Single(g => g.Category == "OST_PipeCurves" && g.IsQuantity).Otherwise.QuantityEnabled, "Later reserve edits and disabling survive reopening");
reserveReload.ConditionalGroups.RemoveAll(g => g.Category == "OST_PipeCurves" && g.IsQuantity);
reserveReload.PrepareDefaults(); Check(!reserveReload.ConditionalGroups.Any(g => g.Category == "OST_PipeCurves" && g.IsQuantity), "Deleted quantity rule is not restored after one-time upgrade");
var oldReserveSettings = new EngineeringRuleSet { ConditionalRulesInitialized = true, RuleListInitialized = true, EnabledDefaultsVersion = 1, PipeNameDefaultsVersion = 1,
    ConditionalGroups = [new() { Category = "OST_PipeCurves", IsQuantity = true, Target = "ADSK_Количество", Otherwise = new() { Category = "OST_PipeCurves", QuantityEnabled = true, Reserve = 1.5 } }] };
oldReserveSettings.PrepareDefaults();
Check(oldReserveSettings.ConditionalGroups.Single().Conditions.Count == 4 && oldReserveSettings.ConditionalGroups.Single().Otherwise.Reserve == 1.3, "Existing 2.0.6 project receives new reserve scheme once");
Console.WriteLine("PASS pipe quantity defaults 2.0.7: four comments at 10 percent, other pipes at 30 percent, upgrade and persistence");

var walls = WallThicknessSettings.Defaults();
Check(EngineeringRulesPolicy.DuctWall(walls, SectionShape.Round, 200, false) == 0.5 && EngineeringRulesPolicy.DuctWall(walls, SectionShape.Round, 250, false) == 0.6, "Round rows ascending, limit inclusive");
Check(EngineeringRulesPolicy.DuctWall(walls, SectionShape.Rectangular, 600, false) == 0.7 && EngineeringRulesPolicy.DuctWall(walls, SectionShape.Rectangular, 600, true) == 0.8, "Fire protection rows");
Check(EngineeringRulesPolicy.DuctWall([new() { Shape = SectionShape.Round, MaxSizeMm = 500, ThicknessMm = 0.6 }], SectionShape.Round, 300, true) == 0.6, "No fire row → ordinary rows");
Check(EngineeringRulesPolicy.DuctWall(walls, SectionShape.Round, 2500, false) == null, "Beyond the table → no value");
var unsorted = new List<WallThicknessRow> { new() { Shape = SectionShape.Round, MaxSizeMm = 1000, ThicknessMm = 0.9 }, new() { Shape = SectionShape.Round, MaxSizeMm = 300, ThicknessMm = 0.5 } };
Check(EngineeringRulesPolicy.DuctWall(unsorted, SectionShape.Round, 250, false) == 0.5, "Rows apply from smaller to larger regardless of order");
Check(EngineeringRulesPolicy.PipeWall(57, 50) == 3.5 && EngineeringRulesPolicy.PipeWall(50, 57) == null && EngineeringRulesPolicy.PipeWall(0, 0) == null, "Pipe wall = (outer − inner)/2");
Fails(() => new WallThicknessSettings { Rows = [new() { MaxSizeMm = 100, ThicknessMm = 0 }] }.Validate(), "Zero thickness rejected");
Fails(() => new WallThicknessSettings { Rows = [new() { MaxSizeMm = 100, ThicknessMm = 1 }, new() { MaxSizeMm = 100, ThicknessMm = 2 }] }.Validate(), "Duplicate rows rejected");
Console.WriteLine("PASS wall thickness: pipes by diameters, ducts by table with fire protection");

Check(EngineeringRulesPolicy.FittingSize([(250, 0, 0), (250, 0, 0)]) == "Ø250", "Elbow");
Check(EngineeringRulesPolicy.FittingSize([(0, 300, 500), (0, 400, 200)]) == "500×300-400×200", "Transition: larger section first, larger side first");
Check(EngineeringRulesPolicy.FittingSize([(160, 0, 0), (250, 0, 0), (250, 0, 0)]) == "Ø250-Ø160", "Tee: main then branch");
Check(EngineeringRulesPolicy.FittingSize([]) == "", "No connectors");
Console.WriteLine("PASS built-in fitting size from connectors");

var target = new EngineeringRuleSet();
target.Put(new EngineeringRule { Category = "OST_PipeCurves", TypeMark = "Сталь", NameEnabled = true, Parts = [new() { Parameter = "@Тип" }] });
var file = new EngineeringRuleSet { WallThickness = new() { Target = "ADSK_Толщина стенки" } };
file.Put(new EngineeringRule { Category = "OST_PipeCurves", TypeMark = "Сталь", NameEnabled = true, Parts = [new() { Parameter = "@Размер" }] });
file.Put(new EngineeringRule { Category = "OST_DuctCurves", TypeMark = "Оцинк", QuantityEnabled = true, QuantitySource = "Длина", Unit = QuantityUnit.Meter });
var imported = EngineeringRuleSet.FromJson(file.ToJson());
Check(target.Merge(imported, r => r.Category == "OST_PipeCurves", false) == 1, "Only selected rules imported");
Check(target.Resolve("OST_PipeCurves", "Сталь")!.Parts[0].Parameter == "@Размер" && target.Resolve("OST_DuctCurves", "Оцинк") == null, "Matched by category and mark, others untouched");
Check(target.WallThickness.Target == "", "Wall table not imported unless chosen");
target.Merge(imported, _ => false, true);
Check(target.WallThickness.Target == "ADSK_Толщина стенки", "Wall table imported on request");
Console.WriteLine("PASS import/export: selected rules by category and mark, optional wall table");

Check(new EngineeringRule().Reserve == 1.1, "General reserve 10 % by default");
var sewer = new EngineeringRule
{
    Category = "OST_PipeCurves", QuantityEnabled = true, QuantitySource = "Длина", Unit = QuantityUnit.Meter, Reserve = 1.1,
    ReserveBySize =
    [
        new() { Systems = ["К1", "К2"], MaxSizeMm = 50, Reserve = 1.15 },
        new() { Systems = ["К1", "К2"], MaxSizeMm = 110, Reserve = 1.1 },
        new() { MaxSizeMm = 20, Reserve = 1.3 },
        new() { MaxSizeMm = 32, Reserve = 1.2 }
    ]
};
double R(double? size, params string[] sys) => EngineeringRulesPolicy.ReserveFor(sewer, size, sys);
Check(R(50, "ADSK_Канализация_К1") == 1.15 && R(40, "ADSK_Канализация_К2") == 1.15, "Sewerage Ø50 and smaller — 15 %");
Check(R(110, "ADSK_Канализация_К1") == 1.1 && R(100, "ADSK_Канализация_К1") == 1.1, "Sewerage Ø110 — 10 %");
Check(R(160, "ADSK_Канализация_К1") == 1.1, "Beyond the system rows: general reserve");
Check(R(20, "ADSK_Канализация_К1") == 1.15, "System rows win over generic rows");
Check(R(15, "ADSK_Водопровод_В1") == 1.3 && R(25, "ADSK_Водопровод_В1") == 1.2 && R(40, "ADSK_Водопровод_В1") == 1.1, "Other systems: generic rows by size, then general");
Check(R(null, "ADSK_Канализация_К1") == 1.1 && R(50) == 1.1, "No size → general; no system → generic rows only (Ø50 > 32)");
Check(EngineeringRulesPolicy.Quantity(sewer, SourceKind.Length, 2, out _, R(50, "ADSK_Канализация_К1")) == 2.3, "Quantity uses the chosen reserve: 2 m × 1.15");
var unorderedRows = new EngineeringRule { Reserve = 1.05, ReserveBySize = [new() { MaxSizeMm = 110, Reserve = 1.1 }, new() { MaxSizeMm = 50, Reserve = 1.15 }] };
Check(EngineeringRulesPolicy.ReserveFor(unorderedRows, 40, []) == 1.15, "Rows apply from the smaller size regardless of order");
Fails(() => new EngineeringRule { Category = "x", QuantityEnabled = true, QuantitySource = "Длина", ReserveBySize = [new() { MaxSizeMm = 0 }] }.Validate(), "Zero size rejected");
Fails(() => new EngineeringRule { Category = "x", QuantityEnabled = true, QuantitySource = "Длина", ReserveBySize = [new() { MaxSizeMm = 50, Reserve = 0.9 }] }.Validate(), "Negative reserve rejected");
Fails(() => new EngineeringRule { Category = "x", QuantityEnabled = true, QuantitySource = "Длина", ReserveBySize = [new() { Systems = ["К1"], MaxSizeMm = 50 }, new() { Systems = [" к1 "], MaxSizeMm = 50, Reserve = 1.2 }] }.Validate(), "Duplicate rows rejected");
var sewerCopy = EngineeringRuleSet.FromJson(new EngineeringRuleSet { Rules = [sewer] }.ToJson()).Rules[0];
Check(sewerCopy.ReserveBySize.Count == 4 && sewerCopy.ReserveBySize[0].Systems[1] == "К2", "Reserve rows stored with the rule");
Check(EngineeringRuleSet.FromJson("""{"Version":2,"Rules":[{"Category":"OST_PipeCurves","QuantityEnabled":true,"QuantitySource":"Длина","Reserve":1.0}]}""").Rules[0].ReserveBySize.Count == 0, "Older rules: no rows, own reserve kept");
Console.WriteLine("PASS reserve: 10 % default, by size and system (sewerage Ø50 — 15 %, Ø110 — 10 %), fallbacks, storage");

var systems = new SystemNameSettings { Enabled = true };
string? Sys(params (string, SystemDomain)[] t) => EngineeringRulesPolicy.SystemName(systems, t);
Check(Sys(("ADSK_Водопровод_В1", SystemDomain.Pipe)) == "В1 - Внутренний хозяйственно-питьевой водопровод", "В1 by suffix");
Check(Sys(("ADSK_Водопровод_Т3", SystemDomain.Pipe)) == Sys(("ADSK_Водопровод_Т4", SystemDomain.Pipe)) && Sys(("ADSK_Водопровод_Т4", SystemDomain.Pipe))!.StartsWith("Т3,Т4 - "), "Т3 and Т4 share one name");
Check(Sys(("ADSK_Пожаротушение водяное_В21", SystemDomain.Pipe))!.StartsWith("В21 - ") && Sys(("ADSK_Пожаротушение дренчерное_В22", SystemDomain.Pipe))!.StartsWith("В22 - "), "Fire fighting В21/В22, not В2");
Check(Sys(("ADSK_Канализация_К1", SystemDomain.Pipe))!.StartsWith("К1 - ") && Sys(("ADSK_Канализация_К2", SystemDomain.Pipe))!.StartsWith("К2 - ") && Sys(("ADSK_Канализация_К3", SystemDomain.Pipe))!.StartsWith("К3 - "), "Sewerage К1–К3");
Check(Sys(("ADSK_Водопровод_В2", SystemDomain.Pipe))!.StartsWith("В2 - "), "В2");
Check(Sys(("в1", SystemDomain.Pipe)) != null && Sys(("ADSK_Водопровод_В11", SystemDomain.Pipe)) == null && Sys(("ХВС В1 стояк", SystemDomain.Pipe)) == null, "Exact or «_code» suffix only, case-insensitive");
Check(Sys(("В1", SystemDomain.Duct)) == null, "Pipe rules do not name a ventilation system В1");
Check(Sys(("ADSK_Водопровод_В1", SystemDomain.Pipe), ("ADSK_Водопровод_Т3", SystemDomain.Pipe), ("ADSK_Водопровод_Т4", SystemDomain.Pipe)) ==
    "В1 - Внутренний хозяйственно-питьевой водопровод", "Multi-system equipment: first system only");
Check(Sys(("Своя система", SystemDomain.Pipe), ("В1", SystemDomain.Pipe)) == null, "Unmapped first system does not fall through");
Check(Sys() == null && Sys(("Своя система", SystemDomain.Pipe)) == null, "No match → no value");
systems.Rules.Insert(0, new SystemNameRule { Types = ["ADSK_Водопровод_В1"], Domain = SystemDomain.Any, Name = "Особый" });
Check(Sys(("ADSK_Водопровод_В1", SystemDomain.Pipe)) == "Особый", "First matching rule wins");
Fails(() => new SystemNameSettings { Rules = [new() { Types = [" ", ""], Name = "x" }] }.Validate(), "Rule without types");
Fails(() => new SystemNameSettings { Rules = [new() { Types = ["В1"], Name = " " }] }.Validate(), "Rule without name");
Fails(() => new SystemNameSettings { Enabled = true, Target = "" }.Validate(), "Enabled without target");
var sysCopy = EngineeringRuleSet.FromJson(new EngineeringRuleSet { SystemNames = systems }.ToJson());
Check(sysCopy.SystemNames.Enabled && sysCopy.SystemNames.Rules[0].Name == "Особый", "System rules stored with the rule set");
var older = EngineeringRuleSet.FromJson("""{"Version":2,"Rules":[]}""");
Check(!older.SystemNames.Enabled && older.SystemNames.Rules.Count == SystemNameSettings.Defaults().Count, "Older projects: off, defaults ready");
var fresh = new SystemNameSettings { Enabled = true };
string? Fresh(string t) => EngineeringRulesPolicy.SystemName(fresh, [(t, SystemDomain.Pipe)]);
Check(Fresh("ADSK_Отопление_Т11")!.StartsWith("Т11,Т21 - ") && Fresh("ADSK_Отопление_Т21") == Fresh("ADSK_Отопление_Т11"), "Heating Т11/Т21");
Check(Fresh("ADSK_Теплоснабжение_Т1")!.StartsWith("Т1,Т2 - Теплоснабжение") && Fresh("ADSK_Холодоснабжение_Х2")!.StartsWith("Х1,Х2 - ")
    && Fresh("ADSK_Кондиционирование_Х4")!.StartsWith("Х3,Х4 - ") && Fresh("ADSK_Кондиционирование_Х5")!.StartsWith("Х5 - "), "Heat supply, chilled water, conditioning");
Check(Fresh("Холодное водоснабжение")!.StartsWith("В1 - ") && Fresh("Горячее водоснабжение")!.StartsWith("Т3,Т4 - ") && Fresh("Вентиляционное отверстие")!.StartsWith("К1 - ")
    && Fresh("Водяная система пожаротушения")!.StartsWith("В21 - ") && Fresh("Дренчерная система пожаротушения")!.StartsWith("В22 - ")
    && Fresh("Газовая система пожаротушения") != null && Fresh("Другие системы пожаротушения") != null && Fresh("Прочее") != null, "Revit standard system types named");
var oldStored = EngineeringRuleSet.FromJson("""{"Version":2,"Rules":[],"SystemNames":{"Enabled":true,"Rules":[{"Types":["В1"],"Domain":"Pipe","Name":"Мой В1"},{"Types":["Т1","Т2"],"Domain":"Pipe","Name":"Отопление"}]}}""").SystemNames;
Check(oldStored.Rules[0].Name == "Мой В1" && oldStored.Rules[0].Types.Contains("Холодное водоснабжение") && oldStored.Rules[1].Name == "Отопление"
    && EngineeringRulesPolicy.SystemName(oldStored, [("ADSK_Отопление_Т11", SystemDomain.Pipe)])!.StartsWith("Т11,Т21 - "), "Stored rules get the new default types once, own names kept");
oldStored.Rules.RemoveAll(r => r.Types.Contains("Прочее"));
var reloaded = EngineeringRuleSet.FromJson(new EngineeringRuleSet { SystemNames = oldStored }.ToJson()).SystemNames;
Check(!reloaded.Rules.Any(r => r.Types.Contains("Прочее")), "A default rule deleted after the upgrade stays deleted");
var sysTarget = new EngineeringRuleSet();
sysTarget.Merge(sysCopy, _ => false, false, true);
Check(sysTarget.SystemNames.Rules[0].Name == "Особый", "System rules imported on request");
Console.WriteLine("PASS ADSK_Система_Имя: ГОСТ 21.601 defaults, shared Т3/Т4, suffix match, networks, multi-system, storage and import");

// ---------- Geometry ----------
var x = new V3(1, 0, 0); var y = new V3(0, 1, 0); var z = V3.ZAxis;
Check(NearV(MepGeometry.Rotate(x, z, Math.PI / 2), y), "Rodrigues rotation");
var align = MepGeometry.Align(x, -x, z);
Check(Near(align.Angle, Math.PI) && NearV(align.Axis, z), "Opposite vectors use the preferred perpendicular axis");
align = MepGeometry.Align(new V3(1, 1, 0), z);
Check(NearV(MepGeometry.Rotate(new V3(1, 1, 0).Unit(), align.Axis, align.Angle), z, 1e-9), "Align arbitrary");
Check(MepGeometry.Align(x, x).Angle == 0, "Already aligned");
Console.WriteLine("PASS align: general, opposite, identity");

Check(MepGeometry.AngleAbout(z, x, y) is { } q1 && Near(q1, Math.PI / 2), "About Z: x → y = +90°");
Check(MepGeometry.AngleAbout(z, x, z) == null, "Unreachable: angle to axis changes");
var both = MepGeometry.OrientAbout(z, x, y);
Check(both.Count == 2 && both.Any(s => s.Along && Near(s.Angle, Math.PI / 2)) && both.Any(s => !s.Along && Near(s.Angle, -Math.PI / 2)), "Two senses of a line");
Check(MepGeometry.OrientAbout(z, x, x).Count == 2, "Along (0) and against (180)");
Check(MepGeometry.OrientAbout(z, new V3(1, 0, 1).Unit(), new V3(0, 1, 1).Unit()).Count == 1, "Cone: only one sense reachable");
Check(MepGeometry.OrientAbout(z, x, z).Count == 0, "Impossible orientation reported");
Check(NearV(MepGeometry.ViewerAxis(z, true), z) && NearV(MepGeometry.ViewerAxis(z, false), -z), "Viewer side sign");
Check(Near(MepGeometry.Normalize(3 * Math.PI), Math.PI) && Near(MepGeometry.Normalize(-Math.PI), Math.PI), "Normalize to (-π, π]");
Console.WriteLine("PASS rotation about a fixed axis: one/two/no solutions, viewer convention");

var points = new List<V3> { new(0, 0, 10), new(5, 0, 0) };
Check(MepGeometry.Nearest(points, new V3(0.5, 0, 0), z) == 0, "Depth along view direction ignored (plan click)");
Check(MepGeometry.Nearest(points, new V3(0.5, 0, 0), null) == 1, "3D distance without view");
Check(MepGeometry.Nearest([], x, null) == -1, "No candidates");
Console.WriteLine("PASS nearest connector to the click in the view plane");

var chain = MepGeometry.OrderCollinear([(new V3(10, 0, 0), new V3(6, 0, 0)), (new V3(0, 0, 0), new V3(5, 0, 0)), (new V3(12, 0, 0), new V3(15, 0, 0))]);
// The first segment (10 → 6) sets the chain direction: -X.
Check(chain.Order.Select(l => l.Index).SequenceEqual([2, 0, 1]) && !chain.Order[1].Reversed && chain.Order[0].Reversed && chain.Order[2].Reversed, "Order along the line, reversed flag");
Check(chain.Gaps.Count == 2 && Near(chain.Gaps[0].Gap, 2) && chain.Gaps[0].From == 2 && chain.Gaps[0].To == 0 && Near(chain.Gaps[1].Gap, 1) && chain.Gaps[1].To == 1, "Gaps between consecutive segments");
Fails(() => MepGeometry.OrderCollinear([(new V3(0, 0, 0), new V3(5, 0, 0)), (new V3(4, 0, 0), new V3(8, 0, 0))]), "Overlap must fail");
Fails(() => MepGeometry.OrderCollinear([(new V3(0, 0, 0), new V3(5, 0, 0)), (new V3(6, 1, 0), new V3(8, 1, 0))]), "Parallel offset must fail");
Fails(() => MepGeometry.OrderCollinear([(new V3(0, 0, 0), new V3(5, 0, 0))]), "Single segment must fail");
Console.WriteLine("PASS collinear chain: order, reversal, gaps, overlap and offset rejected");

// Duct axis at z = 3 along X from 0 to 10, half size 0.5; diffuser below at z = 0 looking up.
var route = MepGeometry.RouteDiffuser(new V3(4, 0.2, 0), z, new V3(0, 0, 3), new V3(10, 0, 3), 0.5, 1, false);
Check(!route.Horizontal && NearV(route.RiserBottom, new V3(4, 0.2, 1)) && NearV(route.RiserTop, new V3(4, 0.2, 3)) && NearV(route.TapPoint, route.RiserTop), "Takeoff straight up within the duct width");
route = MepGeometry.RouteDiffuser(new V3(4, 0.2, 0), z, new V3(0, 0, 3), new V3(10, 0, 3), 0.5, 0, true);
Check(route.Horizontal && NearV(route.TapPoint, new V3(4, 0, 3)), "Tee needs the branch on the axis");
route = MepGeometry.RouteDiffuser(new V3(4, 2, 0), z, new V3(0, 0, 3), new V3(10, 0, 3), 0.5, 0, false);
Check(route.Horizontal && NearV(route.RiserTop, new V3(4, 2, 3)) && NearV(route.TapPoint, new V3(4, 0, 3)), "Offset diffuser: horizontal leg");
Fails(() => MepGeometry.RouteDiffuser(new V3(4, 0, 0), -z, new V3(0, 0, 3), new V3(10, 0, 3), 0.5, 0, false), "Connector facing away");
Fails(() => MepGeometry.RouteDiffuser(new V3(4, 0, 0), x, new V3(0, 0, 3), new V3(10, 0, 3), 0.5, 0, false), "Non-vertical connector");
Fails(() => MepGeometry.RouteDiffuser(new V3(12, 0, 0), z, new V3(0, 0, 3), new V3(10, 0, 3), 0.5, 0, false), "Beyond the duct");
Fails(() => MepGeometry.RouteDiffuser(new V3(4, 0, 0), z, new V3(0, 0, 3), new V3(10, 0, 3), 0.5, 2.8, false), "Flex longer than the drop");
Fails(() => MepGeometry.RouteDiffuser(new V3(4, 0, 0), z, new V3(0, 0, 0), new V3(0, 0, 5), 0.5, 0, false), "Vertical main unsupported");
Console.WriteLine("PASS diffuser route: takeoff, tee on axis, horizontal leg, invalid layouts rejected");

// a - b - c (b on axis to c) - d ; reference blocks a.
var graph = new Dictionary<string, (string, bool, bool)[]>
{
    ["b"] = [("a", false, true), ("c", true, false)],
    ["c"] = [("b", true, false), ("d", false, false)],
    ["d"] = [("c", false, false)],
    ["a"] = [("b", false, true)]
};
var withAxial = MepGeometry.Reachable("b", n => graph[n], true);
var without = MepGeometry.Reachable("b", n => graph[n], false);
Check(withAxial.SetEquals(["b", "c", "d"]) && without.SetEquals(["b"]), "Reference blocks; axial joints optional");
Console.WriteLine("PASS rotated part: reference connector blocks, longitudinal neighbours optional");

// ---------- Выровнять ----------
var X = new V3(1, 0, 0); var Y = new V3(0, 1, 0); var Z = V3.ZAxis;
Check(NearV(MepAlignGeometry.AxisShift(new V3(3, 2, 5), X, new V3(0, 0, 1), X), new V3(0, -2, -4)), "Parallel axes become collinear (no shift along the axis)");
Check(NearV(MepAlignGeometry.AxisShift(new V3(0, 3, 2), X, new V3(7, 1, 0), Z), new V3(0, -2, 0)), "Horizontal element passes through a vertical reference in plan, height kept");
Check(NearV(MepAlignGeometry.AxisShift(new V3(4, 3, 9), Z, new V3(0, 0, 1), X), new V3(0, -3, 0)), "Vertical element moves in plan onto a horizontal reference");
Fails(() => MepAlignGeometry.AxisShift(new V3(0, 0, 0), X, new V3(0, 5, 0), Y), "Crossing horizontal axes → use height");
Check(Near(MepAlignGeometry.HeightShift(new V3(0, 5, 3), X, new V3(2, 0, 1), Y), -2), "Crossing pipes: move down to intersect");
Check(Near(MepAlignGeometry.HeightShift(new V3(0, 0, 3), new V3(1, 0, 0.1), new V3(10, 0, 0), Y), -4), "Sloped moving pipe: height at the crossing point");
Check(Near(MepAlignGeometry.HeightShift(new V3(0, 2, 3), X, new V3(5, 0, 1), X), -2), "Parallel in plan: same elevation");
Fails(() => MepAlignGeometry.HeightShift(new V3(0, 0, 0), Z, new V3(0, 0, 0), X), "Vertical moving element");
Fails(() => MepAlignGeometry.HeightShift(new V3(0, 0, 0), X, new V3(0, 0, 0), Z), "Vertical reference");
Check(NearV(MepAlignGeometry.Plumb(new V3(0, 0, 0), new V3(0.2, 0.1, 3), out bool upB)!.Value, new V3(0, 0, 3)) && upB, "Plumb keeps the lower end");
Check(NearV(MepAlignGeometry.Plumb(new V3(1, 1, 5), new V3(0.8, 1, 0), out bool upB2)!.Value, new V3(0.8, 1, 5)) && !upB2, "Plumb with reversed curve");
Check(NearV(MepAlignGeometry.Plumb(new V3(0, 0, 0), new V3(5, 0, 1), out _)!.Value, new V3(0, 0, 1)) && MepAlignGeometry.Plumb(new V3(0, 0, 2), new V3(5, 0, 2), out _) is null, "Strong slope straightened; horizontal skipped");
Check(NearV(MepAlignGeometry.DistanceShift(new V3(0, 3, 0), X, new V3(0, 0, 0), X, 2, true, 0), new V3(0, -1, 0)), "Horizontal axis distance 3 → 2, same side");
Check(NearV(MepAlignGeometry.DistanceShift(new V3(0, -3, 0), X, new V3(0, 0, 0), X, 2, true, 0), new V3(0, 1, 0)), "Other side kept");
Check(NearV(MepAlignGeometry.DistanceShift(new V3(0, 3, 0), X, new V3(0, 0, 0), X, 2, true, 0.5), new V3(0, -0.5, 0)), "Face distance adds half sizes and insulation");
Check(NearV(MepAlignGeometry.DistanceShift(new V3(0, 0, 1), X, new V3(0, 0, 0), X, 2, false, 0), new V3(0, 0, 1)), "Vertical distance");
Check(NearV(MepAlignGeometry.DistanceShift(new V3(0, 0, 1), X, new V3(0, 0, 0), X, 2, true, 0), new V3(0, 2, 0)), "Coincident in plan: positive side");
Check(NearV(MepAlignGeometry.DistanceShift(new V3(3, 4, 7), Z, new V3(0, 0, 0), Z, 10, true, 0), new V3(3, 4, 0)), "Vertical risers: horizontal distance along their offset");
Fails(() => MepAlignGeometry.DistanceShift(new V3(0, 3, 0), Y, new V3(0, 0, 0), X, 2, true, 0), "Non-parallel");
Fails(() => MepAlignGeometry.DistanceShift(new V3(1, 0, 0), Z, new V3(0, 0, 0), Z, 2, false, 0), "Vertical distance of risers undefined");
Console.WriteLine("PASS align: axis, height, plumb, distance between axes/faces");

var three = new List<RouteEnd> { new(new V3(10, 0, 3), X), new(new V3(10, 1, 3), X), new(new V3(6, 2, 3), X) };
var left = MepAlignGeometry.MultiRoute(three, 0, new V3(10, 10, 0), null, 0.05);
Check(Near(left.AngleDeg, 90) && NearV(left.Direction, Y), "Left turn 90°");
Check(NearV(left.Legs[0].Corner, new V3(10, 0, 3)) && NearV(left.Legs[0].End, new V3(10, 10, 3)), "Base turns at its end, new leg ends at the pick");
Check(NearV(left.Legs[1].Corner, new V3(9, 1, 3)) && NearV(left.Legs[1].End, new V3(9, 10, 3)), "Inner route keeps its offset and turns earlier");
Check(NearV(left.Legs[2].Corner, new V3(8, 2, 3)) && Near(left.Legs[2].Extension, 2), "Shorter route extended: ends aligned automatically");
var snapped = MepAlignGeometry.MultiRoute(three, 0, new V3(20, 4, 0), [15, 30, 45, 60, 90], 0.05);
Check(Near(snapped.AngleDeg, 15), "21.8° snapped to the nearest fixed angle 15°");
var free = MepAlignGeometry.MultiRoute(three, 0, new V3(20, 4, 0), null, 0.05);
Check(Math.Abs(free.AngleDeg - Math.Atan2(4, 10) * 180 / Math.PI) < 1e-9, "Arbitrary angle");
var right = MepAlignGeometry.MultiRoute(three, 0, new V3(10, -10, 0), null, 0.05);
Check(Near(right.AngleDeg, -90) && NearV(right.Legs[1].Corner, new V3(11, 1, 3)), "Right turn: the left route becomes the outer one");
var straight = MepAlignGeometry.MultiRoute(three, 0, new V3(15, 0.3, 0), null, 0.05);
Check(straight.Straight && NearV(straight.Legs[2].End, new V3(15, 2, 3)) && Near(straight.Legs[0].Extension, 5) && Near(straight.Legs[2].Extension, 9), "Pick ahead: extend to the line through the pick");
var sloped = MepAlignGeometry.MultiRoute([new(new V3(0, 0, 1), new V3(1, 0, -0.02))], 0, new V3(5, 5, 0), null, 0.05);
Check(Near(sloped.Legs[0].Corner.Z, 1) && Near(sloped.Legs[0].End.Z, 1 - 0.02 * Math.Sqrt(50)), "Slope kept on the new leg");
Check(MepAlignGeometry.MultiRoute(three, 0, new V3(20, 1, 0), null, 0.05).Straight, "Under 10° means extend, not turn");
Fails(() => MepAlignGeometry.MultiRoute(three, 0, new V3(5, 10, 0), null, 0.05), "Turn over 90° rejected");
Fails(() => MepAlignGeometry.MultiRoute([new(new V3(0, 0, 0), X), new(new V3(0, 1, 0), Y)], 0, new V3(5, 5, 0), null, 0.05), "Non-parallel routes");
Fails(() => MepAlignGeometry.MultiRoute(three, 0, new V3(10.5, 0.3, 0), null, 1), "Point too close");
Check(MepAlignGeometry.ParseAngles("15, 30;45 90").SequenceEqual([15.0, 30, 45, 90]), "Fixed angle list");
Fails(() => MepAlignGeometry.ParseAngles("5"), "Angle below 10"); Fails(() => MepAlignGeometry.ParseAngles(" "), "Empty list");
Check(MepAlignGeometry.SnapAngle(-50, [45, 60]) == -45 && MepAlignGeometry.SnapAngle(70, null) == 70, "Snap keeps the turn side");
Console.WriteLine("PASS multi-routing: turns left/right, offsets, end alignment, snapping, straight extension, slope, invalid input");

Check(Near(MepGeometry.ParseAngle("45"), Math.PI / 4) && Near(MepGeometry.ParseAngle(" 22,5° "), Math.PI / 8) && Near(MepGeometry.ParseAngle("-90"), -Math.PI / 2), "Angle parsing");
Fails(() => MepGeometry.ParseAngle("0"), "Zero angle"); Fails(() => MepGeometry.ParseAngle("abc"), "Text angle"); Fails(() => MepGeometry.ParseAngle("400"), "Too large");
var settingsPath = Path.Combine(Path.GetTempPath(), "botplus-mep-" + Guid.NewGuid() + ".json");
try
{
    var settings = new MepToolSettings();
    foreach (var a in new[] { "15", "30", "45", "60", "90", "180", "22,5", "7", "15" }) settings.Remember(a);
    Check(settings.AngleHistory.Count == 8 && settings.AngleHistory[0] == "15" && settings.AngleHistory.Count(h => h == "15") == 1, "History: recent first, unique, bounded");
    settings.Rectangular.Use = true; settings.Tee = true; settings.Save(settingsPath);
    var loaded = MepToolSettings.Load(settingsPath);
    Check(loaded.Tee && loaded.Rectangular.Use && loaded.Angle == "15", "Settings round trip");
    File.WriteAllText(settingsPath, "{broken");
    Check(MepToolSettings.Load(settingsPath).Angle == "90", "Broken settings fall back to defaults");
}
finally { File.Delete(settingsPath); }
Console.WriteLine("PASS angle input, value history and per-user settings");

// ---------- Pipe insulation rules ----------
var ins = new InsulationSettings();
ins.Put("", "Труба стальная Ø20", "K-Flex 13", 13);
ins.Put("ADSK_Водопровод_Т3", "Труба стальная Ø20", "K-Flex 19", 19);
ins.Put("ADSK_Водопровод_В1", "Труба PP-R Ø25", "Energoflex 9", 9);
Check(ins.Resolve("ADSK_Водопровод_Т3", "Труба стальная Ø20")!.InsulationType == "K-Flex 19", "System rule wins over «all systems»");
Check(ins.Resolve("ADSK_Водопровод_В1", "труба стальная ø20")!.InsulationType == "K-Flex 13", "Fallback to «all systems», case-insensitive name");
Check(ins.Resolve(null, "Труба PP-R Ø25") is null && ins.Resolve("ADSK_Канализация_К1", "Труба PP-R Ø25") is null, "No rule outside the system");
ins.Put("ADSK_Водопровод_В1", "Труба PP-R Ø25", null, 0);
Check(ins.Rules.Count == 2 && ins.Get("ADSK_Водопровод_В1", "Труба PP-R Ø25") is null, "Row without type removed");
var insCopy = InsulationSettings.FromJson(ins.ToJson());
Check(insCopy.Rules.Count == 2 && insCopy.Auto && insCopy.Categories == (InsulationCategories.Pipes | InsulationCategories.Fittings)
    && insCopy.Resolve("ADSK_Водопровод_Т3", "Труба стальная Ø20")!.ThicknessMm == 19, "Insulation settings round trip and default categories");
Check(InsulationSettings.FromJson("""{"Version":1,"Fittings":false}""").Categories == InsulationCategories.Pipes
    && InsulationSettings.FromJson("""{"Version":1,"Fittings":true}""").Categories ==
        (InsulationCategories.Pipes | InsulationCategories.Fittings | InsulationCategories.Accessories), "Legacy switches retain their previous selection");
var removalSettings = new InsulationSettings { Categories = InsulationCategories.Accessories, PreserveManualRemovals = true };
Check(removalSettings.Clone() is { Categories: InsulationCategories.Accessories, PreserveManualRemovals: true }
    && InsulationPolicy.MatchesRecordedHost("original", "original")
    && !InsulationPolicy.MatchesRecordedHost("copy", "original")
    && !InsulationPolicy.MatchesRecordedHost("never insulated", null), "Manual removal preference persists and only the original host matches");
Check(InsulationSettings.FromJson(null).IsEmpty && !ins.IsEmpty, "Empty settings");
Fails(() => InsulationSettings.FromJson("{broken"), "Broken insulation settings reported");
ins.Rules[0].ThicknessMm = 0; Fails(() => ins.Validate(), "Zero thickness rejected");
var k13 = new InsulationRule { InsulationType = "K-Flex 13", ThicknessMm = 13 };
Check(InsulationPolicy.Decide(k13, null, null) == InsulationAction.Create, "No insulation → create");
Check(InsulationPolicy.Decide(k13, "k-flex 13", 13.01) == InsulationAction.Keep, "Same type and thickness → keep");
Check(InsulationPolicy.Decide(k13, "K-Flex 13", 9) == InsulationAction.Update && InsulationPolicy.Decide(k13, "Energoflex", 13) == InsulationAction.Update, "Other thickness or type → update");
Check(InsulationPolicy.Decide(null, "K-Flex 13", 13) == InsulationAction.None && InsulationPolicy.Decide(new InsulationRule(), null, null) == InsulationAction.None, "No rule → existing insulation untouched");
Check(InsulationPolicy.Sizes([20, 15, 50, 20, 0]) == "15–50" && InsulationPolicy.Sizes([32]) == "32" && InsulationPolicy.Sizes([]) == "", "Diameter range");
Check(InsulationPolicy.TryParseThickness(" 9,5 ", out double thk) && thk == 9.5 && !InsulationPolicy.TryParseThickness("0", out _) && !InsulationPolicy.TryParseThickness("400", out _), "Thickness input");
Console.WriteLine("PASS pipe insulation rules: system over all systems, removal, round trip, create/update/keep, sizes, thickness input");
Check(InsulationPolicy.InsulationName(InsulationPolicy.DefaultNameTemplate, 110) == "Трубная изоляция для ø110" && InsulationPolicy.InsulationName("Изоляция ø{Ø} мм", 26.8) == "Изоляция ø" + 26.8.ToString() + " мм", "Insulation name from template");
Check(InsulationPolicy.InsulationName(" ", 50) is null && InsulationSettings.FromJson("{\"Rules\":[{\"Name\":\"A\",\"InsulationType\":\"K\",\"ThicknessMm\":9}]}").Rules[0].NameTemplate == InsulationPolicy.DefaultNameTemplate, "Empty template skipped; old settings get the default name");
Console.WriteLine("PASS insulation name template");

// ---------- Default updater formulas ----------
var defaults = EngineeringRuleSet.DefaultRules();
foreach (var d in defaults) d.Validate();
Check(defaults.Count == 7 && defaults.Select(d => (d.Category, d.TypeMark)).Distinct().Count() == 7, "Seven default rules, one per scope");
var pipeDefault = defaults.First(d => d.Category == "OST_PipeCurves" && d.TypeMark is null);
Check(EngineeringRulesPolicy.ComposeName(pipeDefault.Parts, p => p switch { "Комментарии к типоразмеру" => "Труба стальная", "Внешний диаметр" => "26,8", _ => "2,8" }) == "Труба стальная ø26,8x2,8", "Default pipe name");
var tubes = defaults.First(d => d.TypeMark == "Трубки");
Check(!tubes.NameEnabled && tubes.QuantityEnabled, "Pipe insulation defaults only calculate quantity");
var oldTubes = tubes.Clone();
oldTubes.NameEnabled = true;
oldTubes.Parts = [new() { Parameter = "Комментарии к типоразмеру" }];
var oldInsulationRules = new EngineeringRuleSet { Rules = [oldTubes] };
Check(oldInsulationRules.ResolveConditionals(tubes.Category, tubes.TypeMark, _ => null).Single() is { NameEnabled: false, QuantityEnabled: true },
    "Saved pipe insulation naming no longer runs through legacy rules");
oldInsulationRules.ConditionalGroups.Add(new() { Category = tubes.Category, Otherwise = oldTubes });
oldInsulationRules.PrepareDefaults();
var resolvedInsulation = oldInsulationRules.ResolveConditionals(tubes.Category, tubes.TypeMark, _ => null);
Check(resolvedInsulation.Count > 0 && resolvedInsulation.All(r => !r.NameEnabled && r.QuantityEnabled),
    "Saved conditional naming is skipped and quantity still runs after preparing defaults");
Check(defaults.All(d => d.Reserve == 1 && d.Decimals == 6 && d.Rounding == QuantityRounding.Nearest) && defaults.First(d => d.TypeMark == "Гофры").QuantitySource == EngineeringRulesPolicy.CountSource
    && defaults.First(d => d.TypeMark == "Цилиндры").QuantitySource == "Объем", "Default quantities: no reserve, no rounding, count and volume sources");
var withDefaults = new EngineeringRuleSet(); foreach (var d in defaults) withDefaults.Put(d);
Check(withDefaults.Resolve("OST_FlexPipeCurves", "Подводки")!.Parts.Count == 2, "Defaults resolve by type mark");
Console.WriteLine("PASS default updater formulas");

// ---------- Riser numbering (ADSK Dynamo scripts, native) ----------
Check(RiserNumberingPolicy.IsVertical(0.3, 0.2, 3000) && !RiserNumberingPolicy.IsVertical(5, 0, 3000) && !RiserNumberingPolicy.IsVertical(0, 0, 0.5), "Vertical: same plan point, real height");
RiserPipe P(long id, double x, double y, double z, double len, string v = "") => new(id, x, y, z, len, v);
var riserPipes = new[]
{
    // Riser A (left, top row): three floors, one short piece between fittings.
    P(1, 0, 10000, 0, 2800), P(2, 0.4, 10000.3, 2800, 300), P(3, 0, 10000, 3100, 2700),
    // Riser B (right, same row, 0.5 mm lower in Y).
    P(4, 5000, 9999.5, 0, 3000),
    // Riser C (lower row, left of everything).
    P(5, -2000, 2000, 0, 3000),
    // A drop of 400 mm: not a riser.
    P(6, 3000, 6000, 2600, 400),
    // Two short pieces at one point: 800 + 800 ≥ 1500 → a riser (whole riser length counts).
    P(7, 8000, 2000, 0, 800), P(8, 8000, 2000, 800, 800),
};
var risers = RiserNumberingPolicy.Group(riserPipes, RiserNumberingPolicy.DefaultMinLengthMm);
Check(risers.Count == 4 && risers.All(r => !r.Pipes.Any(p => p.Id == 6)), "Drops shorter than the minimum are not risers");
Check(risers[0].Pipes.Select(p => p.Id).SequenceEqual(new long[] { 1, 2, 3 }), "Segments of one riser grouped bottom to top, short piece included");
Check(risers.Select(r => r.Pipes[0].Id).SequenceEqual(new long[] { 1, 4, 5, 7 }), "Order: top row first (Y descending, 1 mm tolerance), left to right");
var auto = RiserNumberingPolicy.Auto(risers, 1).ToDictionary(x => x.Id, x => x.Number);
Check(auto[1] == 1 && auto[2] == 1 && auto[3] == 1 && auto[4] == 2 && auto[5] == 3 && auto[7] == 4 && auto[8] == 4 && !auto.ContainsKey(6), "Auto: one number per riser, from 1");
Check(RiserNumberingPolicy.Auto(risers, 10)[0].Number == 10, "Auto: start value");
Check(RiserNumberingPolicy.Group(riserPipes, 0).Count == 5 && RiserNumberingPolicy.Group(riserPipes, 5000).Count == 1, "Minimum length filters whole risers");
var given = new[] { P(1, 0, 0, 0, 3000, "Ст В1-1"), P(2, 0, 0, 3000, 3000), P(3, 0, 0, 6000, 3000, " Ст В1-1 "),
    P(4, 500, 0, 0, 3000), P(5, 500, 0, 3000, 3000),
    P(6, 900, 0, 0, 3000, "1"), P(7, 900, 0, 3000, 3000, "2") };
var riserCopy = RiserNumberingPolicy.Copy(RiserNumberingPolicy.Group(given, 1500));
Check(riserCopy.Writes.Select(w => w.Id).OrderBy(i => i).SequenceEqual(new long[] { 2, 3 }) && riserCopy.Writes.All(w => w.Value == "Ст В1-1"), "Copy: the given number goes to the other segments");
Check(riserCopy.Empty.Count == 1 && riserCopy.Empty[0].Pipes[0].Id == 4 && riserCopy.Conflicts.Count == 1 && riserCopy.Conflicts[0].Values.Length == 2 && riserCopy.Risers == 3, "Copy: risers without a number and with different numbers are reported, not changed");
Check(RiserNumberingPolicy.ParseLength("1500") == 1500 && RiserNumberingPolicy.ParseLength("1,5") == 1.5 && RiserNumberingPolicy.ParseLength("-1") is null && RiserNumberingPolicy.ParseLength("x") is null, "Minimum length input");
Console.WriteLine("PASS riser numbering: vertical pipes, grouping, order, auto numbers, copy of given numbers");

RiserPipe SP(long id, double x, double y, string system, double bottom = 0, double length = 3000, string value = "") => new(id, x, y, bottom, length, value, system);
var systemsRisers = RiserNumberingPolicy.Group(new[] { SP(101, 0, 0, "В1"), SP(102, 100, 0, "Т3"), SP(103, 200, 0, "Т4"),
    SP(104, 2000, 0, "В1"), SP(105, 2100, 0, "Т3"), SP(106, 2200, 0, "Т4") }, 1500);
var bySystem = RiserNumberingPolicy.Auto(systemsRisers, 1).ToDictionary(p => p.Id, p => p.Number);
Check(bySystem[101] == 1 && bySystem[102] == 1 && bySystem[103] == 1 && bySystem[104] == 2 && bySystem[105] == 2 && bySystem[106] == 2, "Independent system counters start from 1");
Check(RiserNumberingPolicy.Auto(systemsRisers, 5, false).Select(p => p.Number).SequenceEqual(new[] { 5, 6, 7, 8, 9, 10 }), "Checkbox off: continuous numbering across all systems");
var waterBundles = RiserNumberingPolicy.Neighbors(systemsRisers, 300);
Check(waterBundles.Count == 2 && waterBundles.All(r => r.Pipes.Count == 3), "Cold, hot and return form two neighboring bundles");
var shared = RiserNumberingPolicy.Auto(waterBundles, 1).ToDictionary(p => p.Id, p => p.Number);
Check(shared[101] == shared[102] && shared[102] == shared[103] && shared[104] == shared[105] && shared[105] == shared[106] && shared[104] == 2, "Neighbors receive one number in all three systems");
var sameAxes = new[] { SP(1, 0, 0, "В1"), SP(2, 0, 0, "Т3") };
Check(RiserNumberingPolicy.Group(sameAxes, 1500).Count == 2 && RiserNumberingPolicy.Group(sameAxes, 1500, false).Count == 1, "Coincident axes separated by system when requested");
Check(RiserNumberingPolicy.Neighbors(RiserNumberingPolicy.Group(new[] { SP(1, 0, 0, "В1"), SP(2, 300, 0, "Т3") }, 1500), 300).Count == 1, "Neighbor distance boundary included");
Check(RiserNumberingPolicy.Neighbors(RiserNumberingPolicy.Group(new[] { SP(1, 0, 0, "В1"), SP(2, 301, 0, "Т3") }, 1500), 300).Count == 2, "Beyond distance not combined");
Check(RiserNumberingPolicy.Neighbors(RiserNumberingPolicy.Group(new[] { SP(1, 0, 0, "В1"), SP(2, 100, 0, "В1") }, 1500), 300).Count == 2, "Parallel risers of same type remain independent");
Check(RiserNumberingPolicy.Neighbors(RiserNumberingPolicy.Group(new[] { SP(1, 0, 0, ""), SP(2, 100, 0, "Т3") }, 1500), 300).Count == 2, "Unknown system not combined by proximity alone");
Check(RiserNumberingPolicy.Neighbors(RiserNumberingPolicy.Group(new[] { SP(1, 0, 0, "В1"), SP(2, 100, 0, "Т3", 3000) }, 1500), 300).Count == 2, "Different floors without height overlap stay separate");
Check(RiserNumberingPolicy.Neighbors(RiserNumberingPolicy.Group(new[] { SP(1, 0, 0, "В1"), SP(2, 100, 0, "Т3", 2000) }, 1500), 300).Count == 1, "At least 1 m of common height");
Check(RiserNumberingPolicy.Neighbors(RiserNumberingPolicy.Group(new[] { SP(1, 0, 0, "В1"), SP(2, 100, 0, "Т3", 2001) }, 1500), 300).Count == 2, "Tiny overlap not enough to infer neighbors");
var neighborChain = RiserNumberingPolicy.Neighbors(RiserNumberingPolicy.Group(new[] { SP(1, 0, 0, "В1"), SP(2, 250, 0, "Т3"), SP(3, 500, 0, "Т4") }, 1500), 300);
Check(neighborChain.Count == 2, "Complete-link grouping prevents an unbounded chain of near axes");
var segmentedNeighbors = RiserNumberingPolicy.Group(new[] { SP(1, 0, 0, "В1", 0, 800), SP(2, 0, 0, "В1", 800, 800), SP(3, 100, 0, "Т3", 0, 1600) }, 1500);
Check(RiserNumberingPolicy.Neighbors(segmentedNeighbors, 300).Count == 1, "Common height sums short segments between fittings");
var unequal = RiserNumberingPolicy.Neighbors(RiserNumberingPolicy.Group(new[] { SP(1, 0, 6000, "В1"), SP(2, 0, 3000, "В1"),
    SP(3, 0, 0, "В1"), SP(4, 100, 0, "Т3"), SP(5, 0, -3000, "Т3") }, 1500), 300);
var synchronized = RiserNumberingPolicy.Auto(unequal, 1).ToDictionary(p => p.Id, p => p.Number);
Check(synchronized[3] == 3 && synchronized[4] == 3 && synchronized[5] == 4, "Mixed bundles synchronize counters without duplicates within a system");
var neighborCopy = RiserNumberingPolicy.Copy(RiserNumberingPolicy.Neighbors(RiserNumberingPolicy.Group(new[] { SP(1, 0, 0, "В1", value: "5"), SP(2, 100, 0, "Т3") }, 1500), 300));
Check(neighborCopy.Writes.Single() == (2L, "5"), "Copy mode shares given number with neighboring system");
var neighborConflict = RiserNumberingPolicy.Copy(RiserNumberingPolicy.Neighbors(RiserNumberingPolicy.Group(new[] { SP(1, 0, 0, "В1", value: "5"), SP(2, 100, 0, "Т3", value: "6") }, 1500), 300));
Check(neighborConflict.Conflicts.Count == 1 && neighborConflict.Writes.Count == 0, "Conflicting numbers in neighboring bundle are not overwritten");
var overflowRisers = RiserNumberingPolicy.Group(new[] { SP(1, 0, 0, "В1"), SP(2, 1000, 0, "Т3") }, 1500);
Check(RiserNumberingPolicy.Auto(overflowRisers, int.MaxValue).All(w => w.Number == int.MaxValue), "Max int allowed once in each independent system");
try { RiserNumberingPolicy.Auto(overflowRisers, int.MaxValue, false); throw new Exception("Expected numbering overflow"); } catch (OverflowException) { }
Check(RiserNumberingPolicy.Neighbors(RiserNumberingPolicy.Group(systemsRisers.SelectMany(r => r.Pipes).Reverse(), 1500), 300).SelectMany(r => r.Pipes).Select(p => p.Id).SequenceEqual(waterBundles.SelectMany(r => r.Pipes).Select(p => p.Id)), "Grouping deterministic across input order");
Console.WriteLine("PASS riser systems and neighbors: independent counters, common numbers, height overlap, no chains, copy conflicts, overflow");
