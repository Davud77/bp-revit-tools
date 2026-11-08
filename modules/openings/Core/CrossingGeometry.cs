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
public sealed record CrossingOptions(double Clearance=25,double Step=10,double DepthMargin=10,bool IncludeInsulation=true,bool IncludeLinks=true,
    double ClearanceHeight=-1,double ClearanceDiameter=-1,double MinSize=0,bool RoundForRound=true,bool MergeAdjacent=false,double MergeGap=0)
{
    [System.Text.Json.Serialization.JsonIgnore] public double HeightClearance=>ClearanceHeight<0?Clearance:ClearanceHeight;
    [System.Text.Json.Serialization.JsonIgnore] public double DiameterClearance=>ClearanceDiameter<0?Clearance:ClearanceDiameter;
    public void Validate()
    {if(!new[]{ClearanceHeight,ClearanceDiameter,MinSize,MergeGap}.All(double.IsFinite)||ClearanceHeight>1000||ClearanceDiameter>1000||MinSize<0||MinSize>5000||MergeGap<0||MergeGap>5000)throw new InvalidOperationException("Запас по высоте и диаметру: до 1000 мм; минимальный размер и расстояние склейки: 0–5000 мм.");
        if(!new[]{Clearance,Step,DepthMargin}.All(double.IsFinite)||Clearance<0||Clearance>1000||Step<1||Step>1000||DepthMargin<1||DepthMargin>1000)throw new InvalidOperationException("Зазор: 0–1000 мм; округление и запас глубины: 1–1000 мм.");}
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
        double Extent(Vector3 axis,double clearance)
        {
            double ax=a*px.Dot(axis),by=b*py.Dot(axis);
            return (round?Math.Sqrt(ax*ax+by*by):Math.Abs(ax)+Math.Abs(by))+Math.Abs(d.Dot(axis)/dot)*thickness/2+clearance;
        }
        bool circular=round&&options.RoundForRound&&Math.Abs(dot)>1-1e-7;
        var result=new HoleGeometry{X=center.X,Y=center.Y,Z=center.Z,Orientation=orientation,Yaw=yaw,
            Shape=circular?HoleShape.Round:HoleShape.Rectangle,Width=Math.Max(HoleChecks.RoundUp(2*Extent(u,options.Clearance),options.Step),options.MinSize),Height=Math.Max(HoleChecks.RoundUp(2*Extent(v,options.HeightClearance),options.Step),options.MinSize),
            Diameter=Math.Max(HoleChecks.RoundUp(width+2*extra+2*options.DiameterClearance,options.Step),options.MinSize),Depth=HoleChecks.RoundUp(thickness+2*options.DepthMargin,options.Step)};
        result.Validate();return result;
    }
    static (double[] Min,double[] Max) FrameBounds(HoleGeometry g,double yaw)
    {
        double c=Math.Cos(yaw),s=Math.Sin(yaw);
        var points=g.Corners().Select(p=>g.Orientation==HoleOrientation.Wall?new[]{c*p[0]+s*p[1],p[2],-s*p[0]+c*p[1]}:new[]{c*p[0]+s*p[1],-s*p[0]+c*p[1],p[2]}).ToList();
        return (Enumerable.Range(0,3).Select(i=>points.Min(p=>p[i])).ToArray(),Enumerable.Range(0,3).Select(i=>points.Max(p=>p[i])).ToArray());
    }
    /// <summary>Группы отверстий одной основы и ориентации, расстояние между которыми по каждой оси не больше <paramref name="gap"/> (цепочки считаются одной группой).</summary>
    public static List<List<int>> GroupAdjacent(IReadOnlyList<HoleGeometry> holes,IReadOnlyList<string> hosts,double gap)
    {
        var parent=Enumerable.Range(0,holes.Count).ToArray();
        int Find(int i){while(parent[i]!=i)i=parent[i]=parent[parent[i]];return i;}
        for(int i=0;i<holes.Count;i++)for(int j=i+1;j<holes.Count;j++)
        {
            if(hosts[i]!=hosts[j]||holes[i].Orientation!=holes[j].Orientation||Math.Abs(Math.Sin(holes[i].Yaw-holes[j].Yaw))>1e-7)continue;
            var a=FrameBounds(holes[i],holes[i].Yaw);var b=FrameBounds(holes[j],holes[i].Yaw);bool near=true;
            for(int axis=0;axis<3&&near;axis++)near=Math.Max(0,Math.Max(a.Min[axis]-b.Max[axis],b.Min[axis]-a.Max[axis]))<=gap+1e-7;
            if(near)parent[Find(i)]=Find(j);
        }
        return Enumerable.Range(0,holes.Count).GroupBy(Find).Select(g=>g.ToList()).OrderBy(g=>g[0]).ToList();
    }
    /// <summary>Один прямоугольный габарит, охватывающий все отверстия группы (в системе первой основы).</summary>
    public static HoleGeometry Union(IReadOnlyList<HoleGeometry> holes)
    {
        var first=holes[0];double yaw=first.Yaw,c=Math.Cos(yaw),s=Math.Sin(yaw);
        var bounds=holes.Select(h=>FrameBounds(h,yaw)).ToList();
        var min=Enumerable.Range(0,3).Select(i=>bounds.Min(b=>b.Min[i])).ToArray();var max=Enumerable.Range(0,3).Select(i=>bounds.Max(b=>b.Max[i])).ToArray();
        var mid=Enumerable.Range(0,3).Select(i=>(min[i]+max[i])/2).ToArray();bool wall=first.Orientation==HoleOrientation.Wall;
        var result=first with{Shape=HoleShape.Rectangle,Width=max[0]-min[0],Height=max[1]-min[1],Depth=max[2]-min[2],
            X=c*mid[0]-s*(wall?mid[2]:mid[1]),Y=s*mid[0]+c*(wall?mid[2]:mid[1]),Z=wall?mid[1]:mid[2]};
        result.Validate();return result;
    }
    /// <summary>Кратчайшее расстояние между двумя отрезками и ближайшие точки на каждом.</summary>
    public static double SegmentDistance(Vector3 p1,Vector3 q1,Vector3 p2,Vector3 q2,out Vector3 near1,out Vector3 near2)
    {
        var d1=q1-p1;var d2=q2-p2;var r=p1-p2;double a=d1.Dot(d1),e=d2.Dot(d2),f=d2.Dot(r),s,t;const double eps=1e-12;
        if(a<=eps&&e<=eps){s=0;t=0;}
        else if(a<=eps){s=0;t=Math.Clamp(f/e,0,1);}
        else
        {
            double c=d1.Dot(r);
            if(e<=eps){t=0;s=Math.Clamp(-c/a,0,1);}
            else
            {
                double b=d1.Dot(d2),denominator=a*e-b*b;
                s=denominator>eps?Math.Clamp((b*f-c*e)/denominator,0,1):0;t=(b*s+f)/e;
                if(t<0){t=0;s=Math.Clamp(-c/a,0,1);}else if(t>1){t=1;s=Math.Clamp((b-c)/a,0,1);}
            }
        }
        near1=p1+d1*s;near2=p2+d2*t;return (near1-near2).Length;
    }
    public static string Key(string link,string host,string communication)=>HoleJson.Hash(new[]{link,host,communication});
}
