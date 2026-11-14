using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace BotPlus.RT;

/// <summary>Fixed fixtures run only by the isolated validation command.</summary>
public static class TagValidation
{
    public static object Run(UIApplication ui,Document doc,string folder)
    {
        string root=ui.Application.FamilyTemplatePath;
        string Template(params string[] names)=>Directory.EnumerateFiles(root,"*.rft",SearchOption.AllDirectories).First(p=>names.Contains(Path.GetFileNameWithoutExtension(p),StringComparer.OrdinalIgnoreCase));
        FamilySymbol MakeFamily(bool tag,string suffix="",BuiltInCategory category=BuiltInCategory.OST_GenericModel)
        {
            string template=tag?Template("Метрическая система, типовая марка","Metric Generic Tag"):Template("Метрическая система, типовая модель","Metric Generic Model");
            var family=ui.Application.NewFamilyDocument(template);
            try
            {
                if(tag)
                {
                    string blank=Path.Combine(folder,"Заготовка проверочной марки.rfa");family.SaveAs(blank,new SaveAsOptions{MaximumBackups=1});family.Close(false);
                    family=ui.OpenAndActivateDocument(blank).Document;
                }
                using(var t=new Transaction(family,"Проверочное семейство BotPlus"))
                {
                    t.Start();family.OwnerFamily.FamilyCategory=family.Settings.Categories.get_Item(tag?BuiltInCategory.OST_GenericModelTags:category);
                    if(tag)
                    {
                        var textType=new FilteredElementCollector(family).OfClass(typeof(TextNoteType)).FirstElement();
                        var familyView=ui.ActiveUIDocument.ActiveView;
                        TextNote.Create(family,familyView.Id,XYZ.Zero,"Проверка",textType.Id);
                    }
                    else
                    {
                        // Offset narrow geometry exposes ends incorrectly translated without host rotation.
                        var loop=CurveLoop.Create([Line.CreateBound(P(0.2,-0.1),P(1.2,-0.1)),Line.CreateBound(P(1.2,-0.1),P(1.2,0.1)),Line.CreateBound(P(1.2,0.1),P(0.2,0.1)),Line.CreateBound(P(0.2,0.1),P(0.2,-0.1))]);
                        var curves=new CurveArray();foreach(var curve in loop)curves.Append(curve);var arrays=new CurveArrArray();arrays.Append(curves);family.FamilyCreate.NewExtrusion(true,arrays,SketchPlane.Create(family,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,XYZ.Zero)),Feet(1));
                    }
                    family.FamilyManager.NewType("Проверка");t.Commit();
                }
                family.SaveAs(Path.Combine(folder,tag?"BotPlus — проверочная марка.rfa":"BotPlus — проверочное оборудование"+suffix+".rfa"),new SaveAsOptions{MaximumBackups=1});
                var loaded=family.LoadFamily(doc);return (FamilySymbol)doc.GetElement(loaded.GetFamilySymbolIds().First());
            }
            finally{if(tag)ui.OpenAndActivateDocument(doc.PathName);family.Close(false);}
        }
        var equipment=MakeFamily(false);var tagType=MakeFamily(true);var items=new List<FamilyInstance>();IndependentTag source=null!,target=null!,pinned=null!;ViewPlan view=null!;
        using(var t=new Transaction(doc,"Марки — проверочные экземпляры"))
        {
            t.Start();equipment.Activate();tagType.Activate();var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
            view=new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v=>!v.IsTemplate&&v.ViewType==ViewType.FloorPlan&&v.Name.StartsWith("AC Plan "));
            for(int i=0;i<4;i++)items.Add(doc.Create.NewFamilyInstance(P(20+i*4,20),equipment,level,StructuralType.NonStructural));
            foreach(var (index,angle) in new[]{(1,Math.PI/2),(3,-Math.PI/4)})
            {
                var origin=((LocationPoint)items[index].Location).Point;
                ElementTransformUtils.RotateElement(doc,items[index].Id,Line.CreateBound(origin,origin+XYZ.BasisZ),angle);
            }
            ElementTransformUtils.MoveElement(doc,items[3].Id,XYZ.BasisZ*Feet(0.4));
            doc.Regenerate();
            source=IndependentTag.Create(doc,tagType.Id,view.Id,new Reference(items[0]),true,TagOrientation.Horizontal,P(21.5,21));source.LeaderEndCondition=LeaderEndCondition.Free;
            source.TagHeadPosition=P(21.5,21);doc.Regenerate();
            source.SetLeaderEnd(new Reference(items[0]),items[0].GetTransform().OfPoint(P(0.7,0)+XYZ.BasisZ*Feet(0.5)));source.SetLeaderElbow(new Reference(items[0]),P(20.5,21));
            var other=(FamilySymbol)tagType.Duplicate("Другой тип");
            target=IndependentTag.Create(doc,other.Id,view.Id,new Reference(items[1]),false,TagOrientation.Vertical,P(23,19));
            pinned=IndependentTag.Create(doc,tagType.Id,view.Id,new Reference(items[2]),false,TagOrientation.Horizontal,P(28,19));pinned.Pinned=true;t.Commit();
        }
        string originalTarget=target.UniqueId;var pinnedHead=pinned.TagHeadPosition;var match=TagOperations.Run(doc,view,source,[target,pinned],false);
        if(match.Applied!=1||match.Errors.Count!=1||target.UniqueId!=originalTarget||TagOperations.Single(target).ElementId!=items[1].Id||target.GetTypeId()!=source.GetTypeId()||pinned.TagHeadPosition.DistanceTo(pinnedHead)>1e-8)throw new InvalidOperationException("Сопоставление потеряло опору/тип или изменило закреплённую марку: "+string.Join(";",match.Errors));
        var style=TagOperations.Read(doc,view,source);var shift=TagOperations.Shift(view,style.Anchor,TagOperations.Anchor(doc,new Reference(items[1])));
        if(TagOperations.ViewDistance(view,target.TagHeadPosition,style.Head+shift)>1e-6||TagOperations.ViewDistance(view,target.GetLeaderElbow(TagOperations.Single(target)),style.Elbow!+shift)>1e-6)throw new InvalidOperationException("Положение сопоставленной марки / локтя отличается от эталона.");
        void CheckEnd(IndependentTag tag,FamilyInstance host)
        {
            var owner=(View)doc.GetElement(tag.OwnerViewId);
            var expected=host.GetTransform().OfPoint(P(0.7,0)+XYZ.BasisZ*Feet(0.5));
            if(TagOperations.ViewDistance(owner,tag.GetLeaderEnd(TagOperations.Single(tag)),expected)>1e-6)throw new InvalidOperationException("Конец выноски не сохранил проекцию точки внутри повёрнутого оборудования.");
        }
        IndependentTag FindTag(View owner,FamilyInstance host)=>new FilteredElementCollector(doc,owner.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
            .Single(t=>t.GetTaggedReferences().Any(r=>r.ElementId==host.Id));
        void CheckLayout(View owner,IndependentTag prototype,IndependentTag actual)
        {
            var expected=TagOperations.Read(doc,owner,prototype);var reference=TagOperations.Single(actual);
            var delta=TagOperations.Shift(owner,expected.Anchor,TagOperations.Anchor(doc,reference));
            if(TagOperations.ViewDistance(owner,actual.TagHeadPosition,expected.Head+delta)>1e-6)throw new InvalidOperationException("Голова марки не повторяет эталон.");
            if(expected.Elbow!=null)
            {
                if(!actual.HasLeaderElbow(reference)||TagOperations.ViewDistance(owner,actual.GetLeaderElbow(reference),expected.Elbow+delta)>1e-6
                    ||TagOperations.ViewDistance(owner,actual.TagHeadPosition-actual.GetLeaderElbow(reference),expected.Head-expected.Elbow)>1e-6)
                    throw new InvalidOperationException("Точка перетаскивания / длина и направление полки не повторяют эталон.");
            }
            else if(actual.HasLeader&&actual.HasLeaderElbow(reference))throw new InvalidOperationException("У прямой выноски появился перегиб.");
        }
        CheckLayout(view,source,target);
        CheckEnd(target,items[1]);
        var copy=TagOperations.Run(doc,view,source,[items[1],items[3]],true);if(copy.Applied!=1||copy.Skipped!=1||copy.Errors.Count!=0)throw new InvalidOperationException("Копирование марки: "+string.Join(";",copy.Errors));
        CheckEnd(FindTag(view,items[3]),items[3]);
        CheckLayout(view,source,FindTag(view,items[3]));
        using(var t=new Transaction(doc,"Марки — изменённая точка перетаскивания эталона"))
        {
            t.Start();source.TagHeadPosition=P(22,22);doc.Regenerate();
            source.SetLeaderElbow(TagOperations.Single(source),P(20.25,22));t.Commit();
        }
        var rematch=TagOperations.Run(doc,view,source,[target,FindTag(view,items[3])],false);
        if(rematch.Applied!=2||rematch.Errors.Count!=0)throw new InvalidOperationException("Повторное сопоставление точки перетаскивания: "+string.Join(";",rematch.Errors));
        CheckLayout(view,source,target);CheckLayout(view,source,FindTag(view,items[3]));
        var repeat=TagOperations.Run(doc,view,source,[items[3]],true);if(repeat.Applied!=0||repeat.Skipped!=1)throw new InvalidOperationException("Копирование создало дубли.");
        var otherFamily=MakeFamily(false," — другое семейство");var otherCategory=MakeFamily(false," — мебель",BuiltInCategory.OST_Furniture);
        FamilyInstance sameCategory=null!,differentCategory=null!,otherSize=null!;
        using(var t=new Transaction(doc,"Марки — проверка области копирования"))
        {
            t.Start();otherFamily.Activate();otherCategory.Activate();
            var otherType=(FamilySymbol)equipment.Duplicate("Другой типоразмер");otherType.Activate();
            var level=(Level)doc.GetElement(items[0].LevelId);
            sameCategory=doc.Create.NewFamilyInstance(P(40,20),otherFamily,level,StructuralType.NonStructural);
            differentCategory=doc.Create.NewFamilyInstance(P(44,20),otherCategory,level,StructuralType.NonStructural);
            otherSize=doc.Create.NewFamilyInstance(P(48,20),otherType,level,StructuralType.NonStructural);t.Commit();
        }
        if(!TagOperations.CanCopyTo(items[0],otherSize,TagCopyScope.Family)||TagOperations.CanCopyTo(items[0],sameCategory,TagCopyScope.Family)
            ||!TagOperations.CanCopyTo(items[0],sameCategory,TagCopyScope.Category)||TagOperations.CanCopyTo(items[0],differentCategory,TagCopyScope.Category)
            ||!TagOperations.CanCopyTo(items[0],differentCategory,TagCopyScope.All))throw new InvalidOperationException("Неверная фильтрация семейства / категории / всех целей.");
        var filtered=TagOperations.Run(doc,view,source,[sameCategory,differentCategory],true);
        if(filtered.Applied!=0||filtered.Skipped!=2||filtered.Errors.Count!=0)throw new InvalidOperationException("Семейство по умолчанию не отфильтровало посторонние цели.");
        var categoryCopy=TagOperations.Run(doc,view,source,[sameCategory],true,TagCopyScope.Category);
        if(categoryCopy.Applied!=1||categoryCopy.Errors.Count!=0)throw new InvalidOperationException("Копирование на другое семейство той же категории не выполнено.");
        View3D threeD=null!;IndependentTag source3D=null!,target3D=null!;
        using(var t=new Transaction(doc,"Марки — заблокированный 3D-вид"))
        {
            t.Start();var viewType=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.ThreeDimensional);
            threeD=View3D.CreateIsometric(doc,viewType.Id);threeD.Name="BotPlus — проверка марок 3D";
            threeD.SetOrientation(new ViewOrientation3D(new XYZ(0,0,50),new XYZ(-1,1,2).Normalize(),new XYZ(1,-1,1).Normalize()));
            threeD.SaveOrientationAndLock();
            source3D=IndependentTag.Create(doc,tagType.Id,threeD.Id,new Reference(items[0]),true,TagOrientation.Horizontal,P(21.5,21));
            source3D.LeaderEndCondition=LeaderEndCondition.Free;
            source3D.TagHeadPosition=TagOperations.Anchor(doc,new Reference(items[0]))+threeD.RightDirection*Feet(1.5)+threeD.UpDirection*Feet(1)+threeD.ViewDirection*Feet(3);
            doc.Regenerate();source3D.SetLeaderEnd(new Reference(items[0]),source.GetLeaderEnd(TagOperations.Single(source)));
            source3D.SetLeaderElbow(new Reference(items[0]),source3D.TagHeadPosition-threeD.RightDirection*Feet(0.75));
            target3D=IndependentTag.Create(doc,tagType.Id,threeD.Id,new Reference(items[1]),false,TagOrientation.Horizontal,P(25,21));t.Commit();
        }
        var match3D=TagOperations.Run(doc,threeD,source3D,[target3D],false);
        var copy3D=TagOperations.Run(doc,threeD,source3D,[items[3]],true);
        if(match3D.Applied!=1||copy3D.Applied!=1||match3D.Errors.Count!=0||copy3D.Errors.Count!=0)
            throw new InvalidOperationException("Марки на заблокированном 3D-виде: "+string.Join(";",match3D.Errors.Concat(copy3D.Errors)));
        CheckEnd(target3D,items[1]);CheckEnd(FindTag(threeD,items[3]),items[3]);
        CheckLayout(threeD,source3D,target3D);CheckLayout(threeD,source3D,FindTag(threeD,items[3]));
        using(var t=new Transaction(doc,"Марки — проверка незаблокированного вида"))
        {
            t.Start();threeD.Unlock();bool rejected=false;
            try{TagOperations.ValidateView(threeD);}catch(InvalidOperationException){rejected=true;}
            if(!rejected)throw new InvalidOperationException("Незаблокированный 3D-вид не отклонён.");
            var perspective=View3D.CreatePerspective(doc,threeD.GetTypeId());rejected=false;
            try{TagOperations.ValidateView(perspective);}catch(InvalidOperationException){rejected=true;}
            if(!rejected)throw new InvalidOperationException("Перспективный вид не отклонён.");t.RollBack();
        }
        using(var t=new Transaction(doc,"Проверка марки без выноски")){t.Start();source.HasLeader=false;source.TagOrientation=TagOrientation.Vertical;t.Commit();}
        var without=TagOperations.Run(doc,view,source,[target],false);if(without.Applied!=1||target.HasLeader||target.TagOrientation!=TagOrientation.Vertical)throw new InvalidOperationException("Не перенесены отсутствие выноски / вертикальная ориентация.");
        return new{matched=1,copied=1,repeatWithoutDuplicates=true,targetReferencePreserved=true,pinnedSkipped=true,leaderElbowVerified=true,orientationAndNoLeaderVerified=true,
            rotatedHostEndsVerified=true,elevatedHostEndVerified=true,locked3DMatchAndCopyVerified=true,unlocked3DRejected=true,perspectiveRejected=true,
            familyScopeDefaultVerified=true,sameFamilyOtherTypeVerified=true,categoryScopeVerified=true,allScopeFilterVerified=true,
            dragPointAndLandingVerified=true,changedDragPointRematchVerified=true,axonometricProjectionVerified=true};
    }
    static double Feet(double meters)=>UnitUtils.ConvertToInternalUnits(meters,UnitTypeId.Meters);
    static XYZ P(double x,double y)=>new(Feet(x),Feet(y),0);
}
