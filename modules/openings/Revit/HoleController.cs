using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BotPlus.Openings.Core;
using System.Windows.Interop;

namespace BotPlus.Openings;

internal sealed record LevelChoice(string Id,string Name,double Elevation)
{public override string ToString()=>Name;}
internal sealed record LinkChoice(string Id,string Name)
{public override string ToString()=>Name;}
internal sealed class HoleController:IExternalEventHandler
{
    internal Document Document {get;}
    internal ManagerWindow Window {get;}
    readonly ExternalEvent external;
    Action<UIApplication,Registry>? pending;
    bool closed;
    internal HoleController(UIApplication ui)
    {
        Document=ui.ActiveUIDocument.Document;external=ExternalEvent.Create(this);Window=new(this);
        new WindowInteropHelper(Window){Owner=ui.MainWindowHandle};Window.Closed+=(_,_)=>{closed=true;external.Dispose();App.Controllers.Remove(this);};Refresh();
    }
    internal void Show()=>Window.Show();
    internal void Close()=>Window.Close();
    internal void Request(Action<UIApplication,Registry> action)
    {
        if(closed||pending!=null)return;pending=action;
        if(external.Raise()!=ExternalEventRequest.Accepted){pending=null;Window.SetStatus("Revit занят. Повторите действие после завершения текущей команды.");}
    }
    public string GetName()=>"BotPlus — Менеджер отверстий";
    public void Execute(UIApplication ui)
    {
        var action=pending;pending=null;if(closed||action==null)return;
        try
        {
            if(!Document.IsValidObject||ui.ActiveUIDocument?.Document.Title!=Document.Title||ui.ActiveUIDocument.Document.PathName!=Document.PathName)throw new InvalidOperationException("Активируйте проект, для которого открыт менеджер.");
            var data=HoleStore.Read(Document);HoleMonitor.Reconcile(Document,data);action(ui,data);Refresh();
        }
        catch(Autodesk.Revit.Exceptions.OperationCanceledException){Window.SetStatus("Выбор отменён.");Window.Show();}
        catch(Exception error){Window.SetStatus(error.Message);Window.Show();}
    }
    internal void Refresh()
    {
        var data=HoleStore.Read(Document);
        var levels=new FilteredElementCollector(Document).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>l.Elevation).Select(l=>new LevelChoice(l.UniqueId,l.Name,HoleMarkers.Mm(l.Elevation))).ToList();
        var links=new FilteredElementCollector(Document).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Where(l=>l.GetLinkDocument()!=null).Select(l=>new LinkChoice(l.UniqueId,l.Name)).ToList();
        Window.SetData(data,levels,links,Document.Title);
        HoleMonitor.Track(Document);
    }
    internal static void RequireProject(Registry data){if(data.Project==null)throw new InvalidOperationException("Создайте реестр на вкладке «Настройки».");}
    internal void Save(Registry data,IEnumerable<Hole> changed,string title)
    {
        var holes=changed.ToList();
        HoleStore.InTransaction(Document,"Отверстия — "+title,()=>
        {
            foreach(var h in holes)
            {
                if(h.Deleted&&h.CutIds.Any(id=>Document.GetElement(id)!=null))throw new InvalidOperationException("Сначала снимите физические вырезы удаляемого задания.");
                HoleMarkers.Update(Document,h);
            }
            HoleMarkers.Colour(Document,Document.ActiveView,holes,Window.ApprovalRole);
            HoleStore.Save(Document,data);
        });
    }
    internal void Initialize(Registry data)
    {
        if(data.Project!=null)throw new InvalidOperationException("Реестр уже существует.");
        data.Project=new(){Name=Document.Title};data.Project.Users[HoleStore.User]=Role.Admin;
        data.Project.SettingsHistory.Add(DateTimeOffset.UtcNow+" "+HoleStore.User+" — создан реестр; назначен первый администратор.");
        HoleStore.InTransaction(Document,"Создать реестр отверстий",()=>HoleStore.Save(Document,data));Window.SetStatus("Реестр создан. Назначьте роли участникам на вкладке «Настройки».");
    }
    internal void CreateManual(UIApplication ui,Registry data)
    {
        RequireProject(data);var actor=HoleStore.Actor(data);if(actor.Role is not (Role.Engineer or Role.Admin))throw new InvalidOperationException("Задания создаёт инженер или администратор.");
        Window.Hide();Reference reference;
        try{reference=ui.ActiveUIDocument.Selection.PickObject(ObjectType.PointOnElement,new HostFilter(Document),"Выберите точку на стене или перекрытии, в том числе в связи");}
        finally{Window.Show();}
        var owner=Document;Transform transform=Transform.Identity;Element host=Document.GetElement(reference.ElementId);string linkId="";
        if(host is RevitLinkInstance link){owner=link.GetLinkDocument()??throw new InvalidOperationException("Связь не загружена.");host=owner.GetElement(reference.LinkedElementId);transform=link.GetTotalTransform();linkId=link.UniqueId;}
        var point=reference.GlobalPoint;var orientation=host is Wall?HoleOrientation.Wall:HoleOrientation.Floor;
        // A picked face lies on the surface; place the task on the host's centre plane.
        var localPoint=transform.Inverse.OfPoint(point);
        if(host is Wall&&host.Location is LocationCurve location)
        {
            if(location.Curve is not Line)throw new InvalidOperationException("Ручное размещение на криволинейной стене пока не поддержано.");
            var projected=location.Curve.Project(localPoint)?.XYZPoint??throw new InvalidOperationException("Не удалось определить ось стены.");
            point=transform.OfPoint(new XYZ(projected.X,projected.Y,localPoint.Z));
        }
        else if(host.get_BoundingBox(null) is {} hostBox)point=transform.OfPoint(new XYZ(localPoint.X,localPoint.Y,(hostBox.Min.Z+hostBox.Max.Z)/2));
        if(transform.OfVector(XYZ.BasisZ).DistanceTo(XYZ.BasisZ)>1e-7)throw new InvalidOperationException("Наклонённые связи пока не поддержаны.");
        var normal=host is Wall wall?transform.OfVector(wall.Orientation):XYZ.BasisZ;
        var level=Window.Levels.MinBy(l=>Math.Abs(l.Elevation-HoleMarkers.Mm(point.Z)))??throw new InvalidOperationException("В проекте нет уровней.");
        var box=host.get_BoundingBox(null);double depth=host is Wall w?HoleMarkers.Mm(w.Width)+20:box==null?300:HoleMarkers.Mm(box.Max.Z-box.Min.Z)+20;
        var geometry=new HoleGeometry{X=HoleMarkers.Mm(point.X),Y=HoleMarkers.Mm(point.Y),Z=HoleMarkers.Mm(point.Z),Orientation=orientation,Yaw=orientation==HoleOrientation.Wall?Math.Atan2(-normal.X,normal.Y):0,Depth=depth};
        var dialog=new HoleEditor(geometry,Window.Levels,level.Id,"ОВ"){Owner=Window};if(dialog.ShowDialog()!=true)return;
        var chosen=dialog.Level;var hole=HoleWorkflow.Create(data.Project!,actor,dialog.Geometry,dialog.Discipline,chosen.Id,chosen.Name,chosen.Elevation);
        hole.HostId=host.UniqueId;hole.HostLinkId=linkId;hole.Mark=NextMark(data,hole.Discipline);data.Holes.Add(hole);Save(data,[hole],"Создать задание");
    }
    internal static string NextMark(Registry data,string discipline)
    {var used=data.Holes.Select(h=>h.Mark).ToHashSet(StringComparer.OrdinalIgnoreCase);for(int i=1;;i++){string mark=discipline+"-"+i.ToString("000");if(!used.Contains(mark))return mark;}}
    internal void Edit(Registry data,Hole hole)
    {
        RequireProject(data);var dialog=new HoleEditor(hole.Geometry,Window.Levels,hole.LevelId,hole.Discipline,hole.ShaftId.Length>0){Owner=Window};if(dialog.ShowDialog()!=true)return;
        var actor=HoleStore.Actor(data);var old=hole.Geometry;var selected=dialog.ApplyShaft?data.Holes.Where(h=>!h.Deleted&&h.ShaftId==hole.ShaftId).ToList():[hole];
        foreach(var h in selected)
        {
            var g=h==hole?dialog.Geometry:dialog.Geometry with{X=h.Geometry.X+dialog.Geometry.X-old.X,Y=h.Geometry.Y+dialog.Geometry.Y-old.Y,Z=h.Geometry.Z+dialog.Geometry.Z-old.Z};
            HoleWorkflow.Change(h,actor,g,data.Project!.ToleranceFor(h.Geometry),dialog.Comment);
            if(h==hole){h.LevelId=dialog.Level.Id;h.LevelName=dialog.Level.Name;h.LevelElevation=dialog.Level.Elevation;}
        }
        Save(data,selected,"Изменить задание");
    }
    internal void CopyPoint(UIApplication ui,Registry data,Hole source)
    {
        RequireProject(data);Window.Hide();XYZ point;try{point=ui.ActiveUIDocument.Selection.PickPoint("Укажите новый центр отверстия");}finally{Window.Show();}
        var h=HoleWorkflow.Copy(data.Project!,source,HoleStore.Actor(data),HoleMarkers.Mm(point.X)-source.Geometry.X,HoleMarkers.Mm(point.Y)-source.Geometry.Y,0,source.LevelId,source.LevelName,source.LevelElevation);
        h.HostId="";h.Mark=NextMark(data,h.Discipline);data.Holes.Add(h);Save(data,[h],"Копировать задание");
    }
    internal void ShowHole(UIApplication ui,IReadOnlyList<Hole> holes,bool threeD)
    {
        var ids=holes.Select(h=>HoleMarkers.Marker(Document,h)).Where(m=>m!=null).Select(m=>m!.Id).ToList();if(ids.Count==0)throw new InvalidOperationException("Маркер не найден в этой модели.");
        if(threeD)
        {
            View3D? view=null;
            HoleStore.InTransaction(Document,"Отверстия — показать в 3D",()=>
            {
                view=new FilteredElementCollector(Document).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v=>v.Name=="BotPlus — Отверстия");
                if(view==null){var type=new FilteredElementCollector(Document).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t=>t.ViewFamily==ViewFamily.ThreeDimensional);view=View3D.CreateIsometric(Document,type.Id);view.Name="BotPlus — Отверстия";}
                var corners=holes.SelectMany(h=>h.Geometry.Corners()).ToList();double margin=HoleMarkers.Feet(500);
                view.SetSectionBox(new BoundingBoxXYZ{Min=new XYZ(HoleMarkers.Feet(corners.Min(p=>p[0]))-margin,HoleMarkers.Feet(corners.Min(p=>p[1]))-margin,HoleMarkers.Feet(corners.Min(p=>p[2]))-margin),Max=new XYZ(HoleMarkers.Feet(corners.Max(p=>p[0]))+margin,HoleMarkers.Feet(corners.Max(p=>p[1]))+margin,HoleMarkers.Feet(corners.Max(p=>p[2]))+margin)});
            });ui.ActiveUIDocument.ActiveView=view;
        }
        ui.ActiveUIDocument.Selection.SetElementIds(ids);ui.ActiveUIDocument.ShowElements(ids);
    }
    sealed class HostFilter(Document doc):ISelectionFilter
    {
        public bool AllowElement(Element e)=>e is Wall or Floor or RevitLinkInstance;
        public bool AllowReference(Reference reference,XYZ point)
        {var e=doc.GetElement(reference.ElementId);if(e is RevitLinkInstance link)e=link.GetLinkDocument()?.GetElement(reference.LinkedElementId);return e is Wall or Floor;}
    }
}
