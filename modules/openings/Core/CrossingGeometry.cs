namespace BotPlus.Openings.Core;

public readonly record struct Vector3(double X,double Y,double Z)
{
    public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static Vector3 operator *(Vector3 a,double n)=>new(a.X*n,a.Y*n,a.Z*n);
    public double Dot(Vector3 b)=>X*b.X+Y*b.Y+Z*b.Z;
    public double Length=>Math.Sqrt(Dot(this));
    public Vector3 Unit()=>Length>1e-10?this*(1/Length):throw new InvalidOperationException("Нулевое направление коммуникации.");
}
public sealed record CrossingOptions(double Clearance=25,double Step=10,double DepthMargin=10,bool IncludeInsulation=true,bool IncludeLinks=true)
{
    public void Validate()
    {if(!new[]{Clearance,Step,DepthMargin}.All(double.IsFinite)||Clearance<0||Clearance>1000||Step<1||Step>1000||DepthMargin<1||DepthMargin>1000)throw new InvalidOperationException("Зазор: 0–1000 мм; округление и запас глубины: 1–1000 мм.");}
}
public static class CrossingGeometry
{
    // Projects a straight prism through the full host thickness, including oblique crossings.
    public static HoleGeometry Fit(Vector3 center,Vector3 direction,Vector3 sectionX,Vector3 sectionY,
        double width,double height,bool round,double insulation,double thickness,HoleOrientation orientation,double yaw,CrossingOptions options)
    {
        options.Validate();var d=direction.Unit();var x=sectionX.Unit();var y=sectionY.Unit();
        if(!new[]{width,height,insulation,thickness,center.X,center.Y,center.Z,yaw}.All(double.IsFinite)||width<=0||height<=0||insulation<0||thickness<1
            ||Math.Abs(x.Dot(d))>1e-6||Math.Abs(y.Dot(d))>1e-6||Math.Abs(x.Dot(y))>1e-6)throw new InvalidOperationException("Не определён корректный профиль коммуникации.");
        var u=new Vector3(Math.Cos(yaw),Math.Sin(yaw),0);
        var v=orientation==HoleOrientation.Wall?new Vector3(0,0,1):new Vector3(-Math.Sin(yaw),Math.Cos(yaw),0);
        var n=orientation==HoleOrientation.Wall?new Vector3(-Math.Sin(yaw),Math.Cos(yaw),0):new Vector3(0,0,1);
        double dot=n.Dot(d);if(Math.Abs(dot)<0.2)throw new InvalidOperationException("Слишком острое пересечение: требуется ручное задание.");
        double extra=options.IncludeInsulation?insulation:0,a=width/2+extra,b=height/2+extra;
        var px=x-d*(n.Dot(x)/dot);var py=y-d*(n.Dot(y)/dot);
        double Extent(Vector3 axis)
        {
            double ax=a*px.Dot(axis),by=b*py.Dot(axis);
            return (round?Math.Sqrt(ax*ax+by*by):Math.Abs(ax)+Math.Abs(by))+Math.Abs(d.Dot(axis)/dot)*thickness/2+options.Clearance;
        }
        bool circular=round&&Math.Abs(dot)>1-1e-7;
        var result=new HoleGeometry{X=center.X,Y=center.Y,Z=center.Z,Orientation=orientation,Yaw=yaw,
            Shape=circular?HoleShape.Round:HoleShape.Rectangle,Width=HoleChecks.RoundUp(2*Extent(u),options.Step),Height=HoleChecks.RoundUp(2*Extent(v),options.Step),
            Diameter=HoleChecks.RoundUp(width+2*extra+2*options.Clearance,options.Step),Depth=HoleChecks.RoundUp(thickness+2*options.DepthMargin,options.Step)};
        result.Validate();return result;
    }
    public static string Key(string link,string host,string communication)=>HoleJson.Hash(new[]{link,host,communication});
}
