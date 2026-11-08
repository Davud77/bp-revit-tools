using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>Local diagnostics into new projects; no network listener or arbitrary code execution.</summary>
public static class RuntimeJobs
{
    static string root = "";
    static string inbox = "";
    static string legacyInbox = "";
    static bool busy;
    static ExternalEvent? dispatch;
    static bool queued;
    public static void Start(UIControlledApplication app, string assembly)
    {
        root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(assembly)!, "..", ".."));
        legacyInbox = Path.Combine(root, "artifacts", "runtime", "job.json");
        inbox = Path.Combine(root, "artifacts", "runtime", "job-v"+typeof(App).Assembly.GetName().Version!.Minor+".json");
        dispatch=ExternalEvent.Create(new Handler());
        app.Idling += OnIdle;
    }
    public static void Stop(UIControlledApplication app) {app.Idling -= OnIdle;dispatch?.Dispose();}
    static string WorkspacePath(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root+Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime job path must remain inside the workspace");
        return full;
    }
    static void OnIdle(object? sender, IdlingEventArgs args)
    {
        // An Idling callback cannot switch an already active project. Dispatch
        // the entire operation to a supported ExternalEvent API context instead.
        if(busy || queued || (!File.Exists(inbox) && !File.Exists(legacyInbox)) || dispatch==null)return;
        queued=dispatch.Raise()==ExternalEventRequest.Accepted;
    }
    sealed class Handler:IExternalEventHandler
    {
        public string GetName()=>"Ac2Revit staged diagnostic import";
        public void Execute(UIApplication ui){queued=false;Process(ui);}
    }
    static void Process(UIApplication ui)
    {
        string sourceInbox=File.Exists(inbox)?inbox:legacyInbox;
        if(busy || !File.Exists(sourceInbox))return;
        busy = true;
        string resultPath = Path.Combine(root, "artifacts", "runtime", "job-result.json");
        Document? scratch = null;
        Importer? importer = null;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var job = JsonSerializer.Deserialize<Job>(File.ReadAllText(sourceInbox), Json.Options) ?? throw new InvalidDataException("Empty runtime job");
            File.Move(sourceInbox, Path.Combine(Path.GetDirectoryName(sourceInbox)!, "job-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+".json"));
            resultPath = WorkspacePath(job.ResultPath);
            if(job.Operation=="dimension163SourceValidation")
            {
                var output=WorkspacePath(job.OutputRvt);File.Copy(WorkspacePath(job.SourceRvt),output,false);
                var active=ui.OpenAndActivateDocument(output);var document=active.Document;var dimensionManifest=Manifest.Load(WorkspacePath(job.ManifestPath));
                var dimensionReport=NativeDimensionStage.Run(document,dimensionManifest,ui:ui);dimensionReport.Save(WorkspacePath(job.ReportPath));
                var repeat=NativeDimensionStage.Run(document,dimensionManifest,ui:ui);repeat.Save(Path.ChangeExtension(WorkspacePath(job.ReportPath),"repeat.json"));
                if(repeat.Items.Count(i=>i.Status=="unchanged")!=dimensionReport.Items.Count(i=>i.Status is "created" or "unchanged"))throw new InvalidDataException("Повтор изменил размеры исходного проекта.");
                document.Save();
                var dimensionViews=Storage.Index(document,dimensionManifest.Source.ProjectId).Values.OfType<Dimension>().Select(d=>d.OwnerViewId).Distinct().ToList();
                if(dimensionViews.Count>0)
                {
                    var image=new ImageExportOptions{ExportRange=ExportRange.SetOfViews,FilePath=Path.Combine(Path.GetDirectoryName(output)!,"Размеры"),HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG,ZoomType=ZoomFitType.FitToPage,PixelSize=2200};image.SetViewsAndSheets(dimensionViews);document.ExportImage(image);
                }
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="complete",output,sourceDimensions=dimensionManifest.Elements.Count(e=>e.Kind=="Dimension"),created=dimensionReport.Items.Count(i=>i.Status=="created"),unchanged=dimensionReport.Items.Count(i=>i.Status=="unchanged"),unsupported=dimensionReport.Items.Count(i=>i.Status=="unsupported"),repeatWithoutDuplicates=true},Json.Options));return;
            }
            if(job.Operation=="siteValidation")
            {
                string folder=Path.GetDirectoryName(WorkspacePath(job.ReportPath))!;
                var result=SiteValidation.Run(ui,folder,WorkspacePath(job.TemplatePath));File.WriteAllText(resultPath,JsonSerializer.Serialize(result,Json.Options));return;
            }
            if(job.Operation=="annotation163Validation")
            {
                string folder=Path.GetDirectoryName(WorkspacePath(job.ReportPath))!;
                var result=AnnotationValidation.Run(ui,folder,WorkspacePath(job.TemplatePath));File.WriteAllText(resultPath,JsonSerializer.Serialize(result,Json.Options));return;
            }
            if(job.Operation=="openingManagerValidation")
            {
                string folder=Path.GetDirectoryName(WorkspacePath(job.ReportPath))!;Directory.CreateDirectory(folder);
                var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("BotPlus.Openings.OpeningValidation")).FirstOrDefault(t=>t!=null)
                    ??throw new InvalidOperationException("Модуль менеджера отверстий не загружен.");
                var result=type.GetMethod("Run")!.Invoke(null,[ui,folder,WorkspacePath(job.TemplatePath),string.IsNullOrEmpty(job.ReferenceGeometryPath)?"":WorkspacePath(job.ReferenceGeometryPath)]);
                File.WriteAllText(resultPath,JsonSerializer.Serialize(result,Json.Options));return;
            }
            if(job.Operation=="openingLinkedValidation")
            {
                string folder=Path.GetDirectoryName(job.ReportPath)!;Directory.CreateDirectory(folder);
                var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("BotPlus.Openings.LinkedValidation")).FirstOrDefault(t=>t!=null)
                    ??throw new InvalidOperationException("Модуль менеджера отверстий не загружен.");
                var result=type.GetMethod("Run")!.Invoke(null,[ui,job.SourceRvt,folder,job.TemplatePath,job.Limit>0?job.Limit:3000]);
                File.WriteAllText(resultPath,JsonSerializer.Serialize(result,Json.Options));return;
            }
            if(job.Operation=="release16Check")
            {
                string output=WorkspacePath(job.OutputRvt),folder=Path.GetDirectoryName(output)!;
                var result=RoadmapValidation.Run(ui,WorkspacePath(job.ManifestPath),WorkspacePath(job.TemplatePath),output);
                var document=ui.Application.OpenDocumentFile(output);
                string renamed=Path.Combine(folder,"Проверка сохранения с новым именем.rvt");
                try
                {
                    var journalReport=new Report();journalReport.Add("validation","Journal","verified","Persistence after ordinary Save and SaveAs");ImportJournal.Write(document,journalReport);
                    using(var change=new Transaction(document,"Journal validation change")){change.Start();document.ProjectInformation.Name="Проверка журнала";change.Commit();}
                    document.Save();
                    using(var previous=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(output,"botplus-import.json"))))
                        if(previous.RootElement.GetProperty("outcomes").GetArrayLength()!=1)throw new InvalidDataException("Save lost journal outcomes");
                    document.SaveAs(renamed,new SaveAsOptions());
                    if(!File.Exists(Path.ChangeExtension(renamed,"botplus-import.log")))throw new InvalidDataException("SaveAs journal missing");
                    using(var renamedJournal=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(renamed,"botplus-import.json"))))
                        if(renamedJournal.RootElement.GetProperty("outcomes").GetArrayLength()!=1)throw new InvalidDataException("SaveAs lost journal outcomes");
                }
                finally{document.Close(false);}
                string chosen=Path.Combine(folder,"Выбранная папка","Имя модели.rvt");
                ExchangeCommands.NewProject(ui,job.ManifestPath,WorkspacePath(job.TemplatePath),chosen);
                bool protectedExisting=false;
                try{ExchangeCommands.NewProject(ui,job.ManifestPath,WorkspacePath(job.TemplatePath),chosen);}catch(IOException){protectedExisting=true;}
                if(!protectedExisting)throw new InvalidDataException("Existing RVT not protected");
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="complete",result,chosen,protectedExisting,journalSaveAndSaveAs=true},Json.Options));return;
            }
            if(job.Operation=="hatchValidation")
            {
                var result=HatchValidation.Run(ui.Application,WorkspacePath(job.ManifestPath),WorkspacePath(job.TemplatePath),Path.GetDirectoryName(WorkspacePath(job.ReportPath))!,job.Limit);
                File.WriteAllText(resultPath,JsonSerializer.Serialize(result,Json.Options));return;
            }
            if(job.Operation=="wallNetTrimCopy")
            {
                string output=WorkspacePath(job.OutputRvt);if(File.Exists(output))throw new IOException("Trim output exists");
                File.Copy(WorkspacePath(job.SourceRvt),output,false);
                var document=ui.OpenAndActivateDocument(output).Document;
                var trimReport=EvaluatedWallTrim.Run(document,WorkspacePath(job.ManifestPath),WorkspacePath(job.TemplatePath),Path.Combine(Path.GetDirectoryName(output)!,"trim-families"),job.Limit);
                trimReport.Save(WorkspacePath(job.ReportPath));document.Save();
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="complete",output,outcomes=trimReport.Items.GroupBy(i=>i.Status).ToDictionary(g=>g.Key,g=>g.Count())},Json.Options));return;
            }
            if(job.Operation=="comparisonAudit")
            {
                var comparisonManifest=Manifest.Load(WorkspacePath(job.ManifestPath));
                var document=ui.Application.OpenDocumentFile(WorkspacePath(job.SourceRvt));
                try
                {
                    var index=Storage.Index(document,comparisonManifest.Source.ProjectId);var comparisonReport=new Report();
                    var rooms=comparisonManifest.Elements.Where(e=>e.Kind=="Zone").Select(e=>
                    {
                        var room=index.GetValueOrDefault(e.SourceGuid) as Autodesk.Revit.DB.Architecture.Room;
                        var loops=e.Payload.GetProperty("loops").Read<List<List<Edge>>>();
                        double expected=Math.Abs(SourcePlanarGeometry.SignedArea(loops[0]))-loops.Skip(1).Sum(l=>Math.Abs(SourcePlanarGeometry.SignedArea(l)));
                        double actual=room==null?0:UnitUtils.ConvertFromInternalUnits(room.Area,UnitTypeId.SquareMeters);
                        comparisonReport.ZoneAreas.Add(new(e.SourceGuid,expected,actual,Math.Abs(expected-actual),1,room!=null && actual>0 && Math.Abs(expected-actual)<=1,room==null?"missing":"present"));
                        return new{e.SourceGuid,number=e.Payload.S("number"),name=e.Payload.S("name"),expectedArea=expected,actualArea=actual,id=room?.Id.Value,height=room==null?(double?)null:UnitUtils.ConvertFromInternalUnits(room.UnboundedHeight,UnitTypeId.Meters)};
                    }).ToArray();
                    string path=WorkspacePath(job.ReportPath),folder=Path.GetDirectoryName(path)!;
                    Directory.CreateDirectory(folder);
                    RoomBoundaryAudit.Write(document,comparisonManifest,comparisonReport,Path.Combine(folder,"room-boundaries.json"));
                    HatchMappingStage.Inventory(document,comparisonManifest,Path.Combine(folder,"hatch-inventory.json"));
                    GeometryAudit.Write(document,Path.Combine(folder,"roofs-geometry.jsonl"),comparisonManifest.Elements.Where(e=>e.Kind=="Roof").Select(e=>e.SourceGuid).ToHashSet(),null,BuiltInCategory.OST_GenericModel);
                    foreach(var (name,category) in new[]{("walls",BuiltInCategory.OST_Walls),("doors",BuiltInCategory.OST_Doors),("windows",BuiltInCategory.OST_Windows)})
                        GeometryAudit.Write(document,Path.Combine(folder,name+"-geometry.jsonl"),null,null,category);
                    var physical=comparisonManifest.Elements.Where(e=>e.Kind is "Roof" or "Shell" or "Wall" or "Door" or "Window").Select(e=>
                    {
                        var native=index.GetValueOrDefault(e.SourceGuid);var box=native?.get_BoundingBox(null);
                        double[]? P(XYZ? p)=>p==null?null:new[]{p.X,p.Y,p.Z}.Select(v=>UnitUtils.ConvertFromInternalUnits(v,UnitTypeId.Meters)).ToArray();
                        return new{e.SourceGuid,e.Kind,id=native?.Id.Value,elementClass=native?.GetType().Name,name=native?.Name,min=P(box?.Min),max=P(box?.Max)};
                    }).ToArray();
                    var result=new{status="complete",source=job.SourceRvt,rooms,physical,probeTransactions="rolled back; source RVT not saved"};
                    File.WriteAllText(path,JsonSerializer.Serialize(result,Json.Options));
                    File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="complete",comparisonReport=path,roomCount=rooms.Count(r=>r.actualArea>0),missing=rooms.Where(r=>r.id==null).Select(r=>r.number)},Json.Options));
                }
                finally{document.Close(false);}
                return;
            }
            if(job.Operation=="ovCheck")
            {
                var assembly=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="HeatLossViewer");
                var method=assembly.GetType("HeatLossViewer.Export.ExportValidation",true)!.GetMethod("Run")!;
                var result=method.Invoke(null,[ui,WorkspacePath(job.ManifestPath)]);
                File.WriteAllText(resultPath,JsonSerializer.Serialize(result,Json.Options));return;
            }
            if(job.Operation=="roadmapCheck")
            {
                var result=RoadmapValidation.Run(ui,WorkspacePath(job.ManifestPath),WorkspacePath(job.TemplatePath),WorkspacePath(job.OutputRvt));
                File.WriteAllText(WorkspacePath(job.ReportPath),JsonSerializer.Serialize(result,Json.Options));
                ui.OpenAndActivateDocument(WorkspacePath(job.OutputRvt));
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="complete",result},Json.Options));return;
            }
            if(job.Operation=="namingCheck")
            {
                var result=NamingValidation.Run(ui,WorkspacePath(job.ManifestPath),WorkspacePath(job.OutputRvt));
                File.WriteAllText(WorkspacePath(job.ReportPath),JsonSerializer.Serialize(result,Json.Options));
                ui.OpenAndActivateDocument(WorkspacePath(job.OutputRvt));
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="complete",result},Json.Options));return;
            }
            if(job.Operation is "nominalColumnsCheckCopy" or "completeColumnsCopy")
            {
                string output=WorkspacePath(job.OutputRvt);if(File.Exists(output))throw new IOException("Nominal column output already exists");
                var columnSource=Manifest.Load(WorkspacePath(job.ManifestPath));var settings=ImportConfig.Load(WorkspacePath(job.MappingPath));
                if(job.Operation=="nominalColumnsCheckCopy")
                {
                    if(columnSource.Elements.Count>20 || columnSource.Elements.Any(e=>e.Kind!="Column"))throw new InvalidDataException("Column fixture accepts at most 20 columns");
                    var fresh=ui.Application.NewProjectDocument(WorkspacePath(job.TemplatePath));fresh.SaveAs(output,new SaveAsOptions{MaximumBackups=1});fresh.Close(false);
                }
                else File.Copy(WorkspacePath(job.SourceRvt),output,false);
                var document=ui.OpenAndActivateDocument(output).Document;
                if(job.Operation=="nominalColumnsCheckCopy")new Importer(document,columnSource,settings).Run(new HashSet<string>(),true,false);
                var before=Storage.Index(document,columnSource.Source.ProjectId);
                string expectedReferenceSha="";
                if(job.Operation=="completeColumnsCopy")
                {
                    if(before.GetValueOrDefault("pipeline-source-snapshot") is not Autodesk.Revit.DB.ExtensibleStorage.DataStorage snapshot)throw new InvalidDataException("Source snapshot tag missing");
                    var schema=Storage.Schema();using var saved=JsonDocument.Parse(snapshot.GetEntity(schema).Get<string>(schema.GetField("RawJson")));
                    using var stream=File.OpenRead(WorkspacePath(job.ManifestPath));string sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    if(!sha.Equals(saved.RootElement.S("manifestSha"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Column repair belongs to another source snapshot");
                    expectedReferenceSha=saved.RootElement.S("referenceSha");
                }
                double Expected(ElementRecord e)
                {var loops=e.Payload.GetProperty("loops").Read<List<List<Edge>>>();return Math.Abs(SourcePlanarGeometry.SignedArea(loops[0]))-loops.Skip(1).Sum(l=>Math.Abs(SourcePlanarGeometry.SignedArea(l)));}
                double Area(Element e)=>e is Autodesk.Revit.DB.Architecture.Room room?UnitUtils.ConvertFromInternalUnits(room.Area,UnitTypeId.SquareMeters):0;
                var accepted=columnSource.Elements.Where(e=>e.Kind=="Zone" && before.GetValueOrDefault(e.SourceGuid) is Autodesk.Revit.DB.Architecture.Room room && room.Area>0 && Math.Abs(Area(room)-Expected(e))<=settings.RoomAreaToleranceSquareMeters+1e-9).ToArray();
                Report result;
                using(var group=new TransactionGroup(document,"Add marked API-only columns without losing accepted rooms"))
                {
                    group.Start();result=NominalColumnStage.Run(document,columnSource,settings,Path.GetDirectoryName(output)!);
                    var columnIndex=Storage.Index(document,columnSource.Source.ProjectId);
                    if(accepted.Any(e=>columnIndex.GetValueOrDefault(e.SourceGuid) is not Autodesk.Revit.DB.Architecture.Room room || room.Area<=0 || Math.Abs(Area(room)-Expected(e))>settings.RoomAreaToleranceSquareMeters+1e-9))throw new InvalidDataException("Nominal columns would invalidate an accepted room; changes rolled back");
                    if(result.Items.Any(i=>i.Status=="failed"))throw new InvalidDataException("Nominal column creation failed; see report");
                    group.Assimilate();
                }
                result.Save(WorkspacePath(job.ReportPath));document.Save();
                var after=Storage.Index(document,columnSource.Source.ProjectId);
                if(job.OpeningsPlanPath.Length>0)
                {
                    if(job.Operation!="completeColumnsCopy")throw new InvalidDataException("Opening repairs require an existing verified source snapshot");
                    string openingPath=WorkspacePath(job.OpeningsPlanPath);
                    using(var openingPlan=JsonDocument.Parse(File.ReadAllText(openingPath)))
                    {
                        var sourceRows=columnSource.Elements.ToDictionary(e=>e.SourceGuid,StringComparer.OrdinalIgnoreCase);
                        var plan=openingPlan.RootElement;
                        if(!plan.S("projectId").Equals(columnSource.Source.ProjectId,StringComparison.OrdinalIgnoreCase) || !plan.S("ifcSha256").Equals(expectedReferenceSha,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Opening repair IFC/source differs from the saved snapshot");
                        foreach(var row in plan.GetProperty("elements").EnumerateArray())
                            if(!sourceRows.TryGetValue(row.S("sourceGuid"),out var sourceRow) || sourceRow.Kind is not ("Door" or "Window") || sourceRow.Kind!=row.S("kind") || Json.Hash(sourceRow.Raw)!=Json.Hash(row.GetProperty("rawSource")))throw new InvalidDataException("Opening repair changed the source identity or raw data");
                    }
                    using var references=new VerifiedReferenceScope();string previousCheckpoint="";
                    for(int restart=0;;restart++)
                    {
                        string stageFolder=Path.Combine(Path.GetDirectoryName(output)!,"repair-fillings",$"pass-{restart:00}");
                        try
                        {
                            var fillings=OpeningStage.Run(document,openingPath,WorkspacePath(job.TemplatePath),stageFolder,0,
                                (phase,done,total)=>File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="running",phase,done,total,seconds=timer.Elapsed.TotalSeconds},Json.Options)));
                            result.Items.AddRange(fillings.Items);break;
                        }
                        catch(ImportCheckpointException)when(restart<32)
                        {
                            string checkpoint=Json.Hash(Storage.Index(document,columnSource.Source.ProjectId).Keys.OrderBy(k=>k,StringComparer.Ordinal).ToArray());
                            if(checkpoint==previousCheckpoint)throw new InvalidDataException("Repair checkpoint made no progress; saved copy retained for diagnosis");
                            previousCheckpoint=checkpoint;
                            string partial=Path.Combine(stageFolder,"cut-report.json");
                            if(File.Exists(partial))result.Items.AddRange(JsonSerializer.Deserialize<Report>(File.ReadAllText(partial),Json.Options)!.Items);
                            result.Save(WorkspacePath(job.ReportPath));
                            File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="running",phase="repairMemoryCheckpoint",done=restart+1,total=32,seconds=timer.Elapsed.TotalSeconds},Json.Options));
                            string parking=Path.Combine(Path.GetDirectoryName(output)!,"repair-parking-"+restart+".rvt");
                            var temporary=ui.Application.NewProjectDocument(UnitSystem.Metric);temporary.SaveAs(parking,new SaveAsOptions{MaximumBackups=1});temporary.Close(false);
                            var parked=ui.OpenAndActivateDocument(parking);document.Close(false);document=ui.OpenAndActivateDocument(output).Document;parked.Document.Close(false);
                            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
                        }
                    }
                    after=Storage.Index(document,columnSource.Source.ProjectId);
                    if(accepted.Any(e=>after.GetValueOrDefault(e.SourceGuid) is not Autodesk.Revit.DB.Architecture.Room room || room.Area<=0 || Math.Abs(Area(room)-Expected(e))>settings.RoomAreaToleranceSquareMeters+1e-9))throw new InvalidDataException("Opening repair invalidated an accepted room; baseline remains unchanged, repair copy requires review");
                }
                if(job.MeshKeysPath.Length>0)result.Items.AddRange(GridLabelStage.Run(document,Manifest.Load(WorkspacePath(job.MeshKeysPath))).Items);
                result.Items.AddRange(ServiceCutsStage.Run(document,columnSource.Source.ProjectId).Items);
                result.Save(WorkspacePath(job.ReportPath));document.Save();
                int columns=columnSource.Elements.Count(e=>e.Kind=="Column" && after.ContainsKey(e.SourceGuid));
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="completeWithIssues",output,columns,preservedAcceptedRooms=accepted.Length,outcomes=result.Items.GroupBy(i=>i.Status).ToDictionary(g=>g.Key,g=>g.Count()),seconds=timer.Elapsed.TotalSeconds},Json.Options));return;
            }
            if(job.Operation=="zeroThicknessCheckCopy")
            {
                string output=WorkspacePath(job.OutputRvt);if(File.Exists(output))throw new IOException("Zero-thickness fixture output already exists");
                var input=Manifest.Load(WorkspacePath(job.ManifestPath));var settings=ImportConfig.Load(WorkspacePath(job.MappingPath));
                if(input.Elements.Count>20 || input.Elements.Any(e=>e.Kind is not ("Zone" or "Wall")))throw new InvalidDataException("Fixture accepts at most 20 walls/zones");
                var created=ui.Application.NewProjectDocument(WorkspacePath(job.TemplatePath));created.SaveAs(output,new SaveAsOptions{MaximumBackups=1});created.Close(false);
                var document=ui.OpenAndActivateDocument(output).Document;
                var resources=new Importer(document,input,settings).Run(new HashSet<string>(),true,false);
                var first=WallCompletion.Run(document,input,null,Path.Combine(Path.GetDirectoryName(output)!,"first"));
                var second=WallCompletion.Run(document,input,null,Path.Combine(Path.GetDirectoryName(output)!,"repeat"));
                if(first.Items.Concat(second.Items).Any(i=>i.Status=="failed"))throw new InvalidDataException("Zero-thickness fixture failed; see stage reports");
                var rooms=new Importer(document,input,settings).Run(new HashSet<string>{"Zone"},false,false);
                if(rooms.ZoneAreas.Count!=input.Elements.Count(e=>e.Kind=="Zone") || rooms.ZoneAreas.Any(a=>!a.Accepted))throw new InvalidDataException("Zero-thickness boundaries did not enclose expected native rooms");
                var index=Storage.Index(document,input.Source.ProjectId);int sheets=input.Elements.Count(e=>SourceZeroThicknessWall.IsCandidate(e) && index.GetValueOrDefault(e.SourceGuid) is DirectShape shape && shape.ApplicationId==ZeroThicknessWallStage.ApplicationId);
                int boundaries=index.Count(p=>p.Key.StartsWith("zero-wall-separator:") && p.Value is ModelCurve);
                int walls=input.Elements.Count(e=>e.Kind=="Wall" && index.GetValueOrDefault(e.SourceGuid) is DirectShape);
                if(walls!=input.Elements.Count(e=>e.Kind=="Wall") || sheets!=input.Elements.Count(SourceZeroThicknessWall.IsCandidate) || second.Items.Any(i=>i.Status=="created"))throw new InvalidDataException("Zero-thickness fixture missing walls/sheets or duplicated boundaries");
                first.Items.AddRange(second.Items);first.Items.AddRange(rooms.Items);first.ZoneAreas.AddRange(rooms.ZoneAreas);first.Save(WorkspacePath(job.ReportPath));document.Save();
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="completeWithIssues",output,walls,sheets,boundaries,rooms=rooms.ZoneAreas,repeatCreated=second.Items.Count(i=>i.Status=="created"),seconds=timer.Elapsed.TotalSeconds},Json.Options));return;
            }
            if(job.Operation=="repairPresentationCopy")
            {
                string output=WorkspacePath(job.OutputRvt);if(File.Exists(output))throw new IOException("Repair output already exists");
                File.Copy(WorkspacePath(job.SourceRvt),output,false);var document=ui.OpenAndActivateDocument(output).Document;
                var original=Manifest.Load(WorkspacePath(job.ManifestPath));var repair=new Report{ProjectId=original.Source.ProjectId,Mode="ownedPresentationAndMissingSlabs"};
                if(job.MeshManifestPath.Length>0)
                {
                    var patch=Manifest.Load(WorkspacePath(job.MeshManifestPath));
                    if(patch.Source.ProjectId!=original.Source.ProjectId || patch.Elements.Any(e=>e.Kind!="Slab"))throw new InvalidDataException("Repair accepts source-owned missing slabs only");
                    var originals=original.Elements.ToDictionary(e=>e.SourceGuid,StringComparer.OrdinalIgnoreCase);
                    foreach(var row in patch.Elements)
                        if(!originals.TryGetValue(row.SourceGuid,out var before) || before.Kind!="Slab" || Json.Hash(row.Raw)!=Json.Hash(before.Raw))throw new InvalidDataException("Slab repair changed the raw source");
                    var settings=ImportConfig.Load(WorkspacePath(job.MappingPath));settings.ConnectWalls=false;
                    var slabs=new Importer(document,patch,settings).Run(new HashSet<string>{"Slab"},false,false);repair.Items.AddRange(slabs.Items);
                    if(job.ReferenceGeometryPath.Length>0)
                    {
                        using var reference=JsonDocument.Parse(File.ReadAllText(WorkspacePath(job.ReferenceGeometryPath)));
                        if(reference.RootElement.S("projectId")!=original.Source.ProjectId)throw new InvalidDataException("Slab geometry project differs");
                        VerifiedReferenceScope.Verify(WorkspacePath(reference.RootElement.S("ifcPath")),reference.RootElement.S("ifcSha256"));
                        var index=Storage.Index(document,original.Source.ProjectId);
                        foreach(var row in reference.RootElement.GetProperty("elements").EnumerateArray())
                        {
                            string key=row.S("sourceGuid");
                            try
                            {
                                if(index.GetValueOrDefault(key) is not Floor floor)throw new InvalidDataException("Repaired native floor missing");
                                var points=row.GetProperty("vertices").Read<List<Point>>().Select(NativeGeometry.At).ToList();
                                var center=new XYZ(points.Average(p=>p.X),points.Average(p=>p.Y),points.Average(p=>p.Z));
                                var local=Transform.CreateTranslation(center.Negate());
                                var expected=NativeGeometry.MeshSolids(row,local);var actual=NativeGeometry.Solids(floor).Select(s=>SolidUtils.CreateTransformed(s,local)).ToList();
                                NativeGeometry.VerifySolids(actual,expected);
                                repair.Add(key,"SlabGeometry","verified","Нативное перекрытие сверено с IFC в обоих направлениях булевой разности, допуск 1e-6 м³.",floor.UniqueId);
                            }
                            catch(Exception ex){repair.Add(key,"SlabGeometry","incomplete","Перекрытие по точным полям API создано; проверка полного IFC-тела не пройдена: "+ex.Message);}
                        }
                    }
                }
                if(job.OpeningsPlanPath.Length>0)
                {
                    var fillings=OpeningStage.Run(document,WorkspacePath(job.OpeningsPlanPath),WorkspacePath(job.TemplatePath),Path.Combine(Path.GetDirectoryName(output)!,"repair-fillings"));repair.Items.AddRange(fillings.Items);
                }
                var labels=GridLabelStage.Run(document,Manifest.Load(WorkspacePath(job.MeshKeysPath)));repair.Items.AddRange(labels.Items);
                var cuts=ServiceCutsStage.Run(document,original.Source.ProjectId);repair.Items.AddRange(cuts.Items);
                repair.Save(WorkspacePath(job.ReportPath));document.Save();
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status=repair.Items.Any(i=>i.Status is "failed" or "blocked" or "revitError" or "incomplete")?"completeWithIssues":"complete",output,seconds=timer.Elapsed.TotalSeconds,
                    outcomes=repair.Items.GroupBy(i=>i.Status).ToDictionary(g=>g.Key,g=>g.Count()),floors=new FilteredElementCollector(document).OfClass(typeof(Floor)).GetElementCount()},Json.Options));return;
            }
            if(job.Operation=="modelPreview")
            {
                string source=WorkspacePath(job.SourceRvt),folder=Path.GetDirectoryName(WorkspacePath(job.ReportPath))!;Directory.CreateDirectory(folder);
                var active=ui.ActiveUIDocument;
                if(active==null || !string.Equals(active.Document.PathName,source,StringComparison.OrdinalIgnoreCase))active=ui.OpenAndActivateDocument(source);
                var document=active.Document;var previewManifest=Manifest.Load(WorkspacePath(job.ManifestPath));var index=Storage.Index(document,previewManifest.Source.ProjectId);
                var physical=previewManifest.Elements.Where(e=>e.Kind!="Zone").Select(e=>index.GetValueOrDefault(e.SourceGuid)).Where(e=>e is Wall or Floor or FamilyInstance or DirectShape).Cast<Element>().ToList();
                var boxes=physical.Select(e=>e.get_BoundingBox(null)).Where(b=>b!=null).ToList();
                if(boxes.Count==0)throw new InvalidDataException("No imported physical geometry to preview");
                var lo=new XYZ(boxes.Min(b=>b.Min.X),boxes.Min(b=>b.Min.Y),boxes.Min(b=>b.Min.Z));var hi=new XYZ(boxes.Max(b=>b.Max.X),boxes.Max(b=>b.Max.Y),boxes.Max(b=>b.Max.Z));var center=(lo+hi)*.5;
                var view=new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>().First(v=>v.Name=="AC Complete Model");
                using(var t=new Transaction(document,"Imported model presentation"))
                {
                    t.Start();view.DisplayStyle=DisplayStyle.Shading;view.DetailLevel=ViewDetailLevel.Fine;
                    double margin=Geometry.Feet(5);view.SetSectionBox(new BoundingBoxXYZ{Min=lo-new XYZ(margin,margin,margin),Max=hi+new XYZ(margin,margin,margin)});view.IsSectionBoxActive=true;
                    var direction=new XYZ(-1,1,-.7).Normalize();var up=(XYZ.BasisZ-direction*XYZ.BasisZ.DotProduct(direction)).Normalize();
                    view.SetOrientation(new ViewOrientation3D(center-direction*Math.Max(hi.DistanceTo(lo),100),up,direction));t.Commit();
                }
                active.ActiveView=view;active.GetOpenUIViews().FirstOrDefault(v=>v.ViewId==view.Id)?.ZoomToFit();
                var options=new ImageExportOptions{ExportRange=ExportRange.SetOfViews,FilePath=Path.Combine(folder,"Model_preview"),HLRandWFViewsFileType=ImageFileType.PNG,ShadowViewsFileType=ImageFileType.PNG,PixelSize=2000,ZoomType=ZoomFitType.FitToPage,ImageResolution=ImageResolution.DPI_150};options.SetViewsAndSheets(new List<ElementId>{view.Id});document.ExportImage(options);document.Save();
                var coverage=previewManifest.Elements.GroupBy(e=>e.Kind).ToDictionary(g=>g.Key,g=>new{source=g.Count(),represented=g.Count(e=>index.ContainsKey(e.SourceGuid)),missing=g.Count(e=>!index.ContainsKey(e.SourceGuid)),classes=g.Where(e=>index.ContainsKey(e.SourceGuid)).GroupBy(e=>index[e.SourceGuid].GetType().Name).ToDictionary(c=>c.Key,c=>c.Count())});
                var result=new{status="complete",document=source,coverage,physicalElements=physical.Count,boundsMinMeters=new[]{lo.X,lo.Y,lo.Z}.Select(v=>UnitUtils.ConvertFromInternalUnits(v,UnitTypeId.Meters)),boundsMaxMeters=new[]{hi.X,hi.Y,hi.Z}.Select(v=>UnitUtils.ConvertFromInternalUnits(v,UnitTypeId.Meters)),images=Directory.GetFiles(folder,"Model_preview*.png")};
                File.WriteAllText(WorkspacePath(job.ReportPath),JsonSerializer.Serialize(result,Json.Options));File.WriteAllText(resultPath,JsonSerializer.Serialize(result,Json.Options));return;
            }
            if(job.Operation=="supplementalBodiesCopy")
            {
                var bundle=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(WorkspacePath(job.ManifestPath)))!;
                if(bundle["format"]?.GetValue<string>()!="ac2r.supplemental/1")throw new InvalidDataException("Invalid supplemental body plan");
                var supplementManifest=Manifest.Load(WorkspacePath(bundle["sourceManifest"]!.GetValue<string>()));string project=supplementManifest.Source.ProjectId;
                if(bundle["projectId"]!.GetValue<string>()!=project)throw new InvalidDataException("Supplemental project mismatch");
                string output=WorkspacePath(job.OutputRvt);if(File.Exists(output))throw new IOException("Supplemental output already exists");
                File.Copy(WorkspacePath(job.SourceRvt),output,false);var document=ui.OpenAndActivateDocument(output).Document;
                string folder=Path.GetDirectoryName(output)!;
                var supplementReport=SupplementalBodiesStage.Run(document,WorkspacePath(job.ManifestPath),Path.Combine(folder,"supplemental-inputs"),
                    (phase,done,total)=>File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="running",phase,done,total,seconds=timer.Elapsed.TotalSeconds},Json.Options)));
                var finalIndex=Storage.Index(document,project);
                var coverage=supplementManifest.Elements.Select(e=>new{sourceGuid=e.SourceGuid,kind=e.Kind,represented=finalIndex.ContainsKey(e.SourceGuid),representation=finalIndex.GetValueOrDefault(e.SourceGuid)?.GetType().Name??""}).ToArray();
                File.WriteAllText(Path.Combine(folder,"source-coverage.json"),JsonSerializer.Serialize(new{projectId=project,elements=coverage},Json.Options));
                supplementReport.Save(WorkspacePath(job.ReportPath));document.Save();
                var view=new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>().First(v=>v.Name=="AC Complete Model");ui.ActiveUIDocument.RequestViewChange(view);
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="complete",output,seconds=timer.Elapsed.TotalSeconds,outcomes=supplementReport.Items.GroupBy(i=>i.Status).ToDictionary(g=>g.Key,g=>g.Count()),coverage=coverage.GroupBy(e=>e.kind).ToDictionary(g=>g.Key,g=>new{source=g.Count(),represented=g.Count(e=>e.represented)})},Json.Options));return;
            }
            if(job.Operation=="elementHatchValidationCopy")
            {
                string output=WorkspacePath(job.OutputRvt),folder=Path.GetDirectoryName(output)!;
                if(File.Exists(output))throw new IOException("Diagnostic output exists");Directory.CreateDirectory(folder);
                File.Copy(WorkspacePath(job.SourceRvt),output,false);
                var document=ui.Application.OpenDocumentFile(output);
                try
                {
                    var source=WorkspacePath(job.ManifestPath);var m=Manifest.Load(source);
                    var result=ElementHatchStage.PrepareAndRun(document,m,source,Path.Combine(root,"config","hatch-analogues.ru.json"),folder);
                    result.Save(Path.Combine(folder,"initial-stage.json"));
                    var repeat=ElementHatchStage.Run(document,m,source,Path.Combine(folder,"element-hatches.json"),Path.Combine(folder,"repeat-audit.json"));
                    if(repeat.Items.Any(i=>i.Status=="updated"))throw new InvalidDataException("Element hatch repeat changed graphics");
                    using var audit=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"audit.json")));
                    var first=audit.RootElement.GetProperty("assignments").EnumerateArray().First(r=>r.S("slot")=="cover");
                    var view=(View)document.GetElement(new ElementId(first.GetProperty("viewId").GetInt64()));var id=new ElementId(first.GetProperty("elementId").GetInt64());
                    using var original=view.GetElementOverrides(id);
                    using(var edit=new Transaction(document,"Diagnostic local fill edit")){edit.Start();using var graphics=view.GetElementOverrides(id);graphics.SetSurfaceForegroundPatternColor(new Color(1,2,3));view.SetElementOverrides(id,graphics);edit.Commit();}
                    var conflict=ElementHatchStage.Run(document,m,source,Path.Combine(folder,"element-hatches.json"),Path.Combine(folder,"conflict-audit.json"));
                    using var preserved=view.GetElementOverrides(id);
                    if(!conflict.Items.Any(i=>i.Status=="conflict")||preserved.SurfaceForegroundPatternColor.Red!=1)throw new InvalidDataException("Local hatch edit overwritten");
                    using(var restore=new Transaction(document,"Restore diagnostic fill edit")){restore.Start();view.SetElementOverrides(id,original);restore.Commit();}
                    var options=new ImageExportOptions{ExportRange=ExportRange.SetOfViews,FilePath=Path.Combine(folder,"plan-covers"),HLRandWFViewsFileType=ImageFileType.PNG,
                        ShadowViewsFileType=ImageFileType.PNG,PixelSize=3000,ZoomType=ZoomFitType.FitToPage,ImageResolution=ImageResolution.DPI_150};options.SetViewsAndSheets([view.Id]);document.ExportImage(options);
                    document.Save();document.Close(false);document=ui.Application.OpenDocumentFile(output);
                    var reopen=ElementHatchStage.Run(document,m,source,Path.Combine(folder,"element-hatches.json"),Path.Combine(folder,"reopen-audit.json"));
                    if(reopen.Items.Any(i=>i.Status is "updated" or "conflict"))throw new InvalidDataException("Saved element fill state did not persist");
                    File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="complete",output,repeatUnchanged=true,localEditPreserved=true,reopenUnchanged=true,
                        outcomes=result.Items.GroupBy(i=>i.Status).ToDictionary(g=>g.Key,g=>g.Count())},Json.Options));
                }
                finally{document.Close(false);}
                return;
            }
            if(job.Operation is "hatchInventory" or "hatchCopy" or "hatchAudit")
            {
                var hatchManifest=Manifest.Load(WorkspacePath(job.MeshManifestPath));string source=WorkspacePath(job.SourceRvt);
                if(job.Operation=="hatchCopy")
                {
                    string output=WorkspacePath(job.OutputRvt);if(File.Exists(output))throw new IOException("Hatch output already exists");
                    File.Copy(source,output,false);source=output;
                }
                var active=ui.ActiveUIDocument;
                if(active==null || !string.Equals(active.Document.PathName,source,StringComparison.OrdinalIgnoreCase))active=ui.OpenAndActivateDocument(source);
                object result;
                if(job.Operation=="hatchInventory")result=HatchMappingStage.Inventory(active.Document,hatchManifest,WorkspacePath(job.ReportPath));
                else if(job.Operation=="hatchAudit")result=HatchMappingStage.Audit(active.Document,hatchManifest,WorkspacePath(job.MeshManifestPath),WorkspacePath(job.ManifestPath),WorkspacePath(job.ReportPath));
                else
                {
                    var hatchReport=HatchMappingStage.Run(active.Document,hatchManifest,WorkspacePath(job.MeshManifestPath),WorkspacePath(job.ManifestPath),WorkspacePath(job.ReportPath));
                    hatchReport.Save(Path.ChangeExtension(WorkspacePath(job.ReportPath),"stage.json"));active.Document.Save();
                    result=new{output=source,materials=hatchReport.Items.Count};
                }
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="complete",seconds=timer.Elapsed.TotalSeconds,result},Json.Options));return;
            }
            if(job.Operation is "roomSeparatorAudit" or "roomSeparatorsCheckCopy" or "roomSeparatorsAndRoomsCopy")
            {
                var separatorManifest=Manifest.Load(WorkspacePath(job.MeshManifestPath));
                string source=WorkspacePath(job.SourceRvt);
                if(job.Operation is "roomSeparatorsCheckCopy" or "roomSeparatorsAndRoomsCopy")
                {
                    string output=WorkspacePath(job.OutputRvt);
                    if(File.Exists(output))throw new IOException("Separator check copy already exists");
                    File.Copy(source,output,false);source=output;
                }
                var active=ui.ActiveUIDocument;
                if(active==null || !string.Equals(active.Document.PathName,source,StringComparison.OrdinalIgnoreCase))active=ui.OpenAndActivateDocument(source);
                if(job.Operation is "roomSeparatorsCheckCopy" or "roomSeparatorsAndRoomsCopy")
                {
                    var separatorReport=RoomSeparatorStage.Run(active.Document,separatorManifest,WorkspacePath(job.MeshManifestPath),WorkspacePath(job.ManifestPath),Path.Combine(Path.GetDirectoryName(source)!,"separator-stage"));
                    separatorReport.Save(Path.ChangeExtension(WorkspacePath(job.ReportPath),"stage.json"));active.Document.Save();
                    if(job.Operation=="roomSeparatorsAndRoomsCopy")
                    {
                        var options=ImportConfig.Load(WorkspacePath(job.MappingPath));options.ConnectWalls=false;
                        var spatial=new Importer(active.Document,separatorManifest,options).RebuildSpatialElements();
                        spatial.Save(Path.Combine(Path.GetDirectoryName(source)!,"spatial-report.json"));
                        RoomBoundaryAudit.Write(active.Document,separatorManifest,spatial,Path.Combine(Path.GetDirectoryName(source)!,"largest-room-boundaries.json"));
                        active.Document.Save();
                    }
                }
                RoomSeparatorStage.Audit(active.Document,separatorManifest,WorkspacePath(job.ManifestPath),WorkspacePath(job.ReportPath));
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="complete",source,seconds=timer.Elapsed.TotalSeconds,audit=job.ReportPath},Json.Options));return;
            }
            if(job.Operation=="pipelineAudit")
            {
                var active=ui.ActiveUIDocument;
                if(active==null || !string.Equals(active.Document.PathName,WorkspacePath(job.SourceRvt),StringComparison.OrdinalIgnoreCase))active=ui.OpenAndActivateDocument(WorkspacePath(job.SourceRvt));
                var audit=PipelineAudit.Write(active.Document,WorkspacePath(job.ManifestPath),WorkspacePath(job.ReportPath),(phase,done,total)=>
                    File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="running",phase,done,total,seconds=timer.Elapsed.TotalSeconds},Json.Options)));
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="complete",audit,seconds=timer.Elapsed.TotalSeconds},Json.Options));return;
            }
            if(job.Operation=="openingsCheckCopy")
            {
                string output=WorkspacePath(job.OutputRvt);if(File.Exists(output))throw new IOException("Opening check output already exists");
                File.Copy(WorkspacePath(job.SourceRvt),output,false);var document=ui.OpenAndActivateDocument(output).Document;
                var openingReport=OpeningStage.Run(document,WorkspacePath(job.ManifestPath),WorkspacePath(job.TemplatePath),Path.Combine(Path.GetDirectoryName(output)!,"opening-families"),0,
                    (phase,done,total)=>File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="running",phase,done,total},Json.Options)));
                openingReport.Save(WorkspacePath(job.ReportPath));document.Save();
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status=openingReport.Items.Any(i=>i.Status is "blocked" or "incomplete" or "failed" or "revitError")?"completeWithIssues":"complete",output,
                    seconds=timer.Elapsed.TotalSeconds,outcomes=openingReport.Items.GroupBy(i=>i.Status).ToDictionary(g=>g.Key,g=>g.Count())},Json.Options));return;
            }
            if(job.Operation=="pipelineCopy")
            {
                string output=WorkspacePath(job.OutputRvt);
                if(File.Exists(output))throw new IOException("Pipeline output already exists");
                if(string.IsNullOrEmpty(job.SourceRvt))
                {
                    var created=ui.Application.NewProjectDocument(WorkspacePath(job.TemplatePath));created.SaveAs(output,new SaveAsOptions{OverwriteExistingFile=false,MaximumBackups=1});created.Close(false);
                }
                else File.Copy(WorkspacePath(job.SourceRvt),output,false);
                var document=ui.OpenAndActivateDocument(output).Document;
                var pipelineReport=PipelineExecution.Run(ui,WorkspacePath(job.ManifestPath),Path.Combine(Path.GetDirectoryName(output)!,"stages"),job.Limit,
                    (phase,done,total)=>File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="running",phase,done,total,seconds=timer.Elapsed.TotalSeconds},Json.Options)));
                document=ui.ActiveUIDocument.Document;pipelineReport.Save(WorkspacePath(job.ReportPath));document.Save();
                var view=new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>().First(v=>v.Name=="AC Complete Model");ui.ActiveUIDocument.RequestViewChange(view);
                int Count(BuiltInCategory c)=>new FilteredElementCollector(document).OfCategory(c).WhereElementIsNotElementType().GetElementCount();
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status=pipelineReport.Items.Any(r=>r.Status is "blocked" or "failed" or "retained" or "revitError" or "unsupported" or "partial" or "incomplete")?"completeWithIssues":"complete",output,seconds=timer.Elapsed.TotalSeconds,
                    outcomes=pipelineReport.Items.GroupBy(r=>r.Status).ToDictionary(g=>g.Key,g=>g.Count()),grids=new FilteredElementCollector(document).OfClass(typeof(Grid)).GetElementCount(),
                    columns=Count(BuiltInCategory.OST_StructuralColumns),beams=Count(BuiltInCategory.OST_StructuralFraming),doors=Count(BuiltInCategory.OST_Doors),windows=Count(BuiltInCategory.OST_Windows),rooms=Count(BuiltInCategory.OST_Rooms)},Json.Options));return;
            }
            if(job.Operation=="nativeRectangularBeamsCopy")
            {
                string source=WorkspacePath(job.SourceRvt),output=WorkspacePath(job.OutputRvt);
                if(File.Exists(output))throw new IOException("Beam output already exists");
                File.Copy(source,output,false);var document=ui.OpenAndActivateDocument(output).Document;
                var beamReport=RectangularBeamReplacer.Run(document,WorkspacePath(job.ManifestPath),WorkspacePath(job.TemplatePath),(done,total)=>
                    File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="running",phase="nativeBeams",done,total},Json.Options)));
                beamReport.Save(WorkspacePath(job.ReportPath));document.Save();
                var view=new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v=>v.Name=="AC Beams Check");
                if(view!=null)ui.ActiveUIDocument.RequestViewChange(view);
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status=beamReport.Items.Any(r=>r.Status is "retained" or "revitError")?"completeWithRetained":"complete",output,seconds=timer.Elapsed.TotalSeconds,
                    outcomes=beamReport.Items.GroupBy(r=>r.Status).ToDictionary(g=>g.Key,g=>g.Count()),
                    beams=new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().GetElementCount(),
                    nativeBeams=new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralFraming).OfClass(typeof(FamilyInstance)).GetElementCount(),
                    beamDirectShapes=new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralFraming).OfClass(typeof(DirectShape)).GetElementCount(),
                    columns=new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().GetElementCount()},Json.Options));return;
            }
            if(job.Operation=="nativeExtrudedColumnsCopy")
            {
                string source=WorkspacePath(job.SourceRvt),output=WorkspacePath(job.OutputRvt);
                if(File.Exists(output))throw new IOException("Replacement output already exists");
                File.Copy(source,output,false);var document=ui.OpenAndActivateDocument(output).Document;
                var columnReport=ExtrudedColumnReplacer.Run(document,WorkspacePath(job.ManifestPath),WorkspacePath(job.TemplatePath),Path.Combine(Path.GetDirectoryName(output)!,"families"),
                    (phase,done,total)=>File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="running",phase,done,total,seconds=timer.Elapsed.TotalSeconds},Json.Options)));
                columnReport.Save(WorkspacePath(job.ReportPath));document.Save();
                var view=new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v=>v.Name=="AC Columns Check");
                if(view!=null)ui.ActiveUIDocument.RequestViewChange(view);
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status=columnReport.Items.Any(r=>r.Status is "retained" or "blocked" or "revitError")?"completeWithRetained":"complete",output,seconds=timer.Elapsed.TotalSeconds,
                    outcomes=columnReport.Items.GroupBy(r=>r.Status).ToDictionary(g=>g.Key,g=>g.Count()),
                    columns=new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().GetElementCount(),
                    nativeColumnInstances=new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralColumns).OfClass(typeof(FamilyInstance)).GetElementCount(),
                    columnDirectShapes=new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralColumns).OfClass(typeof(DirectShape)).GetElementCount()},Json.Options));return;
            }
            if(job.Operation=="stageImportCopy")
            {
                var source=WorkspacePath(job.SourceRvt);var output=WorkspacePath(job.OutputRvt);
                if(File.Exists(output))throw new IOException("Stage output already exists");
                var staged=Manifest.Load(WorkspacePath(job.ManifestPath));
                if(staged.Elements.Any(e=>e.Kind is not ("Column" or "Grid")))throw new InvalidDataException("This stage accepts only columns and grids");
                if(!string.IsNullOrEmpty(job.ReferenceIFCPath))
                {
                    using var stream=File.OpenRead(WorkspacePath(job.ReferenceIFCPath));
                    string actual=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    if(!actual.Equals(job.ReferenceIFCSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Stage reference IFC changed");
                }
                File.Copy(source,output,false);var document=ui.OpenAndActivateDocument(output).Document;
                var stagedConfig=ImportConfig.Load(Path.Combine(Path.GetDirectoryName(job.ManifestPath)!,"mapping.json"));
                importer=new Importer(document,staged,stagedConfig);
                importer.Progress=(phase,done,total)=>File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="running",phase,done,total,seconds=timer.Elapsed.TotalSeconds},Json.Options));
                var stageReport=importer.Run();
                if(staged.Surfaces.Count>0)MaterialGraphics.Apply(document,staged,stageReport);
                stageReport.Save(WorkspacePath(job.ReportPath));
                using(var t=new Transaction(document,"Stage diagnostic shading"))
                {
                    t.Start();var view=new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v=>v.Name=="AC Import Diagnostic");
                    if(view!=null)view.DisplayStyle=DisplayStyle.Shading;t.Commit();
                }
                document.Save();
                var outcomes=stageReport.Items.GroupBy(i=>i.Status).ToDictionary(g=>g.Key,g=>g.Count());
                string status=stageReport.Items.Any(i=>i.Status is "blocked" or "failed" or "conflict")?"completeWithErrors":"complete";
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status,output,seconds=timer.Elapsed.TotalSeconds,outcomes,
                    byKind=stageReport.Items.GroupBy(i=>i.Kind).ToDictionary(g=>g.Key,g=>g.GroupBy(i=>i.Status).ToDictionary(s=>s.Key,s=>s.Count())),
                    columns=new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().GetElementCount(),
                    grids=new FilteredElementCollector(document).OfClass(typeof(Grid)).GetElementCount()},Json.Options));return;
            }
            if(job.Operation is "referenceFallbackCopy" or "columnBodyCopy" or "beamBodyCopy")
            {
                var source=WorkspacePath(job.SourceRvt);var output=WorkspacePath(job.OutputRvt);
                if(File.Exists(output))throw new IOException("Fallback output already exists");
                File.Copy(source,output,false);
                var document=ui.OpenAndActivateDocument(output).Document;
                bool columns=job.Operation=="columnBodyCopy";
                bool beams=job.Operation=="beamBodyCopy";
                var category=columns?BuiltInCategory.OST_StructuralColumns:beams?BuiltInCategory.OST_StructuralFraming:BuiltInCategory.OST_Walls;
                var fallbackReport=ReferenceFallback.Run(document,WorkspacePath(job.MeshManifestPath),WorkspacePath,(done,total)=>
                    File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="running",phase="referenceFallback",done,total},Json.Options)),
                    category,columns?"ColumnBody":beams?"BeamBody":"WallGeometry");
                fallbackReport.Save(WorkspacePath(job.ReportPath));
                View3D? columnView=null;
                if(columns || beams)
                {
                    using var t=new Transaction(document,"Columns-only geometry check view");t.Start();
                    var vft=new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.ThreeDimensional);
                    columnView=View3D.CreateIsometric(document,vft.Id);columnView.Name=beams?"AC Beams Check":"AC Columns Check";columnView.DetailLevel=ViewDetailLevel.Fine;columnView.DisplayStyle=DisplayStyle.Shading;
                    var sourceView=new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v=>v.Name=="AC Import Diagnostic");
                    if(sourceView!=null)columnView.SetOrientation(sourceView.GetOrientation());
                    foreach(Category cat in document.Settings.Categories)
                        if(columnView.CanCategoryBeHidden(cat.Id))columnView.SetCategoryHidden(cat.Id,cat.Id!=new ElementId(category));
                    t.Commit();
                }
                document.Save();if(columnView!=null)ui.ActiveUIDocument.RequestViewChange(columnView);
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status=fallbackReport.Items.Any(i=>i.Status is "blocked" or "revitError")?"completeWithErrors":"complete",output,seconds=timer.Elapsed.TotalSeconds,
                    outcomes=fallbackReport.Items.GroupBy(i=>i.Status).ToDictionary(g=>g.Key,g=>g.Count()),
                    beams=beams?new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().GetElementCount():0,
                    beamDirectShapes=beams?new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralFraming).OfClass(typeof(DirectShape)).GetElementCount():0,
                    columns=columns?new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().GetElementCount():0,
                    nativeColumnInstances=columns?new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralColumns).OfClass(typeof(FamilyInstance)).GetElementCount():0,
                    columnDirectShapes=columns?new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralColumns).OfClass(typeof(DirectShape)).GetElementCount():0},Json.Options));
                return;
            }
            if(job.Operation=="geometryAudit")
            {
                var sourcePath=WorkspacePath(job.SourceRvt);
                var active=ui.ActiveUIDocument;
                if(active==null || !string.Equals(active.Document.PathName,sourcePath,StringComparison.OrdinalIgnoreCase))
                    active=ui.OpenAndActivateDocument(sourcePath);
                ISet<string>? keys=null;
                if(!string.IsNullOrEmpty(job.MeshKeysPath))keys=JsonSerializer.Deserialize<List<string>>(File.ReadAllText(WorkspacePath(job.MeshKeysPath)),Json.Options)!.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if(job.GeometryCategory is not ("walls" or "columns" or "beams" or "doors" or "windows"))throw new InvalidDataException("Unsupported geometry audit category");
                var result=GeometryAudit.Write(active.Document,WorkspacePath(job.ReportPath),keys,(done,total)=>
                    File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="running",phase="geometryAudit",done,total},Json.Options)),
                    job.GeometryCategory=="columns"?BuiltInCategory.OST_StructuralColumns:job.GeometryCategory=="beams"?BuiltInCategory.OST_StructuralFraming:
                    job.GeometryCategory=="doors"?BuiltInCategory.OST_Doors:job.GeometryCategory=="windows"?BuiltInCategory.OST_Windows:BuiltInCategory.OST_Walls);
                File.WriteAllText(resultPath,JsonSerializer.Serialize(new{status="complete",result,seconds=timer.Elapsed.TotalSeconds},Json.Options));
                return;
            }
            var manifestPath = WorkspacePath(job.ManifestPath);
            var outputPath = WorkspacePath(job.OutputRvt);
            if (File.Exists(outputPath)) throw new IOException("Diagnostic RVT already exists; choose a new path");
            if (job.Operation is not ("import" or "repairCopy")) throw new InvalidDataException("Unsupported diagnostic operation");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { status = "running", manifestPath, outputPath }, Json.Options));
            var manifest = Manifest.Load(manifestPath);
            var config = ImportConfig.Load(Path.Combine(Path.GetDirectoryName(manifestPath)!, "mapping.json"));
            if(job.Operation=="repairCopy")
            {
                var inputCopy=Path.ChangeExtension(outputPath,".input.rvt");
                File.Copy(WorkspacePath(job.SourceRvt),inputCopy,false);
                scratch=ui.Application.OpenDocumentFile(inputCopy);
            }
            else scratch = ui.Application.NewProjectDocument(WorkspacePath(job.TemplatePath));
            importer = new Importer(scratch, manifest, config);
            importer.Progress = (phase, done, total) => File.WriteAllText(resultPath,
                JsonSerializer.Serialize(new { status = "running", phase, done, total, seconds = timer.Elapsed.TotalSeconds }, Json.Options));
            var report = job.Operation=="repairCopy"?importer.RepairCopy():importer.Run();
            report.Save(WorkspacePath(job.ReportPath));
            using (var t = new Transaction(scratch, "Diagnostic 3D view"))
            {
                t.Start();
                var vft = new FilteredElementCollector(scratch).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                var view=new FilteredElementCollector(scratch).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v=>v.Name=="AC Import Diagnostic") ?? View3D.CreateIsometric(scratch,vft.Id);
                view.Name = "AC Import Diagnostic"; view.DetailLevel = ViewDetailLevel.Fine;
                var planType=new FilteredElementCollector(scratch).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v=>v.ViewFamily==ViewFamily.FloorPlan);
                if(planType!=null)
                    foreach(var level in new FilteredElementCollector(scratch).OfClass(typeof(Level)).Cast<Level>().ToList())
                    {
                        var tag=Storage.Read(level);
                        if(!tag.HasValue || tag.Value.Project!=manifest.Source.ProjectId || !tag.Value.Key.StartsWith("story:"))continue;
                        string name="AC Plan "+level.Name;
                        if(!new FilteredElementCollector(scratch).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Any(v=>v.Name==name))
                        {var plan=ViewPlan.Create(scratch,planType.Id,level.Id);plan.Name=name;plan.DetailLevel=ViewDetailLevel.Fine;}
                    }
                t.Commit();
            }
            scratch.SaveAs(outputPath, new SaveAsOptions { OverwriteExistingFile = false });
            var counts = new { walls = new FilteredElementCollector(scratch).OfClass(typeof(Wall)).GetElementCount(),
                floors = new FilteredElementCollector(scratch).OfClass(typeof(Floor)).GetElementCount(),
                rooms = new FilteredElementCollector(scratch).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().GetElementCount(),
                directShapes = new FilteredElementCollector(scratch).OfClass(typeof(DirectShape)).GetElementCount() };
            scratch.Close(false); scratch = null;
            var opened = ui.OpenAndActivateDocument(outputPath);
            string viewWarning = "";
            try
            {
                // Request a view change after API processing has finished.
                opened.RequestViewChange(new FilteredElementCollector(opened.Document).OfClass(typeof(View3D)).Cast<View3D>().First(v => v.Name == "AC Import Diagnostic"));
            }
            catch (Exception viewError) { viewWarning = viewError.Message; }
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { status = "complete", outputPath, counts, viewWarning, seconds = timer.Elapsed.TotalSeconds,
                outcomes = report.Items.GroupBy(i => i.Status).ToDictionary(g => g.Key, g => g.Count()) }, Json.Options));
        }
        catch (Exception ex)
        {
            try { importer?.Report.Save(Path.ChangeExtension(resultPath, ".report.json")); } catch { }
            if (scratch != null)
            {
                try { scratch.SaveAs(WorkspacePath(Path.ChangeExtension(resultPath, ".recovery.rvt")), new SaveAsOptions { OverwriteExistingFile = false }); scratch.Close(false); } catch { }
            }
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { status = "failed", error = ex.ToString(), seconds = timer.Elapsed.TotalSeconds }, Json.Options));
        }
        finally { busy = false; }
    }
    sealed class Job
    {
        public string Operation { get; set; } = "";
        public string ManifestPath { get; set; } = "";
        public string TemplatePath { get; set; } = "";
        public string OutputRvt { get; set; } = "";
        public string ReportPath { get; set; } = "";
        public string ResultPath { get; set; } = "";
        public string SourceRvt { get; set; } = "";
        public string MeshKeysPath { get; set; } = "";
        public string MeshManifestPath { get; set; } = "";
        public string ReferenceIFCPath { get; set; } = "";
        public string ReferenceIFCSha256 { get; set; } = "";
        public string ReferenceGeometryPath {get;set;}="";
        public string OpeningsPlanPath {get;set;}="";
        public string GeometryCategory { get; set; } = "walls";
        public string MappingPath {get;set;}="";
        public int Limit {get;set;}
    }
}
