using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>Fixed isolated annotation fixtures. Does not modify the user's open document.</summary>
public static class AnnotationValidation
{
    public static object Run(UIApplication ui,string folder,string template)
    {
        Directory.CreateDirectory(folder);var doc=ui.Application.NewProjectDocument(template);string output=Path.Combine(folder,"Марки и нативные размеры.rvt");
        void CloseOwned()
        {
            if(string.Equals(ui.ActiveUIDocument?.Document.PathName,doc.PathName,StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrEmpty(doc.PathName))
            {
                string parking=Path.Combine(folder,"Проверочный пустой проект.rvt");
                if(!File.Exists(parking)){var empty=ui.Application.NewProjectDocument(UnitSystem.Metric);empty.SaveAs(parking,new SaveAsOptions{MaximumBackups=1});empty.Close(false);}
                ui.OpenAndActivateDocument(parking);
            }
            doc.Close(false);
        }
        try
        {
            string project=Guid.NewGuid().ToString("D");var manifest=new Manifest{SchemaVersion="1.0.0",Source=new(){ProjectId=project},Units=new(){Length="m",Angle="rad"},Levels=[new(){Key="story:0",Name="Проверка",Elevation=0}]};
            var walls=new List<Wall>();var wallKeys=new List<string>();var grids=new List<Grid>();var gridKeys=new List<string>();Level level=null!;
            using(var t=new Transaction(doc,"Проверочные опоры размеров"))
            {
                t.Start();level=Level.Create(doc,0);level.Name="Проверка размеров";Storage.Tag(level,project,"story:0","fixture");
                var wt=new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(w=>w.Kind==WallKind.Basic);
                for(int i=0;i<3;i++)
                {
                    var wall=Wall.Create(doc,Line.CreateBound(P(i*3,0),P(i*3,6)),wt.Id,level.Id,Geometry.Feet(3),0,false,false);WallUtils.DisallowWallJoinAtEnd(wall,0);WallUtils.DisallowWallJoinAtEnd(wall,1);
                    string key=Guid.NewGuid().ToString("D");Storage.Tag(wall,project,key,"fixture");walls.Add(wall);wallKeys.Add(key);
                    var grid=Grid.Create(doc,Line.CreateBound(P(i*3,8),P(i*3,14)));key=Guid.NewGuid().ToString("D");Storage.Tag(grid,project,key,"fixture");grids.Add(grid);gridKeys.Add(key);
                }
                t.Commit();
            }
            using(var t=new Transaction(doc,"Проверочный план"))
            {t.Start();var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.FloorPlan);var view=ViewPlan.Create(doc,type.Id,level.Id);view.Name="AC Plan "+level.Name;t.Commit();}
            doc.SaveAs(output,new SaveAsOptions{MaximumBackups=1});doc.Close(false);doc=ui.OpenAndActivateDocument(output).Document;
            ui.ActiveUIDocument.ActiveView=new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v=>v.Name.StartsWith("AC Plan "));
            walls=wallKeys.Select(key=>(Wall)Storage.Index(doc,project)[key]).ToList();
            double side=UnitUtils.ConvertFromInternalUnits(walls[0].Width/2,UnitTypeId.Meters);
            ElementRecord Row(string[] keys,double[] xs,double y,double lineY)=>new(){SourceGuid=Guid.NewGuid().ToString("D"),Kind="Dimension",LevelKey="story:0",Status="ready",Raw=JsonSerializer.SerializeToElement(new{fixture=true}),Payload=JsonSerializer.SerializeToElement(new SourceDimension{Direction=new(){X=1},LinePosition=new(){Y=lineY},Witnesses=keys.Select((key,i)=>new DimensionWitness{SourceGuid=key,Coordinate=new(){X=xs[i],Y=y}}).ToList()},Json.Options)};
            var aliases=Enumerable.Range(0,3).Select(_=>Guid.NewGuid().ToString("D")).ToArray();
            manifest.DimensionAnchors=aliases.Select((key,i)=>new DimensionGridAnchor{SourceGuid=key,Start=new(){X=i*3,Y=8},End=new(){X=i*3,Y=14},Label=(i+1).ToString()}).ToList();
            manifest.Elements.Add(Row(wallKeys.ToArray(),[side,3+side,6+side],2,-1));manifest.Elements.Add(Row(aliases,[0,3,6],10,15));
            var missing=Row([wallKeys[0],Guid.NewGuid().ToString("D")],[side,9],2,-2);manifest.Elements.Add(missing);
            string manifestFile=Path.Combine(folder,"manifest.json");manifest.ManifestHash=Json.Hash(new{project,fixture=true});File.WriteAllText(manifestFile,JsonSerializer.Serialize(manifest,Json.Options));
            string empty=Path.Combine(folder,"empty.json");File.WriteAllText(empty,"{}");
            string asset=Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location)!,"assets","MetricColumn.rft");
            if(!File.Exists(asset))asset=Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location)!,"assets"),"*.rft").First();
            var plan=new PipelineRunner.Plan{PipelineFormat="ac2r.pipeline/1",SourceManifest=manifestFile,Mapping=empty,GridsManifest=manifestFile,ColumnNativeManifest=manifestFile,ColumnNativeMapping=empty,ColumnBodies=empty,ColumnExtrusions=empty,ColumnTemplate=asset,BeamBodies=empty,RectangularBeams=empty,BeamFamily=asset,VoidTemplate=asset,OpeningsPlan=empty,RebuildRooms=false,NativeCutBeams=false};
            string pipeline=Path.Combine(folder,"pipeline.json");File.WriteAllText(pipeline,JsonSerializer.Serialize(plan,Json.Options));
            var events=new List<object>();using(var progress=new ImportProgress(ui.MainWindowHandle))
            {
                PipelineRunner.Run(doc,pipeline,Path.Combine(folder,"pipeline-run"),progress:(phase,done,total)=>
                {progress.Update(phase,done,total);events.Add(new{phase,done,total,percent=progress.State.Percent});if(phase=="nativeDimensions")progress.Capture(Path.Combine(folder,"progress.png"));},selection:new ImportSelection(["Dimension"]),ui:ui);
                if(!progress.State.Complete||progress.State.Percent!=100)throw new InvalidOperationException("Общий прогресс не завершён.");
            }
            var index=Storage.Index(doc,project);var dimensions=manifest.Elements.Take(2).Select(e=>index.GetValueOrDefault(e.SourceGuid) as Dimension).ToArray();
            if(dimensions.Any(d=>d==null||!d.AreReferencesAvailable||d.NumberOfSegments!=2))throw new InvalidOperationException("Не созданы две цепи с нативными опорами.");
            var ids=dimensions.Select(d=>d!.UniqueId).ToArray();var repeat=NativeDimensionStage.Run(doc,manifest,ui:ui);
            if(repeat.Items.Count(i=>i.Status=="unchanged")!=2||repeat.Items.Count(i=>i.Status=="unsupported")!=1)throw new InvalidOperationException("Повтор / отсутствующая опора: "+JsonSerializer.Serialize(repeat,Json.Options));
            File.WriteAllText(Path.Combine(folder,"progress-events.json"),JsonSerializer.Serialize(events,Json.Options));
            var tagType=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("BotPlus.RT.TagValidation")).FirstOrDefault(t=>t!=null)??throw new InvalidOperationException("Диагностика марок не загружена.");
            var tags=tagType.GetMethod("Run")!.Invoke(null,[ui,doc,folder]);
            File.WriteAllText(Path.Combine(folder,"tags-result.json"),JsonSerializer.Serialize(tags,Json.Options));
            doc.Save();CloseOwned();doc=ui.OpenAndActivateDocument(output).Document;
            var reopened=NativeDimensionStage.Run(doc,manifest,ui:ui);if(reopened.Items.Count(i=>i.Status=="unchanged")!=2)throw new InvalidOperationException("Опоры / положение изменились после открытия: "+JsonSerializer.Serialize(reopened,Json.Options));
            var dimension=(Dimension)doc.GetElement(ids[0]);var lastWall=(Wall)Storage.Index(doc,project)[wallKeys[2]];
            using(var t=new Transaction(doc,"Проверка ассоциативности")){t.Start();ElementTransformUtils.MoveElement(doc,lastWall.Id,P(0.1,0));doc.Regenerate();if(Math.Abs(dimension.Segments.Cast<DimensionSegment>().Last().Value!.Value-Geometry.Feet(3.1))>1e-6)throw new InvalidOperationException("Размер не последовал за стеной.");t.RollBack();}
            var changed=(Dimension)doc.GetElement(ids[1]);using(var t=new Transaction(doc,"Ручная правка подписи размера")){t.Start();changed.Segments.Cast<DimensionSegment>().First().ValueOverride="Проверено";t.Commit();}
            var conflict=NativeDimensionStage.Run(doc,manifest,ui:ui);if(!conflict.Items.Any(i=>i.SourceKey==manifest.Elements[1].SourceGuid&&i.Status=="conflict")||changed.Segments.Cast<DimensionSegment>().First().ValueOverride!="Проверено")throw new InvalidOperationException("Потеря ручного изменения размера.");
            CloseOwned();doc=null!;ui.OpenAndActivateDocument(output);
            return new{status="complete",output,nativeDimensions=2,segments=4,gridSourceAliasesVerified=true,missingOwnerRejected=true,repeatWithoutDuplicates=true,reopenVerified=true,wallAssociationVerified=true,manualEditPreserved=true,progressComplete=true,tags};
        }
        catch(Exception error){File.WriteAllText(Path.Combine(folder,"validation-error.txt"),error.ToString());throw;}
        finally{if(doc is {IsValidObject:true})CloseOwned();}
    }
    static XYZ P(double x,double y)=>new(Geometry.Feet(x),Geometry.Feet(y),0);
}
