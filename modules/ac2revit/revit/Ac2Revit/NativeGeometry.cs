using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

public static class NativeGeometry
{
    public static XYZ At(Point p)=>new(Geometry.Feet(p.X),Geometry.Feet(p.Y),Geometry.Feet(p.Z));
    public static List<Solid> Solids(Element element)
    {
        var list=new List<Solid>();
        // Copies are independent; the element geometry is released here, on Revit's thread, not by the GC finalizer.
        void Read(GeometryElement g)
        {
            foreach(var o in g)
                if(o is GeometryInstance i){using var instance=i.GetInstanceGeometry();Read(instance);}
                else if(o is Solid s && s.Volume>1e-12)list.Add(SolidUtils.CreateTransformed(s,Transform.Identity));
        }
        using var geometry=element.get_Geometry(new Options{DetailLevel=ViewDetailLevel.Fine});
        if(geometry!=null)Read(geometry);return list;
    }
    public static List<Solid> MeshSolids(JsonElement record,Transform worldToLocal)
    {
        var points=record.GetProperty("vertices").Read<List<Point>>().Select(p=>worldToLocal.OfPoint(At(p))).ToList();
        var faces=record.GetProperty("faces").Read<List<List<int>>>();
        try {return TessellatedSolids(points,faces);}
        catch(Exception tessellationError)
        {
            // BRepBuilder explicitly permits exact short edges. Do not thicken
            // or simplify millimetre reveals to satisfy sketch tolerances.
            try {return BrepSolids(points,faces);}
            catch(Exception brepError){throw new AggregateException("Neither native solid builder could preserve the reference mesh",tessellationError,brepError);}
        }
    }
    /// <summary>
    /// A void that is a prism through the wall (an arch, a round window): its two parallel faces are the same profile.
    /// The profile is read from the mesh and extruded, because an arch tessellated by IFC rarely closes as a solid
    /// through the tessellation builders. The volume is compared with the mesh before the result is accepted.
    /// </summary>
    public static List<Solid> PrismSolids(JsonElement record,Transform worldToLocal)
    {
        var points=record.GetProperty("vertices").Read<List<Point>>().Select(p=>worldToLocal.OfPoint(At(p))).ToList();
        var faces=record.GetProperty("faces").Read<List<List<int>>>();
        double metres=UnitUtils.ConvertFromInternalUnits(1,UnitTypeId.Meters);
        // Distinct depths across the wall (local Y). An arch with rebates has a few: 0, 1 mm, 26 mm, full thickness.
        long Level(XYZ p)=>(long)Math.Round(p.Y*metres*10000);
        var levels=points.Select(Level).Distinct().OrderBy(v=>v).ToList();
        if(levels.Count<2 || levels[^1]-levels[0]<100)throw new InvalidDataException("Void is not a prism through the wall");
        var depth=levels.Select((v,i)=>(v,i)).ToDictionary(x=>x.v,x=>x.i);
        double signed=0;
        foreach(var f in faces){var p0=points[f[0]];var p1=points[f[1]];var p2=points[f[2]];signed+=p0.DotProduct(p1.CrossProduct(p2))/6;}
        bool outward=signed>=0;
        // Triangles lying in a plane across the wall: the caps where the void ends (facing +Y) or starts (facing -Y).
        var ends=new List<(int Level,int[] Triangle)>();var starts=new List<(int Level,int[] Triangle)>();
        foreach(var f in faces)
        {
            int a=depth[Level(points[f[0]])],b=depth[Level(points[f[1]])],c=depth[Level(points[f[2]])];
            if(a!=b || b!=c)continue;
            var normal=(points[f[1]]-points[f[0]]).CrossProduct(points[f[2]]-points[f[0]]);
            if(Math.Abs(normal.Y)<1e-12)continue;
            bool facesPlusY=(normal.Y>0)==outward;
            if(facesPlusY)ends.Add((a,f.ToArray()));else starts.Add((a,f.ToArray()));
        }
        if(ends.Count==0 || starts.Count==0)throw new InvalidDataException("Void has no end faces across the wall");
        // Monotone voids only: either every start face is at the first depth and the void narrows with depth,
        // or every end face is at the last depth and the void widens with depth.
        bool narrowing=starts.All(s=>s.Level==0),widening=ends.All(e=>e.Level==levels.Count-1);
        if(!narrowing && !widening)throw new InvalidDataException("The void changes both ways across the wall");
        var mapped=points.Select(p=>(X:p.X*metres,Y:-p.Z*metres,Z:p.Y*metres)).ToList();
        var result=new List<Solid>();
        double volume=0;
        for(int i=0;i+1<levels.Count;++i)
        {
            // Over the layer between depths i and i+1 the void is what still exists beyond it: the union of the end faces at deeper levels.
            // Narrowing: what still exists beyond the layer = end faces at deeper levels. Widening: what already exists = start faces up to this level.
            var deeper=narrowing
                ? ends.Where(e=>e.Level>=i+1).Select(e=>outward?e.Triangle:[e.Triangle[0],e.Triangle[2],e.Triangle[1]]).ToList()
                : starts.Where(s=>s.Level<=i).Select(s=>outward?[s.Triangle[0],s.Triangle[2],s.Triangle[1]]:s.Triangle).ToList();
            if(deeper.Count==0)continue;
            (IReadOnlyList<RoofPlanner.P> Outer,IReadOnlyList<IReadOnlyList<RoofPlanner.P>> Holes) profile;
            try{profile=RoofPlanner.Silhouette(mapped,deeper,0.001,0.0002);}
            catch(InvalidDataException ex){throw new InvalidDataException("Layer "+i+": "+ex.Message);}
            if(profile.Holes.Count>0)throw new InvalidDataException("Layer "+i+" has a hole");
            double y0=levels[i]/10000.0/metres,y1=levels[i+1]/10000.0/metres;
            var loop=new CurveLoop();
            for(int k=0;k<profile.Outer.Count;++k)
            {
                var p=profile.Outer[k];var q=profile.Outer[(k+1)%profile.Outer.Count];
                loop.Append(Line.CreateBound(new XYZ(p.X/metres,y0,-p.Y/metres),new XYZ(q.X/metres,y0,-q.Y/metres)));
            }
            var layer=GeometryCreationUtilities.CreateExtrusionGeometry([loop],XYZ.BasisY,y1-y0);
            volume+=layer.Volume;result.Add(layer);
        }
        if(result.Count==0 || Math.Abs(volume-Math.Abs(signed))>Math.Abs(signed)*0.005)throw new InvalidDataException("Stacked profile volume differs from the IFC void by more than 0.5 %");
        return result;
    }

    static List<Solid> TessellatedSolids(List<XYZ> points,List<List<int>> faces)
    {
        using var builder=new TessellatedShapeBuilder();builder.OpenConnectedFaceSet(true);
        foreach(var triangle in faces)
        {
            if(triangle.Count!=3 || triangle.Any(i=>i<0 || i>=points.Count))throw new InvalidDataException("Invalid solid triangle");
            using var face=new TessellatedFace(triangle.Select(i=>points[i]).ToList(),ElementId.InvalidElementId);
            if(!builder.DoesFaceHaveEnoughLoopsAndVertices(face))throw new InvalidDataException("Invalid native solid face");
            builder.AddFace(face);
        }
        builder.CloseConnectedFaceSet();builder.Target=TessellatedShapeBuilderTarget.Solid;builder.Fallback=TessellatedShapeBuilderFallback.Abort;builder.Build();
        var result=builder.GetBuildResult();var geometry=result.GetGeometricalObjects();
        if(result.HasInvalidData || geometry.Count==0 || geometry.Any(g=>g is not Solid))throw new InvalidDataException("Reference mesh cannot form native solids without salvage");
        return geometry.Cast<Solid>().ToList();
    }
    static List<Solid> BrepSolids(List<XYZ> points,List<List<int>> faces)
    {
        using var builder=new BRepBuilder(BRepType.Solid);builder.SetAllowShortEdges();
        var edges=new Dictionary<(int,int),BRepBuilderGeometryId>();
        double signed=faces.Sum(f=>points[f[0]].DotProduct(points[f[1]].CrossProduct(points[f[2]])))/6;
        foreach(var original in faces)
        {
            var f=signed<0?original.AsEnumerable().Reverse().ToList():original;
            var p=points[f[0]];var normal=(points[f[1]]-p).CrossProduct(points[f[2]]-p);
            if(normal.GetLength()<1e-16)throw new InvalidDataException("Degenerate exact BRep triangle");
            using var surface=BRepBuilderSurfaceGeometry.Create(Plane.CreateByNormalAndOrigin(normal.Normalize(),p),null);
            var face=builder.AddFace(surface,false);var loop=builder.AddLoop(face);
            for(int i=0;i<3;i++)
            {
                int a=f[i],b=f[(i+1)%3];var key=(Math.Min(a,b),Math.Max(a,b));
                if(!edges.TryGetValue(key,out var edge))
                {
                    using var geometry=BRepBuilderEdgeGeometry.Create(points[key.Item1],points[key.Item2]);edge=builder.AddEdge(geometry);edges[key]=edge;
                }
                builder.AddCoEdge(loop,edge,a>b);
            }
            builder.FinishLoop(loop);builder.FinishFace(face);
        }
        if(builder.Finish()!=BRepBuilderOutcome.Success || builder.RemovedSomeFaces() || !builder.IsResultAvailable())throw new InvalidDataException("Exact short-edge BRep could not be built without removing faces");
        var result=builder.GetResult();
        if(Math.Abs(result.Volume-Math.Abs(signed))>Math.Max(Geometry.Feet(1e-6)*Geometry.Feet(1)*Geometry.Feet(1),Math.Abs(signed)*1e-5))throw new InvalidDataException("Exact BRep volume differs from source triangles");
        return SolidUtils.SplitVolumes(result).ToList();
    }
    public static double DifferenceVolume(IEnumerable<Solid> from,IEnumerable<Solid> subtract)
    {
        double total=0;
        foreach(var input in from)
        {
            var remainder=SolidUtils.CreateTransformed(input,Transform.Identity);
            foreach(var cut in subtract)
            {
                if(remainder.Volume<1e-12)break;
                var next=BooleanOperationsUtils.ExecuteBooleanOperation(remainder,cut,BooleanOperationsType.Difference);
                remainder.Dispose();remainder=next;
            }
            total+=remainder.Volume;remainder.Dispose();
        }
        return UnitUtils.ConvertFromInternalUnits(total,UnitTypeId.CubicMeters);
    }
    public static void VerifySolids(IEnumerable<Solid> actual,IEnumerable<Solid> expected,double toleranceM3=1e-6)
    {
        var a=actual.ToList();var b=expected.ToList();
        if(a.Count==0 || b.Count==0 || DifferenceVolume(a,b)>toleranceM3 || DifferenceVolume(b,a)>toleranceM3)
            throw new InvalidDataException("Native solids do not match reference in both Boolean directions");
    }
    public static void DeleteMeshOnly(Document doc,DirectShape source)
    {
        var id=source.Id;
        var allowed=source.GetDependentElements(null).Select(id=>doc.GetElement(id)).Where(e=>e!=null && e.GetType()==typeof(Element)
            && e.Category==null && string.IsNullOrEmpty(e.Name) && !e.ViewSpecific && Storage.Read(e)==null
            && e.Parameters.Cast<Parameter>().All(p=>p.IsReadOnly) && e.get_BoundingBox(null)==null).Select(e=>e.Id).ToHashSet();
        var removed=doc.Delete(id);
        if(!removed.Contains(id) || removed.Any(other=>other!=id && !allowed.Contains(other)))throw new InvalidDataException("Replacement would delete user dependents");
    }
    public static void Configure(Transaction transaction,Report report,string key,string kind)
    {
        var options=transaction.GetFailureHandlingOptions();options.SetFailuresPreprocessor(new Failures(report,key,kind));options.SetClearAfterRollback(true);transaction.SetFailureHandlingOptions(options);
    }
    sealed class Failures(Report report,string key,string kind):IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool error=false;
            foreach(var f in accessor.GetFailureMessages())
            {
                report.Add(key,kind,f.GetSeverity()==FailureSeverity.Warning?"warning":"revitError",f.GetDescriptionText());
                if(f.GetSeverity()==FailureSeverity.Warning)accessor.DeleteWarning(f);else error=true;
            }
            return error?FailureProcessingResult.ProceedWithRollBack:FailureProcessingResult.Continue;
        }
    }
}
