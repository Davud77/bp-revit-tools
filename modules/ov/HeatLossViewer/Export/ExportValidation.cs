using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using System.Text.Json;
using System.IO;

namespace HeatLossViewer.Export;

/// <summary>Opt-in local checks in disposable projects and read-only source documents.</summary>
public static class ExportValidation
{
    public sealed class Config
    {
        public string Template {get;set;}="";
        public List<string> Files {get;set;}=[];
    }
    public static object Run(UIApplication ui,string configPath)
    {
        var config=JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
        string output=Path.GetDirectoryName(configPath)!;var reports=new List<object>();
        string fixture=Path.Combine(output,"opening-fixture.rvt");
        if(File.Exists(fixture))throw new IOException("Opening fixture already exists");
        var doc=ui.Application.NewProjectDocument(config.Template);
        try
        {
            var schema=Schema.Lookup(SourceOpeningMetadata.SchemaId);
            if(schema==null)
            {
                var builder=new SchemaBuilder(SourceOpeningMetadata.SchemaId);builder.SetSchemaName("Ac2RevitSourceV1");builder.SetReadAccessLevel(AccessLevel.Public);builder.SetWriteAccessLevel(AccessLevel.Public);
                foreach(var name in new[]{"ProjectId","SourceKey","SourceHash","RawJson"})builder.AddSimpleField(name,typeof(string));schema=builder.Finish();
            }
            string project=Guid.NewGuid().ToString();
            void Tag(Element e,string key,object raw)
            {
                var entity=new Entity(schema);entity.Set(schema.GetField("ProjectId"),project);entity.Set(schema.GetField("SourceKey"),key);entity.Set(schema.GetField("SourceHash"),"fixture");entity.Set(schema.GetField("RawJson"),JsonSerializer.Serialize(raw));e.SetEntity(entity);
            }
            double F(double m)=>UnitUtils.ConvertToInternalUnits(m,UnitTypeId.Meters);
            XYZ P(double x,double y,double z=0)=>new(F(x),F(y),F(z));
            using(var t=new Transaction(doc,"OV opening export fixture"))
            {
                t.Start();AreaVolumeSettings.GetAreaVolumeSettings(doc).ComputeVolumes=true;
                var level=Level.Create(doc,0);level.Name="Экспорт — проверка";
                var type=new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(w=>w.Kind==WallKind.Basic);
                var walls=new List<Wall>();
                Wall W(double x1,double y1,double x2,double y2)
                {var wall=Wall.Create(doc,Line.CreateBound(P(x1,y1),P(x2,y2)),type.Id,level.Id,F(3),0,false,false);walls.Add(wall);return wall;}
                var south=W(0,0,6,0);W(6,0,12,0);var east=W(12,0,12,6);W(12,6,6,6);var north=W(6,6,0,6);W(0,6,0,0);W(6,0,6,6);doc.Regenerate();
                doc.Create.NewOpening(south,P(1,0),P(2,0,2));doc.Create.NewOpening(south,P(3,0,.8),P(4,0,2.1));
                var hostKeys=walls.ToDictionary(w=>w.Id.Value,_=>Guid.NewGuid().ToString().ToUpperInvariant());
                foreach(var wall in walls)Tag(wall,hostKeys[wall.Id.Value],new{kind="Wall"});
                void Filling(Wall? host,string kind,double x,double y,double width,double depth,double z,double height)
                {
                    string guid=Guid.NewGuid().ToString().ToUpperInvariant();var points=new[]{P(x,y,z),P(x+width,y,z),P(x+width,y+depth,z),P(x,y+depth,z)};
                    var loop=CurveLoop.Create(points.Select((p,i)=>(Curve)Line.CreateBound(p,points[(i+1)%4])).ToList());
                    var solid=GeometryCreationUtilities.CreateExtrusionGeometry([loop],XYZ.BasisZ,F(height));
                    var shape=DirectShape.CreateElement(doc,new ElementId(kind=="Door"?BuiltInCategory.OST_Doors:BuiltInCategory.OST_Windows));shape.SetShape([solid]);shape.Name="АС «"+kind+"»";
                    string hostGuid=host==null?"":hostKeys[host.Id.Value];Tag(shape,guid,new{sourceGuid=guid,kind,hostGuid});
                    if(host!=null)
                    {
                        var cut=host==east?doc.Create.NewOpening(host,P(12,y,z),P(12,y+depth,z+height)):doc.Create.NewOpening(host,P(x,6,z),P(x+width,6,z+height));
                        Tag(cut,"opening:"+guid,new{sourceGuid=guid,kind,hostGuid});
                    }
                }
                Filling(east,"Door",11.9,1,.2,1,0,2);
                Filling(north,"Window",4,5.9,1,.2,.8,1.3);
                Filling(null,"Window",30,30,1,.2,1,1.3);
                doc.Regenerate();foreach(double x in new[]{3d,9d})
                {
                    var room=doc.Create.NewRoom(level,new UV(F(x),F(3)));room.get_Parameter(BuiltInParameter.ROOM_UPPER_LEVEL).Set(level.Id);room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET).Set(F(3));
                }
                t.Commit();
            }
            doc.SaveAs(fixture,new SaveAsOptions{MaximumBackups=1});
            var data=new RevitRoomExtractor().Extract(doc);new BuildingExportWriter().Write(data,Path.Combine(output,"fixture-export"));
            if(data.Rooms.Count!=2 || data.Openings.Count!=5 || data.Openings.Count(o=>o.IsEmpty)!=2 || data.Openings.Count(o=>o.Kind=="door")!=2 || data.Openings.Count(o=>o.RoomKeys.Count==0)!=1)
                throw new InvalidDataException($"Opening fixture mismatch: rooms={data.Rooms.Count}, openings={data.Openings.Count}, empty={data.Openings.Count(o=>o.IsEmpty)}, unassociated={data.Openings.Count(o=>o.RoomKeys.Count==0)}");
            reports.Add(new{fixture,rooms=data.Rooms.Count,openings=data.Openings.Count,doors=data.Openings.Count(o=>o.Kind=="door"),windows=data.Openings.Count(o=>o.Kind=="window"),empty=data.Openings.Count(o=>o.IsEmpty),unassociated=data.Openings.Count(o=>o.RoomKeys.Count==0),status="verified"});
        }
        finally{doc.Close(false);}
        foreach(var file in config.Files)
        {
            Document? source=null;
            try
            {
                source=ui.Application.OpenDocumentFile(file);var data=new RevitRoomExtractor().Extract(source);
                var categories=new ElementMulticategoryFilter(new[]{BuiltInCategory.OST_Doors,BuiltInCategory.OST_Windows});
                var expected=new FilteredElementCollector(source).WherePasses(categories).WhereElementIsNotElementType().ToList();
                var missing=expected.Where(e=>!data.Openings.Any(o=>o.Key=="host/"+e.UniqueId)).Select(e=>e.Id.Value.ToString()).ToList();
                var files=new BuildingExportWriter().Write(data,Path.Combine(output,Path.GetFileNameWithoutExtension(file)));
                reports.Add(new{file,expectedCategoryElements=expected.Count,exportedOpenings=data.Openings.Count,rooms=data.Rooms.Count,missingCategoryElements=missing,
                    empty=data.Openings.Count(o=>o.IsEmpty),unassociated=data.Openings.Count(o=>o.RoomKeys.Count==0),data.OpeningCoverage,warnings=data.Warnings,files.Html});
                if(missing.Count>0)throw new InvalidDataException("Category openings missing from export; see validation report");
            }
            finally{source?.Close(false);File.WriteAllText(Path.Combine(output,"export-validation.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));}
        }
        File.WriteAllText(Path.Combine(output,"export-validation.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
        return new{status="verified",reports};
    }
}
