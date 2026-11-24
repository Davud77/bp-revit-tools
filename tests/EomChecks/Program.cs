using BotPlus.Eom.Core;

PointLightingChecks.Run();

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static bool Near(double a, double b, double eps = 1e-6) => Math.Abs(a - b) <= eps;

var s = new EomSettings();
var catalog = EomCatalog.Demo();

if (OperatingSystem.IsWindows())
{
    var root = Directory.CreateTempSubdirectory("BotPlus-EomChecks-");
    try
    {
        var nested = root.CreateSubdirectory("Семейства");
        var example = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AC2R_RectangularBeam.rfa");
        var primary = Path.Combine(nested.FullName, "Балка.RFA");
        File.Copy(example, primary);
        File.Copy(example, Path.Combine(nested.FullName, "Балка.0001.rfa"));
        File.WriteAllText(Path.Combine(nested.FullName, "Сломанный.rfa"), "not a Revit family");
        var empty = new EquipmentLibrary("", []);
        var categoryNames = new Dictionary<string, int> { ["Каркас несущий"] = -2001320 };
        var scanned = EquipmentLibrary.ReadFolder(root.FullName, empty, categoryNames, (_, _) => { }, default);
        var family = scanned.Library.Entries.Single();
        Check(scanned.Skipped == 1, "one corrupt file skipped, backup omitted");
        Check(family.Name == "Балка" && family.CategoryName == "Каркас несущий" && family.Category == -2001320,
            "localized RFA category and filename fallback for an internal GUID");
        Check(family.File == Path.Combine("Семейства", "Балка.RFA") && family.Preview is { Length: > 8 } &&
            family.Preview.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG and relative path stored");
        var unknown = EquipmentLibrary.ReadFolder(root.FullName, empty, new Dictionary<string, int>(), (_, _) => { }, default);
        Check(unknown.Library.Entries.Length == 0 && unknown.Skipped == 2,
            "unmapped categories are skipped together with damaged files");
        var cached = EquipmentLibrary.ReadFolder(root.FullName, scanned.Library, new Dictionary<string, int>(), (_, _) => { }, default);
        Check(ReferenceEquals(family, cached.Library.Entries.Single()), "reuse metadata only for unchanged files");
        var legacyDuplicates = scanned.Library with
        {
            Entries =
            [
                family with { Name = "Устаревшая запись" },
                family with { File = family.File.ToLowerInvariant().Replace('\\', '/'), Name = "Другой дубль" }
            ]
        };
        var recovered = EquipmentLibrary.ReadFolder(root.FullName, legacyDuplicates, categoryNames, (_, _) => { }, default);
        Check(recovered.Library.Entries.Single().Name == family.Name, "ambiguous legacy entries force a fresh RFA read");
        string alias = Path.Combine(nested.FullName, "балка.rfa");
        int completedFiles = 0;
        var duplicates = EquipmentLibrary.ReadFiles(root.FullName, [alias, primary, primary], empty, categoryNames,
            (done, _) => completedFiles = done, default);
        Check(duplicates.Library.Entries.Single().File == family.File && duplicates.Skipped == 2 && completedFiles == 3,
            "case-insensitive and exact duplicates do not stop indexing or progress");
        EquipmentLibraryIndex.Publish(duplicates.Library, null, default);
        Check(EquipmentLibraryIndex.Open(root.FullName)!.Entries.Length == 1, "deduplicated scan publishes a readable shared index");
        File.SetLastWriteTimeUtc(primary, family.Modified.AddMinutes(5));
        var changed = EquipmentLibrary.ReadFolder(root.FullName, scanned.Library, new Dictionary<string, int>(), (_, _) => { }, default);
        Check(changed.Library.Entries.Length == 0 && changed.Skipped == 2,
            "a changed RFA invalidates its indexed category and thumbnail");

        var portable = EquipmentLibraryIndex.Publish(scanned.Library, null, default);
        var fromDisk = EquipmentLibraryIndex.Open(root.FullName)!;
        Check(fromDisk.Entries is [{ Preview: null, PreviewSize: > 0 }], "opening the index only reads metadata");
        Check(ReferenceEquals(fromDisk, EquipmentLibraryIndex.Open(root.FullName, fromDisk)), "unchanged shared revision is reused");
        File.SetLastWriteTimeUtc(primary, family.Modified);
        var incremental = EquipmentLibrary.ReadFolder(root.FullName, fromDisk, categoryNames, (_, _) => { }, default);
        Check(ReferenceEquals(fromDisk.Entries.Single(), incremental.Library.Entries.Single()), "unchanged entries reuse preview offsets");
        var republished = EquipmentLibraryIndex.Publish(incremental.Library, fromDisk, default);
        Check(republished.Index!.Revision != portable.Index!.Revision, "publishing replaces the revision");
        using (var reused = republished.Index.OpenPreviews())
            Check(republished.Index.ReadPreview(reused, republished.Entries.Single()).SequenceEqual(family.Preview!),
                "incremental publishing retains cached previews");
        var copied = root.CreateSubdirectory("Другой ПК");
        var large = Enumerable.Range(0, 30_000).Select(i => family with { File = $"Папка\\{i}.rfa" }).ToArray();
        EquipmentLibraryIndex.Publish(new(root.FullName, large), null, default);
        File.Copy(Path.Combine(root.FullName, EquipmentLibraryIndex.FileName), Path.Combine(copied.FullName, EquipmentLibraryIndex.FileName));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var opened = EquipmentLibraryIndex.Open(copied.FullName)!;
        timer.Stop();
        Check(opened.Folder == copied.FullName && opened.Entries.Length == 30_000 && opened.Entries.All(entry => entry.Preview == null),
            "portable index opens under another root without RFA files or eager previews");
        using var previews = opened.Index!.OpenPreviews();
        Check(opened.Index.ReadPreview(previews, opened.Entries[29_999]).SequenceEqual(family.Preview!),
            "final preview is addressable independently");
        Console.WriteLine($"PASS UGO portable index: {opened.Entries.Length} metadata rows opened in {timer.ElapsedMilliseconds} ms");
        Console.WriteLine("PASS UGO index: background metadata, PNG, backup exclusion, skipped files and cache invalidation");
    }
    finally { root.Delete(recursive: true); }
}

const double coincidenceMm = 0.03;
const double minimumMm = 3;
Check(!ConduitRoutingRule.OfferAlignment(0, 50, coincidenceMm), "an axial connection needs no move");
Check(ConduitRoutingRule.OfferAlignment(50, 50, coincidenceMm), "alignment includes exactly 50 mm");
Check(!ConduitRoutingRule.OfferAlignment(50.01, 50, coincidenceMm), "larger offset leaves the device in place");
Check(!ConduitRoutingRule.OfferAlignment(20, 0, coincidenceMm), "zero alignment threshold disables the offer");
Check(Near(ConduitRoutingRule.ParallelLead(1000, 0, coincidenceMm, minimumMm), 0), "collinear connections are straight");
Check(Near(ConduitRoutingRule.ParallelLead(1000, 125, coincidenceMm, minimumMm), 500), "zigzag corners split the forward run");
Check(Near(ConduitRoutingRule.SnapTurn(97.6, [30, 45, 90]), 90), "97.6 degree turn snaps to 90 before construction");
Check(Near(ConduitRoutingRule.SnapTurn(-49, [30, 45, 90]), -45), "clockwise turn keeps its sign");
Check(Near(ConduitRoutingRule.SnapTurn(46, [30, 90]), 30), "disabled 45 degree bend cannot be selected");
Check(Near(ConduitRoutingRule.SnapTurn(4, [30, 45, 90]), 0), "small changes in heading extend the straight run");
Check(Near(ConduitRoutingRule.SnapTurn(97.6, []), 0), "a type without tested bends only draws straight");
Check(Near(ConduitRoutingRule.SnapHeading(6.9), 0) && Near(ConduitRoutingRule.SnapHeading(11), 15), "free heading snaps to 0 or 15 degrees");
Check(Near(ConduitRoutingRule.SnapHeading(-27), -30) && Near(ConduitRoutingRule.SnapHeading(137), 135), "free heading snaps in both directions beyond the first quadrant");
Check(Near(ConduitRoutingRule.SnapTurn(73, ConduitRoutingRule.BendAngles), 75), "75 degree fitting is considered by the 15 degree grid");
foreach (var (forward, offset) in new[] { (-100.0, 80.0), (5.0, 80.0), (100.0, 2.0) })
{
    bool rejectedRoute = false;
    try { ConduitRoutingRule.ParallelLead(forward, offset, coincidenceMm, minimumMm); }
    catch (InvalidOperationException) { rejectedRoute = true; }
    Check(rejectedRoute, "reversed or short zigzag must be rejected");
}
var existingRouteSettings = EomSettings.FromJson("{\"LightingFixtureHeightMm\":3000}");
Check(existingRouteSettings.RouteAlignmentMm == 50 && existingRouteSettings.SwitchBoxOffsetMm == 100
    && existingRouteSettings.LightingFixtureHeightMm == 3000, "old RVT settings gain routing defaults");
var customRouteSettings = s with { RouteAlignmentMm = 12.5, SwitchBoxOffsetMm = 150 };
Check(EomSettings.FromJson(customRouteSettings.ToJson()) == customRouteSettings, "routing settings round trip");
Console.WriteLine("PASS lighting conduit geometry thresholds and settings");

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

var panelFeeder = PanelCalculation.Calculate(new("Щ1", "1", "Розетки", 2300, 1, 1, 20), 230, 0.8, s with { CableReserve = 0 }, catalog);
Check(Near(panelFeeder.Demand, 1.84) && Near(panelFeeder.Apparent, 1.84) && Near(panelFeeder.Current, 10), "Elpro group current uses installed power");
Check(Near(panelFeeder.Leakage, 4.2) && Near(PanelCalculation.VoltageDrop(10, 230, 1, 20, 1.5, 1), 2.609), "Elpro leakage and copper voltage drop");
var panelSupply = PanelCalculation.Calculate(new("Щ1", "2", "Щ2", 2300, 1, 1, 20), 230, 0.8, s with { CableReserve = 0 }, catalog, feedsPanel: true);
Check(Near(panelSupply.Current, 8), "Elpro downstream panel current uses demand power");
Check(PanelCalculation.SelectBreaker(10, 1, catalog)?.Rating >= 12.5, "Elpro breaker reserve factor");
var panelPhases = PanelCalculation.Balance([10, 4, 5, 2], 3);
Check(Near(panelPhases.Totals.Sum(), 30) && panelPhases.Totals.Max() - panelPhases.Totals.Min() < 8, "Elpro phase reallocation reduces imbalance while preserving current");
Console.WriteLine("PASS Elpro feeder formulas: installed/demand current, apparent power, leakage and copper voltage drop");

// Кабинет 6х5 м, h = 2,2 м: i = 2·30/(2,2·22) ≈ 1,24.
double index = Lighting.RoomIndex(30, 22, 2.2);
Check(Near(index, 60 / 48.4), "room index");
var table = UtilizationTable.Standard();
Check(table.IsValid && Near(table.Get(1.0, 0.7, 0.5, 0.3), 0.64) && Near(table.Get(1.0, 0.5, 0.3, 0.1), 0.50), "standard table nodes by reflection");
Check(Near(table.Get(1.125, 0.7, 0.5, 0.3), (0.64 + 0.71) / 2), "utilization interpolates by index");
Check(table.Get(0.1, 0.7, 0.5, 0.3) == 0.47 && table.Get(10, 0.7, 0.5, 0.3) == 1.00, "utilization clamps");
Check(table.Nearest(0.05, 0, 0) == 5 && table.Nearest(0.5, 0.3, 0.1) == 3 && table.Nearest(0.7, 0.5, 0.3) == 0, "nearest reflection combination");
var led = catalog.Luminaires[0];
var fluorescent = catalog.Luminaires.First(l => l.Efficiency < 1);
Check(Near(Lighting.Utilization(led, 1.0, 0.7, 0.5, 0.3), 0.64) && Near(Lighting.Utilization(fluorescent, 1.0, 0.7, 0.5, 0.3), 0.64 * fluorescent.Efficiency), "Ki = room utilance × efficiency");
double eta = Lighting.Utilization(led, index, 0.7, 0.5, 0.3);
int n = Lighting.Count(300, 30, 4000, eta);
Check(n == (int)Math.Ceiling(300 * 30 * 1.4 / (4000 * eta)), "luminaire count");
Check(Lighting.Illuminance(n, 30, 4000, eta) >= 300 && Lighting.Illuminance(n - 1, 30, 4000, eta) < 300, "count is the minimum meeting the norm");
Console.WriteLine("PASS lighting: room index, utilization table, Ki, count and actual illuminance");

// Пример nanoCAD BIM Электро: помещение 24,48 м², высота 3,3 м, рабочая поверхность 0,8 м, Eн 100 лк, Kз 1,3,
// ARS/R 418 (4 лампы × 1300 лм), Ки 48,73 % по таблице светильника → необходимо 2, освещённость 159,24 лк, i = 0,99.
double side = Math.Sqrt(24.48);
var room = new RoomGeometry(24.48, 4 * side, side, side, 3.3);
var ars = new LuminaireItem
{
    Name = "ARS/R 418 (595)", LampType = "ЛЛ", LampCount = 4, Power = 72, Flux = 5200, Efficiency = 0.66,
    Utilization = new() { Indexes = [0.5, 5], Reflections = [[0.7, 0.5, 0.3]], Values = [[0.4873], [0.4873]] }
};
var light = new RoomLight { Lux = 100, Maintenance = 1.3, WorkPlaneM = 0.8, MountM = 3.3, Luminaire = ars.Name };
var ki = Lighting.Calculate(room, light, ars);
Check(ki.Ok && Near(ki.Index, 0.99, 0.005) && Near(ki.Ki, 0.4873) && ki.Required == 2 && Near(ki.ERequired, 159.24, 0.05), $"nanoCAD Ki example: i={ki.Index:0.000} N={ki.Required} E={ki.ERequired:0.00}");
Check(ki.Recommended == 2 && ki.Rows * ki.Cols == 2, "nanoCAD recommended count");
Check(Near(Lighting.Calculate(room, light with { MountM = 0 }, ars).Index, ki.Index), "mount height defaults to room height");
var fixedCount = Lighting.Calculate(room, light with { FixedCount = 1 }, ars);
Check(fixedCount.Required == 1 && fixedCount.Recommended == 1 && !fixedCount.Ok && fixedCount.Problem.Contains("ниже нормы"), "fixed count below the norm is reported");
Check(!Lighting.Calculate(room, light with { MountM = 0.5 }, ars).Ok, "mount below work plane is reported");
Check(!Lighting.Calculate(room, light, new LuminaireItem { Name = "Без потока" }).Ok, "luminaire without flux is reported");
Console.WriteLine("PASS Ki method matches the nanoCAD BIM Electro example (2 luminaires, 159.24 lx, i = 0.99)");

Check(Lighting.Layout(8, 6, 3) == (2, 4), "office grid 2×4");
Check(Lighting.Layout(7, 6, 3) == (2, 4), "prime count rounds up to an even grid");
Check(Lighting.Layout(7, 6, 3, exact: true) == (1, 7), "exact count keeps 7");
Check(Lighting.Layout(4, 20, 2) == (1, 5), "corridor spacing limited to twice the width");
Check(Lighting.Layout(0, 6, 3) == (0, 0), "empty layout");
var grid = Lighting.Grid(0, 0, 6, 3, 8);
Check(grid.Count == 8 && grid.All(p => p.X > 0 && p.X < 6 && p.Y > 0 && p.Y < 3), "grid count and bounds");
Check(Near(grid[0].X, 6 / 4.0 / 2) && Near(grid[0].Y, 3 / 2.0 / 2), "grid half-step offset from walls");
Check(Lighting.Grid(0, 0, 6, 3, 2, 4).Count == 8 && Near(Lighting.Grid(0, 0, 6, 3, 2, 4)[^1].X, 6 - 0.75), "rows × cols grid");
Check(Lighting.Grid(0, 0, 1, 1, 0).Count == 0, "empty grid");
Console.WriteLine("PASS recommended grid layout and uniform grid with half-step wall offsets");

var prototypes = PrototypeLibrary.Builtin();
Check(prototypes.Match("Кабинет 101")?.Lux == 300 && prototypes.Match("Кабинет врача-терапевта")?.Lux == 500, "prototype by longest keyword");
Check(prototypes.Match("Коридор")?.Lux == 75 && prototypes.Match("Рабочая комната")?.Name.StartsWith("Кабинеты") == true && prototypes.Match("Санузел")?.Lux == 75, "prototype matches typical names");
Check(prototypes.Match("Помещение X") == null, "no prototype for unknown names");
Check(prototypes.Items.GroupBy(p => p.Group + "\\" + p.Name).All(g => g.Count() == 1) && prototypes.Items.All(p => p.Lux > 0), "builtin prototypes are unique");
var fromPrototype = light.From(prototypes.Match("Кабинет")!);
Check(fromPrototype.Lux == 300 && fromPrototype.Maintenance == 1.4 && fromPrototype.Luminaire == ars.Name && fromPrototype.MountM == 3.3, "prototype keeps luminaire and mount");
bool rejected = false;
try { (light with { Ceiling = 1.5 }).Validate(); } catch (InvalidOperationException) { rejected = true; }
Check(rejected, "invalid reflection rejected");
var stored = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, RoomLight>>(
    System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, RoomLight> { ["id"] = light with { Placed = ["a", "b"], FixedCount = 3 } }))!;
Check(stored["id"].Placed.Count == 2 && stored["id"].FixedCount == 3 && stored["id"].Lux == 100, "room light storage round-trip");
Console.WriteLine("PASS room prototypes, room light properties and storage");

Check(EomSettings.Mark(s.GroupMask, "ЩР1", "3") == "ЩР1.Гр3" && EomSettings.Mark(s.CableMask, " ЩО ", "12") == "ЩО-12", "marking masks");
var restored = EomSettings.FromJson((s with { CableReserve = 15, GroupMask = "Гр.{N}" }).ToJson());
Check(restored.CableReserve == 15 && restored.GroupMask == "Гр.{N}", "settings round-trip");
Check(EomSettings.FromJson("{bad").CableReserve == 10 && EomSettings.FromJson("""{"CableReserve":99}""").CableReserve == 10, "invalid settings fall back to defaults");
var legacyHeights = EomSettings.FromJson("""{"CableReserve":15}""");
Check(legacyHeights.CableReserve == 15 && legacyHeights.SwitchHeightMm == 1000
    && legacyHeights.ElectricalFixtureHeightMm == 300 && legacyHeights.ElectricalEquipmentHeightMm == 1500
    && legacyHeights.LightingFixtureHeightMm == 2700, "placement heights default in older project settings");
var customHeights = EomSettings.FromJson((legacyHeights with
{
    SwitchHeightMm = 1100, ElectricalFixtureHeightMm = 0, ElectricalEquipmentHeightMm = 1400,
    LightingFixtureHeightMm = 2600.5
}).ToJson());
Check(customHeights.SwitchHeightMm == 1100 && customHeights.ElectricalFixtureHeightMm == 0
    && customHeights.ElectricalEquipmentHeightMm == 1400 && customHeights.LightingFixtureHeightMm == 2600.5,
    "placement heights round-trip with zero and fractional offsets");
Check(EomSettings.FromJson("""{"SwitchHeightMm":-1}""").SwitchHeightMm == 1000, "negative placement height rejected");
Console.WriteLine("PASS marking masks and settings storage");

string file = Path.Combine(Path.GetTempPath(), "eom-catalog-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    catalog.Cables[0].AllowedCurrent = 18; catalog.Save(file);
    var loaded = EomCatalog.Load(file);
    Check(loaded.Cables.Count == catalog.Cables.Count && loaded.Cables[0].AllowedCurrent == 18 && loaded.Luminaires.Count == catalog.Luminaires.Count && loaded.Luminaires.Any(l => l.Efficiency < 1), "catalog round-trip");
    Check(EomCatalog.Load(file + ".missing").Cables.Count > 0, "missing catalog falls back to demo");
}
finally { File.Delete(file); }
Console.WriteLine("PASS catalog storage and demo fallback");

string libraryFile = Path.Combine(Path.GetTempPath(), "eom-rooms-" + Guid.NewGuid().ToString("N") + ".json");
string oldCatalog = Path.Combine(Path.GetTempPath(), "eom-catalog-old-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    prototypes.Items.Add(new() { Group = "Пользовательские", Name = "Серверная", Lux = 300, Keywords = "сервер" });
    prototypes.Save(libraryFile);
    var library = PrototypeLibrary.Load(libraryFile);
    Check(library.Items.Count == prototypes.Items.Count && library.Match("Серверная 2")?.Lux == 300, "prototype library round-trip");
    Check(PrototypeLibrary.Load(libraryFile + ".missing").Items.Count == PrototypeLibrary.Builtin().Items.Count, "missing library falls back to builtin");
    var custom = EomCatalog.Demo(); custom.Luminaires.Add(ars); custom.Save(oldCatalog);
    Check(EomCatalog.Load(oldCatalog).Luminaires.Last().Utilization?.IsValid == true, "luminaire Ki table round-trip");
    File.WriteAllText(oldCatalog, """{"Cables":[{"Cores":3,"Section":1.5,"AllowedCurrent":19,"Resistance":12.1}],"Luminaires":[{"Name":"Старый","Power":36,"Flux":4000,"Photometry":""}]}""");
    var old = EomCatalog.Load(oldCatalog).Luminaires.Single();
    Check(old.Efficiency == 1 && old.LampCount == 1 && old.Utilization == null && Lighting.Calculate(room, light, old).Ok, "catalog of the previous version loads with defaults");
}
finally { File.Delete(libraryFile); File.Delete(oldCatalog); }
Console.WriteLine("PASS prototype library and luminaire catalog storage, previous catalog format");

// Импорт «Прототипов комнат» nanoCAD: синтетический файл в формате roomPrototypes.prt.
string nanoFile = Path.Combine(Path.GetTempPath(), "eom-nano-" + Guid.NewGuid().ToString("N") + ".prt");
const string Public = "СП 52.13330.2016\\Основные помещения общественных, жилых и вспомогательных зданий";
const string Industrial = "СП 52.13330.2016\\Помещения и сооружения объектов общепромышленного назначения";
string Record(string name, string category, string lux, string extra = "") =>
    $"<ElRoomPrototypeRecord><PrototypeName>{name}</PrototypeName><Category>{category}</Category><EnvRoomCategory>Room_D</EnvRoomCategory>" +
    $"<EnvExplCategory>Expl_B1a</EnvExplCategory><EnvFireCategory>Fire_P2a</EnvFireCategory><EnvSurrounding>Moist</EnvSurrounding><EnvTemperature>18</EnvTemperature>" +
    $"<EnvLightLookCategory>А-2</EnvLightLookCategory><EnvLumLl>{lux}</EnvLumLl><WorkSurfaceHeight>0,8</WorkSurfaceHeight><SafeCoefK>1,4</SafeCoefK>" +
    $"<CeilReflection>0,7</CeilReflection><WallReflection>0,5</WallReflection><FloorReflection>0,3</FloorReflection>{extra}</ElRoomPrototypeRecord>";
try
{
    File.WriteAllText(nanoFile, "<Table>" +
        Record("60 Спальни", Public + "\\05. Дошкольные образовательные организации (ДОО) (для детей младше 7 лет)", "150") +
        Record("1 Кабинеты и рабочие комнаты, офисы, представительства", Public + "\\01. Административные здания, здания государственных учреждений\\\\", "300") +
        Record("199 а) главные коридоры и проходы", Public + "\\16. Общие помещения зданий\\199 Коридоры и проходы, пути движения", "100") +
        Record("- с измерительной аппаратурой", Industrial + "\\04. Электропомещения\\29 Пульты, щиты управления\\а) внутри зданий", "300") +
        Record("36 Электрощитовые в жилых и общественных зданиях", Industrial + "\\04. Электропомещения", "200") +
        Record("1 Освещение на лице у зеркала", "СП 31-110-2003\\Помещения культурно-зрелищных учреждений\\Артистические, гримерные", "500") + "</Table>");
    var read = NanoCadPrototypes.Read(nanoFile);
    Check(read.Count == 6 && read[0].Lux == 150 && read[0].Maintenance == 1.4 && read[0].Floor == 0.3, "nanoCAD records with comma decimals");
    Check(read[0].Environment == "Влажная" && read[0].RoomCategory == "Д" && read[0].ExplosionZone == "В-Iа" && read[0].FireZone == "П-IIа" && read[0].Temperature == 18 && read[0].VisualCategory == "А-2", "nanoCAD environment enums");
    Check(read[0].Label == "СП 52.13330.2016 05 60 Спальни", $"label with norm, section and item: {read[0].Label}");
    Check(read[1].Label == "СП 52.13330.2016 01 1 Кабинеты и рабочие комнаты, офисы, представительства" && !read[1].Group.EndsWith('\\'), "trailing separator removed");
    Check(read[3].Label == "СП 52.13330.2016 04 29 Пульты, щиты управления а) внутри зданий - с измерительной аппаратурой", $"sub-item label: {read[3].Label}");
    Check(read[5].Label == "СП 31-110-2003 1 Освещение на лице у зеркала", $"norm without sections: {read[5].Label}");
    var imported = PrototypeLibrary.Builtin();
    imported.Items.Add(new() { Group = PrototypeLibrary.UserGroup, Name = "Серверная", Lux = 300, Keywords = "сервер" });
    Check(imported.Import(nanoFile) == 6 && imported.Source == nanoFile && imported.Items.Count(i => i.Group == PrototypeLibrary.UserGroup) == 2, "import keeps user prototypes and drops the builtin set");
    Check(imported.Match("Спальня 3") == null, "no auto match when the rule target is absent");
    Check(imported.Match("Кабинет 12")?.Lux == 300 && imported.Match("Электрощитовая")?.Lux == 200 && imported.Match("Коридор")?.Lux == 100 && imported.Match("Серверная")?.Lux == 300, "auto match on imported items");
    Check(new RoomLight().From(read[0]).Prototype == "СП 52.13330.2016 05 60 Спальни" && new RoomLight().From(read[0]).ExplosionZone == "В-Iа", "room keeps prototype label and environment");
    imported.Save(nanoFile + ".json");
    var reloaded = PrototypeLibrary.Load(nanoFile + ".json");
    Check(reloaded.Source == nanoFile && reloaded.Items.Count == imported.Items.Count && reloaded.Items[0].Label == read[0].Label, "imported library round-trip");
}
finally { File.Delete(nanoFile); File.Delete(nanoFile + ".json"); }
Console.WriteLine("PASS nanoCAD room prototype import, labels «СП 52.13330.2016 05 60 Спальни», environment and auto match");

// Настоящая библиотека nanoCAD, если она установлена на этом компьютере (не входит в репозиторий и установщик).
if (NanoCadPrototypes.Find() is { } installed)
{
    var all = NanoCadPrototypes.Read(installed);
    var sp52 = all.Where(p => p.Norm == "СП 52.13330.2016").ToList();
    var missingRules = AutoMatch.Apply(all);
    Check(all.Count >= 700 && sp52.Count >= 360 && all.All(p => p.Lux > 0), $"installed nanoCAD library: {all.Count} prototypes, СП 52: {sp52.Count}");
    Check(missingRules.Count == 0, "every auto-match rule resolves: " + string.Join(", ", missingRules));
    Check(sp52.Any(p => p.Label == "СП 52.13330.2016 05 60 Спальные"), "bedroom label in the installed library");
    var lib = new PrototypeLibrary(); lib.Import(installed);
    foreach (var (roomName, expected) in new[] { ("Спальня", "190 "), ("Кухня", "191 "), ("Кабинет", "1 "), ("Электрощитовая", "36 "), ("Коридор", "199 а)"), ("Санузел", "194 а)"), ("Лестничная клетка", "197 в)"), ("Венткамера", "47 а)") })
        Check(lib.Match(roomName)?.Name.StartsWith(expected) == true, $"{roomName} → {lib.Match(roomName)?.Label}");
    Console.WriteLine($"PASS installed nanoCAD library: {all.Count} prototypes, СП 52.13330.2016: {sp52.Count}, all auto-match rules resolve");
}
else Console.WriteLine("SKIP installed nanoCAD library: nanoCAD BIM Электро not found");
