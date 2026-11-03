using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

public static class NativeTerrain
{
    public static Toposolid Create(Document doc,Manifest manifest,ElementRecord record,Level level,Geometry geometry)
    {
        var payload=record.Payload;
        if(payload.S("format")!="ac2r.terrain/1" || payload.GetProperty("parts").GetArrayLength()==0)throw new InvalidDataException("Prepared source Mesh surface required");
        var index=Storage.Index(doc,manifest.Source.ProjectId);Toposolid? first=null;int number=0;
        foreach(var part in payload.GetProperty("parts").EnumerateArray())
        {
            var vertices=part.GetProperty("vertices").Read<List<Point>>().Select(geometry.Xyz).ToList();
            var source=part.GetProperty("faces").Read<List<List<int>>>().Select(f=>f.Select(i=>vertices[i]).ToArray()).ToList();
            string surfaceGuid=part.S("surfaceGuid"),materialKey="surface:"+surfaceGuid;
            var material=index.GetValueOrDefault(materialKey) as Material;
            if(material==null)
            {
                var surface=manifest.Surfaces.SingleOrDefault(s=>s.TryGetProperty("attributeId",out var id) && id.S("guid").Equals(surfaceGuid,StringComparison.OrdinalIgnoreCase));
                if(surface.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Mesh source surface material missing: "+surfaceGuid);
                var occupied=new FilteredElementCollector(doc).OfClass(typeof(Material)).Select(e=>e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                material=(Material)doc.GetElement(Material.Create(doc,GridLabels.Unique(SourceLabel.Format(surface.S("name")),occupied)));
                if(surface.TryGetProperty("surfaceColor",out var rgb))material.Color=new Color((byte)Math.Round(rgb.D("red")*255),(byte)Math.Round(rgb.D("green")*255),(byte)Math.Round(rgb.D("blue")*255));
                material.Transparency=(int)Math.Clamp(Math.Round(surface.D("transparency")),0,100);
                Storage.Tag(material,manifest.Source.ProjectId,materialKey,Json.Hash(surface),surface.GetRawText());index[materialKey]=material;
            }
            string typeKey="terrain-type:"+Json.Hash(new{surfaceGuid,thickness=part.D("thickness")});
            var type=index.GetValueOrDefault(typeKey) as ToposolidType;
            if(type==null)
            {
                var original=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().FirstOrDefault()??throw new InvalidDataException("Project contains no Toposolid type");
                var names=new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Select(e=>e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                type=(ToposolidType)original.Duplicate(GridLabels.Unique(SourceLabel.Format(record.Mark+" — "+material.Name),names));
                using var structure=original.GetCompoundStructure();
                structure.SetLayers([new CompoundStructureLayer(Geometry.Feet(part.D("thickness")),MaterialFunctionAssignment.Structure,material.Id)]);
                structure.VariableLayerIndex=0;type.SetCompoundStructure(structure);
                Storage.Tag(type,manifest.Source.ProjectId,typeKey,typeKey,part.GetRawText());index[typeKey]=type;
            }
            var loops=part.GetProperty("loops").Read<List<List<Point>>>().Select(loop=>
            {
                var pts=loop.Select(geometry.Xyz).Select(p=>new XYZ(p.X,p.Y,level.Elevation)).ToList();
                return CurveLoop.Create(pts.Select((p,i)=>(Curve)Line.CreateBound(p,pts[(i+1)%pts.Count])).ToList());
            }).ToList();
            var terrain=Toposolid.Create(doc,loops,vertices,type.Id,level.Id);doc.Regenerate();
            foreach(var joined in JoinGeometryUtils.GetJoinedElements(doc,terrain))JoinGeometryUtils.UnjoinGeometry(doc,terrain,doc.GetElement(joined));
            terrain.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(record.Mark);
            if(first==null)first=terrain;
            else Storage.Tag(terrain,manifest.Source.ProjectId,"mesh-part:"+record.SourceGuid+":"+number,record.ContentHash,record.Raw.GetRawText());
            number++;
        }
        return first!;
    }
    public static void Verify(Document doc,Manifest manifest,ElementRecord record,Element first,Geometry geometry)
    {
        var index=Storage.Index(doc,manifest.Source.ProjectId);int number=0;
        foreach(var part in record.Payload.GetProperty("parts").EnumerateArray())
        {
            var terrain=(Toposolid)(number==0?first:index["mesh-part:"+record.SourceGuid+":"+number]);number++;
            var vertices=part.GetProperty("vertices").Read<List<Point>>().Select(geometry.Xyz).ToList();
            var source=part.GetProperty("faces").Read<List<List<int>>>().Select(f=>f.Select(i=>vertices[i]).ToArray()).ToList();
            var joined=JoinGeometryUtils.GetJoinedElements(doc,terrain);
            if(joined.Count>0)
            {
                using var unjoin=new Transaction(doc,"Retain independent Archicad terrain surfaces");unjoin.Start();
                foreach(var id in joined)JoinGeometryUtils.UnjoinGeometry(doc,terrain,doc.GetElement(id));unjoin.Commit();
            }
            var native=TerrainSurface.Triangles(terrain);
            string? diagnostics=Environment.GetEnvironmentVariable("BOTPLUS_TERRAIN_DIAGNOSTICS");
            if(!string.IsNullOrEmpty(diagnostics))
            {
                Directory.CreateDirectory(diagnostics);
                object P(XYZ p)=>new{x=p.X,y=p.Y,z=p.Z};
                File.WriteAllText(Path.Combine(diagnostics,record.SourceGuid+".json"),JsonSerializer.Serialize(new{source=source.Select(t=>t.Select(P)),native=native.Select(t=>t.Select(P)),joined=joined.Count},Json.Options));
            }
            // Geometry of a newly created Toposolid is complete only after its creation transaction.
            try{TerrainSurface.Verify(source,native);}
            catch(InvalidDataException initial)
            {
                using var transaction=new Transaction(doc,"Preserve source terrain breaklines");transaction.Start();
                var editor=terrain.GetSlabShapeEditor();if(!editor.IsEnabled)editor.Enable();doc.Regenerate();
                var points=editor.SlabShapeVertices.Cast<SlabShapeVertex>().ToList();
                foreach(var triangle in source)for(int i=0;i<3;i++)
                {
                    var a=points.FirstOrDefault(v=>v.Position.DistanceTo(triangle[i])<1e-6);
                    var b=points.FirstOrDefault(v=>v.Position.DistanceTo(triangle[(i+1)%3])<1e-6);
                    if(a==null || b==null || a==b)continue;
                    try{editor.AddSplitLine(a,b);}catch(Autodesk.Revit.Exceptions.ArgumentException){}
                }
                doc.Regenerate();transaction.Commit();
                try{TerrainSurface.Verify(source,TerrainSurface.Triangles(terrain));}
                catch(InvalidDataException final){throw new InvalidDataException("Initial: "+initial.Message+"; after breaklines: "+final.Message);}
            }
        }
    }
}
