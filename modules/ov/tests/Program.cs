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
var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/demo");
var paths = new BuildingExportWriter().Write(building,output);
using var doc = JsonDocument.Parse(File.ReadAllText(paths.Json));
Check(doc.RootElement.GetProperty("rooms")[0].GetProperty("id").GetInt64() == long.MaxValue, "JSON retains 64-bit ElementId");
Check(doc.RootElement.GetProperty("rooms").GetArrayLength() == 24, "JSON contract and room count");
var html = File.ReadAllText(paths.Html);
Check(!html.Contains("<script>window.injected"), "room strings cannot escape JSON script");
Check(!html.Contains("__BUILDING_JSON__") && !html.Contains("__VIEWER_SCRIPT__"), "HTML embeds data and script");
Console.WriteLine(paths.Html);
