using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using BotPlus.Openings.Core;
using System.Diagnostics;

namespace BotPlus.Openings;

/// <summary>Проверка на копии реальной модели со связями; исходный файл не изменяется и не сохраняется.</summary>
public static class LinkedValidation
{
    public static object Run(UIApplication ui,string source,string folder,string template,int limit)
    {
        Directory.CreateDirectory(folder);string copy=Path.Combine(folder,"Рабочая копия.rvt");File.Copy(source,copy,true);
        var timer=Stopwatch.StartNew();var active=ui.OpenAndActivateDocument(copy);var doc=active.Document;double openSeconds=timer.Elapsed.TotalSeconds;
        var links=new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
        var linkInfo=links.Select(l=>new{l.UniqueId,l.Name,Loaded=l.GetLinkDocument()!=null,Walls=l.GetLinkDocument()==null?0:new FilteredElementCollector(l.GetLinkDocument()).OfClass(typeof(Wall)).GetElementCount(),
            Floors=l.GetLinkDocument()==null?0:new FilteredElementCollector(l.GetLinkDocument()).OfClass(typeof(Floor)).GetElementCount(),
            Mep=l.GetLinkDocument()==null?0:new FilteredElementCollector(l.GetLinkDocument()).OfClass(typeof(MEPCurve)).GetElementCount()}).ToList();
        int mepTotal=new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).Cast<MEPCurve>().Count(e=>e is Pipe or Duct or CableTray or Conduit);
        var loaded=links.Where(l=>l.GetLinkDocument()!=null).ToList();
        var registry=new Registry{Project=new HoleProject{Name="Проверка по реальной модели"}};registry.Project.Users[HoleStore.User]=Role.Admin;
        var report=new Dictionary<string,object>{["openSeconds"]=openSeconds,["links"]=linkInfo,["mepInCurrentModel"]=mepTotal};
        if(loaded.Count==0){report["status"]="no-loaded-links";return report;}
        // Выбор: коммуникации, чьи габариты пересекают основы или сети выбранных связей (в пределах лимита).
        var selection=new HashSet<ElementId>();
        foreach(var link in loaded)
        {
            var linked=link.GetLinkDocument()!;var transform=link.GetTotalTransform();
            foreach(var host in new FilteredElementCollector(linked).WhereElementIsNotElementType().Where(e=>e is Wall or Floor or Pipe or Duct or CableTray or Conduit).Take(4000))
            {
                if(selection.Count>=limit)break;var box=host.get_BoundingBox(null);if(box==null)continue;var world=HoleCrossings.WorldBounds(box,transform);
                var filter=new BoundingBoxIntersectsFilter(new Outline(world.Min,world.Max));
                foreach(var id in new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).WherePasses(filter).ToElementIds()){if(doc.GetElement(id) is Pipe or Duct or CableTray or Conduit)selection.Add(id);if(selection.Count>=limit)break;}
            }
        }
        report["selectedMep"]=selection.Count;
        var options=new CrossingOptions();var order=loaded.Select(l=>l.UniqueId).ToList();
        timer.Restart();var scan=HoleCrossings.Scan(doc,registry,options,selection.ToArray(),order);report["scanSeconds"]=timer.Elapsed.TotalSeconds;
        report["candidates"]=scan.Candidates.GroupBy(c=>c.Kind+" / "+c.State).ToDictionary(g=>g.Key,g=>g.Count());
        report["byLink"]=scan.Candidates.GroupBy(c=>c.LinkId).ToDictionary(g=>links.First(l=>l.UniqueId==g.Key).Name,g=>g.Count());
        var linkIndex=order.Select((id,n)=>(id,n)).ToDictionary(p=>p.id,p=>p.n);
        var sequence=scan.Candidates.Select(c=>linkIndex[c.LinkId]).ToList();
        report["orderedByLink"]=sequence.Zip(sequence.Skip(1),(a,b)=>a<=b).All(x=>x);
        report["warnings"]=scan.Warnings.Take(15).ToList();report["warningCount"]=scan.Warnings.Count;
        report["communications"]=scan.Communications;report["hosts"]=scan.HostCount;
        report["sample"]=scan.Candidates.Take(6).Select(c=>new{c.Kind,c.State,c.Communication,c.Host,c.Size,c.Depth,c.Geometry.X,c.Geometry.Y,c.Geometry.Z}).ToList();
        var creatable=scan.Candidates.Where(c=>c.CanCreate).ToList();report["creatable"]=creatable.Count;
        if(creatable.Count==0){report["status"]="scan-only";return report;}
        // Создание заданий только в выбранной модели АР: строка другой связи отклоняется.
        string architect=creatable[0].LinkId;var chosen=creatable.Where(c=>c.LinkId==architect).Take(6).ToList();
        var other=scan.Candidates.FirstOrDefault(c=>c.CanCreate&&c.LinkId!=architect);
        if(other!=null)
        {
            bool foreignRejected=false;
            try{HoleCrossings.Create(doc,registry,options,selection.ToArray(),[other],"ОВ",order,[architect]);}catch(InvalidOperationException){foreignRejected=true;}
            report["foreignHostRejected"]=foreignRejected;
        }
        var admin=HoleStore.Actor(registry);List<Hole> created=null!;
        HoleStore.InTransaction(doc,"Проверка — задания",()=>
        {
            created=HoleCrossings.Create(doc,registry,options,selection.ToArray(),chosen,"ОВ",order,[architect]);
            foreach(var h in created){foreach(var role in new[]{Role.Engineer,Role.Architect,Role.Structural})HoleWorkflow.Approve(h,admin,role);HoleMarkers.Update(doc,h);}
            HoleStore.Save(doc,registry);
        });
        report["created"]=created.Count;report["approved"]=created.Count(h=>h.FullyApproved);
        report["bodies"]=created.Select(h=>new{h.Mark,h.Size,h.Geometry.Shape,Marker=HoleMarkers.Marker(doc,h)!=null}).ToList();
        // Перенос в сводный файл (новый проект): тела строятся здесь, затем копируются.
        var target=ui.Application.NewProjectDocument(template);
        try
        {
            int Count()=>new FilteredElementCollector(target).OfClass(typeof(DirectShape)).Cast<DirectShape>().Count(s=>s.ApplicationId==HoleStore.AppId);
            var first=HoleTransfer.Run(doc,target,registry,created,true);int count1=Count();
            var second=HoleTransfer.Run(doc,target,registry,created,true);int count2=Count();
            bool same=true;foreach(var h in created)
            {
                var src=HoleMarkers.Marker(doc,h)!.get_BoundingBox(null)!;
                var copyShape=new FilteredElementCollector(target).OfClass(typeof(DirectShape)).Cast<DirectShape>().FirstOrDefault(s=>s.ApplicationId==HoleStore.AppId&&s.ApplicationDataId==h.Key);
                var dst=copyShape?.get_BoundingBox(null);
                if(dst==null||src.Min.DistanceTo(dst.Min)>1e-5||src.Max.DistanceTo(dst.Max)>1e-5)same=false;
            }
            // Удалённое задание исчезает из сводного файла при повторном переносе.
            var victim=created[0];HoleWorkflow.RequestDelete(victim,admin);
            HoleStore.InTransaction(doc,"Проверка — удаление",()=>{HoleMarkers.Update(doc,victim);HoleStore.Save(doc,registry);});
            var third=HoleTransfer.Run(doc,target,registry,created,true);int count3=Count();
            report["transfer"]=new{first=first.Text,second=second.Text,third=third.Text,count1,count2,count3,expected=created.Count,positionsEqual=same,repeatWithoutDuplicates=count1==count2&&count1==created.Count,deletedRemoved=count3==created.Count-1};
            string output=Path.Combine(folder,"Сводный файл — проверка.rvt");target.SaveAs(output,new SaveAsOptions{OverwriteExistingFile=true});report["target"]=output;
        }
        finally{target.Close(false);}
        report["status"]="complete";return report;
    }
}
