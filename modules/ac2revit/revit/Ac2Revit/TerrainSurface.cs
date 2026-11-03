using Autodesk.Revit.DB;

namespace Ac2Revit;

/// <summary>Compare piecewise planar height fields on the complete triangle overlay.</summary>
public static class TerrainSurface
{
    public static double Area(IReadOnlyList<XYZ> p)=>p.Count<3?0:Math.Abs(p.Select((a,i)=>
        (a.X-p[0].X)*(p[(i+1)%p.Count].Y-p[0].Y)-(p[(i+1)%p.Count].X-p[0].X)*(a.Y-p[0].Y)).Sum())/2;
    static double Side(XYZ a,XYZ b,XYZ p)=>(b.X-a.X)*(p.Y-a.Y)-(b.Y-a.Y)*(p.X-a.X);
    static List<XYZ> Clip(XYZ[] source,XYZ[] target)
    {
        var output=source.ToList();double sign=Side(target[0],target[1],target[2])>=0?1:-1;
        for(int i=0;i<3 && output.Count>0;i++)
        {
            var a=target[i];var b=target[(i+1)%3];var input=output;output=[];
            var previous=input[^1];double old=sign*Side(a,b,previous);
            foreach(var current in input)
            {
                double next=sign*Side(a,b,current);
                if((next>=0)!=(old>=0))output.Add(previous+(current-previous)*(old/(old-next)));
                if(next>=0)output.Add(current);previous=current;old=next;
            }
        }
        return output;
    }
    static double Z(XYZ[] t,XYZ p)
    {var n=(t[1]-t[0]).CrossProduct(t[2]-t[0]);return t[0].Z-(n.X*(p.X-t[0].X)+n.Y*(p.Y-t[0].Y))/n.Z;}
    static List<(XYZ a,XYZ b)> Boundary(List<XYZ[]> triangles)
    {
        string Key(XYZ p)=>$"{Math.Round(p.X,6):R},{Math.Round(p.Y,6):R}";
        var edges=new Dictionary<string,(XYZ a,XYZ b,int count)>();
        foreach(var t in triangles)for(int i=0;i<3;i++)
        {
            var a=t[i];var b=t[(i+1)%3];string x=Key(a),y=Key(b),key=string.CompareOrdinal(x,y)<0?x+"/"+y:y+"/"+x;
            edges[key]=(a,b,edges.TryGetValue(key,out var old)?old.count+1:1);
        }
        return edges.Values.Where(e=>e.count==1).Select(e=>(e.a,e.b)).ToList();
    }
    static double Distance(XYZ p,(XYZ a,XYZ b) edge)
    {
        var d=edge.b-edge.a;double length=d.X*d.X+d.Y*d.Y;
        double u=length>0?Math.Clamp(((p.X-edge.a.X)*d.X+(p.Y-edge.a.Y)*d.Y)/length,0,1):0;
        return Math.Sqrt(Math.Pow(p.X-edge.a.X-u*d.X,2)+Math.Pow(p.Y-edge.a.Y-u*d.Y,2));
    }
    public static double Verify(List<XYZ[]> source,List<XYZ[]> target)
    {
        double area=source.Sum(Area),actual=target.Sum(Area),overlap=0,max=0;string worst="";
        foreach(var a in source)foreach(var b in target)
        {
            if(a.Max(p=>p.X)<b.Min(p=>p.X) || b.Max(p=>p.X)<a.Min(p=>p.X) || a.Max(p=>p.Y)<b.Min(p=>p.Y) || b.Max(p=>p.Y)<a.Min(p=>p.Y))continue;
            var polygon=Clip(a,b);if(polygon.Count<3)continue;
            double intersect=Area(polygon);if(intersect<1e-12)continue;
            overlap+=intersect;
            foreach(var p in polygon)
            {
                double difference=Math.Abs(Z(a,p)-Z(b,p));
                if(difference>max){max=difference;worst=$" XY {p.X:G12}/{p.Y:G12}, source Z={Z(a,p):G12}, native Z={Z(b,p):G12}, overlap area={intersect:G12}";}
            }
        }
        var boundary=Boundary(source);var nativeBoundary=Boundary(target);
        if(boundary.Count==0 || nativeBoundary.Count==0)throw new InvalidDataException("Terrain boundary missing");
        foreach(var (a,b) in boundary)
            foreach(var p in new[]{a,b,(a+b)/2})if(nativeBoundary.Min(e=>Distance(p,e))>Geometry.Feet(.001))throw new InvalidDataException("Toposolid boundary/hole differs by more than 1 mm");
        foreach(var (a,b) in nativeBoundary)
            foreach(var p in new[]{a,b,(a+b)/2})if(boundary.Min(e=>Distance(p,e))>Geometry.Feet(.001))throw new InvalidDataException("Toposolid introduced a boundary/hole");
        // Revit's face triangulation uses rounded coordinates. Allow only a 0.01 mm boundary strip in area arithmetic.
        double tolerance=Math.Max(1e-7,boundary.Sum(e=>e.a.DistanceTo(e.b))*Geometry.Feet(.00001));
        if(area<=0 || Math.Abs(area-overlap)>tolerance || Math.Abs(actual-overlap)>tolerance)
            throw new InvalidDataException($"Toposolid footprint differs from source, including boundaries/holes: source={area:G12}, native={actual:G12}, overlap={overlap:G12}, triangles={source.Count}/{target.Count}");
        if(max>Geometry.Feet(.001))throw new InvalidDataException($"Toposolid surface differs from source by more than 1 mm on the triangle overlay ({max:G12} ft): "+worst);
        return max;
    }
    public static List<XYZ[]> Triangles(Toposolid terrain)=>HostObjectUtils.GetTopFaces(terrain).Select(r=>(Face)terrain.GetGeometryObjectFromReference(r))
        .SelectMany(f=>
        {var mesh=f.Triangulate();return Enumerable.Range(0,mesh.NumTriangles).Select(i=>{var t=mesh.get_Triangle(i);return new[]{t.get_Vertex(0),t.get_Vertex(1),t.get_Vertex(2)};});}).ToList();
}
