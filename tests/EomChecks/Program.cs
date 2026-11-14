using BotPlus.Eom.Core;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static bool Near(double a, double b, double eps = 1e-6) => Math.Abs(a - b) <= eps;

var s = new EomSettings();
var catalog = EomCatalog.Demo();

// Токи: 2,3 кВт при cosφ = 1 на 230 В — 10 А; 3ф 10 кВт при cosφ 0,9 — 16,04 А.
Check(Near(EomCalc.Current(2300, 1, 1, s), 10), "single-phase current");
Check(Near(EomCalc.Current(10000, 0.9, 3, s), 10000 / (Math.Sqrt(3) * 400 * 0.9)), "three-phase current");
Console.WriteLine("PASS currents for single- and three-phase circuits");

// Розеточная группа 2 кВт, 20 м: автомат 10 А, кабель 3х1,5 (Iдоп 19 ≥ 10).
var socket = EomCalc.Select(new("ЩР1", "1", "Розетки", 2000, 0.95, 1, 20), s, catalog);
Check(socket.Ok && socket.Breaker!.Rating == 10 && socket.Cable!.Section == 1.5 && socket.Cable.Cores == 3, "socket circuit selection");
Check(Near(socket.CableLengthM, 22), "cable reserve applied");
// Длинная линия: сечение растёт по потере напряжения, а не по току.
var longLine = EomCalc.Select(new("ЩР1", "2", "Наружное освещение", 2000, 0.95, 1, 120), s, catalog);
Check(longLine.Ok && longLine.Cable!.Section > 1.5 && longLine.VoltageDrop <= s.VoltageDropLimit, "voltage drop drives section");
// Трёхфазная нагрузка 30 кВт: 5-жильный кабель, 3P-автомат.
var motor = EomCalc.Select(new("ЩР1", "3", "Вентустановка", 30000, 0.85, 3, 30), s, catalog);
Check(motor.Ok && motor.Breaker!.Poles == 3 && motor.Cable!.Cores == 5 && motor.Cable.AllowedCurrent >= motor.Breaker.Rating, "three-phase selection");
var huge = EomCalc.Select(new("ЩР1", "4", "Слишком много", 500000, 1, 1, 10), s, catalog);
Check(!huge.Ok && huge.Breaker == null, "oversized load reported");
Console.WriteLine("PASS breaker and cable selection by current, voltage drop and cable reserve");

var load = EomCalc.PanelLoad([new("Щ", "1", "", 1000, 1, 1, 0), new("Щ", "2", "", 1000, 0.8, 1, 0)]);
Check(Near(load.P, 2000) && Near(load.Q, 750) && Near(load.S, Math.Sqrt(2000 * 2000 + 750 * 750)), "panel load");
Console.WriteLine("PASS panel load P, Q, S");

// Кабинет 6х5 м, h = 2,2 м: i = 2·30/(2,2·22) ≈ 1,24; 300 лк, 4000 лм.
double index = Lighting.RoomIndex(30, 22, 2.2);
Check(Near(index, 60 / 48.4), "room index");
double eta = Lighting.Utilization(index);
Check(eta > 0.5 && eta < 0.56, "utilization interpolation");
int n = Lighting.Count(300, 30, 4000, eta);
Check(n == (int)Math.Ceiling(300 * 30 * 1.4 * 1.1 / (4000 * eta)), "luminaire count");
Check(Lighting.Illuminance(n, 30, 4000, eta) >= 300, "actual illuminance meets the norm");
Check(Lighting.Utilization(0.1) == 0.36 && Lighting.Utilization(10) == 0.81, "utilization clamps");
Console.WriteLine("PASS lighting: room index, utilization, count and actual illuminance");

var grid = Lighting.Grid(0, 0, 6, 3, 8);
Check(grid.Count == 8 && grid.All(p => p.X > 0 && p.X < 6 && p.Y > 0 && p.Y < 3), "grid count and bounds");
Check(Near(grid[0].X, 6 / 4.0 / 2) && Near(grid[0].Y, 3 / 2.0 / 2), "grid half-step offset from walls");
Check(Lighting.Grid(0, 0, 1, 1, 0).Count == 0, "empty grid");
Console.WriteLine("PASS uniform grid with half-step wall offsets");

Check(EomSettings.Mark(s.GroupMask, "ЩР1", "3") == "ЩР1.Гр3" && EomSettings.Mark(s.CableMask, " ЩО ", "12") == "ЩО-12", "marking masks");
var restored = EomSettings.FromJson((s with { CableReserve = 15, GroupMask = "Гр.{N}" }).ToJson());
Check(restored.CableReserve == 15 && restored.GroupMask == "Гр.{N}", "settings round-trip");
Check(EomSettings.FromJson("{bad").CableReserve == 10 && EomSettings.FromJson("""{"CableReserve":99}""").CableReserve == 10, "invalid settings fall back to defaults");
Check(Lighting.DefaultLux("Кабинет 101") == 300 && Lighting.DefaultLux("Коридор") == 100, "default lux by room name");
Console.WriteLine("PASS marking masks, settings storage and default illuminance norms");

string file = Path.Combine(Path.GetTempPath(), "eom-catalog-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    catalog.Cables[0].AllowedCurrent = 18; catalog.Save(file);
    var loaded = EomCatalog.Load(file);
    Check(loaded.Cables.Count == catalog.Cables.Count && loaded.Cables[0].AllowedCurrent == 18 && loaded.Luminaires.Count == 4, "catalog round-trip");
    Check(EomCatalog.Load(file + ".missing").Cables.Count > 0, "missing catalog falls back to demo");
}
finally { File.Delete(file); }
Console.WriteLine("PASS catalog storage and demo fallback");
