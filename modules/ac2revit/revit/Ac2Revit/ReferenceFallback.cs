using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

public static class ReferenceFallback
{
    public static Report Run(Document doc,string meshManifest,Func<string,string> workspacePath,Action<int,int>? progress=null,
        BuiltInCategory category=BuiltInCategory.OST_Walls,string recordKind="WallGeometry",bool uniqueMarks=false,IReadOnlySet<string>? existingKeys=null)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(meshManifest));var root=json.RootElement;
        string project=root.GetProperty("projectId").GetString()!,ifcHash=root.GetProperty("ifcSha256").GetString()!;
        VerifiedReferenceScope.Verify(workspacePath(root.GetProperty("ifcPath").GetString()!),ifcHash);
        var records=(root.TryGetProperty("elements",out var recordsProperty)?recordsProperty:root.GetProperty("walls")).EnumerateArray().ToList();
        var keys=records.Select(r=>r.GetProperty("sourceGuid").GetString()!).ToList();
        if(keys.Count!=keys.Distinct(StringComparer.OrdinalIgnoreCase).Count())throw new InvalidDataException("Duplicate source GUIDs");
        // A caller running many batches passes the source keys it already holds: indexing reads the tag of every
        // imported element, which took ~45 s per batch on the finished Hippodrome model.
        IReadOnlySet<string> existing=existingKeys??new HashSet<string>(Storage.Index(doc,project).Keys,StringComparer.OrdinalIgnoreCase);var geometry=new Geometry(doc,root.GetProperty("coordinates").Read<CoordinateInfo>().Transform);
        var report=new Report{ProjectId=project,Mode="referenceFallbackCopy"};int done=0;
        if(!DirectShape.IsValidCategoryId(new ElementId(category),doc))throw new InvalidDataException("Category does not support DirectShape: "+category);
        foreach(var record in records)
        {
            string key=record.GetProperty("sourceGuid").GetString()!;
            if(existing.Contains(key))throw new InvalidDataException("Fallback would duplicate source element "+key);
        }
        // Verification is per element and outside any transaction; creation is batched: one transaction and one
        // regeneration for a whole batch (11 157 objects of the Hippodrome took ~1 s each, mostly Commit/Regenerate,
        // and the undo history forced 55 reopenings). A rolled-back batch is retried element by element.
        var prepared=new List<Prepared>();
        var styleMaterials=IfcSurfaceStyles.Ensure(doc,records);
        foreach(var record in records)
        {
            string key=record.GetProperty("sourceGuid").GetString()!;
            string stage="readMesh";
            try
            {
                var points=record.GetProperty("vertices").Read<List<Point>>().Select(geometry.Xyz).ToList();
                var faces=record.GetProperty("faces").Read<List<List<int>>>();
                var styles=IfcSurfaceStyles.Read(record,faces.Count);
                if(points.Count<3 || faces.Count==0)throw new InvalidDataException("Empty reference mesh");
                stage="buildFaces";int faceNumber=0;using var builder=new TessellatedShapeBuilder();builder.OpenConnectedFaceSet(false);
                foreach(var indices in faces)
                {
                    if(indices.Count!=3 || indices.Any(i=>i<0||i>=points.Count))throw new InvalidDataException("Invalid triangle indices");
                    var style=styles==null?-1:styles.Value.Faces[faceNumber++];
                    using var face=new TessellatedFace(indices.Select(i=>points[i]).ToList(),style<0?ElementId.InvalidElementId:styleMaterials[styles!.Value.Styles[style]]);
                    if(!builder.DoesFaceHaveEnoughLoopsAndVertices(face))throw new InvalidDataException("Revit rejected reference triangle");
                    builder.AddFace(face);
                }
                stage="buildMesh";builder.CloseConnectedFaceSet();builder.Target=TessellatedShapeBuilderTarget.Mesh;builder.Fallback=TessellatedShapeBuilderFallback.Salvage;builder.Build();
                var built=builder.GetBuildResult();
                // This method transfers ownership and clears the result. It must
                // be called once; repeating it throws an exception without text.
                var objects=built.GetGeometricalObjects();
                if(objects.Count==0 || built.HasInvalidData)throw new InvalidDataException("Invalid or empty tessellated result");
                if(objects.Any(o=>o is not Mesh) || objects.OfType<Mesh>().Sum(m=>m.NumTriangles)!=faces.Count)
                    throw new InvalidDataException("Revit changed the source triangle count");
                // Bounds/count alone can miss moved or replaced faces. Compare
                // all unoriented triangles as a multiset, at one micrometre.
                double quantum=Geometry.Feet(0.000001);
                string VertexKey(XYZ p)=>$"{Math.Round(p.X/quantum)},{Math.Round(p.Y/quantum)},{Math.Round(p.Z/quantum)}";
                string TriangleKey(IEnumerable<XYZ> triangle)=>string.Join("|",triangle.Select(VertexKey).OrderBy(v=>v,StringComparer.Ordinal));
                double vertexDeviation=0;
                double searchCell=Geometry.Feet(0.001);
                (long,long,long) Cell(XYZ p)=>((long)Math.Floor(p.X/searchCell),(long)Math.Floor(p.Y/searchCell),(long)Math.Floor(p.Z/searchCell));
                var cells=points.GroupBy(Cell).ToDictionary(g=>g.Key,g=>g.ToList());
                XYZ MatchVertex(XYZ value)
                {
                    // Revit Mesh stores coordinates less precisely than XYZ.
                    // Match the returned vertex to an actual source vertex, then
                    // compare triangle topology. Never round or move the input.
                    XYZ? nearest=null;double distance=double.PositiveInfinity;
                    var cell=Cell(value);
                    for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                        if(cells.TryGetValue((cell.Item1+x,cell.Item2+y,cell.Item3+z),out var neighbours))
                            foreach(var point in neighbours){double delta=point.DistanceTo(value);if(delta<distance){distance=delta;nearest=point;}}
                    if(nearest==null || distance>Geometry.Feet(0.001))throw new InvalidDataException("Revit mesh vertex moved by more than 1 mm");
                    vertexDeviation=Math.Max(vertexDeviation,distance);return nearest;
                }
                var expectedTriangles=faces.GroupBy(f=>TriangleKey(f.Select(i=>points[i]))).ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.Ordinal);
                var actualTriangles=new Dictionary<string,int>(StringComparer.Ordinal);
                foreach(var mesh in objects.OfType<Mesh>())for(int i=0;i<mesh.NumTriangles;i++)
                {var tri=mesh.get_Triangle(i);string tk=TriangleKey(Enumerable.Range(0,3).Select(j=>MatchVertex(tri.get_Vertex(j))));actualTriangles[tk]=actualTriangles.GetValueOrDefault(tk)+1;}
                if(expectedTriangles.Count!=actualTriangles.Count || expectedTriangles.Any(k=>actualTriangles.GetValueOrDefault(k.Key)!=k.Value))
                    throw new InvalidDataException("Revit changed the reference triangles beyond the 1 micrometre quantisation");
                prepared.Add(new(key,record,objects,new XYZ(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)),new XYZ(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z)),vertexDeviation));
            }
            catch(Exception ex){report.Add(key,recordKind,"blocked",stage+": "+ex);progress?.Invoke(++done,records.Count);}
        }
        foreach(var batch in prepared.Chunk(BatchSize))
        {
            if(CreateBatch(doc,batch,project,ifcHash,category,recordKind,uniqueMarks,report))
            {done+=batch.Length;progress?.Invoke(done,records.Count);continue;}
            // Isolate: the same checks, one element per transaction, so one bad element cannot drop the batch.
            foreach(var one in batch){CreateBatch(doc,[one],project,ifcHash,category,recordKind,uniqueMarks,report);progress?.Invoke(++done,records.Count);}
        }
        return report;
    }

    public const int BatchSize=100;
    sealed record Prepared(string Key,JsonElement Record,IList<GeometryObject> Objects,XYZ Low,XYZ High,double VertexDeviation);

    /// <summary>Creates the batch in one transaction. Elements failing the bounds check are removed and reported;
    /// false means the transaction rolled back (Revit error) and nothing of the batch was reported yet.</summary>
    static bool CreateBatch(Document doc,IReadOnlyList<Prepared> batch,string project,string ifcHash,BuiltInCategory category,string recordKind,bool uniqueMarks,Report report)
    {
        var pending=new Report{ProjectId=report.ProjectId,Mode=report.Mode};var shapes=new Dictionary<ElementId,Prepared>();
        using var transaction=new Transaction(doc,"IFC "+recordKind+" geometry fallback ("+batch.Count+")");
        var failures=new Failures(pending,batch[0].Key,recordKind,id=>shapes.TryGetValue(id,out var hit)?hit.Key:null);
        transaction.Start();var options=transaction.GetFailureHandlingOptions();options.SetFailuresPreprocessor(failures);options.SetClearAfterRollback(true);transaction.SetFailureHandlingOptions(options);
        try
        {
            foreach(var item in batch)
            {
                var record=item.Record;
                var shape=DirectShape.CreateElement(doc,new ElementId(category));
                shape.ApplicationId="Ac2Revit.IFC.ReferenceFallback";shape.ApplicationDataId=item.Key;shape.SetShape(item.Objects);
                shape.Name=SourceLabel.Format(record.S("mark",recordKind))+" — геометрия IFC";
                shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("IFC geometry fallback; source "+item.Key+"; "+record.GetProperty("reason").GetString()+(uniqueMarks?"; Исходная марка: "+record.S("mark"):""));
                if(record.TryGetProperty("mark",out var mark))SourceMark.Set(shape,mark.GetString());
                Storage.Tag(shape,project,item.Key,"ifc-mesh:"+ifcHash,record.GetRawText());shapes[shape.Id]=item;
            }
            doc.Regenerate();
            foreach(var (id,item) in shapes.ToList())
            {
                var box=doc.GetElement(id).get_BoundingBox(null);
                string? problem=box==null?"Fallback has no bounds":Math.Max(box.Min.DistanceTo(item.Low),box.Max.DistanceTo(item.High))>Geometry.Feet(0.001)?"Fallback bounds differ by more than 1 mm":null;
                if(problem==null)continue;
                doc.Delete(id);shapes.Remove(id);pending.Add(item.Key,recordKind,"blocked","tagAndRegenerate: "+problem);
            }
            if(transaction.Commit()!=TransactionStatus.Committed)return false;
        }
        catch(Exception ex)
        {
            if(transaction.GetStatus()==TransactionStatus.Started)transaction.RollBack();
            if(batch.Count>1)return false;
            report.Add(batch[0].Key,recordKind,"blocked","createElement: "+ex);return true;
        }
        report.Items.AddRange(pending.Items);
        foreach(var (id,item) in shapes)
        {
            double deviationMeters=UnitUtils.ConvertFromInternalUnits(item.VertexDeviation,UnitTypeId.Meters);
            report.Add(item.Key,recordKind,"created",$"DirectShape: source triangle multiset verified after vertex correspondence; maximum vertex deviation {deviationMeters:G9} m, bounds within 1 mm. IFC tessellation is used; native parametric functionality/material layers are not assigned.",doc.GetElement(id).UniqueId);
        }
        return true;
    }
    sealed class Failures(Report report,string key,string kind,Func<ElementId,string?>? owner=null):IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool error=false;foreach(var failure in accessor.GetFailureMessages())
            {string target=owner==null?key:failure.GetFailingElementIds().Select(owner).FirstOrDefault(k=>k!=null)??key;report.Add(target,kind,failure.GetSeverity()==FailureSeverity.Warning?"warning":"revitError",failure.GetDescriptionText());if(failure.GetSeverity()==FailureSeverity.Warning)accessor.DeleteWarning(failure);else error=true;}
            return error?FailureProcessingResult.ProceedWithRollBack:FailureProcessingResult.Continue;
        }
    }
}
