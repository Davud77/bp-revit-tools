using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>Isolated source-derived Mesh and zone vertical-constraint checks.</summary>
public static class RoadmapValidation
{
    public static object Run(UIApplication ui,string fixture,string template,string output)
    {
        if(File.Exists(output))throw new IOException("Validation output exists");
        var source=Manifest.Load(fixture);var doc=ui.Application.NewProjectDocument(template);
        try
        {
            var config=new ImportConfig{ConnectWalls=false,RoomAreaToleranceSquareMeters=.0001};
            new Importer(doc,source,config).Run(new HashSet<string>(),true,false);
            var index=Storage.Index(doc,source.Source.ProjectId);
            using(var t=new Transaction(doc,"Isolated room boundaries and volume calculation"))
            {
                t.Start();AreaVolumeSettings.GetAreaVolumeSettings(doc).ComputeVolumes=true;
                foreach(var record in source.Elements.Where(e=>e.Kind=="Zone"))
                {
                    var level=(Level)index[record.LevelKey];var curves=new CurveArray();
                    var g=new Geometry(doc,source.Coordinates.Transform);
                    foreach(var loop in g.Loops(record.Payload.GetProperty("loops").Read<List<List<Edge>>>(),level.Elevation))foreach(var curve in loop)curves.Append(curve);
                    var plane=SketchPlane.Create(doc,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,new XYZ(0,0,level.Elevation)));
                    var view=new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v=>!v.IsTemplate && v.GenLevel?.Id==level.Id);
                    if(view==null)
                    {
                        var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.FloorPlan);
                        view=ViewPlan.Create(doc,type.Id,level.Id);
                    }
                    doc.Create.NewRoomBoundaryLines(plane,curves,view);
                }
                t.Commit();
            }
            var report=new Importer(doc,source,config).Run(new HashSet<string>{"Mesh","Zone"},false,false);
            report.Save(Path.ChangeExtension(output,"import.json"));
            if(report.Items.Any(i=>i.Status is "blocked" or "failed" or "conflict" or "revitError"))throw new InvalidDataException("Roadmap fixture contains blocked/failed imports; see import report");
            index=Storage.Index(doc,source.Source.ProjectId);var volumes=new List<object>();
            foreach(var e in source.Elements.Where(e=>e.Kind=="Zone"))
            {
                var room=(Room)index[e.SourceGuid];var p=e.Payload;
                var baseLevel=(Level)index[e.LevelKey];var upper=(Level)index[p.S("upperLevelKey")];
                double height=UnitUtils.ConvertFromInternalUnits(room.UnboundedHeight,UnitTypeId.Meters);
                double volume=UnitUtils.ConvertFromInternalUnits(room.Volume,UnitTypeId.CubicMeters);
                double area=UnitUtils.ConvertFromInternalUnits(room.Area,UnitTypeId.SquareMeters);
                if(room.LevelId!=baseLevel.Id || room.get_Parameter(BuiltInParameter.ROOM_UPPER_LEVEL).AsElementId()!=upper.Id ||
                    Math.Abs(UnitUtils.ConvertFromInternalUnits(room.BaseOffset,UnitTypeId.Meters)-p.D("baseOffset"))>1e-6 ||
                    Math.Abs(height-p.D("height"))>1e-6 || Math.Abs(volume-area*p.D("height"))>1e-4)
                    throw new InvalidDataException("Room constraints/volume mismatch: "+e.SourceGuid);
                volumes.Add(new{e.SourceGuid,height,area,volume,baseOffset=p.D("baseOffset"),upper=e.Payload.S("upperLevelKey"),upperOffset=p.D("upperOffset")});
            }
            int before=new FilteredElementCollector(doc).OfClass(typeof(Toposolid)).GetElementCount();
            var repeat=new Importer(doc,source,config).Run(new HashSet<string>{"Mesh","Zone"},false,false);
            if(new FilteredElementCollector(doc).OfClass(typeof(Toposolid)).GetElementCount()!=before || repeat.Items.Any(i=>i.Status is "created" or "failed" or "conflict"))throw new InvalidDataException("Repeat fixture import duplicated source elements");
            ImportJournal.Register(doc,source,null);
            doc.SaveAs(output,new SaveAsOptions{MaximumBackups=1});
            if(!File.Exists(Path.ChangeExtension(output,"botplus-import.log")))throw new InvalidDataException("Journal missing after SaveAs");
            return new{status="verified",nativeToposolids=before,meshes=source.Elements.Count(e=>e.Kind=="Mesh"),zones=volumes,repeatWithoutDuplicates=true,output};
        }
        finally{doc.Close(false);}
    }
}
