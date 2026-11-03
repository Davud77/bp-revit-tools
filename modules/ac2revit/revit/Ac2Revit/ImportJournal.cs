using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.ExtensibleStorage;
using System.Text;
using System.Text.Json;

namespace Ac2Revit;

internal static class ImportJournal
{
    public static void Register(Document doc,Manifest manifest,ImportSelection? selection)
    {
        const string key="import-journal-source";
        using var t=new Transaction(doc,"Register import journal");t.Start();
        var state=Storage.Index(doc,manifest.Source.ProjectId).GetValueOrDefault(key) as DataStorage??DataStorage.Create(doc);
        var rows=manifest.Elements.Select(e=>new{guid=e.SourceGuid,kind=e.Kind,mark=e.Mark,selected=selection==null||selection.Includes(e.Kind)}).ToArray();
        Storage.Tag(state,manifest.Source.ProjectId,key,Json.Hash(rows),JsonSerializer.Serialize(rows,Json.Options));t.Commit();
    }
    public static void Saved(object? sender,DocumentSavedEventArgs e)=>WriteSafe(e.Document);
    public static void SavedAs(object? sender,DocumentSavedAsEventArgs e)=>WriteSafe(e.Document,e.OriginalPath);
    static void WriteSafe(Document doc,string previous=""){try{Write(doc,null,previous);}catch(Exception ex){System.Diagnostics.Trace.WriteLine("Import journal: "+ex.Message);}}
    public static void Write(Document doc,Report? report=null,string originalPath="")
    {
        if(string.IsNullOrEmpty(doc.PathName))return;
        var schema=Storage.Schema();var sources=new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Where(e=>Storage.Read(e)?.Key=="import-journal-source").ToArray();
        if(sources.Length==0)return;
        var all=new List<object>();var text=new StringBuilder($"BotPlus RT — журнал импорта\nRVT: {doc.PathName}\nСохранено: {DateTimeOffset.Now:O}\n");
        foreach(var source in sources)
        {
            string project=Storage.Read(source)!.Value.Project;var index=Storage.Index(doc,project);
            using var raw=JsonDocument.Parse(source.GetEntity(schema).Get<string>(schema.GetField("RawJson")));
            var rows=raw.RootElement.EnumerateArray().Select(r=>new{guid=r.S("guid"),kind=r.S("kind"),mark=r.S("mark"),selected=r.B("selected"),native=index.GetValueOrDefault(r.S("guid"))}).ToArray();
            foreach(var group in rows.GroupBy(r=>r.kind))text.AppendLine($"{group.Key}: исходных {group.Count()}, выбрано {group.Count(r=>r.selected)}, перенесено {group.Count(r=>r.native!=null)}, отсутствует из выбранных {group.Count(r=>r.selected&&r.native==null)}");
            foreach(var row in rows)
            {
                var result=new{project,row.guid,row.kind,row.mark,row.selected,represented=row.native!=null,revitId=row.native?.Id.Value,representation=row.native?.GetType().Name};all.Add(result);
                text.AppendLine($"{row.kind}\t{row.mark}\t{row.guid}\t{(row.native==null?row.selected?"НЕ ПЕРЕНЕСЕНО":"НЕ ВЫБРАНО":row.native.GetType().Name+" #"+row.native.Id.Value)}");
            }
        }
        string prefix=Path.ChangeExtension(doc.PathName,"botplus-import");
        var outcomes=report?.Items??[];
        string previousJournal=prefix+".json";
        if(!File.Exists(previousJournal) && !string.IsNullOrEmpty(originalPath))previousJournal=Path.ChangeExtension(originalPath,"botplus-import.json");
        if(report==null && File.Exists(previousJournal))
        {
            try{using var previous=JsonDocument.Parse(File.ReadAllText(previousJournal));if(previous.RootElement.TryGetProperty("outcomes",out var saved)&&saved.ValueKind==JsonValueKind.Array)outcomes=saved.Read<List<Outcome>>();}
            catch(JsonException){text.AppendLine("Предыдущий файл журнала повреждён; сохранена текущая инвентаризация модели.");}
        }
        foreach(var item in outcomes)text.AppendLine($"{item.Kind}\t{item.SourceKey}\t{item.Status}\t{item.Message}");
        File.WriteAllText(prefix+".log",text.ToString(),Encoding.UTF8);
        File.WriteAllText(prefix+".json",JsonSerializer.Serialize(new{saved=DateTimeOffset.Now,document=doc.PathName,elements=all,outcomes},Json.Options),Encoding.UTF8);
    }
}
