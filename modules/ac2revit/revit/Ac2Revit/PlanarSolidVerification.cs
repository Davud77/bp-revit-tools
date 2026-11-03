using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

internal static class PlanarSolidVerification
{
    static object Mesh(List<Solid> solids)
    {
        var vertices=new List<double[]>();var faces=new List<int[]>();
        foreach(var solid in solids)foreach(Face face in solid.Faces)
        {
            if(face is not PlanarFace)throw new InvalidDataException("Independent planar verification requires only planar faces");
            using var mesh=face.Triangulate();
            for(int i=0;i<mesh.NumTriangles;i++)
            {
                var tri=mesh.get_Triangle(i);int start=vertices.Count;
                for(int j=0;j<3;j++){var p=tri.get_Vertex(j);vertices.Add([p.X*0.3048,p.Y*0.3048,p.Z*0.3048]);}
                faces.Add([start,start+1,start+2]);
            }
        }
        return new{vertices,faces,volumeM3=solids.Sum(s=>s.Volume)*Math.Pow(.3048,3)};
    }
    public static void Verify(IEnumerable<Solid> actual,IEnumerable<Solid> expected,string folder,string key)
    {
        string id=Guid.NewGuid().ToString("N");string input=Path.Combine(folder,"proof-"+key+"-"+id+".json"),output=Path.Combine(folder,"proof-"+key+"-"+id+"-result.json");
        File.WriteAllText(input,JsonSerializer.Serialize(new{reference=Mesh(expected.ToList()),actual=Mesh(actual.ToList())},Json.Options));
        ExchangeServices.Run("verify_planar_solids.py",["--input",input,"--out",output]);
        using var result=JsonDocument.Parse(File.ReadAllText(output));
        if(!result.RootElement.B("verified"))throw new InvalidDataException("Independent planar coverage verification failed");
    }
}
