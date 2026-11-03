using System.IO;
using System.Text.Json;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using HeatLossViewer;
using HeatLossViewer.Export;

namespace RevitIntegration;

public static class OvLibraryValidation
{
    public static object Run(UIApplication ui, string output, bool showWindow)
    {
        var checks = new List<string>();
        void Check(bool pass, string label) { if (!pass) throw new InvalidOperationException(label); checks.Add(label); File.WriteAllText(Path.Combine(output, "library-progress.txt"), string.Join("\n", checks)); }
        string path = Path.Combine(output, "library-fixture.rvt"); if (File.Exists(path)) throw new IOException("Fixture already exists");
        var doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
        string materialKey = "", secondKey = "", wallKey = "", roomKey = "";
        double F(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Meters);
        XYZ P(double x, double y, double z = 0) => new(F(x), F(y), F(z));
        try
        {
            using (var t = new Transaction(doc, "Library validation fixture"))
            {
                t.Start(); AreaVolumeSettings.GetAreaVolumeSettings(doc).ComputeVolumes = true;
                var material = (Material)doc.GetElement(Material.Create(doc, "ОВ — проверка λ"));
                var second = (Material)doc.GetElement(Material.Create(doc, "ОВ — общий тепловой ресурс"));
                using var asset = new ThermalAsset("ОВ — исходный общий ресурс", ThermalMaterialType.Solid)
                {
                    ThermalConductivity = UnitUtils.ConvertToInternalUnits(.4, UnitTypeId.WattsPerMeterKelvin),
                    Density = UnitUtils.ConvertToInternalUnits(1000, UnitTypeId.KilogramsPerCubicMeter),
                    SpecificHeat = UnitUtils.ConvertToInternalUnits(900, UnitTypeId.JoulesPerKilogramDegreeCelsius)
                };
                var property = PropertySetElement.Create(doc, asset);
                material.SetMaterialAspectByPropertySet(MaterialAspect.Thermal, property.Id); second.SetMaterialAspectByPropertySet(MaterialAspect.Thermal, property.Id);
                var basic = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(w => w.Kind == WallKind.Basic);
                var wall = (WallType)basic.Duplicate("ОВ — стена проверки");
                using var structure = CompoundStructure.CreateSimpleCompoundStructure([new CompoundStructureLayer(F(.2), MaterialFunctionAssignment.Structure, material.Id)]); wall.SetCompoundStructure(structure); wall.Function = WallFunction.Exterior;
                var secondWall = (WallType)basic.Duplicate("ОВ — второй тип проверки");
                using var structureB = CompoundStructure.CreateSimpleCompoundStructure([new CompoundStructureLayer(F(.2), MaterialFunctionAssignment.Structure, second.Id)]); secondWall.SetCompoundStructure(structureB);
                var level = Level.Create(doc, 0); level.Name = "ОВ — первый этаж";
                foreach (var (a,b) in new[] { (P(0,0),P(6,0)), (P(6,0),P(6,5)), (P(6,5),P(0,5)), (P(0,5),P(0,0)) }) Wall.Create(doc, Line.CreateBound(a,b), wall.Id, level.Id, F(3), 0, false, false);
                var floorBase = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First();
                var floor = (FloorType)floorBase.Duplicate("ОВ — пол проверки");
                using var floorStructure = floor.GetCompoundStructure(); floorStructure.SetLayers([new CompoundStructureLayer(F(.2), MaterialFunctionAssignment.Structure, material.Id)]); floor.SetCompoundStructure(floorStructure);
                var points = new[] { P(0,0),P(6,0),P(6,5),P(0,5) }; var loop = CurveLoop.Create(points.Select((p,i)=>(Curve)Line.CreateBound(p,points[(i+1)%4])).ToList());
                Floor.Create(doc, [loop], floor.Id, level.Id);
                foreach (var (category,name) in new[] { (BuiltInCategory.OST_Windows,"ОВ — окно проверки"), (BuiltInCategory.OST_Doors,"ОВ — дверь проверки") })
                {
                    var type = DirectShapeType.Create(doc, name, new ElementId(category));
                    var shape = DirectShape.CreateElement(doc, new ElementId(category)); shape.SetTypeId(type.Id); shape.Name = name;
                    var ring = CurveLoop.Create(new[] { P(1,-.1,1),P(2,-.1,1),P(2,.1,1),P(1,.1,1) }.Select((p,i)=>(Curve)Line.CreateBound(p,new[]{P(1,-.1,1),P(2,-.1,1),P(2,.1,1),P(1,.1,1)}[(i+1)%4])).ToList());
                    shape.SetShape([GeometryCreationUtilities.CreateExtrusionGeometry([ring], XYZ.BasisZ, F(1))]);
                }
                doc.Regenerate(); var room = doc.Create.NewRoom(level, new UV(F(3),F(2))); room.Name = "Кабинет проверки";
                materialKey = "host/" + material.UniqueId; secondKey = "host/" + second.UniqueId; wallKey = "host/" + wall.UniqueId; roomKey = "host/" + room.UniqueId;
                t.Commit();
            }
            var before = RevitOvLibrary.Read(doc);
            Check(Math.Abs(before.Materials.Single(m=>m.Key==materialKey).Lambda!.Value-.4)<1e-9,"read native thermal asset in SI");
            Check(new[]{"wall","floor","window","door"}.All(kind=>before.Constructions.Any(c=>c.Kind==kind)),"all construction categories are populated from Revit");
            var draft = JsonSerializer.Deserialize<OvLibraryData>(JsonSerializer.Serialize(before))!;
            var m = draft.Materials.Single(m=>m.Key==materialKey); m.Lambda=.6;m.Density=1200;m.SpecificHeat=950;
            var w = draft.Constructions.Single(c=>c.Key==wallKey);w.UseInCalculation=true;w.Homogeneity=.85;w.N=.9;
            var floorRow = draft.Constructions.Single(c=>c.Name=="ОВ — пол проверки");floorRow.GroundFloor=true;floorRow.UseInCalculation=true;
            foreach(var type in draft.Constructions.Where(c=>c.Kind is "window" or "door")){type.UseInCalculation=true;type.UseLayers=false;type.R0=.7;type.Gn=2;}
            draft.Radiators.Settings=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>("{\"supply\":75,\"return\":55,\"roomOverrides\":{\""+roomKey+"\":{\"targetWatts\":1200,\"accessReviewed\":true}}}")!;
            using(var t=new Transaction(doc,"Write library")){t.Start();RevitOvLibrary.Write(doc,draft,before);t.Commit();}
            var after=RevitOvLibrary.Read(doc);
            Check(Math.Abs(after.Materials.Single(m=>m.Key==materialKey).Lambda!.Value-.6)<1e-9,"material conductivity writes to native asset");
            Check(Math.Abs(after.Materials.Single(m=>m.Key==secondKey).Lambda!.Value-.4)<1e-9,"editing a shared asset does not change another material");
            var rollback=JsonSerializer.Deserialize<OvLibraryData>(JsonSerializer.Serialize(after))!;rollback.Materials.Single(m=>m.Key==materialKey).Lambda=.9;
            using(var t=new Transaction(doc,"Rollback library")){t.Start();RevitOvLibrary.Write(doc,rollback,after);t.RollBack();}
            Check(Math.Abs(RevitOvLibrary.Read(doc).Materials.Single(m=>m.Key==materialKey).Lambda!.Value-.6)<1e-9,"rollback restores native properties and metadata");
            doc.SaveAs(path,new SaveAsOptions());
        }
        finally{doc.Close(false);}
        doc=ui.Application.OpenDocumentFile(path);
        try
        {
            var saved=RevitOvLibrary.Read(doc);
            Check(saved.Constructions.Single(c=>c.Key==wallKey).N==.9 && saved.Constructions.Single(c=>c.Key==wallKey).Homogeneity==.85,"construction settings survive RVT reopen");
            Check(saved.Constructions.Any(c=>c.GroundFloor),"ground floor classification survives RVT reopen");
            Check(saved.Radiators.Settings["supply"].GetInt32()==75,"radiator settings survive RVT reopen");
            var building=new RevitRoomExtractor().Extract(doc);building.NativeRadiators=true;
            Check(building.Rooms.Count==1 && building.Rooms.SelectMany(r=>r.Surfaces).Any(s=>s.TypeKey==wallKey),"export connects room surfaces to Revit type identity");
            new BuildingExportWriter().Write(building,Path.Combine(output,"export"));
        }
        finally{doc.Close(false);}
        if(showWindow)
        {
            var active=ui.OpenAndActivateDocument(path).Document;
            var original=RevitOvLibrary.Read(active);var draft=JsonSerializer.Deserialize<OvLibraryData>(JsonSerializer.Serialize(original))!;
            var building=new RevitRoomExtractor().Extract(active);building.OvLibrary=draft;
            var window=new OvLibraryWindow(draft,building,true);new WindowInteropHelper(window).Owner=ui.MainWindowHandle;
            File.WriteAllText(Path.Combine(output,"window-open.txt"),"ready for UI test");
            if(window.ShowDialog()==true)
            {
                using(var t=new Transaction(active,"Save native radiator UI")){t.Start();RevitOvLibrary.Write(active,draft,original);t.Commit();}active.Save();
                Check(RevitOvLibrary.Read(active).Radiators.Settings["supply"].GetInt32()==68,"native radiator tab saves edited settings to Revit");
                File.WriteAllText(Path.Combine(output,"native-saved.json"),JsonSerializer.Serialize(RevitOvLibrary.Read(active),BuildingExportWriter.JsonOptions));
            }
            else throw new InvalidOperationException("Native UI validation was cancelled");
        }
        return new{status="passed",checks,fixture=path};
    }
}
