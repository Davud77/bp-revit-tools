using System.Text.Json;
using HeatLossViewer;
using HeatLossViewer.Export;

if(args.Length==2 && args[0]=="--render")
{
    var source=Path.GetFullPath(args[1]);
    var buildingData=JsonSerializer.Deserialize<BuildingData>(File.ReadAllText(source),BuildingExportWriter.JsonOptions)!;
    Console.WriteLine(new BuildingExportWriter().Write(buildingData,Path.GetDirectoryName(source)!).Html);
    return;
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
static List<double[]> Rect(double x, double y, double w, double h) => [[x,y],[x+w,y],[x+w,y+h],[x,y+h]];
var scope = new BuildingData { Rooms = [new() {Key="room"}], Openings = [
    new(){Key="keep",Kind="window",RoomKeys=["room","not-exported"]},
    new(){Key="empty",Kind="window",RoomKeys=[]},
    new(){Key="outside",Kind="door",RoomKeys=["not-exported"]}
] };
Check(OvExportScope.ExcludeUnboundOpenings(scope)==2 && scope.Openings.Single().Key=="keep","OV excludes only windows/doors without exported calculation rooms");
Check(OvExportScope.ExcludeUnboundOpenings(scope)==0 && scope.Warnings.Count==1,"OV opening exclusion is idempotent and retains its audit warning");
var library = new OvLibraryData {
    Materials = [new() {Key="host/material",Lambda=.5,Density=1200,SpecificHeat=950}],
    Constructions = [new() {Key="host/type",UseInCalculation=true,UseLayers=true,Homogeneity=.8,Layers=[new(){MaterialKey="host/material",Thickness=.2}]}],
    Radiators = new() { Settings=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>("{\"supply\":68,\"roomOverrides\":{\"host/room\":{\"targetWatts\":1200}}}")! }
};
library.Validate();
var libraryCopy=JsonSerializer.Deserialize<OvLibraryData>(JsonSerializer.Serialize(library))!;
Check(Math.Abs(libraryCopy.Resistance(libraryCopy.Constructions[0])!.Value-.8*(.115+.4+.043))<1e-10 && libraryCopy.Radiators.Settings["supply"].GetInt32()==68,"OV library SI resistance and radiator model state round-trip");
libraryCopy.Materials[0].Lambda=null;
Check(libraryCopy.Resistance(libraryCopy.Constructions[0]) is null,"missing material lambda does not invent construction resistance");
libraryCopy.Materials[0].Lambda=-1;
bool invalidLibrary=false;try{libraryCopy.Validate();}catch(ArgumentException){invalidLibrary=true;}
Check(invalidLibrary,"invalid library material properties rejected");
var normalHtml=HtmlTemplate.GetHtml(JsonSerializer.Serialize(new BuildingData {OvLibrary=library},BuildingExportWriter.JsonOptions));
var nativeHtml=HtmlTemplate.GetHtml(JsonSerializer.Serialize(new BuildingData {OvLibrary=library,NativeRadiators=true},BuildingExportWriter.JsonOptions));
Check(!normalHtml.Contains("id=\"radiator-form\"") && normalHtml.Contains("id=\"radiator-room-preview\"") && nativeHtml.Contains("id=\"radiator-form\""),"radiator editing moves to native Revit mode; exported HTML keeps result preview");
new ProjectConditions().Validate();
new ProjectConditions { OutdoorTemperature = -23.5, WindSpeed = 0, HeatingPeriodDays = 366 }.Validate();
foreach (var bad in new[] { new ProjectConditions { WindSpeed = -1 }, new ProjectConditions { OutdoorTemperature = double.NaN }, new ProjectConditions { HeatingPeriodDays = 367 }, new ProjectConditions { IndoorTemperature = -274 } })
{
    bool rejected = false;
    try { bad.Validate(); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "invalid project climate rejected");
}
var projectConditions = new ProjectConditions { Name = "ОВ тест", OutdoorTemperature = -23.5, WindSpeed = 0, Notes = "</script><script>window.injected=true</script>" };
projectConditions.Calculation.Building["wall"] = .34;
projectConditions.Calculation.FloorOverrides["level"] = new() { ["indoor"] = 22 };
var climateJson = JsonSerializer.Serialize(new BuildingData { ProjectConditions = projectConditions }, BuildingExportWriter.JsonOptions);
var climateRoundtrip = JsonSerializer.Deserialize<BuildingData>(climateJson, BuildingExportWriter.JsonOptions)!;
Check(climateRoundtrip.ProjectConditions!.OutdoorTemperature == -23.5 && climateRoundtrip.ProjectConditions.WindSpeed == 0 && climateRoundtrip.ProjectConditions.IndoorTemperature is null, "project climate round-trip keeps zero and unspecified values");
Check(climateRoundtrip.ProjectConditions.Calculation.Building["wall"] == .34 && climateRoundtrip.ProjectConditions.Calculation.FloorOverrides["level"]["indoor"] == 22, "project calculation inputs round-trip");
Check(!HtmlTemplate.GetHtml(climateJson).Contains("<script>window.injected"), "project climate text cannot escape HTML data");
var outer = Rect(0,0,10,10); var hole = Rect(2,2,6,6); var island = Rect(3,3,1,1); var separate = Rect(20,0,2,2);
hole.Reverse(); outer.Reverse();
var polygons = ContourTopology.Build([hole, separate, island, outer]);
Check(polygons.Count == 3 && polygons[0].Holes.Count == 1, "unordered and reversed loops, holes, islands, disjoint regions");
Check(Math.Abs(ContourTopology.SignedArea(Rect(1e8,1e8,2,3))) == 6, "large coordinate precision");
Check(ContourTopology.Build([[[0,0],[1,0],[2,0]]]).Count == 0, "degenerate contour rejected");
var building = new BuildingData { ProjectName = "Учебное здание · геометрия" };
for (int floor = 0; floor < 3; floor++)
for (int i = 0; i < 8; i++)
{
    var ring = Rect((i%4)*6, (i/4)*7, 5.8, 6.8);
    building.Rooms.Add(new RoomData {
        Id = i == 0 ? long.MaxValue : 1000+i, Key = $"link-{floor}/room-{i}",
        SourceDocument = "АР · пример", SourceInstance = $"АР · экземпляр {floor}",
        Number = $"{floor+1}{i+1:00}", Name = i == 0 ? "Холл <тест> </script><script>window.injected=true</script>" : i % 3 == 0 ? "Кабинет" : "Жилая комната",
        LevelName = floor == 1 ? "Этаж 1" : $"Этаж {floor+1}", LevelKey = $"level-{floor}",
        Elevation = floor*3.4-3.4, BaseElevation = floor*3.4-3.4,
        Area = 39.44-i*.1, Volume = i == 7 ? 0 : (39.44-i*.1)*3, Height = 3,
        HeightSource = i == 7 ? "unboundedHeight" : "volume/area", Contour = ring,
        Polygons = [new RoomPolygon { Contour = ring, Holes = i == 0 ? [Rect(.8,.8,1,1)] : [] }], BoundariesCount = i == 0 ? 8 : 4,
        EnvelopeStatus = "beta", Surfaces = [new SurfaceData { Key = $"surface-{floor}-{i}", HostKey = $"wall-{floor}-{i}", Kind = "wall", Exposure = "outside", GrossArea = 5.8*3, TypeName = "Тестовая стена", ElementId = "1200", ClassificationSource = "demo-test" }]
    });
    building.Openings.Add(new OpeningData { Key = $"window-{floor}-{i}", IdText = "2200", HostKey = $"wall-{floor}-{i}", Kind = "window", Name = "Демо-окно", RoomKeys = [$"link-{floor}/room-{i}"], Position = [(i%4)*6+2.9,(i/4)*7,floor*3.4-3.4+1.6], Tangent = [1,0,0], Width = 1.2, Height = 1.5, IsExterior = true });
}
building.Warnings.Add("Демонстрационные данные. Для реальной модели выполните экспорт из Revit.");
var output = Path.GetFullPath(args.FirstOrDefault() ?? "../../Выходные данные/Проверки ОВ/demo");
var paths = new BuildingExportWriter().Write(building,output);
using var doc = JsonDocument.Parse(File.ReadAllText(paths.Json));
Check(doc.RootElement.GetProperty("rooms")[0].GetProperty("id").GetInt64() == long.MaxValue, "JSON retains 64-bit ElementId");
Check(doc.RootElement.GetProperty("rooms").GetArrayLength() == 24, "JSON contract and room count");
var html = File.ReadAllText(paths.Html);
Check(!html.Contains("<script>window.injected"), "room strings cannot escape JSON script");
Check(!html.Contains("__BUILDING_JSON__") && !html.Contains("__VIEWER_SCRIPT__"), "HTML embeds data and script");
Console.WriteLine(paths.Html);
