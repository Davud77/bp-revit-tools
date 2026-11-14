using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace BotPlus.RT;

internal sealed record TagStyle(ElementId TypeId,long Category,XYZ Anchor,Transform HostTransform,XYZ Head,TagOrientation Orientation,double Angle,bool Leader,
    LeaderEndCondition EndCondition,XYZ? Elbow,XYZ? End);

internal static class TagOperations
{
    internal static Element Host(Document doc,Reference reference)
    {
        var element=doc.GetElement(reference.ElementId)??throw new InvalidOperationException("Опора марки не найдена.");
        return element is RevitLinkInstance link
            ?link.GetLinkDocument()?.GetElement(reference.LinkedElementId)??throw new InvalidOperationException("Связь марки не загружена.")
            :element;
    }
    internal static bool CanCopyTo(Element sourceHost,Element target,TagCopyScope scope)
    {
        if(target is not FamilyInstance instance)return false;
        return scope switch
        {
            TagCopyScope.All=>true,
            TagCopyScope.Category=>sourceHost.Category!=null&&target.Category?.Id==sourceHost.Category.Id,
            _=>sourceHost is FamilyInstance source&&source.Document.Equals(target.Document)&&source.Symbol.Family.Id==instance.Symbol.Family.Id
        };
    }
    internal static XYZ Anchor(Document doc,Reference reference)
    {
        var element=doc.GetElement(reference.ElementId)??throw new InvalidOperationException("Опора марки не найдена.");var transform=Transform.Identity;
        if(element is RevitLinkInstance link)
        {transform=link.GetTotalTransform();element=link.GetLinkDocument()?.GetElement(reference.LinkedElementId)??throw new InvalidOperationException("Связь марки не загружена.");}
        XYZ point=element.Location switch{LocationPoint location=>location.Point,LocationCurve location=>location.Curve.Evaluate(0.5,true),_=>Center(element)};
        return transform.OfPoint(point);
    }
    static XYZ Center(Element element)
    {var box=element.get_BoundingBox(null)??throw new InvalidOperationException("Не определён центр опоры.");return box.Transform.OfPoint((box.Min+box.Max)/2);}
    internal static Transform HostTransform(Document doc,Reference reference)
    {
        var element=doc.GetElement(reference.ElementId)??throw new InvalidOperationException("Опора марки не найдена.");
        var linkTransform=Transform.Identity;
        if(element is RevitLinkInstance link)
        {
            linkTransform=link.GetTotalTransform();
            element=link.GetLinkDocument()?.GetElement(reference.LinkedElementId)??throw new InvalidOperationException("Связь марки не загружена.");
        }
        // Keep the leader end in host coordinates, including rotation, reflection and depth.
        return element is FamilyInstance instance?linkTransform.Multiply(instance.GetTransform()):Transform.CreateTranslation(Anchor(doc,reference));
    }
    internal static XYZ LeaderEnd(Document doc,TagStyle style,Reference target)
        =>HostTransform(doc,target).OfPoint(style.HostTransform.Inverse.OfPoint(style.End!));
    internal static void ValidateView(View view)
    {
        if(view.IsTemplate||view is ViewSheet)throw new InvalidOperationException("Откройте план, разрез, фасад или заблокированный ортогональный 3D-вид.");
        if(view is View3D threeD&&(threeD.IsPerspective||!threeD.IsLocked))
            throw new InvalidOperationException("Для работы с марками откройте ортогональный 3D-вид и сохраните его ориентацию с блокировкой.");
    }
    internal static Reference Single(IndependentTag tag)
    {
        var refs=tag.GetTaggedReferences();if(tag.IsOrphaned||refs.Count!=1)throw new InvalidOperationException("Нужна марка с одной доступной опорой.");return refs[0];
    }
    internal static TagStyle Read(Document doc,View view,IndependentTag tag)
    {
        if(tag.OwnerViewId!=view.Id)throw new InvalidOperationException("Выберите марку на активном виде.");
        var reference=Single(tag);var elbow=tag.HasLeader&&tag.HasLeaderElbow(reference)?tag.GetLeaderElbow(reference):null;
        return new(tag.GetTypeId(),tag.Category.Id.Value,Anchor(doc,reference),HostTransform(doc,reference),tag.TagHeadPosition,tag.TagOrientation,tag.RotationAngle,tag.HasLeader,
            tag.HasLeader?tag.LeaderEndCondition:LeaderEndCondition.Attached,elbow,tag.HasLeader&&tag.LeaderEndCondition==LeaderEndCondition.Free?tag.GetLeaderEnd(reference):null);
    }
    internal static XYZ Shift(View view,XYZ from,XYZ to)
    {var delta=to-from;return view.RightDirection*delta.DotProduct(view.RightDirection)+view.UpDirection*delta.DotProduct(view.UpDirection);}
    internal static double ViewDistance(View view,XYZ first,XYZ second)
    {
        var delta=first-second;var right=view.RightDirection;var up=view.UpDirection;
        return TagViewGeometry.Distance((delta.X,delta.Y,delta.Z),(right.X,right.Y,right.Z),(up.X,up.Y,up.Z));
    }
    internal static void Apply(Document doc,View view,TagStyle style,IndependentTag tag)
    {
        if(tag.Pinned||tag.OwnerViewId!=view.Id||tag.Category.Id.Value!=style.Category)throw new InvalidOperationException("Марка закреплена, другого вида или несовместимой категории.");
        var reference=Single(tag);var shift=Shift(view,style.Anchor,Anchor(doc,reference));
        if(tag.GetTypeId()!=style.TypeId)
        {
            if(!tag.GetValidTypes().Contains(style.TypeId))throw new InvalidOperationException("Тип эталона не подходит этой марке.");
            if(tag.ChangeTypeId(style.TypeId)!=ElementId.InvalidElementId)throw new InvalidOperationException("Изменение типа потребовало замены марки; операция отменена.");
        }
        tag.TagOrientation=style.Orientation;
        if(style.Orientation==TagOrientation.AnyModelDirection)tag.RotationAngle=style.Angle;
        tag.HasLeader=style.Leader;
        // Moving the head can carry the elbow with it. Finish the head move before
        // restoring the dragged elbow / landing, including the default offset of Create.
        tag.TagHeadPosition=style.Head+shift;
        doc.Regenerate();
        if(style.Leader)
        {
            if(!tag.CanLeaderEndConditionBeAssigned(style.EndCondition))throw new InvalidOperationException("Эта марка не поддерживает изменение выноски.");
            tag.LeaderEndCondition=style.EndCondition;
            if(style.End!=null)tag.SetLeaderEnd(reference,LeaderEnd(doc,style,reference));
            if(style.Elbow!=null)tag.SetLeaderElbow(reference,style.Elbow+shift);
            else if(tag.HasLeaderElbow(reference))tag.SetLeaderElbow(reference,style.Head+shift);
        }
        doc.Regenerate();
        // In axonometric views Revit may normalize annotation depth. Compare what
        // is drawn in the view for all three leader control points.
        Audit3D(view,style,tag,reference,shift,doc);
        if(ViewDistance(view,tag.TagHeadPosition,style.Head+shift)>1e-6)throw new InvalidOperationException("Revit изменил положение марки в плоскости вида; операция отменена.");
        if(style.Leader&&style.Elbow!=null&&(!tag.HasLeaderElbow(reference)||ViewDistance(view,tag.GetLeaderElbow(reference),style.Elbow+shift)>1e-6))
            throw new InvalidOperationException("Revit изменил точку перетаскивания / перегиб выноски; операция отменена.");
        if(style.Leader&&style.Elbow==null&&tag.HasLeaderElbow(reference))
            throw new InvalidOperationException("Revit добавил перегиб прямой выноски; операция отменена.");
        if(style.Leader&&style.End!=null&&ViewDistance(view,tag.GetLeaderEnd(reference),LeaderEnd(doc,style,reference))>1e-6)
            throw new InvalidOperationException("Revit изменил конец выноски в плоскости вида; операция отменена.");
    }
    static void Audit3D(View view,TagStyle style,IndependentTag tag,Reference reference,XYZ shift,Document doc)
    {
        if(view is not View3D)return;
        try
        {
            object Delta(XYZ actual,XYZ expected)=>new{screenMm=ViewDistance(view,actual,expected)*304.8,depthMm=(actual-expected).DotProduct(view.ViewDirection)*304.8};
            var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BotPlus.RT","logs");Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder,"tags-geometry.log"),System.Text.Json.JsonSerializer.Serialize(new
            {
                time=DateTimeOffset.Now,viewId=view.Id.Value,tagId=tag.Id.Value,
                head=Delta(tag.TagHeadPosition,style.Head+shift),
                elbow=style.Leader&&style.Elbow!=null&&tag.HasLeaderElbow(reference)?Delta(tag.GetLeaderElbow(reference),style.Elbow+shift):null,
                end=style.Leader&&style.End!=null?Delta(tag.GetLeaderEnd(reference),LeaderEnd(doc,style,reference)):null
            })+Environment.NewLine);
        }
        catch(Exception error){System.Diagnostics.Trace.WriteLine(error);}
    }
    static string RefKey(Reference r)=>r.ElementId.Value+"/"+r.LinkedElementId.Value;
    internal static (int Applied,int Skipped,List<string> Errors) Run(Document doc,View view,IndependentTag source,IReadOnlyList<Element> targets,bool copy,TagCopyScope scope=TagCopyScope.Family)
    {
        ValidateView(view);
        var style=Read(doc,view,source);int applied=0,skipped=0;var errors=new List<string>();
        var sourceHost=copy?Host(doc,Single(source)):null;
        var existing=new FilteredElementCollector(doc,view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
            .Where(t=>!t.IsOrphaned&&t.GetTypeId()==style.TypeId).SelectMany(t=>t.GetTaggedReferences().Select(RefKey)).ToHashSet();
        using var group=new TransactionGroup(doc,copy?"BotPlus — копировать марки":"BotPlus — сопоставить марки");group.Start();
        foreach(var target in targets.DistinctBy(t=>t.Id))
        {
            if(target.Id==source.Id){skipped++;continue;}
            if(copy&&!CanCopyTo(sourceHost!,target,scope)){skipped++;continue;}
            using var transaction=new Transaction(doc,"Марки — "+target.Id.Value);transaction.Start();
            transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new TagFailures()).SetClearAfterRollback(true));
            try
            {
                IndependentTag tag;
                if(copy)
                {
                    if(target is not FamilyInstance)throw new InvalidOperationException("Выберите экземпляр оборудования / загружаемого семейства.");
                    var reference=new Reference(target);if(existing.Contains(RefKey(reference))){skipped++;transaction.RollBack();continue;}
                    tag=IndependentTag.Create(doc,style.TypeId,view.Id,reference,style.Leader,style.Orientation,style.Head+Shift(view,style.Anchor,Anchor(doc,reference)));
                }
                else tag=target as IndependentTag??throw new InvalidOperationException("Выбранный элемент не является маркой.");
                Apply(doc,view,style,tag);
                if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Revit отклонил изменение марки.");
                applied++;if(copy)existing.Add(RefKey(new Reference(target)));
            }
            catch(Exception error){if(transaction.GetStatus()==TransactionStatus.Started)transaction.RollBack();errors.Add(target.Id.Value+": "+error.Message);}
        }
        group.Assimilate();return(applied,skipped,errors);
    }
    sealed class TagFailures:IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {bool errors=false;foreach(var failure in accessor.GetFailureMessages()){if(failure.GetSeverity()==FailureSeverity.Warning)accessor.DeleteWarning(failure);else errors=true;}return errors?FailureProcessingResult.ProceedWithRollBack:FailureProcessingResult.Continue;}
    }
    internal static Result Execute(UIApplication ui,bool copy,ref string message)
    {
        try
        {
            var uidoc=ui.ActiveUIDocument??throw new InvalidOperationException("Откройте проект Revit.");var doc=uidoc.Document;var view=doc.ActiveView;
            if(doc.IsFamilyDocument||doc.IsReadOnly)throw new InvalidOperationException("Откройте редактируемый проект Revit.");
            ValidateView(view);
            var source=(IndependentTag)doc.GetElement(uidoc.Selection.PickObject(ObjectType.Element,new TagFilter(),"Выберите эталонную марку"));
            _=Read(doc,view,source);
            using var options=TagOptionsBar.Show(ui,copy);
            ISelectionFilter filter=copy?new EquipmentFilter(Host(doc,Single(source))):new TagFilter();
            while(true)
            {
                IReadOnlyList<Element> targets;
                try
                {
                    targets=uidoc.Selection.PickElementsByRectangle(filter,copy
                        ?"Обведите оборудование рамкой — марки создадутся сразу. Esc — завершить."
                        :"Обведите марки рамкой — сопоставление выполнится сразу. Esc — завершить.").ToList();
                }
                catch(Autodesk.Revit.Exceptions.OperationCanceledException){return Result.Succeeded;}
                if(targets.Count==0)continue;
                var scope=TagCopyOptions.Current;
                var result=Run(doc,view,source,targets,copy,scope);
                WriteResult(copy,TagCopyOptions.Labels[(int)scope],result);
            }
        }
        catch(Autodesk.Revit.Exceptions.OperationCanceledException){return Result.Cancelled;}
        catch(Exception error){message=error.Message;return Result.Failed;}
    }
    static void WriteResult(bool copy,string scope,(int Applied,int Skipped,List<string> Errors) result)
    {
        try
        {
            var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BotPlus.RT","logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder,"tags.log"),$"{DateTimeOffset.Now:O} {(copy?"Копирование — "+scope:"Сопоставление")}: выполнено {result.Applied}, пропущено {result.Skipped}, замечаний {result.Errors.Count}\n"+string.Join("\n",result.Errors)+(result.Errors.Count>0?"\n":""));
        }
        catch(Exception error){System.Diagnostics.Trace.WriteLine("Не удалось записать журнал марок: "+error.Message);}
    }
    sealed class TagFilter:ISelectionFilter{public bool AllowElement(Element e)=>e is IndependentTag;public bool AllowReference(Reference r,XYZ p)=>false;}
    sealed class EquipmentFilter(Element sourceHost):ISelectionFilter{public bool AllowElement(Element e)=>CanCopyTo(sourceHost,e,TagCopyOptions.Current);public bool AllowReference(Reference r,XYZ p)=>false;}
}
[Transaction(TransactionMode.Manual)]
public sealed class MatchTagCommand:IExternalCommand
{public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>TagOperations.Execute(data.Application,false,ref message);}
[Transaction(TransactionMode.Manual)]
public sealed class CopyTagCommand:IExternalCommand
{public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>TagOperations.Execute(data.Application,true,ref message);}
