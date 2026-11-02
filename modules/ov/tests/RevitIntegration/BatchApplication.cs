using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using HeatLossViewer.Export;

namespace RevitIntegration;

/// <summary>Opt-in read-only integration checks, enabled only by HLV_BATCH_CONFIG in a dedicated Revit process.</summary>
public sealed class BatchApplication : IExternalApplication
{
    private string? config;
    public Result OnStartup(UIControlledApplication app)
    {
        config=Environment.GetEnvironmentVariable("HLV_BATCH_CONFIG");
        if(!string.IsNullOrEmpty(config)) app.Idling+=Run;
        return Result.Succeeded;
    }
    public Result OnShutdown(UIControlledApplication app) { app.Idling-=Run; return Result.Succeeded; }
    private void Run(object? sender, IdlingEventArgs args)
    {
        if(sender is not UIApplication ui || config is null) return;
        ui.Idling-=Run;
        var settings=JsonSerializer.Deserialize<BatchConfig>(File.ReadAllText(config))!;
        Directory.CreateDirectory(settings.OutputDirectory);
        var reports=new List<object>();
        foreach(string path in settings.Files)
        {
            string name=Path.GetFileNameWithoutExtension(path);
            File.WriteAllText(Path.Combine(settings.OutputDirectory,"progress.txt"),name);
            Document? doc=null; bool owned=false;
            try
            {
                doc=ui.Application.Documents.Cast<Document>().FirstOrDefault(d=>string.Equals(d.PathName,path,StringComparison.OrdinalIgnoreCase));
                if(doc is null) { doc=ui.Application.OpenDocumentFile(path);owned=true; }
                var expected=new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .OfType<Autodesk.Revit.DB.Architecture.Room>().Where(r=>r.Area>0).ToList();
                var data=new RevitRoomExtractor().Extract(doc);
                var output=Path.Combine(settings.OutputDirectory,name);
                var written=new BuildingExportWriter().Write(data,output);
                var modelOpeningElements=new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(new[]{BuiltInCategory.OST_Windows,BuiltInCategory.OST_Doors})).WhereElementIsNotElementType().ToList();
                reports.Add(new {file=path,expectedRooms=expected.Count,exportedRooms=data.Rooms.Count,
                    expectedArea=expected.Sum(r=>UnitUtils.ConvertFromInternalUnits(r.Area,UnitTypeId.SquareMeters)),
                    exportedArea=data.Rooms.Sum(r=>r.Area),surfaces=data.Rooms.Sum(r=>r.Surfaces.Count),
                    expectedOpenings=modelOpeningElements.Count,windows=data.Openings.Count(o=>o.Kind=="window"),doors=data.Openings.Count(o=>o.Kind=="door"),
                    openingClasses=modelOpeningElements.GroupBy(e=>e.GetType().Name).Select(g=>new {name=g.Key,count=g.Count()}),
                    associatedOpenings=data.Openings.Count(o=>o.RoomKeys.Count>0),warnings=data.Warnings,html=written.Html});
            }
            catch(Exception ex) { reports.Add(new {file=path,error=ex.ToString()}); }
            finally { if(owned) doc?.Close(false); }
            File.WriteAllText(Path.Combine(settings.OutputDirectory,"report.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
        }
        File.WriteAllText(Path.Combine(settings.OutputDirectory,"complete.txt"),DateTimeOffset.Now.ToString("O"));
    }
    public sealed class BatchConfig
    {
        public string OutputDirectory {get;set;}="";
        public List<string> Files {get;set;}=[];
    }
}
