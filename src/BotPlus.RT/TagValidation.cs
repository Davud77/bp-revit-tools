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
        FamilySymbol MakeFamily(bool tag)
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
                    t.Start();family.OwnerFamily.FamilyCategory=family.Settings.Categories.get_Item(tag?BuiltInCategory.OST_GenericModelTags:BuiltInCategory.OST_GenericModel);
                    if(tag)
                    {
                        var textType=new FilteredElementCollector(family).OfClass(typeof(TextNoteType)).FirstElement();
                        var familyView=ui.ActiveUIDocument.ActiveView;
                        TextNote.Create(family,familyView.Id,XYZ.Zero,"Проверка",textType.Id);
                    }
                    else
                    {
                        var loop=CurveLoop.Create([Line.CreateBound(P(-0.2,-0.2),P(0.2,-0.2)),Line.CreateBound(P(0.2,-0.2),P(0.2,0.2)),Line.CreateBound(P(0.2,0.2),P(-0.2,0.2)),Line.CreateBound(P(-0.2,0.2),P(-0.2,-0.2))]);
                        var curves=new CurveArray();foreach(var curve in loop)curves.Append(curve);var arrays=new CurveArrArray();arrays.Append(curves);family.FamilyCreate.NewExtrusion(true,arrays,SketchPlane.Create(family,Plane.CreateByNormalAndOrigin(XYZ.BasisZ,XYZ.Zero)),Feet(1));
                    }
                    family.FamilyManager.NewType("Проверка");t.Commit();
                }
                family.SaveAs(Path.Combine(folder,tag?"BotPlus — проверочная марка.rfa":"BotPlus — проверочное оборудование.rfa"),new SaveAsOptions{MaximumBackups=1});
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
            source=IndependentTag.Create(doc,tagType.Id,view.Id,new Reference(items[0]),true,TagOrientation.Horizontal,P(21.5,21));source.LeaderEndCondition=LeaderEndCondition.Free;
            source.SetLeaderEnd(new Reference(items[0]),P(20,20));source.SetLeaderElbow(new Reference(items[0]),P(20.5,21));source.TagHeadPosition=P(21.5,21);
            var other=(FamilySymbol)tagType.Duplicate("Другой тип");
            target=IndependentTag.Create(doc,other.Id,view.Id,new Reference(items[1]),false,TagOrientation.Vertical,P(23,19));
            pinned=IndependentTag.Create(doc,tagType.Id,view.Id,new Reference(items[2]),false,TagOrientation.Horizontal,P(28,19));pinned.Pinned=true;t.Commit();
        }
        string originalTarget=target.UniqueId;var pinnedHead=pinned.TagHeadPosition;var match=TagOperations.Run(doc,view,source,[target,pinned],false);
        if(match.Applied!=1||match.Errors.Count!=1||target.UniqueId!=originalTarget||TagOperations.Single(target).ElementId!=items[1].Id||target.GetTypeId()!=source.GetTypeId()||pinned.TagHeadPosition.DistanceTo(pinnedHead)>1e-8)throw new InvalidOperationException("Сопоставление потеряло опору/тип или изменило закреплённую марку: "+string.Join(";",match.Errors));
        var style=TagOperations.Read(doc,view,source);var shift=TagOperations.Shift(view,style.Anchor,TagOperations.Anchor(doc,new Reference(items[1])));
        if(target.TagHeadPosition.DistanceTo(style.Head+shift)>1e-6||target.GetLeaderElbow(TagOperations.Single(target)).DistanceTo(style.Elbow!+shift)>1e-6)throw new InvalidOperationException("Положение сопоставленной марки / локтя отличается от эталона.");
        var copy=TagOperations.Run(doc,view,source,[items[1],items[3]],true);if(copy.Applied!=1||copy.Skipped!=1||copy.Errors.Count!=0)throw new InvalidOperationException("Копирование марки: "+string.Join(";",copy.Errors));
        var repeat=TagOperations.Run(doc,view,source,[items[3]],true);if(repeat.Applied!=0||repeat.Skipped!=1)throw new InvalidOperationException("Копирование создало дубли.");
        using(var t=new Transaction(doc,"Проверка марки без выноски")){t.Start();source.HasLeader=false;source.TagOrientation=TagOrientation.Vertical;t.Commit();}
        var without=TagOperations.Run(doc,view,source,[target],false);if(without.Applied!=1||target.HasLeader||target.TagOrientation!=TagOrientation.Vertical)throw new InvalidOperationException("Не перенесены отсутствие выноски / вертикальная ориентация.");
        return new{matched=1,copied=1,repeatWithoutDuplicates=true,targetReferencePreserved=true,pinnedSkipped=true,leaderElbowVerified=true,orientationAndNoLeaderVerified=true};
    }
    static double Feet(double meters)=>UnitUtils.ConvertToInternalUnits(meters,UnitTypeId.Meters);
    static XYZ P(double x,double y)=>new(Feet(x),Feet(y),0);
}
