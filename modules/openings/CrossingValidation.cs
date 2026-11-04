using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using BotPlus.Openings.Core;

namespace BotPlus.Openings;

internal static class CrossingValidation
{
    internal static object Run(Autodesk.Revit.ApplicationServices.Application app,string template,string folder)
    {
        var doc=app.NewProjectDocument(template);string file=Path.Combine(folder,"Пересечения MEP.rvt");
        try
        {
            if(!new FilteredElementCollector(doc).OfClass(typeof(PipeInsulationType)).Any())
            {
                string mepTemplate=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Autodesk","RVT 2026","Templates","Russian","Systems-DefaultRUSRUS.rte");
                var source=app.NewProjectDocument(mepTemplate);
                try
                {
                    var type=new FilteredElementCollector(source).OfClass(typeof(PipeInsulationType)).FirstElement();
                    if(type==null)throw new InvalidOperationException("В системном шаблоне нет проверочного типа изоляции.");
                    HoleStore.InTransaction(doc,"Проверочный тип изоляции Autodesk",()=>ElementTransformUtils.CopyElements(source,[type.Id],doc,Transform.Identity,new CopyPasteOptions()));
                }
                finally{source.Close(false);}
            }
            var data=new Registry{Project=new HoleProject{Name="Проверка пересечений"}};data.Project.Users[HoleStore.User]=Role.Admin;
            var options=new CrossingOptions(IncludeLinks:false);Pipe normal=null!;
            HoleStore.InTransaction(doc,"Проверочные MEP и основы",()=>
            {
                var level=Level.Create(doc,0);var wt=new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(w=>w.Kind==WallKind.Basic);
                var wall=Wall.Create(doc,Line.CreateBound(P(0,0,0),P(6000,0,0)),wt.Id,level.Id,HoleMarkers.Feet(3000),0,false,false);
                WallUtils.DisallowWallJoinAtEnd(wall,0);WallUtils.DisallowWallJoinAtEnd(wall,1);
                var ft=new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First();
                var pts=new[]{P(0,0,0),P(6000,0,0),P(6000,6000,0),P(0,6000,0)};
                Floor.Create(doc,[CurveLoop.Create(Enumerable.Range(0,4).Select(i=>(Curve)Line.CreateBound(pts[i],pts[(i+1)%4])).ToList())],ft.Id,level.Id);
                var system=new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().FirstOrDefault()??PipingSystemType.Create(doc,MEPSystemClassification.DomesticColdWater,"BotPlus — проверка");
                var pipeType=new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().First();
                Pipe PipeAt(XYZ a,XYZ b){var pipe=Pipe.Create(doc,system.Id,pipeType.Id,level.Id,a,b);pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(HoleMarkers.Feet(100));return pipe;}
                normal=PipeAt(P(1500,-1500,1500),P(1500,1500,1500));
                var insulationType=new FilteredElementCollector(doc).OfClass(typeof(PipeInsulationType)).Cast<PipeInsulationType>().FirstOrDefault();
                doc.Regenerate();
                if(PipeInsulation.Create(doc,normal.Id,insulationType!.Id,HoleMarkers.Feet(20))==null)throw new InvalidOperationException("Revit не создал проверочную изоляцию.");
                PipeAt(P(1500,3500,-1500),P(1500,3500,1500));
                PipeAt(P(5000,-1500,1500),P(5000,0,1500)); // Ends inside the wall: must be reported, not auto-created.
                var ductSystem=new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().FirstOrDefault()??MechanicalSystemType.Create(doc,MEPSystemClassification.SupplyAir,"BotPlus — воздух");
                var ductType=new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>().First(d=>d.Shape==ConnectorProfileType.Rectangular);
                void DuctAt(XYZ a,XYZ b){var duct=Duct.Create(doc,ductSystem.Id,ductType.Id,level.Id,a,b);duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM).Set(HoleMarkers.Feet(300));duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM).Set(HoleMarkers.Feet(200));}
                DuctAt(P(2500,-1000,1500),P(4500,1000,1500));DuctAt(P(3500,3500,-1500),P(3500,3500,1500));
                HoleStore.Save(doc,data);
            });
            var found=HoleCrossings.Scan(doc,data,options);
            if(found.Candidates.Count!=4||found.Candidates.Any(c=>!c.CanCreate)||!found.Warnings.Any(s=>s.Contains("заканчивается")))throw new InvalidOperationException("Ожидались четыре прохода и замечание о конце трубы: "+HoleJson.Write(found));
            var round=found.Candidates.Single(c=>c.CommunicationId==normal.UniqueId);
            double outside=HoleMarkers.Mm(normal.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER)?.AsDouble()??HoleMarkers.Feet(100));
            if(round.Geometry.Diameter!=HoleChecks.RoundUp(Math.Max(100,outside)+40+50,10))throw new InvalidOperationException($"Наружный диаметр / изоляция трубы / зазор не учтены: отверстие {round.Geometry.Diameter:R}; наружный {outside:R}; изоляция "+string.Join(",",InsulationLiningBase.GetInsulationIds(doc,normal.Id).Select(id=>((InsulationLiningBase)doc.GetElement(id)).Thickness).Select(HoleMarkers.Mm)));
            HoleStore.InTransaction(doc,"Изменить трубу после предпросмотра",()=>ElementTransformUtils.MoveElement(doc,normal.Id,P(100,0,0)));
            bool stale=false;try{HoleCrossings.Create(doc,data,options,null,[round],"ОВ");}catch(InvalidOperationException){stale=true;}
            if(!stale||data.Holes.Count!=0)throw new InvalidOperationException("Устаревший предпросмотр создал задание.");
            HoleStore.InTransaction(doc,"Вернуть трубу",()=>ElementTransformUtils.MoveElement(doc,normal.Id,P(-100,0,0)));
            var created=HoleCrossings.Create(doc,data,options,null,found.Candidates,"ОВ");
            HoleStore.InTransaction(doc,"Создать задания по MEP",()=>{foreach(var h in created)HoleMarkers.Update(doc,h);HoleStore.Save(doc,data);});
            var repeat=HoleCrossings.Scan(doc,data,options);if(repeat.Candidates.Count!=4||repeat.Candidates.Any(c=>c.CanCreate))throw new InvalidOperationException("Повтор поиска предлагает дубли.");
            var selected=HoleCrossings.Scan(doc,data,options,[normal.Id]);if(selected.Candidates.Count!=1)throw new InvalidOperationException("Область выбранных MEP не соблюдена.");
            var actor=HoleStore.Actor(data);foreach(var h in data.Holes)foreach(var role in new[]{Role.Engineer,Role.Architect,Role.Structural})HoleWorkflow.Approve(h,actor,role);
            HoleStore.InTransaction(doc,"Согласовать MEP задания",()=>{foreach(var h in data.Holes)HoleMarkers.Update(doc,h);HoleStore.Save(doc,data);});
            var cut=HoleCuts.Run(doc,data,data.Holes);if(data.Holes.Any(h=>h.CutIds.Count!=1))throw new InvalidOperationException("Автоматические задания не дали четыре точных выреза: "+string.Join("\n",cut));
            string linkedFile=Path.Combine(folder,"Основа для связи.rvt");var linkedDoc=app.NewProjectDocument(template);
            try
            {
                HoleStore.InTransaction(linkedDoc,"Стена в проверочной связи",()=>
                {var l=Level.Create(linkedDoc,0);var wt=new FilteredElementCollector(linkedDoc).OfClass(typeof(WallType)).Cast<WallType>().First(w=>w.Kind==WallKind.Basic);Wall.Create(linkedDoc,Line.CreateBound(P(0,0,0),P(6000,0,0)),wt.Id,l.Id,HoleMarkers.Feet(3000),0,false,false);});
                linkedDoc.SaveAs(linkedFile,new SaveAsOptions{MaximumBackups=1});
            }
            finally{linkedDoc.Close(false);}
            Pipe linkPipe=null!;
            HoleStore.InTransaction(doc,"Повернутая и сдвинутая связь",()=>
            {
                var type=RevitLinkType.Create(doc,ModelPathUtils.ConvertUserVisiblePathToModelPath(linkedFile),new RevitLinkOptions(false));var instance=RevitLinkInstance.Create(doc,type.ElementId);
                ElementTransformUtils.RotateElement(doc,instance.Id,Line.CreateBound(XYZ.Zero,XYZ.BasisZ),Math.PI/2);ElementTransformUtils.MoveElement(doc,instance.Id,P(10000,10000,0));
                var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();var pt=new FilteredElementCollector(doc).OfClass(typeof(PipeType)).FirstElement();var ps=new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElement();
                linkPipe=Pipe.Create(doc,ps.Id,pt.Id,level.Id,P(8500,12500,1500),P(11500,12500,1500));
            });
            var linkedScan=HoleCrossings.Scan(doc,data,options with{IncludeLinks=true},[linkPipe.Id]);
            if(linkedScan.Candidates.Count!=1||Math.Abs(linkedScan.Candidates[0].Geometry.X-10000)>0.1||Math.Abs(linkedScan.Candidates[0].Geometry.Y-12500)>0.1||linkedScan.Candidates[0].LinkId.Length==0)throw new InvalidOperationException("Поворот / сдвиг основы связи не учтены: "+HoleJson.Write(linkedScan));
            if(HoleCrossings.Scan(doc,data,options,[linkPipe.Id]).Candidates.Count!=0)throw new InvalidOperationException("Отключённые связи попали в поиск.");
            var linkedHoles=HoleCrossings.Create(doc,data,options with{IncludeLinks=true},[linkPipe.Id],linkedScan.Candidates,"ОВ");
            HoleStore.InTransaction(doc,"Задание к связанной основе",()=>{foreach(var h in linkedHoles)HoleMarkers.Update(doc,h);HoleStore.Save(doc,data);});
            doc.SaveAs(file,new SaveAsOptions{MaximumBackups=1});doc.Close(false);doc=app.OpenDocumentFile(file);
            var reopened=HoleStore.Read(doc);if(reopened.Holes.Count!=5||reopened.Holes.Any(h=>h.CrossingKey.Length==0)||reopened.Holes.Count(h=>h.CutIds.Count==1)!=4)throw new InvalidOperationException("Потеря привязки к MEP после открытия.");
            return new{file,candidates=4,created=4,cuts=4,linkedCreated=1,rotatedTranslatedLinkVerified=true,insulationVerified=true,stalePreviewRejected=true,repeatWithoutDuplicates=true,selectedScopeVerified=true,reopenVerified=true,warnings=found.Warnings};
        }
        finally{if(doc.IsValidObject)doc.Close(false);}
    }
    static XYZ P(double x,double y,double z)=>new(HoleMarkers.Feet(x),HoleMarkers.Feet(y),HoleMarkers.Feet(z));
}
