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
Check(copy.Version == 2 && copy.Rules.Count == 1 && copy.Rules[0].Parts[0].Parameter == "@Тип" && copy.Resolve("OST_DuctCurves", null) != null, "JSON round trip");
Check(set.ToJson().Contains("ADSK_Наименование") && !set.ToJson().Contains("TypeKey"), "Readable Cyrillic JSON without legacy keys");
Check(EngineeringRuleSet.FromJson(null).Rules.Count == 0 && EngineeringRuleSet.FromJson("").Rules.Count == 0, "Empty storage");
Fails(() => EngineeringRuleSet.FromJson("{\"Version\":3,\"Rules\":[]}"), "Unknown version must fail");
Fails(() => EngineeringRuleSet.FromJson("{broken"), "Broken JSON must fail with a message");
Console.WriteLine("PASS rule scope: «Маркировка типоразмера», «Без типа», «игнорировать», removal, validation, storage round trip");

// Version 1 (rules by type UniqueId) → marks of those types; type rules win over the former category rule.
var v1 = EngineeringRuleSet.FromJson("{\"Version\":1,\"Rules\":[" +
    "{\"Category\":\"OST_PipeCurves\",\"TypeKey\":null,\"NameEnabled\":true,\"Parts\":[{\"Parameter\":\"@Тип\"}]}," +
    "{\"Category\":\"OST_PipeCurves\",\"TypeKey\":\"uid-steel\",\"NameEnabled\":true,\"Parts\":[{\"Parameter\":\"@Размер\"}]}," +
    "{\"Category\":\"OST_PipeCurves\",\"TypeKey\":\"uid-unmarked\",\"QuantityEnabled\":true,\"QuantitySource\":\"Длина\",\"Unit\":\"Meter\"}," +
    "{\"Category\":\"OST_PipeCurves\",\"TypeKey\":\"uid-ignored\",\"NameEnabled\":true,\"Parts\":[{\"Parameter\":\"@Тип\"}]}]}");
Check(v1.Version == 2, "v1 upgraded");
Check(v1.Migrate(k => k switch { "uid-steel" => "Сталь", "uid-ignored" => "игнорировать", _ => null }), "Migration ran");
Check(v1.Resolve("OST_PipeCurves", "Сталь")!.Parts[0].Parameter == "@Размер", "Type rule → its mark");
Check(v1.Resolve("OST_PipeCurves", null)!.QuantityEnabled, "Unmarked type rule wins over the former category rule");
Check(v1.Rules.Count == 2 && !v1.Migrate(_ => null), "Ignored type dropped; second migration is a no-op");
Console.WriteLine("PASS version 1 rules migrate to type marks");

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
    "В1 - Внутренний хозяйственно-питьевой водопровод; Т3,Т4 - Внутренний водопровод горячей воды, подающий и циркуляционный", "Multi-system equipment: distinct names joined");
Check(Sys() == null && Sys(("Прочее", SystemDomain.Pipe)) == null, "No match → no value");
systems.Rules.Insert(0, new SystemNameRule { Types = ["ADSK_Водопровод_В1"], Domain = SystemDomain.Any, Name = "Особый" });
Check(Sys(("ADSK_Водопровод_В1", SystemDomain.Pipe)) == "Особый", "First matching rule wins");
Fails(() => new SystemNameSettings { Rules = [new() { Types = [" ", ""], Name = "x" }] }.Validate(), "Rule without types");
Fails(() => new SystemNameSettings { Rules = [new() { Types = ["В1"], Name = " " }] }.Validate(), "Rule without name");
Fails(() => new SystemNameSettings { Enabled = true, Target = "" }.Validate(), "Enabled without target");
var sysCopy = EngineeringRuleSet.FromJson(new EngineeringRuleSet { SystemNames = systems }.ToJson());
Check(sysCopy.SystemNames.Enabled && sysCopy.SystemNames.Rules[0].Name == "Особый", "System rules stored with the rule set");
var older = EngineeringRuleSet.FromJson("""{"Version":2,"Rules":[]}""");
Check(!older.SystemNames.Enabled && older.SystemNames.Rules.Count == 10, "Older projects: off, defaults ready");
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
Check(NearV(MepAlignGeometry.Plumb(new V3(0, 0, 0), new V3(0.2, 0.1, 3), out bool upB), new V3(0, 0, 3)) && upB, "Plumb keeps the lower end");
Check(NearV(MepAlignGeometry.Plumb(new V3(1, 1, 5), new V3(0.8, 1, 0), out bool upB2), new V3(0.8, 1, 5)) && !upB2, "Plumb with reversed curve");
Fails(() => MepAlignGeometry.Plumb(new V3(0, 0, 0), new V3(5, 0, 1), out _), "Not a riser");
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
Check(insCopy.Rules.Count == 2 && insCopy.Auto && insCopy.Fittings && insCopy.Resolve("ADSK_Водопровод_Т3", "Труба стальная Ø20")!.ThicknessMm == 19, "Insulation settings round trip");
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
