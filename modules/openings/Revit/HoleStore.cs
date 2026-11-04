using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using BotPlus.Openings.Core;

namespace BotPlus.Openings;

internal sealed class Registry
{
    public HoleProject? Project {get;set;}
    public List<Hole> Holes {get;set;}=[];
}
internal static class HoleStore
{
    internal static readonly Guid SchemaId=new("E51A459B-5FA4-4998-A8F1-C0ECB6A1EC13");
    internal const string AppId="BotPlus.Openings";
    internal static bool Writing;
    static Schema Schema()
    {
        var schema=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);if(schema!=null)return schema;
        var builder=new SchemaBuilder(SchemaId);builder.SetSchemaName("BotPlusOpeningsV1");builder.SetReadAccessLevel(AccessLevel.Public);builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("Key",typeof(string));builder.AddSimpleField("Json",typeof(string));return builder.Finish();
    }
    internal static Dictionary<string,DataStorage> Index(Document doc)
    {
        var result=new Dictionary<string,DataStorage>(StringComparer.OrdinalIgnoreCase);
        var schema=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);if(schema==null)return result;
        foreach(var item in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).WherePasses(new ExtensibleStorageFilter(SchemaId)).Cast<DataStorage>())
        {
            string key=item.GetEntity(schema).Get<string>(schema.GetField("Key"));if(!result.TryAdd(key,item))throw new InvalidDataException("Дублирован ключ реестра отверстий: "+key);
        }
        return result;
    }
    internal static Registry Read(Document doc)
    {
        var index=Index(doc);var schema=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);var result=new Registry();if(schema==null)return result;
        string Read(DataStorage item)=>item.GetEntity(schema).Get<string>(schema.GetField("Json"));
        if(index.TryGetValue("project",out var project))result.Project=HoleJson.Read<HoleProject>(Read(project));
        result.Holes=index.Where(p=>p.Key.StartsWith("hole:")).Select(p=>HoleJson.Read<Hole>(Read(p.Value))).ToList();return result;
    }
    internal static void Save(Document doc,Registry registry)
    {
        if(!doc.IsModifiable)throw new InvalidOperationException("Для записи реестра нужна транзакция Revit.");
        var index=Index(doc);var schema=Schema();
        void Write(string key,string json)
        {
            if(index.TryGetValue(key,out var item)&&item.GetEntity(schema).Get<string>(schema.GetField("Json"))==json)return;
            if(item==null){item=DataStorage.Create(doc);item.Name=key=="project"?"BotPlus — Реестр отверстий":"BotPlus — Задание отверстия";index.Add(key,item);}
            var entity=new Entity(schema);entity.Set(schema.GetField("Key"),key);entity.Set(schema.GetField("Json"),json);item.SetEntity(entity);
        }
        if(registry.Project!=null)Write("project",HoleJson.Write(registry.Project));foreach(var h in registry.Holes)Write("hole:"+h.Key,HoleJson.Write(h));
    }
    internal static void InTransaction(Document doc,string title,Action action)
    {
        Writing=true;
        try
        {
            using var transaction=new Transaction(doc,title);transaction.Start();var failures=new Failures();
            transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
            action();if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Revit отменил операцию: "+string.Join("; ",failures.Messages));
        }
        finally{Writing=false;}
    }
    internal static string User=>Environment.UserName;
    internal static Actor Actor(Registry data)=>data.Project?.Actor(User)??new(User,Role.Viewer);
    internal static void TagFamily(Family family,string signature)
    {var schema=Schema();var entity=new Entity(schema);entity.Set(schema.GetField("Key"),"family:"+signature);entity.Set(schema.GetField("Json"),signature);family.SetEntity(entity);}
    internal static bool OwnFamily(Family family,string signature)
    {var schema=Schema();var entity=family.GetEntity(schema);return entity.IsValid()&&entity.Get<string>(schema.GetField("Key"))=="family:"+signature;}
    sealed class Failures:IFailuresPreprocessor
    {
        internal List<string> Messages {get;}=[];
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool error=false;foreach(var f in accessor.GetFailureMessages())
            {Messages.Add(f.GetDescriptionText());if(f.GetSeverity()==FailureSeverity.Warning){System.Diagnostics.Trace.WriteLine(f.GetDescriptionText());accessor.DeleteWarning(f);}else error=true;}
            return error?FailureProcessingResult.ProceedWithRollBack:FailureProcessingResult.Continue;
        }
    }
}
