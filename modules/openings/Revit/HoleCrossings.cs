using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using BotPlus.Openings.Core;

namespace BotPlus.Openings;

internal sealed record CrossingCandidate(string Key,string HostId,string LinkId,string CommunicationId,string Communication,string Host,
    string LevelId,string LevelName,double LevelElevation,HoleGeometry Geometry,string State)
{
    public bool CanCreate=>State=="Новое задание";
    public string Size=>Geometry.Shape==HoleShape.Round?$"Ø{Geometry.Diameter:0.#}":$"{Geometry.Width:0.#} × {Geometry.Height:0.#}";
    public double Depth=>Geometry.Depth;
    public string Signature=>HoleJson.Hash(new{Key,Geometry,LevelId});
}
internal sealed record CrossingScan(List<CrossingCandidate> Candidates,List<string> Warnings,int Communications,int HostCount);
internal static class HoleCrossings
{
    static Vector3 Vector(XYZ p)=>new(p.X,p.Y,p.Z);
    static Vector3 Millimeters(XYZ p)=>new(HoleMarkers.Mm(p.X),HoleMarkers.Mm(p.Y),HoleMarkers.Mm(p.Z));
    sealed record HostSource(Element Element,string Link,Transform Transform,string Label);
    internal static CrossingScan Scan(Document doc,Registry data,CrossingOptions options,IReadOnlyCollection<ElementId>? selection=null)
    {
        options.Validate();var warnings=new List<string>();var result=new List<CrossingCandidate>();
        var meps=(selection==null?new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).Cast<MEPCurve>():selection.Select(doc.GetElement).OfType<MEPCurve>())
            .Where(e=>e is Pipe or Duct or CableTray or Conduit).OrderBy(e=>e.Id.Value).ToList();
        if(meps.Count==0)throw new InvalidOperationException("Нет прямых труб, воздуховодов, лотков или коробов в выбранной области поиска.");
        if(meps.Count>10000)throw new InvalidOperationException("Выберите до 10000 коммуникаций для одного поиска.");
        var hosts=new List<HostSource>();
        void Collect(Document source,string link,Transform transform,string label)
        {
            if(transform.OfVector(XYZ.BasisZ).DistanceTo(XYZ.BasisZ)>1e-7){warnings.Add(label+": наклонённая связь пропущена.");return;}
            hosts.AddRange(new FilteredElementCollector(source).WhereElementIsNotElementType().Where(e=>e is Wall or Floor).Select(e=>new HostSource(e,link,transform,label)));
        }
        Collect(doc,"",Transform.Identity,"Текущий проект");
        if(options.IncludeLinks)foreach(var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {if(link.GetLinkDocument() is {} linked)Collect(linked,link.UniqueId,link.GetTotalTransform(),link.Name);else warnings.Add(link.Name+": связь не загружена.");}
        var levels=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>l.Elevation).ToList();
        if(levels.Count==0)throw new InvalidOperationException("В проекте нет уровней.");
        var bounds=hosts.Select(h=>(Host:h,Box:h.Element.get_BoundingBox(null))).Where(h=>h.Box!=null).Select(h=>(h.Host,Box:WorldBounds(h.Box!,h.Host.Transform))).ToList();
        if((long)meps.Count*bounds.Count>5000000)throw new InvalidOperationException("Для этого проекта выберите меньшую группу MEP: поиск ограничен 5 млн пар габаритов за запуск.");
        var geometryCache=new Dictionary<HostSource,List<Solid>>();int pairs=0;
        foreach(var mep in meps)
        {
            if(mep.Location is not LocationCurve {Curve:Line line}){warnings.Add($"{mep.Id.Value}: криволинейная коммуникация пропущена.");continue;}
            Connector connector;double width,height,insulation=0;bool round;
            try
            {
                connector=mep.ConnectorManager.Connectors.Cast<Connector>().First(c=>c.ConnectorType==ConnectorType.End);
                round=connector.Shape==ConnectorProfileType.Round;
                if(!round&&connector.Shape!=ConnectorProfileType.Rectangular)throw new InvalidOperationException("Профиль не круглый и не прямоугольный.");
                width=round?connector.Radius*2:connector.Width;height=round?width:connector.Height;
                if(round&&mep is Pipe or Conduit)
                {
                    var outer=mep.get_Parameter(mep is Pipe?BuiltInParameter.RBS_PIPE_OUTER_DIAMETER:BuiltInParameter.RBS_CONDUIT_OUTER_DIAM_PARAM);
                    if(outer is {HasValue:true}&&outer.AsDouble()>width)width=height=outer.AsDouble();
                }
                if(options.IncludeInsulation&&(mep is Pipe or Duct))
                    insulation=InsulationLiningBase.GetInsulationIds(doc,mep.Id).Select(doc.GetElement).OfType<InsulationLiningBase>().Select(i=>i.Thickness).DefaultIfEmpty(0).Max();
            }
            catch(Exception error){warnings.Add($"{mep.Id.Value}: не удалось прочитать профиль / изоляцию — {error.Message}");continue;}
            var mepBox=mep.get_BoundingBox(null);if(mepBox==null)continue;
            var outline=new Outline(mepBox.Min-new XYZ(insulation,insulation,insulation),mepBox.Max+new XYZ(insulation,insulation,insulation));
            foreach(var (host,box) in bounds)
            {
                if(!outline.Intersects(new Outline(box.Min,box.Max),1e-7))continue;
                if(++pairs>100000)throw new InvalidOperationException("Слишком много пар пересечений. Сузьте выбор коммуникаций.");
                try
                {
                    HoleOrientation orientation=host.Element is Wall?HoleOrientation.Wall:HoleOrientation.Floor;double yaw=0;
                    if(host.Element is Wall wall)
                    {
                        if(wall.Location is not LocationCurve {Curve:Line}||Math.Abs(host.Transform.OfVector(wall.Orientation).Z)>1e-7)throw new InvalidOperationException("Криволинейная / наклонная стена: требуется ручное задание.");
                        var normal=host.Transform.OfVector(wall.Orientation);yaw=Math.Atan2(-normal.X,normal.Y);
                        if(wall.CrossSection!=WallCrossSection.Vertical)throw new InvalidOperationException("Наклонная стена: требуется ручное задание.");
                    }
                    else
                    {
                        if(HostObjectUtils.GetTopFaces((HostObject)host.Element).Any(r=>host.Element.GetGeometryObjectFromReference(r) is not PlanarFace face||Math.Abs(face.FaceNormal.Z)<1-1e-7))throw new InvalidOperationException("Наклонное перекрытие: требуется ручное задание.");
                    }
                    if(!geometryCache.TryGetValue(host,out var solids))
                    {solids=HoleMarkers.Solids(host.Element.get_Geometry(new Options{DetailLevel=ViewDetailLevel.Fine})).Select(s=>SolidUtils.CreateTransformed(s,host.Transform)).ToList();geometryCache.Add(host,solids);}
                    var segments=new List<Curve>();
                    foreach(var solid in solids){using var hit=solid.IntersectWithCurve(line,new SolidCurveIntersectionOptions());for(int i=0;i<hit.SegmentCount;i++)if(hit.GetCurveSegment(i).Length>1e-6)segments.Add(hit.GetCurveSegment(i));}
                    if(segments.Count==0)continue;
                    if(segments.Count!=1)throw new InvalidOperationException("Составное пересечение: требуется ручная проверка.");
                    var segment=segments[0];var entry=segment.GetEndPoint(0);var exit=segment.GetEndPoint(1);
                    if(new[]{entry,exit}.Any(p=>p.DistanceTo(line.GetEndPoint(0))<1e-6||p.DistanceTo(line.GetEndPoint(1))<1e-6))throw new InvalidOperationException("Коммуникация заканчивается внутри основы: сквозной проход не подтверждён.");
                    var center=(entry+exit)/2;var normalAxis=orientation==HoleOrientation.Wall?new XYZ(-Math.Sin(yaw),Math.Cos(yaw),0):XYZ.BasisZ;
                    double thickness=HoleMarkers.Mm(Math.Abs((exit-entry).DotProduct(normalAxis)));
                    var g=CrossingGeometry.Fit(Millimeters(center),Vector(line.Direction),Vector(connector.CoordinateSystem.BasisX),Vector(connector.CoordinateSystem.BasisY),
                        HoleMarkers.Mm(width),HoleMarkers.Mm(height),round,HoleMarkers.Mm(insulation),thickness,orientation,yaw,options);
                    var level=levels.LastOrDefault(l=>l.Elevation<=center.Z+1e-6)??levels[0];
                    if(host.Link.Length==0&&doc.GetElement(host.Element.LevelId) is Level hostLevel)level=hostLevel;
                    string key=CrossingGeometry.Key(host.Link,host.Element.UniqueId,mep.UniqueId);
                    var matching=data.Holes.Where(h=>h.CrossingKey==key).ToList();
                    // Manual tasks can also cover the crossing; compare in the task's own frame.
                    var covering=data.Holes.FirstOrDefault(h=>!h.Deleted&&!h.GeometryUnresolved&&(h.HostId.Length==0||h.HostId==host.Element.UniqueId&&h.HostLinkId==host.Link)&&Covers(h.Geometry,g));
                    string state=matching.Any(h=>h.Deleted)?"Ранее удалено":matching.Count>0?(matching.Any(h=>h.GeometryUnresolved)?"Проверить изменённое задание":covering!=null?"Есть задание":"Задание требует изменения"):covering!=null?"Покрыто ручным заданием":"Новое задание";
                    result.Add(new(key,host.Element.UniqueId,host.Link,mep.UniqueId,$"{mep.Category.Name} · {mep.Id.Value}",$"{host.Label} · {host.Element.Id.Value}",level.UniqueId,level.Name,HoleMarkers.Mm(level.Elevation),g,state));
                }
                catch(Exception error){warnings.Add($"{mep.Id.Value} → {host.Label}/{host.Element.Id.Value}: {error.Message}");}
            }
        }
        return new(result,warnings,meps.Count,hosts.Count);
    }
    static bool Covers(HoleGeometry a,HoleGeometry b)
    {
        if(a.Orientation!=b.Orientation||Math.Abs(Math.Sin(a.Yaw-b.Yaw))>1e-7)return false;
        double c=Math.Cos(a.Yaw),s=Math.Sin(a.Yaw),dx=b.X-a.X,dy=b.Y-a.Y;
        double u=c*dx+s*dy,v=a.Orientation==HoleOrientation.Wall?b.Z-a.Z:-s*dx+c*dy,n=a.Orientation==HoleOrientation.Wall?-s*dx+c*dy:b.Z-a.Z;
        if(Math.Abs(n)+b.Depth/2>a.Depth/2+0.1)return false;
        if(a.Shape==HoleShape.Round)return b.Shape==HoleShape.Round&&Math.Sqrt(u*u+v*v)+b.Diameter/2<=a.Diameter/2+0.1;
        return Math.Abs(u)+b.A/2<=a.A/2+0.1&&Math.Abs(v)+b.B/2<=a.B/2+0.1;
    }
    internal static BoundingBoxXYZ WorldBounds(BoundingBoxXYZ box,Transform transform)
    {
        var points=new List<XYZ>();foreach(double x in new[]{box.Min.X,box.Max.X})foreach(double y in new[]{box.Min.Y,box.Max.Y})foreach(double z in new[]{box.Min.Z,box.Max.Z})points.Add(transform.OfPoint(box.Transform.OfPoint(new XYZ(x,y,z))));
        return new(){Min=new(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)),Max=new(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z))};
    }
    internal static List<Hole> Create(Document doc,Registry data,CrossingOptions options,IReadOnlyCollection<ElementId>? selection,IReadOnlyList<CrossingCandidate> chosen,string discipline)
    {
        HoleController.RequireProject(data);var actor=HoleStore.Actor(data);if(actor.Role is not (Role.Engineer or Role.Admin))throw new InvalidOperationException("Задания создаёт ИОС или администратор.");
        if(chosen.Count==0||chosen.Any(c=>!c.CanCreate)||chosen.Select(c=>c.Key).Distinct().Count()!=chosen.Count)throw new InvalidOperationException("Выберите строки со статусом «Новое задание».");
        var fresh=Scan(doc,data,options,selection).Candidates.ToDictionary(c=>c.Key);
        if(chosen.Any(c=>!fresh.TryGetValue(c.Key,out var now)||!now.CanCreate||c.Signature!=now.Signature))throw new InvalidOperationException("Модель или параметры изменились после поиска. Обновите предварительный просмотр.");
        var created=new List<Hole>();foreach(var item in chosen)
        {
            var hole=HoleWorkflow.Create(data.Project!,actor,item.Geometry,discipline,item.LevelId,item.LevelName,item.LevelElevation,HoleController.NextMark(data,discipline));
            hole.HostId=item.HostId;hole.HostLinkId=item.LinkId;hole.CommunicationId=item.CommunicationId;hole.CrossingKey=item.Key;
            HoleWorkflow.Log(hole,actor,"Создано по пересечению",$"{item.Communication}; зазор {options.Clearance:0.#} мм; округление {options.Step:0.#} мм; изоляция {(options.IncludeInsulation?"учтена":"не учитывается")}");
            data.Holes.Add(hole);created.Add(hole);
        }
        return created;
    }
}
