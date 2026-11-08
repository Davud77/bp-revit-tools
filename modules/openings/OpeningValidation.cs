using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BotPlus.Openings.Core;

namespace BotPlus.Openings;

/// <summary>Isolated native fixtures; never edits the supplied reference files.</summary>
public static class OpeningValidation
{
    public static object Run(UIApplication ui,string folder,string template,string referenceFolder="")
    {
        Directory.CreateDirectory(folder);var app=ui.Application;var doc=app.NewProjectDocument(template);
        string output=Path.Combine(folder,"Менеджер отверстий — проверка.rvt");var messages=new List<string>();
        try
        {
            var registry=new Registry{Project=new HoleProject{Name="Проверка менеджера отверстий"}};registry.Project.Users[HoleStore.User]=Role.Admin;
            var engineer=new Actor("test.engineer",Role.Engineer);var ar=new Actor("test.architect",Role.Architect);var kr=new Actor("test.structural",Role.Structural);
            Level level=null!;Wall wall=null!;Floor floor=null!;
            HoleStore.InTransaction(doc,"Проверочные основы и задания",()=>
            {
                level=Level.Create(doc,0);level.Name="Проверка — 0";var upper=Level.Create(doc,HoleMarkers.Feet(3000));upper.Name="Проверка — 3 м";
                var wallType=new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(w=>w.Kind==WallKind.Basic);
                wall=Wall.Create(doc,Line.CreateBound(XYZ.Zero,new XYZ(HoleMarkers.Feet(6000),0,0)),wallType.Id,level.Id,HoleMarkers.Feet(3000),0,false,false);
                WallUtils.DisallowWallJoinAtEnd(wall,0);WallUtils.DisallowWallJoinAtEnd(wall,1);
                var floorType=new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First();
                var points=new[]{new XYZ(0,0,0),new XYZ(HoleMarkers.Feet(6000),0,0),new XYZ(HoleMarkers.Feet(6000),HoleMarkers.Feet(6000),0),new XYZ(0,HoleMarkers.Feet(6000),0)};
                floor=Floor.Create(doc,[CurveLoop.Create(Enumerable.Range(0,4).Select(i=>(Curve)Line.CreateBound(points[i],points[(i+1)%4])).ToList())],floorType.Id,level.Id);
                doc.Regenerate();var box=floor.get_BoundingBox(null);double floorDepth=HoleMarkers.Mm(box.Max.Z-box.Min.Z),floorZ=HoleMarkers.Mm((box.Min.Z+box.Max.Z)/2);
                foreach(var orientation in Enum.GetValues<HoleOrientation>())foreach(var shape in Enum.GetValues<HoleShape>())
                {
                    var geometry=new HoleGeometry{Orientation=orientation,Shape=shape,Width=400,Height=300,Diameter=200,
                        X=shape==HoleShape.Round?1500:3500,Y=orientation==HoleOrientation.Wall?0:3500,Z=orientation==HoleOrientation.Wall?1500:floorZ,
                        Depth=orientation==HoleOrientation.Wall?HoleMarkers.Mm(wall.Width)+40:floorDepth+40};
                    var h=HoleWorkflow.Create(registry.Project,engineer,geometry,"ОВ",level.UniqueId,level.Name,0,"ОВ-"+(registry.Holes.Count+1).ToString("000"));
                    h.HostId=orientation==HoleOrientation.Wall?wall.UniqueId:floor.UniqueId;
                    foreach(var actor in new[]{engineer,ar,kr})HoleWorkflow.Approve(h,actor,actor.Role);registry.Holes.Add(h);HoleMarkers.Update(doc,h);
                }
                HoleStore.Save(doc,registry);
            });
            messages.AddRange(HoleCuts.Run(doc,registry,registry.Holes));
            if(registry.Holes.Any(h=>h.CutIds.Count!=1))throw new InvalidOperationException("Не все четыре сочетания формы и основы вырезаны.\n"+string.Join("\n",messages));
            var cuts=registry.Holes.SelectMany(h=>h.CutIds).ToArray();messages.AddRange(HoleCuts.Run(doc,registry,registry.Holes));
            if(!cuts.SequenceEqual(registry.Holes.SelectMany(h=>h.CutIds)))throw new InvalidOperationException("Повторное вырезание создало дубли.");
            var bad=HoleJson.Clone(registry.Holes[0]);bad.CutIds=[];bad.CutGeometryHash="";bad.Geometry=bad.Geometry with{X=5000,Depth=1};
            var countBefore=new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).GetElementCount();
            var refused=HoleCuts.Run(doc,registry,[bad]);
            if(bad.CutIds.Count!=0||new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).GetElementCount()!=countBefore||!refused.Any(s=>s.Contains("недостаточна")))throw new InvalidOperationException("Недостаточная глубина не отклонена без остаточных элементов.");
            messages.AddRange(refused);
            var first=registry.Holes[0];HoleWorkflow.Change(first,engineer,first.Geometry with{X=first.Geometry.X+250},new(),"Проверка изменения согласованного задания");
            HoleStore.InTransaction(doc,"Проверка нового положения",()=>{HoleMarkers.Update(doc,first);HoleStore.Save(doc,registry);});
            if(first.FullyApproved)throw new InvalidOperationException("Изменение не сняло согласования.");
            messages.AddRange(HoleCuts.Run(doc,registry,[first],true));
            if(first.CutIds.Count!=0)throw new InvalidOperationException("Снятие выреза не завершилось.");
            foreach(var actor in new[]{engineer,ar,kr})HoleWorkflow.Approve(first,actor,actor.Role);
            messages.AddRange(HoleCuts.Run(doc,registry,[first]));if(first.CutIds.Count!=1)throw new InvalidOperationException("Повторный вырез после согласования не выполнен.");
            HoleStore.InTransaction(doc,"Проверка шахты и копии",()=>
            {
                var upper=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First(l=>l.Name=="Проверка — 3 м");var source=registry.Holes.First(h=>h.Geometry.Orientation==HoleOrientation.Floor);
                var copy=HoleWorkflow.Copy(registry.Project,source,engineer,0,0,3000,upper.UniqueId,upper.Name,3000);copy.Mark="ОВ-005";registry.Holes.Add(copy);HoleChecks.SetShaft([source,copy],engineer);HoleMarkers.Update(doc,copy);HoleStore.Save(doc,registry);
            });
            doc.SaveAs(output,new SaveAsOptions{MaximumBackups=1});doc.Close(false);doc=app.OpenDocumentFile(output);
            var reopened=HoleStore.Read(doc);if(reopened.Holes.Count!=5||reopened.Holes.Count(h=>h.FullyApproved)!=4||reopened.Holes.SelectMany(h=>h.CutIds).Count()!=4)throw new InvalidOperationException("Потеря реестра или вырезов после открытия.");
            if(HoleMonitor.Reconcile(doc,reopened)!=0)throw new InvalidOperationException("Сохранённые маркеры ошибочно распознаны как изменённые.");
            var moved=reopened.Holes[0];
            HoleStore.InTransaction(doc,"Проверка внешнего перемещения",()=>ElementTransformUtils.MoveElement(doc,HoleMarkers.Marker(doc,moved)!.Id,new XYZ(HoleMarkers.Feet(10),0,0)));
            if(HoleMonitor.Reconcile(doc,reopened)!=1||moved.FullyApproved||!moved.GeometryUnresolved)throw new InvalidOperationException("Внешнее перемещение не сняло согласования.");
            HoleWorkflow.Change(moved,engineer,moved.Geometry,new(),"Возврат контрольного положения после внешнего перемещения");
            foreach(var actor in new[]{engineer,ar,kr})HoleWorkflow.Approve(moved,actor,actor.Role);
            HoleStore.InTransaction(doc,"Восстановить контрольный маркер",()=>{HoleMarkers.Update(doc,moved);HoleStore.Save(doc,reopened);});
            doc.Save();
            var package=HoleExchange.Export(reopened.Project!,reopened.Holes);File.WriteAllText(Path.Combine(folder,"Задания.json"),HoleJson.Write(package));
            var reference=StudyReference(app,referenceFolder);
            doc.Close(false);doc=null!;var crossings=CrossingValidation.Run(app,template,folder);var active=ui.OpenAndActivateDocument(Path.Combine(folder,"Пересечения MEP.rvt"));
            var controller=new HoleController(ui);App.Controllers.Add(controller);controller.Show(false);
            return new{status="complete",output,holes=5,approved=4,cuts=4,repeatWithoutDuplicates=true,reopenVerified=true,insufficientDepthRejected=true,externalMoveDetected=true,crossings,reference,messages};
        }
        finally{if(doc is {IsValidObject:true})doc.Close(false);}
    }
    static object StudyReference(Autodesk.Revit.ApplicationServices.Application app,string folder)
    {
        if(!Directory.Exists(folder))return new{files=0};var rows=new List<object>();
        foreach(var file in Directory.EnumerateFiles(folder,"*.rvt"))
        {
            using var info=BasicFileInfo.Extract(file);var options=new OpenOptions();if(info.IsWorkshared)options.DetachFromCentralOption=DetachFromCentralOption.DetachAndDiscardWorksets;
            var document=app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(file),options);
            try
            {
                var openings=new FilteredElementCollector(document).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(i=>i.Symbol.FamilyName.Contains("Отверст",StringComparison.OrdinalIgnoreCase)).ToList();
                rows.Add(new{file=Path.GetFileName(file),sourceVersion=info.Format,levels=new FilteredElementCollector(document).OfClass(typeof(Level)).GetElementCount(),
                    families=openings.Select(i=>i.Symbol.FamilyName).Distinct().ToArray(),instances=openings.Count,
                    parameters=openings.Take(1).SelectMany(i=>i.Parameters.Cast<Parameter>().Select(p=>new{name=p.Definition.Name,shared=p.IsShared})).ToArray()});
            }
            finally{document.Close(false);}
        }
        return rows;
    }
}
