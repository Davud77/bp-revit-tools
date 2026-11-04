namespace BotPlus.Openings.Core;

public sealed record HoleIssue(string First,string Second,string Level,string Message);
public static class HoleChecks
{
    public static double RoundUp(double value,double step)
    {if(!double.IsFinite(value)||!double.IsFinite(step)||value<0||step<0)throw new ArgumentOutOfRangeException();return step==0?value:Math.Ceiling(value/step-1e-10)*step;}
    public static List<HoleIssue> Spacing(IEnumerable<Hole> source,Tolerance gap)
    {
        gap.Validate();var holes=source.Where(h=>!h.Deleted&&!h.DeletionRequested).ToList();var result=new List<HoleIssue>();
        for(int i=0;i<holes.Count;i++)for(int j=i+1;j<holes.Count;j++)
        {
            var a=holes[i];var b=holes[j];if(a.Geometry.Orientation!=b.Geometry.Orientation)continue;
            var ap=a.Geometry.Corners();var bp=b.Geometry.Corners();
            double Gap(int n)=>Math.Max(0,Math.Max(ap.Min(p=>p[n])-bp.Max(p=>p[n]),bp.Min(p=>p[n])-ap.Max(p=>p[n])));
            if(Gap(0)<=gap.X&&Gap(1)<=gap.Y&&Gap(2)<=gap.Z)result.Add(new(a.Key,b.Key,a.LevelName,$"Близко по габаритам: X {Gap(0):0.#}, Y {Gap(1):0.#}, Z {Gap(2):0.#} мм. Проверьте взаимное положение."));
        }
        return result;
    }
    public static void SetShaft(IEnumerable<Hole> holes,Actor actor,string? shaftId=null)
    {
        var list=holes.ToList();if(list.Count<2)throw new InvalidOperationException("Выберите минимум два отверстия.");
        if(actor.Role is not (Role.Engineer or Role.Admin))throw new InvalidOperationException("Шахты создаёт инженер.");
        if(list.Any(h=>h.Imported||h.Deleted||h.Geometry.Orientation!=HoleOrientation.Floor))throw new InvalidOperationException("В шахту объединяются собственные действующие отверстия в перекрытиях.");
        string id=shaftId??Guid.NewGuid().ToString("D");foreach(var h in list){h.ShaftId=id;h.Revision++;HoleWorkflow.Log(h,actor,"Добавлено в шахту",id);}
    }
    public static void RemoveFromShaft(IEnumerable<Hole> holes,Actor actor)
    {
        if(actor.Role is not (Role.Engineer or Role.Admin))throw new InvalidOperationException("Шахтами управляет инженер.");
        foreach(var h in holes){if(h.Imported)throw new InvalidOperationException("Измените шахту в исходном реестре.");string old=h.ShaftId;h.ShaftId="";h.Revision++;HoleWorkflow.Log(h,actor,"Удалено из шахты",old);}
    }
}
