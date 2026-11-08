using Autodesk.Revit.DB;
using BotPlus.Openings.Core;

namespace BotPlus.Openings;

internal sealed record TransferResult(int Copied,int Replaced,int Removed,int Skipped,string Target,List<string> Notes)
{
    public string Text=>$"Перенос в «{Target}»: скопировано {Copied}, заменено прежних копий {Replaced}, удалено устаревших {Removed}, пропущено {Skipped}."+(Notes.Count>0?Environment.NewLine+string.Join(Environment.NewLine,Notes.Take(8)):"");
}
/// <summary>Тела отверстий строятся в рабочем файле (маркеры DirectShape) и копируются в сводный файл между открытыми документами.</summary>
internal static class HoleTransfer
{
    internal static string Id(Document doc)=>doc.PathName.Length>0?doc.PathName:doc.Title;
    internal static List<LinkChoice> Targets(Document source)=>source.Application.Documents.Cast<Document>()
        .Where(d=>d.IsValidObject&&!d.Equals(source)&&!d.IsLinked&&!d.IsFamilyDocument&&!d.IsReadOnly).Select(d=>new LinkChoice(Id(d),d.Title)).ToList();
    internal static Document Find(Document source,string id)=>source.Application.Documents.Cast<Document>().FirstOrDefault(d=>d.IsValidObject&&!d.Equals(source)&&Id(d)==id)
        ??throw new InvalidOperationException("Сводный файл больше не открыт. Откройте его и обновите список.");
    internal static TransferResult Run(Document source,Document target,Registry data,IReadOnlyList<Hole> holes,bool approvedOnly)
    {
        if(target.Equals(source))throw new InvalidOperationException("Сводный файл должен отличаться от рабочего.");
        if(target.IsReadOnly||target.IsLinked)throw new InvalidOperationException("Сводный файл недоступен для записи.");
        var existing=new FilteredElementCollector(target).OfClass(typeof(DirectShape)).Cast<DirectShape>().Where(s=>s.ApplicationId==HoleStore.AppId).ToLookup(s=>s.ApplicationDataId);
        var send=new List<ElementId>();var obsolete=new HashSet<ElementId>();int skipped=0,replaced=0;var notes=new List<string>();
        foreach(var hole in holes)
        {
            var old=existing[hole.Key].Select(s=>s.Id).ToList();
            bool want=!hole.Deleted&&(!approvedOnly||hole.FullyApproved)&&!hole.GeometryUnresolved;
            if(!want){foreach(var id in old)obsolete.Add(id);if(!hole.Deleted)skipped++;continue;}
            var marker=HoleMarkers.Marker(source,hole);
            if(marker==null){skipped++;notes.Add($"{(hole.Mark.Length>0?hole.Mark:hole.Id)}: в рабочем файле нет тела отверстия.");continue;}
            send.Add(marker.Id);replaced+=old.Count;foreach(var id in old)obsolete.Add(id);
        }
        int copied=0,removed=0;
        HoleStore.InTransaction(target,"Отверстия — перенос из «"+source.Title+"»",()=>
        {
            foreach(var id in obsolete)if(target.GetElement(id)!=null){target.Delete(id);removed++;}
            if(send.Count>0)copied=ElementTransformUtils.CopyElements(source,send,target,Transform.Identity,new CopyPasteOptions()).Count;
        });
        removed-=replaced;
        return new(copied,replaced,Math.Max(removed,0),skipped,target.Title,notes);
    }
}
