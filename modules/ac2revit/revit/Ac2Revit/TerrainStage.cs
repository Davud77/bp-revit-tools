using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>Transfer the upward terrain surface, retaining concave boundaries and holes.</summary>
public static class TerrainStage
{
    public static Report Run(Document doc,string path,Manifest manifest)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(path));var root=json.RootElement;
        string project=root.S("projectId");
        VerifiedReferenceScope.Verify(root.S("ifcPath"),root.S("ifcSha256"));
        var geometry=new Geometry(doc,root.GetProperty("coordinates").Read<CoordinateInfo>().Transform);
        var index=Storage.Index(doc,project);var report=new Report{ProjectId=project,Mode="nativeToposolid"};
        foreach(var row in root.GetProperty("elements").EnumerateArray())
        {
            string key=row.S("sourceGuid");
            using var transaction=new Transaction(doc,"АС — Топотело");
            try
            {
                if(index.ContainsKey(key))continue;
                var source=manifest.Elements.Single(e=>e.SourceGuid.Equals(key,StringComparison.OrdinalIgnoreCase));
                if(source.Kind!="Mesh")throw new InvalidDataException("Only Archicad Mesh can become terrain");
                if(index.GetValueOrDefault(source.LevelKey) is not Level level)throw new InvalidDataException("Terrain story missing");
                var type=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).FirstElementId();
                if(type==ElementId.InvalidElementId)throw new InvalidDataException("Template contains no Toposolid type");
                var original=row.GetProperty("vertices").Read<List<Point>>().Select(geometry.Xyz).ToList();
                string Vertex(XYZ p)=>$"{Math.Round(p.X,8)},{Math.Round(p.Y,8)},{Math.Round(p.Z,8)}";
                var points=original.GroupBy(Vertex).Select(g=>g.First()).ToList();
                var ids=points.Select((p,i)=>(key:Vertex(p),i)).ToDictionary(x=>x.key,x=>x.i);
                var triangles=row.GetProperty("faces").Read<List<List<int>>>().Select(f=>f.Select(i=>ids[Vertex(original[i])]).ToArray())
                    .Where(f=>f.Length==3 && (points[f[1]]-points[f[0]]).CrossProduct(points[f[2]]-points[f[0]]).Z>1e-10).ToList();
                if(triangles.Count==0)throw new InvalidDataException("No upward terrain surface");
                var edges=new Dictionary<(int,int),int>();
                foreach(var f in triangles)for(int i=0;i<3;i++){int a=f[i],b=f[(i+1)%3];var edge=(Math.Min(a,b),Math.Max(a,b));edges[edge]=edges.GetValueOrDefault(edge)+1;}
                if(edges.Values.Any(n=>n>2))throw new InvalidDataException("Non-manifold terrain surface");
                var boundary=edges.Where(e=>e.Value==1).Select(e=>e.Key).ToHashSet();
                var neighbours=new Dictionary<int,List<int>>();
                foreach(var (a,b) in boundary){if(!neighbours.ContainsKey(a))neighbours[a]=[];if(!neighbours.ContainsKey(b))neighbours[b]=[];neighbours[a].Add(b);neighbours[b].Add(a);}
                if(neighbours.Count==0 || neighbours.Values.Any(v=>v.Count!=2))throw new InvalidDataException("Terrain boundary is not closed");
                var loops=new List<CurveLoop>();
                while(boundary.Count>0)
                {
                    var edge=boundary.First();int start=edge.Item1,current=start,previous=-1;var loop=new CurveLoop();
                    do
                    {
                        int next=neighbours[current].First(n=>n!=previous);
                        if(!boundary.Remove((Math.Min(current,next),Math.Max(current,next))))throw new InvalidDataException("Repeated terrain boundary edge");
                        var a=points[current];var b=points[next];loop.Append(Line.CreateBound(new XYZ(a.X,a.Y,0),new XYZ(b.X,b.Y,0)));
                        previous=current;current=next;
                    }while(current!=start);
                    loops.Add(loop);
                }
                var top=triangles.SelectMany(f=>f).Distinct().Select(i=>points[i]).ToList();
                if(top.GroupBy(p=>(Math.Round(p.X,8),Math.Round(p.Y,8))).Any(g=>g.Count()>1))throw new InvalidDataException("Terrain has overhangs or multiple heights at the same XY");
                transaction.Start();var terrain=Toposolid.Create(doc,loops,top,type,level.Id);doc.Regenerate();
                var faces=NativeGeometry.Solids(terrain).SelectMany(s=>s.Faces.Cast<Face>()).OfType<PlanarFace>().Where(f=>f.FaceNormal.Z>1e-8).ToList();
                var samples=top.Concat(triangles.Select(f=>(points[f[0]]+points[f[1]]+points[f[2]])/3));
                foreach(var sample in samples)
                    if(!faces.Any(face=>face.Project(sample) is IntersectionResult hit && hit.Distance<=Geometry.Feet(.001)))
                        throw new InvalidDataException("Revit terrain surface differs from source by more than 1 mm");
                SourceMark.Set(terrain,source.Mark);
                Storage.Tag(terrain,project,key,"toposolid:"+root.S("ifcSha256"),row.GetRawText());
                if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidDataException("Terrain creation rolled back");
                report.Add(key,"Mesh","created","Native Toposolid; source boundary and surface samples verified within 1 mm",terrain.UniqueId);
            }
            catch(Exception error)
            {
                if(transaction.GetStatus()==TransactionStatus.Started)transaction.RollBack();
                report.Add(key,"Mesh","blocked",error.Message);
            }
        }
        return report;
    }
}
