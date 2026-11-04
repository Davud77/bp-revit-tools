using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace BotPlus.RT;

internal sealed record TagStyle(ElementId TypeId,long Category,XYZ Anchor,XYZ Head,TagOrientation Orientation,double Angle,bool Leader,
    LeaderEndCondition EndCondition,XYZ? Elbow,XYZ? End);

internal static class TagOperations
{
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
    internal static Reference Single(IndependentTag tag)
    {
        var refs=tag.GetTaggedReferences();if(tag.IsOrphaned||refs.Count!=1)throw new InvalidOperationException("Нужна марка с одной доступной опорой.");return refs[0];
    }
    internal static TagStyle Read(Document doc,View view,IndependentTag tag)
    {
        if(tag.OwnerViewId!=view.Id)throw new InvalidOperationException("Выберите марку на активном виде.");
        var reference=Single(tag);var elbow=tag.HasLeader&&tag.HasLeaderElbow(reference)?tag.GetLeaderElbow(reference):null;
        return new(tag.GetTypeId(),tag.Category.Id.Value,Anchor(doc,reference),tag.TagHeadPosition,tag.TagOrientation,tag.RotationAngle,tag.HasLeader,
            tag.HasLeader?tag.LeaderEndCondition:LeaderEndCondition.Attached,elbow,tag.HasLeader&&tag.LeaderEndCondition==LeaderEndCondition.Free?tag.GetLeaderEnd(reference):null);
    }
    internal static XYZ Shift(View view,XYZ from,XYZ to)
    {var delta=to-from;return view.RightDirection*delta.DotProduct(view.RightDirection)+view.UpDirection*delta.DotProduct(view.UpDirection);}
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
        if(style.Leader)
        {
            if(!tag.CanLeaderEndConditionBeAssigned(style.EndCondition))throw new InvalidOperationException("Эта марка не поддерживает изменение выноски.");
            tag.LeaderEndCondition=style.EndCondition;
            if(style.End!=null)tag.SetLeaderEnd(reference,style.End+shift);
            if(style.Elbow!=null)tag.SetLeaderElbow(reference,style.Elbow+shift);
            else if(tag.HasLeaderElbow(reference))tag.SetLeaderElbow(reference,style.Head+shift);
        }
        tag.TagHeadPosition=style.Head+shift;
        doc.Regenerate();
        if(tag.TagHeadPosition.DistanceTo(style.Head+shift)>1e-6)throw new InvalidOperationException("Revit изменил положение марки; операция отменена.");
    }
    static string RefKey(Reference r)=>r.ElementId.Value+"/"+r.LinkedElementId.Value;
    internal static (int Applied,int Skipped,List<string> Errors) Run(Document doc,View view,IndependentTag source,IReadOnlyList<Element> targets,bool copy)
    {
        var style=Read(doc,view,source);int applied=0,skipped=0;var errors=new List<string>();
        var existing=new FilteredElementCollector(doc,view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
            .Where(t=>!t.IsOrphaned&&t.GetTypeId()==style.TypeId).SelectMany(t=>t.GetTaggedReferences().Select(RefKey)).ToHashSet();
        using var group=new TransactionGroup(doc,copy?"BotPlus — копировать марки":"BotPlus — сопоставить марки");group.Start();
        foreach(var target in targets.DistinctBy(t=>t.Id))
        {
            if(target.Id==source.Id){skipped++;continue;}
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
            if(view is View3D or ViewSheet||view.IsTemplate||doc.IsFamilyDocument||doc.IsReadOnly)throw new InvalidOperationException("Откройте редактируемый план, разрез или фасад.");
            var source=(IndependentTag)doc.GetElement(uidoc.Selection.PickObject(ObjectType.Element,new TagFilter(),"Выберите эталонную марку"));
            var targets=uidoc.Selection.PickObjects(ObjectType.Element,copy?new EquipmentFilter():new TagFilter(),copy?"Выберите оборудование и нажмите «Готово»":"Выберите марки для сопоставления и нажмите «Готово»").Select(r=>doc.GetElement(r)).ToList();
            var result=Run(doc,view,source,targets,copy);
            var dialog=new TaskDialog("BotPlus RT — Марки"){MainInstruction=$"{(copy?"Создано":"Сопоставлено")}: {result.Applied}",MainContent=$"Пропущено существующих / эталонных: {result.Skipped}\nЗамечаний: {result.Errors.Count}",ExpandedContent=string.Join("\n",result.Errors)};dialog.Show();return Result.Succeeded;
        }
        catch(Autodesk.Revit.Exceptions.OperationCanceledException){return Result.Cancelled;}
        catch(Exception error){message=error.Message;return Result.Failed;}
    }
    sealed class TagFilter:ISelectionFilter{public bool AllowElement(Element e)=>e is IndependentTag;public bool AllowReference(Reference r,XYZ p)=>false;}
    sealed class EquipmentFilter:ISelectionFilter{public bool AllowElement(Element e)=>e is FamilyInstance;public bool AllowReference(Reference r,XYZ p)=>false;}
}
[Transaction(TransactionMode.Manual)]
public sealed class MatchTagCommand:IExternalCommand
{public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>TagOperations.Execute(data.Application,false,ref message);}
[Transaction(TransactionMode.Manual)]
public sealed class CopyTagCommand:IExternalCommand
{public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)=>TagOperations.Execute(data.Application,true,ref message);}
