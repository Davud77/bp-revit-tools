namespace Ac2Revit;

public sealed class DimensionWitness
{
    public string SourceGuid {get;set;}="";
    public Point Coordinate {get;set;}=new();
    public double DimensionValue {get;set;}
}
public sealed class SourceDimension
{
    public Point Direction {get;set;}=new();
    public Point LinePosition {get;set;}=new();
    public List<DimensionWitness> Witnesses {get;set;}=[];
    public void Validate()
    {
        double norm=Math.Sqrt(Direction.X*Direction.X+Direction.Y*Direction.Y);
        if(!Direction.Finite||!LinePosition.Finite||norm<1e-9||Witnesses.Count<2||Witnesses.Count>500||Witnesses.Any(w=>!Guid.TryParse(w.SourceGuid,out _)||!w.Coordinate.Finite||!double.IsFinite(w.DimensionValue)))throw new InvalidDataException("Нужна линейная размерная цепь с конечными координатами и GUID всех опор.");
        var positions=Witnesses.Select(w=>(w.Coordinate.X*Direction.X+w.Coordinate.Y*Direction.Y)/norm).Order().ToArray();
        if(positions.Zip(positions.Skip(1),(a,b)=>b-a).Any(n=>n<1e-6))throw new InvalidDataException("Совпадающие опоры размерной цепи.");
    }
}
