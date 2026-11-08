using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace BotPlus.Openings;

internal sealed class HoleMonitor:IExternalEventHandler
{
    static readonly Dictionary<Document,HashSet<long>> tracked=[];
    static readonly HashSet<Document> dirty=[];
    static ExternalEvent? external;
    internal static void Start(UIControlledApplication app)
    {external=ExternalEvent.Create(new HoleMonitor());app.ControlledApplication.DocumentChanged+=Changed;app.ControlledApplication.DocumentOpened+=Opened;app.Idling+=OnIdle;}
    static void OnIdle(object? sender,Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {if(sender is UIApplication ui)foreach(var controller in App.Controllers.ToArray())try{controller.SyncSelection(ui);}catch(Exception error){System.Diagnostics.Trace.WriteLine(error);}}
    internal static void Stop(UIControlledApplication app)
    {app.ControlledApplication.DocumentChanged-=Changed;app.ControlledApplication.DocumentOpened-=Opened;app.Idling-=OnIdle;external?.Dispose();external=null;tracked.Clear();dirty.Clear();}
    static void Opened(object? sender,DocumentOpenedEventArgs e)=>Track(e.Document);
    internal static void Track(Document doc)
    {var data=HoleStore.Read(doc);if(data.Project==null)return;tracked[doc]=data.Holes.Select(h=>HoleMarkers.Marker(doc,h)?.Id.Value??-1).Where(id=>id>0).ToHashSet();}
    static void Changed(object? sender,DocumentChangedEventArgs e)
    {
        if(HoleStore.Writing)return;var doc=e.GetDocument();
        if(!tracked.TryGetValue(doc,out var ids)||!e.GetModifiedElementIds().Concat(e.GetDeletedElementIds()).Any(id=>ids.Contains(id.Value)))return;
        dirty.Add(doc);external?.Raise();
    }
    internal static int Reconcile(Document doc,Registry data)
    {
        var changed=new List<Core.Hole>();
        foreach(var hole in data.Holes.Where(h=>!h.Deleted&&!h.GeometryUnresolved))
        {
            var marker=HoleMarkers.Marker(doc,hole);string actual=marker==null?"missing":HoleMarkers.Fingerprint(marker);
            if(actual==hole.MarkerHash)continue;
            Core.HoleWorkflow.MarkExternalChange(hole,HoleStore.Actor(data),marker==null?"Маркер удалён вне менеджера.":"Маркер перемещён или изменён вне менеджера. Проверьте положение и размеры перед повторным согласованием.");
            hole.MarkerHash=actual;changed.Add(hole);
        }
        if(changed.Count>0)HoleStore.InTransaction(doc,"Отверстия — изменения в модели",()=>HoleStore.Save(doc,data));
        return changed.Count;
    }
    public string GetName()=>"BotPlus — контроль изменений отверстий";
    public void Execute(UIApplication app)
    {
        foreach(var doc in dirty.ToArray())
        {
            dirty.Remove(doc);if(!doc.IsValidObject||doc.IsReadOnly)continue;
            try{var data=HoleStore.Read(doc);Reconcile(doc,data);Track(doc);}
            catch(Exception error){System.Diagnostics.Trace.WriteLine(error);}
        }
        foreach(var controller in App.Controllers.ToArray())if(controller.Document.IsValidObject)controller.Refresh();
    }
}
