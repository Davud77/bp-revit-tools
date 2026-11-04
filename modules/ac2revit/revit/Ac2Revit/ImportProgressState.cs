namespace Ac2Revit;

/// <summary>Stage progress is not elapsed-time progress; survives checkpoint reopens.</summary>
public sealed class ImportProgressState
{
    public double Percent {get;private set;}
    public int Stage {get;private set;}
    public int Stages {get;private set;}
    public string StageName {get;private set;}="Подготовка";
    public string Phase {get;private set;}="Подготовка";
    public int Done {get;private set;}
    public int Total {get;private set;}
    public bool Complete {get;private set;}
    double localHigh;
    public void Update(string phase,int done,int total)
    {
        if(Complete)return;
        if(phase=="pipeline:start"){Stages=Math.Max(1,total);return;}
        if(phase=="pipeline:complete"){Percent=100;Complete=true;Done=Total;return;}
        if(phase.StartsWith("pipeline:stage:",StringComparison.Ordinal))
        {Stages=Math.Max(1,total);Stage=Math.Clamp(done,0,Stages-1);StageName=phase[15..];Phase=StageName;Done=Total=0;localHigh=0;Percent=Math.Max(Percent,100d*Stage/Stages);return;}
        if(phase.StartsWith("pipeline:end:",StringComparison.Ordinal))
        {Percent=Math.Max(Percent,Math.Min(99.9,100d*Math.Clamp(done,0,Math.Max(1,total))/Math.Max(1,total)));return;}
        Phase=phase;Done=Math.Max(0,done);Total=Math.Max(0,total);
        if(phase=="memoryCheckpoint")return;
        if(Stages>0&&Total>0){localHigh=Math.Max(localHigh,Math.Clamp((double)Done/Total,0,1));Percent=Math.Max(Percent,100d*(Stage+localHigh*0.9)/Stages);}
    }
}
