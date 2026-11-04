using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace Ac2Revit;

public static class NativeDimensionStage
{
    const double ReferenceToleranceMeters=0.0005;
    sealed record FaceReference(Reference Reference,XYZ Origin,XYZ Normal);
    static IEnumerable<FaceReference> Faces(GeometryElement geometry,Transform transform)
    {
        foreach(var item in geometry)
        {
            if(item is Solid solid)foreach(var face in solid.Faces.OfType<PlanarFace>())
            {if(face.Reference!=null)yield return new(face.Reference,transform.OfPoint(face.Origin),transform.OfVector(face.FaceNormal).Normalize());}
            else if(item is GeometryInstance instance)foreach(var face in Faces(instance.GetSymbolGeometry(),transform.Multiply(instance.Transform)))yield return face;
        }
    }
    static Reference Resolve(Document doc,Element owner,XYZ point,XYZ direction)
    {
        if(owner is Grid grid)
        {
            if(grid.Curve is not Line axis||Math.Abs(axis.Direction.DotProduct(direction))>1e-7)throw new InvalidOperationException("Ось не перпендикулярна направлению размера.");
            var delta=point-axis.GetEndPoint(0);var normal=axis.Direction.CrossProduct(XYZ.BasisZ).Normalize();
            if(Math.Abs(delta.DotProduct(normal))>Geometry.Feet(ReferenceToleranceMeters))throw new InvalidOperationException("Исходная точка не лежит на нативной оси.");
            return new Reference(grid);
        }
        if(owner is not (Wall or Floor or FamilyInstance))throw new InvalidOperationException("Опора не является нативной стеной, перекрытием, семейством или осью.");
        var geometry=owner.get_Geometry(new Options{ComputeReferences=true,DetailLevel=ViewDetailLevel.Fine})??throw new InvalidOperationException("Геометрия опоры недоступна.");
        var faces=Faces(geometry,Transform.Identity).Where(f=>f.Reference.ElementId==owner.Id&&Math.Abs(Math.Abs(f.Normal.DotProduct(direction))-1)<1e-7)
            .Select(f=>(Face:f,Distance:Math.Abs((point-f.Origin).DotProduct(f.Normal)))).Where(f=>f.Distance<=Geometry.Feet(ReferenceToleranceMeters)).OrderBy(f=>f.Distance).ToList();
        if(faces.Count==0)throw new InvalidOperationException("Нет нативной плоской грани в исходной точке привязки (допуск 0,5 мм).");
        return faces[0].Face.Reference;
    }
    static ViewPlan View(Document doc,Level level)
    {
        var existing=new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v=>!v.IsTemplate&&v.ViewType==ViewType.FloorPlan&&v.GenLevel.Id==level.Id&&v.Name.StartsWith("AC Plan ",StringComparison.Ordinal));
        if(existing!=null)return existing;
        var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.FloorPlan);
        var plan=ViewPlan.Create(doc,type.Id,level.Id);plan.Name="AC Plan "+level.Name;return plan;
    }
    static double[] Values(Dimension dim)=>dim.NumberOfSegments==0?[dim.Value??double.NaN]:dim.Segments.Cast<DimensionSegment>().Select(s=>s.Value??double.NaN).ToArray();
    static string Snapshot(Document doc,Dimension dim)
    {
        var curve=dim.Curve as Line??throw new InvalidOperationException("Размерная линия не прямая.");var p=curve.Origin;var q=curve.Direction;
        object? text=dim.NumberOfSegments==0?new{dim.ValueOverride,dim.Prefix,dim.Suffix,dim.Above,dim.Below,position=PointOf(dim.TextPosition)}:null;
        var segments=dim.NumberOfSegments>0?dim.Segments.Cast<DimensionSegment>().Select(s=>new{s.ValueOverride,s.Prefix,s.Suffix,s.Above,s.Below,position=PointOf(s.TextPosition)}).ToArray():null;
        return Json.Hash(new{type=dim.GetTypeId().Value,view=dim.OwnerViewId.Value,a=PointOf(p),b=PointOf(q),refs=dim.References.Cast<Reference>().Select(r=>r.ConvertToStableRepresentation(doc)).ToArray(),values=Values(dim),text,segments});
    }
    static double[] PointOf(XYZ p)=>[p.X,p.Y,p.Z];
    static Element Owner(Document doc,Manifest manifest,Dictionary<string,Element> index,Geometry geometry,string key)
    {
        if(index.TryGetValue(key,out var element))return element;
        var anchor=manifest.DimensionAnchors.SingleOrDefault(a=>a.SourceGuid.Equals(key,StringComparison.OrdinalIgnoreCase))??throw new InvalidOperationException("Не перенесена исходная опора: "+key);
        if(!anchor.Start.Finite||!anchor.End.Finite)throw new InvalidDataException("Некорректные координаты исходной оси.");
        var a=geometry.Xyz(anchor.Start);var b=geometry.Xyz(anchor.End);var axis=(b-a).Normalize();
        var matching=index.Values.OfType<Grid>().Where(g=>g.Curve is Line line&&Math.Abs(Math.Abs(line.Direction.DotProduct(axis))-1)<1e-7&&
            (a-line.GetEndPoint(0)).CrossProduct(line.Direction).GetLength()<=Geometry.Feet(ReferenceToleranceMeters)&&
            (b-line.GetEndPoint(0)).CrossProduct(line.Direction).GetLength()<=Geometry.Feet(ReferenceToleranceMeters)).ToList();
        if(matching.Count!=1)throw new InvalidOperationException($"Опора сетки {anchor.Label}: совпавших нативных осей {matching.Count}; требуется однозначное соответствие.");
        return matching[0];
    }
    public static Report Run(Document doc,Manifest manifest,Action<string,int,int>? progress=null,UIApplication? ui=null)
    {
        var original=ui?.ActiveUIDocument?.ActiveView;
        try{return RunCore(doc,manifest,progress,ui);}
        finally{if(original is {IsValidObject:true}&&ui?.ActiveUIDocument?.Document.PathName==doc.PathName)ui.ActiveUIDocument.ActiveView=original;}
    }
    static Report RunCore(Document doc,Manifest manifest,Action<string,int,int>? progress,UIApplication? ui)
    {
        var report=new Report{ProjectId=manifest.Source.ProjectId,Mode="nativeDimensions"};var index=Storage.Index(doc,manifest.Source.ProjectId);
        var geometry=new Geometry(doc,manifest.Coordinates.Transform);var rows=manifest.Elements.Where(e=>e.Kind=="Dimension").ToList();int done=0;
        foreach(var row in rows)
        {
            progress?.Invoke("nativeDimensions",done++,rows.Count);string hash=Json.Hash(new{row.Payload,manifest.Coordinates.Transform,anchors=manifest.DimensionAnchors.OrderBy(a=>a.SourceGuid,StringComparer.Ordinal).ToArray()});
            if(ui!=null&&index.GetValueOrDefault(row.LevelKey) is Level nativeLevel)
            {
                ViewPlan target;
                using(var preparation=new Transaction(doc,"План нативных размеров")){preparation.Start();target=View(doc,nativeLevel);preparation.Commit();}
                ui.ActiveUIDocument.ActiveView=target;
            }
            if(index.TryGetValue(row.SourceGuid,out var old))
            {
                try
                {
                    var native=old as Dimension??throw new InvalidOperationException("Ключ размера занят другим элементом.");
                    var raw=native.GetEntity(Storage.Schema()).Get<string>(Storage.Schema().GetField("RawJson"));using var state=JsonDocument.Parse(raw);
                    bool same=Storage.Read(native)?.Hash==hash&&native.AreReferencesAvailable&&state.RootElement.S("nativeHash")==Snapshot(doc,native);
                    report.Add(row.SourceGuid,"Dimension",same?"unchanged":"conflict",same?"Нативные опоры и положение сохранены.":"Размер или опоры изменились; существующий размер сохранён.",native.UniqueId);
                }
                catch(Exception error){report.Add(row.SourceGuid,"Dimension","conflict",error.Message,old.UniqueId);}
                continue;
            }
            using var group=new TransactionGroup(doc,"Archicad — проверенный нативный размер");group.Start();
            using var transaction=new Transaction(doc,"Archicad — нативный размер");transaction.Start();var failures=new DimensionFailures();
            transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
            try
            {
                if(row.Status!="ready")throw new InvalidOperationException(row.Reason);
                var source=row.Payload.Read<SourceDimension>();source.Validate();
                if(index.GetValueOrDefault(row.LevelKey) is not Level level)throw new InvalidOperationException("Не найден нативный уровень исходного размера.");
                var view=View(doc,level);double yaw=manifest.Coordinates.Transform.RotationRadians;
                var d=new XYZ(source.Direction.X*Math.Cos(yaw)-source.Direction.Y*Math.Sin(yaw),source.Direction.X*Math.Sin(yaw)+source.Direction.Y*Math.Cos(yaw),0).Normalize();
                var witnesses=source.Witnesses.Select(w=>(Source:w,Point:geometry.Xyz(w.Coordinate))).OrderBy(w=>w.Point.DotProduct(d)).ToList();
                var refs=new ReferenceArray();var stable=new HashSet<string>();
                foreach(var witness in witnesses)
                {
                    var owner=Owner(doc,manifest,index,geometry,witness.Source.SourceGuid);
                    var reference=Resolve(doc,owner,witness.Point,d);if(!stable.Add(reference.ConvertToStableRepresentation(doc)))throw new InvalidOperationException("Повтор нативной опоры в цепи.");refs.Append(reference);
                }
                var position=geometry.Xyz(source.LinePosition);position=new XYZ(position.X,position.Y,level.Elevation);
                double min=witnesses.Min(w=>(w.Point-position).DotProduct(d)),max=witnesses.Max(w=>(w.Point-position).DotProduct(d));
                var line=Line.CreateBound(position+d*(min-Geometry.Feet(0.2)),position+d*(max+Geometry.Feet(0.2)));
                var dim=doc.Create.NewDimension(view,line,refs);doc.Regenerate();
                var expected=witnesses.Zip(witnesses.Skip(1),(a,b)=>(b.Point-a.Point).DotProduct(d)).ToArray();var measured=Values(dim);
                if(expected.Length!=measured.Length||expected.Zip(measured,(a,b)=>Math.Abs(a-b)).Any(v=>!double.IsFinite(v)||v>Geometry.Feet(ReferenceToleranceMeters*2)))throw new InvalidOperationException($"Значения Revit не совпали с исходной размерной цепью: expected={string.Join(",",expected)}; actual={string.Join(",",measured)}.");
                if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Revit отклонил размер: "+string.Join("; ",failures.Messages));
                using(var state=new Transaction(doc,"Проверить и записать опоры размера"))
                {
                    state.Start();doc.Regenerate();
                    if(!dim.AreReferencesAvailable)throw new InvalidOperationException("После фиксации Revit не разрешил нативные опоры размера.");
                    Storage.Tag(dim,manifest.Source.ProjectId,row.SourceGuid,hash,JsonSerializer.Serialize(new{source=row.Raw,nativeHash=Snapshot(doc,dim)},Json.Options));
                    if(state.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Не сохранены исходные опоры размера.");
                }
                group.Assimilate();
                index[row.SourceGuid]=dim;report.Add(row.SourceGuid,"Dimension","created","Нативные опоры проверены; отклонение цепи не более 1 мм.",dim.UniqueId);
            }
            catch(Exception error){if(transaction.GetStatus()==TransactionStatus.Started)transaction.RollBack();if(group.GetStatus()==TransactionStatus.Started)group.RollBack();report.Add(row.SourceGuid,"Dimension","unsupported",error.Message);}
        }
        progress?.Invoke("nativeDimensions",rows.Count,rows.Count);return report;
    }
    sealed class DimensionFailures:IFailuresPreprocessor
    {
        internal List<string> Messages {get;}=[];
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {foreach(var f in accessor.GetFailureMessages())Messages.Add(f.GetDescriptionText());return Messages.Count>0?FailureProcessingResult.ProceedWithRollBack:FailureProcessingResult.Continue;}
    }
}
