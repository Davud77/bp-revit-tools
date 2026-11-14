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
var category = new EngineeringRule { Category = "OST_DuctCurves", NameEnabled = true, Parts = [new() { Parameter = "@Тип" }] };
var typed = new EngineeringRule { Category = "OST_DuctCurves", TypeKey = "T1", NameEnabled = true, Parts = [new() { Parameter = "@Размер" }] };
set.Put(category); set.Put(typed);
Check(set.Resolve("OST_DuctCurves", "T1") == typed, "Type rule wins");
Check(set.Resolve("OST_DuctCurves", "T2") == category, "«Без типа» covers types without own rule");
Check(set.Resolve("OST_PipeCurves", "T1") == null, "Category isolation");
set.Put(new EngineeringRule { Category = "OST_DuctCurves", TypeKey = "T1" });
Check(set.Rules.Count == 1 && set.Resolve("OST_DuctCurves", "T1") == category, "Disabling a type rule removes it and falls back");
Fails(() => set.Put(new EngineeringRule { Category = "OST_DuctCurves", NameEnabled = true }), "Name rule without components must fail");
Fails(() => set.Put(new EngineeringRule { Category = "OST_DuctCurves", QuantityEnabled = true, Reserve = 0.5 }), "Reserve below 1 must fail");
var copy = EngineeringRuleSet.FromJson(set.ToJson());
Check(copy.Rules.Count == 1 && copy.Rules[0].Parts[0].Parameter == "@Тип" && copy.Resolve("OST_DuctCurves", null) != null, "JSON round trip");
Check(EngineeringRuleSet.FromJson(null).Rules.Count == 0 && EngineeringRuleSet.FromJson("").Rules.Count == 0, "Empty storage");
Fails(() => EngineeringRuleSet.FromJson("{\"Version\":2,\"Rules\":[]}"), "Unknown version must fail");
Console.WriteLine("PASS rule scope: type over «Без типа», removal, validation, storage round trip");

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
