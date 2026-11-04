namespace BotPlus.Openings.Core;

public sealed class HolePackage
{
    public string Format {get;set;}="botplus.openings/1";
    public string Units {get;set;}="mm";
    public string ProjectId {get;set;}="";
    public string ProjectName {get;set;}="";
    public DateTimeOffset ExportedAt {get;set;}=DateTimeOffset.UtcNow;
    public List<Hole> Holes {get;set;}=[];
    public void Validate()
    {
        if(Format!="botplus.openings/1"||Units!="mm"||!Guid.TryParse(ProjectId,out _)||Holes.Count>20000)throw new InvalidDataException("Неподдерживаемый файл заданий отверстий.");
        var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var h in Holes)
        {
            if(!Guid.TryParse(h.Id,out _)||h.ProjectId!=ProjectId||!keys.Add(h.Key)||h.Revision<1||h.History.Count>100000
                ||h.Approvals.Select(a=>a.Role).Distinct().Count()!=h.Approvals.Count||h.Approvals.Any(a=>a.Role is not (Role.Engineer or Role.Architect or Role.Structural)))throw new InvalidDataException("Некорректная идентичность или согласования задания.");
            h.Geometry.Validate();
        }
    }
}
public static class HoleExchange
{
    public static HolePackage Export(HoleProject project,IEnumerable<Hole> holes)
    {
        var package=new HolePackage{ProjectId=project.ProjectId,ProjectName=project.Name};
        foreach(var original in holes.Where(h=>!h.Imported&&h.ProjectId==project.ProjectId))
        {
            var h=HoleJson.Clone(original);h.MarkerId="";h.MarkerHash="";h.CutIds.Clear();h.CutGeometryHash="";h.SourceDigest="";package.Holes.Add(h);
        }
        package.Validate();return package;
    }
    public static (List<Hole> Holes,List<string> Changes) Import(HoleProject target,IEnumerable<Hole> local,HolePackage package,bool approvedOnly=true,Func<HoleGeometry,HoleGeometry>? transform=null)
    {
        package.Validate();if(package.ProjectId==target.ProjectId)throw new InvalidOperationException("Это файл текущего реестра, а не другой модели.");
        var result=local.Select(HoleJson.Clone).ToList();var existing=result.ToDictionary(h=>h.Key,StringComparer.OrdinalIgnoreCase);var changes=new List<string>();
        foreach(var source in package.Holes)
        {
            string digest=HoleJson.Hash(source);var geometry=transform?.Invoke(source.Geometry)??source.Geometry;geometry.Validate();
            existing.TryGetValue(source.Key,out var old);
            if(old==null&&approvedOnly&&!source.FullyApproved&&!source.Deleted)continue;
            if(old!=null)
            {
                if(!old.Imported||old.GeometryUnresolved){changes.Add("Конфликт локальных изменений: "+source.Id);continue;}
                if(source.Revision<old.Revision){changes.Add("Пропущена устаревшая ревизия: "+source.Id);continue;}
                if(source.Revision==old.Revision&&digest!=old.SourceDigest){changes.Add("Конфликт одинаковых ревизий: "+source.Id);continue;}
                if(digest==old.SourceDigest&&geometry==old.Geometry){changes.Add("Без изменений: "+source.Id);continue;}
            }
            var h=HoleJson.Clone(source);h.Imported=true;h.SourceDigest=digest;h.Geometry=geometry;
            h.MarkerId=old?.MarkerId??"";h.MarkerHash=old?.MarkerHash??"";h.CutIds=old?.CutIds.ToList()??[];h.CutGeometryHash=old?.CutGeometryHash??"";
            if(old!=null)result[result.IndexOf(old)]=h;else result.Add(h);
            existing[h.Key]=h;changes.Add((old==null?"Добавлено: ":"Обновлено: ")+source.Id);
        }
        return(result,changes);
    }
}
