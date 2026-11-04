using Autodesk.Revit.DB;
using BotPlus.Openings.Core;

namespace BotPlus.Openings;

internal static class HoleMarkers
{
    internal static double Feet(double millimeters)=>UnitUtils.ConvertToInternalUnits(millimeters,UnitTypeId.Millimeters);
    internal static double Mm(double feet)=>UnitUtils.ConvertFromInternalUnits(feet,UnitTypeId.Millimeters);
    internal static XYZ Center(HoleGeometry g)=>new(Feet(g.X),Feet(g.Y),Feet(g.Z));
    internal static XYZ U(HoleGeometry g)=>new(Math.Cos(g.Yaw),Math.Sin(g.Yaw),0);
    internal static XYZ V(HoleGeometry g)=>g.Orientation==HoleOrientation.Wall?XYZ.BasisZ:new(-Math.Sin(g.Yaw),Math.Cos(g.Yaw),0);
    internal static XYZ Axis(HoleGeometry g)=>g.Orientation==HoleOrientation.Floor?XYZ.BasisZ:new(-Math.Sin(g.Yaw),Math.Cos(g.Yaw),0);
    internal static CurveLoop Profile(HoleGeometry g,XYZ origin,XYZ u,XYZ v)
    {
        if(g.Shape==HoleShape.Round)return CurveLoop.Create([Arc.Create(origin,Feet(g.Diameter/2),0,Math.PI,u,v),Arc.Create(origin,Feet(g.Diameter/2),Math.PI,2*Math.PI,u,v)]);
        var p=new[]{origin-u*Feet(g.Width/2)-v*Feet(g.Height/2),origin+u*Feet(g.Width/2)-v*Feet(g.Height/2),origin+u*Feet(g.Width/2)+v*Feet(g.Height/2),origin-u*Feet(g.Width/2)+v*Feet(g.Height/2)};
        return CurveLoop.Create(Enumerable.Range(0,4).Select(i=>(Curve)Line.CreateBound(p[i],p[(i+1)%4])).ToList());
    }
    internal static Solid Solid(HoleGeometry g)=>GeometryCreationUtilities.CreateExtrusionGeometry([Profile(g,Center(g)-Axis(g)*Feet(g.Depth/2),U(g),V(g))],Axis(g),Feet(g.Depth));
    internal static IEnumerable<Solid> Solids(GeometryElement? geometry)
    {
        if(geometry==null)yield break;
        foreach(var obj in geometry)
        {
            if(obj is Solid solid&&solid.Volume>1e-10)yield return solid;
            else if(obj is GeometryInstance instance)foreach(var nested in Solids(instance.GetInstanceGeometry()))yield return nested;
        }
    }
    internal static string Fingerprint(Element element)
    {
        var box=element.get_BoundingBox(null);if(box==null)return "missing";
        var values=Solids(element.get_Geometry(new Options{DetailLevel=ViewDetailLevel.Fine})).SelectMany(s=>s.Faces.Cast<Face>()).SelectMany(f=>
        {var mesh=f.Triangulate();return mesh.Vertices;})
            .Select(p=>$"{Math.Round(p.X,7):R}|{Math.Round(p.Y,7):R}|{Math.Round(p.Z,7):R}").Distinct().Order(StringComparer.Ordinal).ToArray();
        return HoleJson.Hash(new{min=new[]{box.Min.X,box.Min.Y,box.Min.Z},max=new[]{box.Max.X,box.Max.Y,box.Max.Z},vertices=values});
    }
    internal static DirectShape? Marker(Document doc,Hole hole)=>string.IsNullOrEmpty(hole.MarkerId)?null:doc.GetElement(hole.MarkerId) as DirectShape;
    internal static void Update(Document doc,Hole hole)
    {
        var marker=Marker(doc,hole);
        if(hole.Deleted)
        {
            if(marker!=null)DeleteOwned(doc,marker.Id);
            hole.MarkerId="";hole.MarkerHash="";return;
        }
        if(marker!=null&&(marker.ApplicationId!=HoleStore.AppId||marker.ApplicationDataId!=hole.Key))throw new InvalidOperationException("Идентификатор маркера занят другим элементом.");
        // An external move/delete must be resolved explicitly through the geometry editor.
        // Comments or status changes must not silently restore the previous shape.
        if(hole.GeometryUnresolved)return;
        marker??=DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));
        marker.ApplicationId=HoleStore.AppId;marker.ApplicationDataId=hole.Key;marker.SetShape([Solid(hole.Geometry)]);
        marker.Name="Отверстие "+(hole.Mark.Length>0?hole.Mark:hole.Discipline+" "+hole.Size);
        marker.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(hole.Mark);
        marker.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"{hole.Status} · {hole.Discipline} → {hole.Assignment} · {hole.Size} · {hole.ApprovalText}");
        doc.Regenerate();hole.MarkerId=marker.UniqueId;hole.MarkerHash=Fingerprint(marker);
    }
    internal static void DeleteOwned(Document doc,ElementId id)
    {
        var removed=doc.Delete(id);if(removed.Any(other=>other!=id))throw new InvalidOperationException("Удаление затронет зависимые элементы. Сначала проверьте марки и размеры этого отверстия.");
    }
    internal static void Colour(Document doc,View view,IEnumerable<Hole> holes,Role role)
    {
        if(view.IsTemplate||!view.AreGraphicsOverridesAllowed())return;
        var solid=new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(p=>p.GetFillPattern().IsSolidFill);
        foreach(var hole in holes.Where(h=>!h.Deleted))
        {
            var marker=Marker(doc,hole);if(marker==null)continue;
            var rgb=hole.DeletionRequested?new Color(157,80,190):hole.FullyApproved?new Color(45,170,90):hole.Approvals.Any(a=>a.Role==role)?new Color(235,175,35):new Color(220,65,65);
            using var graphics=view.GetElementOverrides(marker.Id);graphics.SetProjectionLineColor(rgb);graphics.SetCutLineColor(rgb);graphics.SetSurfaceTransparency(65);
            if(solid!=null){graphics.SetSurfaceForegroundPatternId(solid.Id);graphics.SetSurfaceForegroundPatternColor(rgb);graphics.SetCutForegroundPatternId(solid.Id);graphics.SetCutForegroundPatternColor(rgb);}
            view.SetElementOverrides(marker.Id,graphics);
        }
    }
}
