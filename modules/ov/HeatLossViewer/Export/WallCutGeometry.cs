using Autodesk.Revit.DB;

namespace HeatLossViewer.Export;

/// <summary>Intersect a room boundary slice with the actual cut wall solid, including every geometric void.</summary>
public static class WallCutGeometry
{
    private static double M(double v) => UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Meters);
    private static double Area(double v) => UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.SquareMeters);
    public static List<Solid> Solids(Element element)
    {
        using var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false };
        var result = new List<Solid>();
        void Read(GeometryElement geometry)
        {
            foreach (var item in geometry)
                if (item is Solid solid && solid.Volume > 1e-9) result.Add(solid);
                else if (item is GeometryInstance instance) Read(instance.GetInstanceGeometry());
        }
        var geometry = element.get_Geometry(options);
        if (geometry is not null) Read(geometry);
        return result;
    }

    public static List<double[]> Triangles(Face face, Transform transform)
    {
        using var mesh = face.Triangulate();
        var triangles = new List<double[]>();
        for (int i=0;i<mesh.NumTriangles;i++)
        {
            var t=mesh.get_Triangle(i);var points=new List<double>();
            for(int j=0;j<3;j++) { var p=transform.OfPoint(t.get_Vertex(j));points.AddRange(new[]{M(p.X),M(p.Y),M(p.Z)}); }
            triangles.Add(points.ToArray());
        }
        return triangles;
    }

    public static bool Apply(SurfaceData surface, Face boundary, List<Solid> wallSolids, Transform transform)
    {
        if (boundary is not PlanarFace plane || wallSolids.Count == 0) return false;
        // The room normal points into the bounding wall. Stay on that side of the finish face.
        double depth = UnitUtils.ConvertToInternalUnits(.01,UnitTypeId.Meters);
        using var slice=GeometryCreationUtilities.CreateExtrusionGeometry(boundary.GetEdgesAsCurveLoops(),plane.FaceNormal,depth);
        Solid? wall=null;
        foreach(var solid in wallSolids)
        {
            if(wall is null) wall=SolidUtils.CreateTransformed(solid,Transform.Identity);
            else { var previous=wall;wall=BooleanOperationsUtils.ExecuteBooleanOperation(previous,solid,BooleanOperationsType.Union);previous.Dispose(); }
        }
        if(wall is null) return false;
        using(wall)
        using(var intersection=BooleanOperationsUtils.ExecuteBooleanOperation(slice,wall,BooleanOperationsType.Intersect))
        using(var difference=BooleanOperationsUtils.ExecuteBooleanOperation(slice,wall,BooleanOperationsType.Difference))
        {
            List<PlanarFace> FrontFaces(Solid solid) => solid.Faces.Cast<Face>().OfType<PlanarFace>()
                .Where(f=>f.FaceNormal.DotProduct(plane.FaceNormal)<-.999 && Math.Abs((f.Origin-plane.Origin).DotProduct(plane.FaceNormal))<depth*.2).ToList();
            var faces=FrontFaces(intersection);
            double net=faces.Sum(f=>Area(f.Area));
            if(net > surface.GrossArea+.005) return false;
            var cuts=new List<SurfaceCutData>();
            foreach(var volume in SolidUtils.SplitVolumes(difference))
            {
                var cutFaces=FrontFaces(volume);
                double cutArea=cutFaces.Sum(f=>Area(f.Area));
                if(cutArea<1e-5) continue;
                var triangles=cutFaces.SelectMany(f=>Triangles(f,transform)).ToList();
                var points=triangles.SelectMany(t=>new[]{new[]{t[0],t[1],t[2]},new[]{t[3],t[4],t[5]},new[]{t[6],t[7],t[8]}}).ToList();
                var min=Enumerable.Range(0,3).Select(i=>points.Min(p=>p[i])).ToArray();
                var max=Enumerable.Range(0,3).Select(i=>points.Max(p=>p[i])).ToArray();
                cuts.Add(new SurfaceCutData{Key=surface.Key+"/cut/"+cuts.Count,Area=cutArea,BoundsMin=min,BoundsMax=max,
                    Center=Enumerable.Range(0,3).Select(i=>(min[i]+max[i])/2).ToArray(),Triangles=triangles});
            }
            // The area balance must hold before replacing the gross wall by a net mesh.
            if(Math.Abs(net+cuts.Sum(c=>c.Area)-surface.GrossArea)>.01) return false;
            surface.NetArea=net;surface.AreaSource="revit-solid-intersection";
            surface.Triangles=faces.SelectMany(f=>Triangles(f,transform)).ToList();surface.Cuts=cuts;
            return true;
        }
    }
}
