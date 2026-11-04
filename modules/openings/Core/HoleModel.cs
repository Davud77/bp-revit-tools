using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotPlus.Openings.Core;

public enum Role { Viewer, Engineer, Architect, Structural, Admin }
public enum HoleShape { Rectangle, Round }
public enum HoleOrientation { Wall, Floor }
public sealed record Actor(string User,Role Role,string ActingFor="")
{
    public string Owner=>string.IsNullOrWhiteSpace(ActingFor)?User:ActingFor;
}
public sealed record HoleGeometry
{
    public double X {get;init;}
    public double Y {get;init;}
    public double Z {get;init;}
    public double Yaw {get;init;}
    public double Width {get;init;}=300;
    public double Height {get;init;}=300;
    public double Diameter {get;init;}=200;
    public double Depth {get;init;}=300;
    public HoleShape Shape {get;init;}
    public HoleOrientation Orientation {get;init;}
    [JsonIgnore] public double A=>Shape==HoleShape.Round?Diameter:Width;
    [JsonIgnore] public double B=>Shape==HoleShape.Round?Diameter:Height;
    public void Validate()
    {
        if(!new[]{X,Y,Z,Yaw,Width,Height,Diameter,Depth}.All(double.IsFinite)||new[]{A,B,Depth}.Any(n=>n<1||n>100000)
            ||!Enum.IsDefined(Shape)||!Enum.IsDefined(Orientation))throw new InvalidOperationException("Проверьте координаты и размеры отверстия (1–100000 мм).");
    }
    public double[][] Corners()
    {
        var result=new List<double[]>();double c=Math.Cos(Yaw),s=Math.Sin(Yaw);
        foreach(int i in new[]{-1,1})foreach(int j in new[]{-1,1})foreach(int k in new[]{-1,1})
        {
            double u=i*A/2,v=j*B/2,n=k*Depth/2;
            result.Add(Orientation==HoleOrientation.Wall?[X+c*u-s*n,Y+s*u+c*n,Z+v]:[X+c*u-s*v,Y+s*u+c*v,Z+n]);
        }
        return result.ToArray();
    }
}
public sealed record Tolerance(double X=0,double Y=0,double Z=0,double Size=0)
{
    public bool Contains(HoleGeometry before,HoleGeometry after)=>before.Shape==after.Shape&&before.Orientation==after.Orientation
        &&Math.Abs(Math.Sin((before.Yaw-after.Yaw)/2))<1e-9&&Math.Abs(before.X-after.X)<=X+1e-7
        &&Math.Abs(before.Y-after.Y)<=Y+1e-7&&Math.Abs(before.Z-after.Z)<=Z+1e-7
        &&Math.Abs(before.A-after.A)<=Size+1e-7&&Math.Abs(before.B-after.B)<=Size+1e-7&&Math.Abs(before.Depth-after.Depth)<=Size+1e-7;
    public void Validate(){if(!new[]{X,Y,Z,Size}.All(n=>double.IsFinite(n)&&n>=0&&n<=10000))throw new InvalidOperationException("Допуски должны быть от 0 до 10000 мм.");}
}
public sealed record HoleEvent(DateTimeOffset Time,string User,Role Role,string Action,string Comment,int Revision,HoleGeometry Geometry,string ActingFor="")
{
    [JsonIgnore] public string TimeText=>Time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
    [JsonIgnore] public string RoleText=>Names.Role(Role);
}
public sealed record Signoff(Role Role,string User,DateTimeOffset Time);
public sealed record MergedHistory(string Id,List<HoleEvent> History);
public sealed class Hole
{
    public string Id {get;set;}=Guid.NewGuid().ToString("D");
    public string ProjectId {get;set;}="";
    public string Mark {get;set;}="";
    public string Discipline {get;set;}="ОВ";
    public string Assignment {get;set;}="АР";
    public string LevelId {get;set;}="";
    public string LevelName {get;set;}="";
    public double LevelElevation {get;set;}
    public string HostId {get;set;}="";
    public string HostLinkId {get;set;}="";
    public string CommunicationId {get;set;}="";
    public string CrossingKey {get;set;}="";
    public string CreatedBy {get;set;}="";
    public string ShaftId {get;set;}="";
    public HoleGeometry Geometry {get;set;}=new();
    public HoleGeometry? ApprovalAnchor {get;set;}
    public int Revision {get;set;}=1;
    public List<Signoff> Approvals {get;set;}=[];
    public List<HoleEvent> History {get;set;}=[];
    public List<MergedHistory> MergedFrom {get;set;}=[];
    public bool Rejected {get;set;}
    public bool DeletionRequested {get;set;}
    public bool Deleted {get;set;}
    public bool GeometryUnresolved {get;set;}
    public List<Role> DeleteRequired {get;set;}=[];
    public List<Role> DeleteAllowed {get;set;}=[];
    public bool Imported {get;set;}
    public string SourceDigest {get;set;}="";
    public string MarkerId {get;set;}="";
    public string MarkerHash {get;set;}="";
    public List<string> CutIds {get;set;}=[];
    public string CutGeometryHash {get;set;}="";
    public int HistoryStart {get;set;}
    [JsonIgnore] public string ShaftText=>ShaftId.Length==0?"—":"Шахта "+ShaftId[..Math.Min(8,ShaftId.Length)];
    [JsonIgnore] public string Key=>ProjectId+"/"+Id;
    [JsonIgnore] public bool FullyApproved=>!Deleted&&!DeletionRequested&&!Rejected&&!GeometryUnresolved
        &&new[]{Role.Engineer,Role.Architect,Role.Structural}.All(r=>Approvals.Any(a=>a.Role==r));
    [JsonIgnore] public string Status=>Deleted?"Удалено":DeletionRequested?"На удаление":GeometryUnresolved?"Изменено в модели":Rejected?"Отказ":FullyApproved?"Согласовано":Approvals.Count>0?"Частично согласовано":"Не согласовано";
    [JsonIgnore] public double Bottom=>Geometry.Z-(Geometry.Orientation==HoleOrientation.Wall?Geometry.B:Geometry.Depth)/2;
    [JsonIgnore] public string Size=>Geometry.Shape==HoleShape.Round?$"Ø{Geometry.Diameter:0.#}":$"{Geometry.Width:0.#} × {Geometry.Height:0.#}";
    [JsonIgnore] public string OrientationText=>Geometry.Orientation==HoleOrientation.Wall?"Стена":"Перекрытие";
    [JsonIgnore] public string ApprovalText=>string.Join(", ",Approvals.OrderBy(a=>a.Role).Select(a=>Names.Role(a.Role)));
    [JsonIgnore] public string CutStatus=>CutIds.Count==0?"Не вырезано":CutGeometryHash==HoleJson.Hash(Geometry)&&FullyApproved?"Вырезано":"Требует проверки выреза";
}
public sealed class HoleProject
{
    public string ProjectId {get;set;}=Guid.NewGuid().ToString("D");
    public string Name {get;set;}="";
    public Dictionary<string,Role> Users {get;set;}=new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string,List<string>> Delegates {get;set;}=new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string,Tolerance> Tolerances {get;set;}=[];
    public List<string> SettingsHistory {get;set;}=[];
    public bool AllowArchitectStructuralApproval {get;set;}
    public Tolerance ToleranceFor(HoleGeometry g)=>Tolerances.GetValueOrDefault(g.Orientation+"/"+g.Shape)??new();
    public Actor Actor(string user,string actingFor="")
    {
        var role=Users.FirstOrDefault(p=>p.Key.Equals(user,StringComparison.OrdinalIgnoreCase)).Value;
        if(actingFor.Length>0&&!(Delegates.FirstOrDefault(p=>p.Key.Equals(user,StringComparison.OrdinalIgnoreCase)).Value??[]).Contains(actingFor,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("Замещение этого пользователя не настроено.");
        return new(user,role,actingFor);
    }
}
public static class Names
{
    public static string Role(Role r)=>r switch{Core.Role.Engineer=>"ИОС",Core.Role.Architect=>"АР",Core.Role.Structural=>"КР",Core.Role.Admin=>"Администратор",_=>"Просмотр"};
}
public static class HoleJson
{
    public static readonly JsonSerializerOptions Options=new(){WriteIndented=true,PropertyNameCaseInsensitive=true,Converters={new JsonStringEnumConverter()}};
    public static string Write<T>(T value)=>JsonSerializer.Serialize(value,Options);
    public static T Read<T>(string value)=>JsonSerializer.Deserialize<T>(value,Options)??throw new InvalidDataException("Пустые данные менеджера отверстий.");
    public static T Clone<T>(T value)=>Read<T>(Write(value));
    public static string Hash<T>(T value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,Options)));
}

