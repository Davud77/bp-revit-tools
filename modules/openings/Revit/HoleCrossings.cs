using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using BotPlus.Openings.Core;

namespace BotPlus.Openings;

internal sealed record CrossingCandidate(string Key,string HostId,string LinkId,string CommunicationId,string Communication,string Host,
    string LevelId,string LevelName,double LevelElevation,HoleGeometry Geometry,string State,string Kind="Отверстие")
{
    public bool CanCreate=>State=="Новое задание";
    public bool HasBody=>Kind=="Отверстие"&&State!="Новое задание"&&State!="Ранее удалено";
    public string Size=>Kind!="Отверстие"?"—":Geometry.Shape==HoleShape.Round?$"Ø{Geometry.Diameter:0.#}":$"{Geometry.Width:0.#} × {Geometry.Height:0.#}";
    public double Depth=>Geometry.Depth;
    public string Signature=>HoleJson.Hash(new{Key,Geometry,LevelId});
}
internal sealed record CrossingScan(List<CrossingCandidate> Candidates,List<string> Warnings,int Communications,int HostCount);
internal static class HoleCrossings
{
    static Vector3 Vector(XYZ p)=>new(p.X,p.Y,p.Z);
    static Vector3 Millimeters(XYZ p)=>new(HoleMarkers.Mm(p.X),HoleMarkers.Mm(p.Y),HoleMarkers.Mm(p.Z));
    sealed record HostSource(Element Element,string Link,Transform Transform,string Label);
    internal static CrossingScan Scan(Document doc,Registry data,CrossingOptions options,IReadOnlyCollection<ElementId>? selection=null,IReadOnlyList<string>? linkOrder=null,Action<string,double>? progress=null,Func<bool>? cancelled=null)
    {
        options.Validate();var warnings=new List<string>();var result=new List<CrossingCandidate>();
        var meps=(selection==null?new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).Cast<MEPCurve>():selection.Select(doc.GetElement).OfType<MEPCurve>())
            .Where(e=>e is Pipe or Duct or CableTray or Conduit).OrderBy(e=>e.Id.Value).ToList();
        if(meps.Count==0)throw new InvalidOperationException("Нет прямых труб, воздуховодов, лотков или коробов в выбранной области поиска.");
        var hosts=new List<HostSource>();
        void Collect(Document source,string link,Transform transform,string label)
        {
            if(transform.OfVector(XYZ.BasisZ).DistanceTo(XYZ.BasisZ)>1e-7){warnings.Add(label+": наклонённая связь пропущена.");return;}
            hosts.AddRange(new FilteredElementCollector(source).WhereElementIsNotElementType().Where(e=>e is Wall or Floor).Select(e=>new HostSource(e,link,transform,label)));
        }
        if(linkOrder==null)
        {
            Collect(doc,"",Transform.Identity,"Текущий проект");
            if(options.IncludeLinks)foreach(var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {if(link.GetLinkDocument() is {} linked)Collect(linked,link.UniqueId,link.GetTotalTransform(),link.Name);else warnings.Add(link.Name+": связь не загружена.");}
        }
        else
        {
            Collect(doc,"",Transform.Identity,"Текущий проект");
            foreach(var id in linkOrder)
            {
                if(doc.GetElement(id) is not RevitLinkInstance link){warnings.Add("Связь не найдена в проекте.");continue;}
                if(link.GetLinkDocument() is {} linked)Collect(linked,link.UniqueId,link.GetTotalTransform(),link.Name);else warnings.Add(link.Name+": связь не загружена.");
            }
        }
        var levels=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>l.Elevation).ToList();
        if(levels.Count==0)throw new InvalidOperationException("В проекте нет уровней.");
        var bounds=hosts.Select(h=>(Host:h,Box:h.Element.get_BoundingBox(null))).Where(h=>h.Box!=null).Select(h=>(h.Host,Box:WorldBounds(h.Box!,h.Host.Transform))).ToList();
        progress?.Invoke("Подготовка габаритов стен и перекрытий…",-1);
        var hostGrid=new BoxGrid<int>();for(int k=0;k<bounds.Count;k++)hostGrid.Add(k,bounds[k].Box.Min,bounds[k].Box.Max);
        var geometryCache=new Dictionary<HostSource,List<Solid>>();int pairs=0;
        int doneMeps=0;
        foreach(var mep in meps)
        {
            if(++doneMeps%25==0||doneMeps==1){progress?.Invoke($"Коммуникации текущей модели: {doneMeps} из {meps.Count}; найдено пересечений: {result.Count}",(double)doneMeps/meps.Count);if(cancelled?.Invoke()==true)throw new OperationCanceledException();}
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
            foreach(var (host,box) in hostGrid.Query(outline.MinimumPoint,outline.MaximumPoint).Select(i=>bounds[i]))
            {
                if(!outline.Intersects(new Outline(box.Min,box.Max),1e-7))continue;
                ++pairs;
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
                    result.Add(new(key,host.Element.UniqueId,host.Link,mep.UniqueId,$"{mep.Category.Name} · {mep.Id.Value}",$"{host.Element.Category?.Name} · {host.Element.Id.Value}",level.UniqueId,level.Name,HoleMarkers.Mm(level.Elevation),g,state));
                }
                catch(Exception error){warnings.Add($"{mep.Id.Value} → {host.Label}/{host.Element.Id.Value}: {error.Message}");}
            }
        }
        if(linkOrder!=null)
        {
            // Сети: собственная модель сама с собой и с выбранными связями. Быстрый отбор по осям и габаритам,
            // затем точная проверка по пересечению тел; касание и зазоры в пределах допуска коллизией не считаются.
            var radii=meps.ToDictionary(m=>m.Id.Value,m=>Radius(m,options.IncludeInsulation?Insulation(doc,m):0));
            var ownSolids=new Dictionary<long,List<Solid>>();
            List<Solid> OwnBody(MEPCurve m){if(!ownSolids.TryGetValue(m.Id.Value,out var body))ownSolids[m.Id.Value]=body=MepSolids(m,Transform.Identity);return body;}
            HashSet<long> Neighbours(MEPCurve m)
            {
                var set=new HashSet<long>();
                foreach(Connector c in m.ConnectorManager.Connectors)foreach(Connector r in c.AllRefs){if(r.Owner.Id!=m.Id)set.Add(r.Owner.Id.Value);}
                return set;
            }
            void Sweep(string name,string linkId,Document otherDoc,Transform transform,IEnumerable<MEPCurve> source,bool self)
            {
                var others=new List<(MEPCurve Element,Line Line,double Radius,BoundingBoxXYZ Box)>();
                foreach(var other in source)
                {
                    if(other.Location is not LocationCurve {Curve:Line otherLine}||other.get_BoundingBox(null) is not {} otherBox)continue;
                    double radius=self?radii.GetValueOrDefault(other.Id.Value):Radius(other,options.IncludeInsulation?Insulation(otherDoc,other):0);if(radius<=0)continue;
                    others.Add((other,transform.IsIdentity?otherLine:(Line)otherLine.CreateTransformed(transform),radius,WorldBounds(otherBox,transform)));
                }
                var grid=new BoxGrid<int>();double maxOther=others.Count==0?0:others.Max(o=>o.Radius);for(int k=0;k<others.Count;k++)grid.Add(k,others[k].Box.Min,others[k].Box.Max);
                int done=0;var otherBodies=new Dictionary<long,List<Solid>>();
                foreach(var mep in meps)
                {
                    if(++done%25==0){progress?.Invoke($"Сети: {name}: {done} из {meps.Count}",(double)done/meps.Count);if(cancelled?.Invoke()==true)throw new OperationCanceledException();}
                    if(mep.Location is not LocationCurve {Curve:Line own}||mep.get_BoundingBox(null) is not {} ownBox||radii[mep.Id.Value]<=0)continue;
                    double margin=radii[mep.Id.Value]+maxOther;HashSet<long>? mine=null;
                    foreach(var other in grid.Query(ownBox.Min-new XYZ(margin,margin,margin),ownBox.Max+new XYZ(margin,margin,margin)).Select(k=>others[k]))
                    {
                        if(self&&other.Element.Id.Value<=mep.Id.Value)continue;
                        double reach=radii[mep.Id.Value]+other.Radius;
                        double distance=CrossingGeometry.SegmentDistance(Vector(own.GetEndPoint(0)),Vector(own.GetEndPoint(1)),Vector(other.Line.GetEndPoint(0)),Vector(other.Line.GetEndPoint(1)),out var near,out _);
                        if(distance>=reach)continue;
                        if(self)
                        {
                            mine??=Neighbours(mep);var theirs=Neighbours(other.Element);
                            if(mine.Contains(other.Element.Id.Value)||theirs.Contains(mep.Id.Value)||mine.Overlaps(theirs))continue;
                        }
                        if(!otherBodies.TryGetValue(other.Element.Id.Value,out var body))otherBodies[other.Element.Id.Value]=body=MepSolids(other.Element,transform);
                        if(!SolidsIntersect(OwnBody(mep),body,out var point))continue;
                        var level=levels.LastOrDefault(l=>l.Elevation<=point.Z+1e-6)??levels[0];
                        string key=CrossingGeometry.Key(linkId,other.Element.UniqueId,mep.UniqueId);
                        var marker=new HoleGeometry{X=HoleMarkers.Mm(point.X),Y=HoleMarkers.Mm(point.Y),Z=HoleMarkers.Mm(point.Z)};
                        result.Add(new(key,other.Element.UniqueId,linkId,mep.UniqueId,$"{mep.Category.Name} · {mep.Id.Value}",$"{other.Element.Category?.Name} · {other.Element.Id.Value}",level.UniqueId,level.Name,HoleMarkers.Mm(level.Elevation),marker,"Коллизия сетей","Сеть × сеть"));
                    }
                }
            }
            Sweep("текущая модель",string.Empty,doc,Transform.Identity,meps,true);
            foreach(var id in linkOrder)
            {
                if(doc.GetElement(id) is not RevitLinkInstance link||link.GetLinkDocument() is not {} linked)continue;
                Sweep(link.Name,link.UniqueId,linked,link.GetTotalTransform(),new FilteredElementCollector(linked).OfClass(typeof(MEPCurve)).Cast<MEPCurve>().Where(e=>e is Pipe or Duct or CableTray or Conduit).ToList(),false);
            }
        }
        if(linkOrder!=null){var index=linkOrder.Select((id,n)=>(id,n)).ToDictionary(p=>p.id,p=>p.n);result=result.OrderBy(c=>index.GetValueOrDefault(c.LinkId,c.LinkId.Length==0?-1:int.MaxValue)).ToList();}
        return new(result,warnings,meps.Count,hosts.Count);
    }
    static List<Solid> MepSolids(Element e,Transform t)=>HoleMarkers.Solids(e.get_Geometry(new Options{DetailLevel=ViewDetailLevel.Medium})).Select(s=>t.IsIdentity?s:SolidUtils.CreateTransformed(s,t)).ToList();
    static bool SolidsIntersect(List<Solid> first,List<Solid> second,out XYZ point)
    {
        foreach(var a in first)foreach(var b in second)
        {
            try
            {
                var common=BooleanOperationsUtils.ExecuteBooleanOperation(a,b,BooleanOperationsType.Intersect);
                if(common!=null&&common.Volume>2e-6){point=common.ComputeCentroid();return true;}
            }
            catch(Autodesk.Revit.Exceptions.InvalidOperationException){}
        }
        point=XYZ.Zero;return false;
    }
    static double Insulation(Document d,MEPCurve m)=>m is Pipe or Duct?InsulationLiningBase.GetInsulationIds(d,m.Id).Select(d.GetElement).OfType<InsulationLiningBase>().Select(i=>i.Thickness).DefaultIfEmpty(0).Max():0;
    static double Radius(MEPCurve m,double insulation)
    {
        try{var c=m.ConnectorManager.Connectors.Cast<Connector>().First(x=>x.ConnectorType==ConnectorType.End);return (c.Shape==ConnectorProfileType.Round?c.Radius:Math.Max(c.Width,c.Height)/2)+insulation;}
        catch{return 0;}
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
    internal static List<Hole> Create(Document doc,Registry data,CrossingOptions options,IReadOnlyCollection<ElementId>? selection,IReadOnlyList<CrossingCandidate> chosen,string discipline,IReadOnlyList<string>? linkOrder=null,IReadOnlyCollection<string>? architectLinks=null,Action<string,double>? progress=null,Func<bool>? cancelled=null)
    {
        HoleController.RequireProject(data);var actor=HoleStore.Actor(data);if(actor.Role is not (Role.Engineer or Role.Admin))throw new InvalidOperationException("Задания создаёт ИОС или администратор.");
        if(chosen.Count==0||chosen.Any(c=>!c.CanCreate)||chosen.Select(c=>c.Key).Distinct().Count()!=chosen.Count)throw new InvalidOperationException("Выберите строки со статусом «Новое задание».");
        if(architectLinks is {Count:>0}&&chosen.Any(c=>c.LinkId.Length>0&&!architectLinks.Contains(c.LinkId)))throw new InvalidOperationException("Часть выбранных строк относится к основам вне выбранной модели АР. Отметьте строки только по стенам и перекрытиям выбранной модели АР.");
        var fresh=Scan(doc,data,options,selection,linkOrder,progress,cancelled).Candidates.ToDictionary(c=>c.Key);
        if(chosen.Any(c=>!fresh.TryGetValue(c.Key,out var now)||!now.CanCreate||c.Signature!=now.Signature))throw new InvalidOperationException("Модель или параметры изменились после поиска. Обновите предварительный просмотр.");
        var groups=options.MergeAdjacent?CrossingGeometry.GroupAdjacent(chosen.Select(c=>c.Geometry).ToList(),chosen.Select(c=>c.LinkId+"/"+c.HostId).ToList(),options.MergeGap)
            :chosen.Select((_,i)=>new List<int>{i}).ToList();
        var created=new List<Hole>();
        foreach(var group in groups)
        {
            var items=group.Select(i=>chosen[i]).ToList();var item=items[0];
            var geometry=items.Count==1?item.Geometry:CrossingGeometry.Union(items.Select(i=>i.Geometry).ToList());
            var hole=HoleWorkflow.Create(data.Project!,actor,geometry,discipline,item.LevelId,item.LevelName,item.LevelElevation,HoleController.NextMark(data,discipline));
            hole.HostId=item.HostId;hole.HostLinkId=item.LinkId;hole.CommunicationId=item.CommunicationId;hole.CrossingKey=item.Key;
            HoleWorkflow.Log(hole,actor,"Создано по пересечению",$"{item.Communication}; запас {options.Clearance:0.#}/{options.HeightClearance:0.#}/{options.DiameterClearance:0.#} мм; округление {options.Step:0.#} мм; изоляция {(options.IncludeInsulation?"учтена":"не учитывается")}");
            if(items.Count>1)HoleWorkflow.Log(hole,actor,"Склеены примыкающие отверстия",string.Join("; ",items.Select(i=>i.Communication)));
            data.Holes.Add(hole);created.Add(hole);
        }
        return created;
    }
}
