using Autodesk.Revit.UI;
using System.Windows;
using System.Windows.Media;

namespace BotPlus.RT;

internal static class RibbonStyle
{
    public static void Apply(UIControlledApplication app)
    {
        var panels=Panels(app.GetRibbonPanels);
        foreach(var (panel,_) in panels)foreach(var item in panel.GetItems().OfType<PushButton>())
        {item.Image=Icon(item.Name,16);item.LargeImage=Icon(item.Name,32);}
        foreach(var (panel,_) in panels)foreach(var menu in panel.GetItems().OfType<PulldownButton>())
        {
            menu.Image=Icon(menu.Name,16);menu.LargeImage=Icon(menu.Name,32);
            foreach(var item in menu.GetItems()){item.Image=Icon(item.Name,16);item.LargeImage=Icon(item.Name,32);}
        }
        ApplyVisibility(panels);
    }
    /// <summary>Панели вкладок плагина с ключом модуля: на «BotPlus RT» ключ — имя панели, на вкладке ЭОМ — «ЭОМ». Вкладки ЭОМ нет, если модуль не запущен.</summary>
    internal static List<(RibbonPanel Panel,string Key)> Panels(Func<string,List<RibbonPanel>> get)
    {
        var result=get("BotPlus RT").Select(p=>(p,p.Name)).ToList();
        // Вкладки модулей создаются только при включённом модуле; отсутствующую пропускаем.
        foreach(var tab in new[]{BotPlus.Shared.BotPlusSettings.OvTab,BotPlus.Shared.BotPlusSettings.VkTab})
            try{result.AddRange(get(tab).Select(p=>(p,p.Name)));}catch(Autodesk.Revit.Exceptions.ArgumentException){}
        try{result.AddRange(get(BotPlus.Shared.BotPlusSettings.EomTab).Select(p=>(p,"ЭОМ")));}
        catch(Autodesk.Revit.Exceptions.ArgumentException){}
        return result;
    }
    /// <summary>Скрывает панели, снятые в настройках. Панель «Настройки» не входит в список и остаётся всегда.</summary>
    internal static void ApplyVisibility(IEnumerable<(RibbonPanel Panel,string Key)> panels)
    {
        BotPlus.Shared.BotPlusSettings.Load();
        foreach(var (panel,key) in panels)
        {
            if(!BotPlus.Shared.BotPlusSettings.Panels.Contains(key))continue;
            bool visible=!BotPlus.Shared.BotPlusSettings.HiddenPanels.Contains(key);
            if(panel.Visible!=visible)panel.Visible=visible;
        }
    }
    /// <summary>Иконки модуля ЭОМ: жёлто-оранжевый акцент «электрики» поверх общей палитры.</summary>
    static void EomIcon(DrawingContext d,string key,Pen outline,Brush blue,Brush green,Brush paper)
    {
        var amber=new SolidColorBrush(Color.FromRgb(236,160,40));var red=new SolidColorBrush(Color.FromRgb(214,64,52));
        void Shape(string path,Brush? fill=null,Pen? pen=null)=>d.DrawGeometry(fill,pen??outline,Geometry.Parse(path));
        void Line(double x,double y,double a,double b)=>d.DrawLine(outline,new Point(x,y),new Point(a,b));
        void Bolt(double dx=0,double dy=0)=>Shape(FormattableString.Invariant($"M{17+dx},{2+dy} L{9+dx},{17+dy} L{15+dx},{17+dy} L{13+dx},{30+dy} L{23+dx},{13+dy} L{17+dx},{13+dy} Z"),amber);
        switch(key)
        {
            case "Settings":Shape("M4,4 L28,4 L28,28 L4,28 Z",paper);Line(8,10,24,10);Line(8,16,18,16);Line(8,22,15,22);Bolt(6,2);break;
            case "Catalog":for(int i=0;i<3;i++){d.DrawEllipse(paper,outline,new Point(16,8+i*7),11,3.5);}Shape("M5,8 L5,22 M27,8 L27,22",null);d.DrawEllipse(paper,outline,new Point(16,22),11,3.5);Bolt(4,4);break;
            case "Import":Shape("M5,3 L20,3 L26,9 L26,29 L5,29 Z",paper);Line(9,12,20,12);Line(9,17,18,17);Shape("M14,20 L22,20 L22,16 L29,23 L22,30 L22,26 L14,26 Z",green);break;
            case "Equipment":d.DrawRoundedRectangle(paper,outline,new Rect(4,4,24,24),3,3);d.DrawEllipse(Brushes.White,outline,new Point(16,16),8,8);d.DrawEllipse(blue,null,new Point(13,16),1.6,1.6);d.DrawEllipse(blue,null,new Point(19,16),1.6,1.6);break;
            case "Lighting":d.DrawEllipse(new SolidColorBrush(Color.FromRgb(255,224,130)),outline,new Point(16,13),8,8);Shape("M12,21 L20,21 L19,27 L13,27 Z",paper);for(int i=0;i<5;i++){d.PushTransform(new RotateTransform(-60+i*30,16,13));Line(16,1,16,3);d.Pop();}break;
            case "Layout":d.DrawRectangle(paper,outline,new Rect(3,3,26,26));for(int x=0;x<3;x++)for(int y=0;y<3;y++)d.DrawEllipse(amber,null,new Point(9+x*7,9+y*7),2.4,2.4);break;
            case "Isolux":for(int i=0;i<4;i++)d.DrawEllipse(new SolidColorBrush(Color.FromArgb(255,(byte)(255-i*10),(byte)(90+i*45),(byte)(40+i*45))),i==3?outline:null,new Point(16,16),13-i*3.5,10-i*2.6);break;
            case "RouteTray":Shape("M2,14 L30,14 L30,22 L2,22 Z",paper);Line(2,18,30,18);Line(9,14,9,22);Line(16,14,16,22);Line(23,14,23,22);d.DrawLine(new Pen(blue,2),new Point(2,10),new Point(30,10));break;
            case "RouteConduit":d.DrawRoundedRectangle(paper,outline,new Rect(2,12,28,8),4,4);d.DrawLine(new Pen(blue,2){DashStyle=DashStyles.Dash},new Point(4,16),new Point(28,16));break;
            case "Wiring":Line(2,16,30,16);foreach(var x in new[]{7.0,16,25})Shape(FormattableString.Invariant($"M{x-3},13 L{x-3},19 L{x+3},19 L{x+3},13"),paper);d.DrawLine(new Pen(blue,2),new Point(2,26),new Point(30,26));break;
            case "Riser":d.DrawRectangle(paper,outline,new Rect(12,2,8,28));Line(2,10,30,10);Line(2,22,30,22);Shape("M16,4 L20,9 L12,9 Z M16,28 L20,23 L12,23 Z",blue,null);break;
            case "Connect":d.DrawRectangle(paper,outline,new Rect(19,4,10,24));Line(22,9,26,9);Line(22,14,26,14);d.DrawEllipse(amber,outline,new Point(6,9),3,3);d.DrawEllipse(amber,outline,new Point(6,23),3,3);Shape("M9,9 L19,12 M9,23 L19,20",null,new Pen(green,2));break;
            case "Cable":Shape("M3,26 C10,26 8,8 16,8 C24,8 22,26 29,26",null,new Pen(blue,2.4));d.DrawEllipse(amber,outline,new Point(3,26),2.5,2.5);d.DrawEllipse(amber,outline,new Point(29,26),2.5,2.5);break;
            case "Etm":d.DrawRectangle(amber,outline,new Rect(3,3,10,7));d.DrawRectangle(paper,outline,new Rect(16,13,13,6));d.DrawRectangle(paper,outline,new Rect(16,23,13,6));Shape("M8,10 L8,26 L16,26 M8,16 L16,16",null);break;
            case "Loads":Shape("M24,5 L8,5 L17,16 L8,27 L24,27",null,new Pen(blue,2.6));Bolt(8,0);break;
            case "Protection":d.DrawRoundedRectangle(paper,outline,new Rect(8,2,16,28),2,2);d.DrawRectangle(Brushes.White,outline,new Rect(12,11,8,10));d.DrawRectangle(green,null,new Rect(13,12,6,4));Line(16,2,16,6);Line(16,26,16,30);break;
            case "Indicators":d.DrawRoundedRectangle(paper,outline,new Rect(4,16,14,12),2,2);d.DrawEllipse(Brushes.White,outline,new Point(11,22),3,3);d.DrawEllipse(red,outline,new Point(20,8),4,4);d.DrawEllipse(green,outline,new Point(28,8),3.5,3.5);break;
            case "Checks":Shape("M5,3 L22,3 L27,8 L27,29 L5,29 Z",paper);Shape("M9,12 L12,15 L17,9",null,new Pen(green,2));Shape("M9,22 L12,25 L17,19",null,new Pen(green,2));d.DrawEllipse(red,null,new Point(23,23),3,3);break;
            case "SingleLine":Line(4,6,28,6);for(int x=8;x<=24;x+=8){Line(x,6,x,12);Line(x,12,x+3,18);Line(x,19,x,28);}d.DrawRectangle(null,outline,new Rect(3,22,26,7));break;
            case "Journal":d.DrawRectangle(paper,outline,new Rect(3,4,26,24));Line(3,10,29,10);Line(3,16,29,16);Line(3,22,29,22);Line(11,4,11,28);Line(20,4,20,28);break;
            case "Spec":Shape("M5,3 L27,3 L27,29 L5,29 Z",paper);for(int y=9;y<=24;y+=5){d.DrawEllipse(blue,null,new Point(9,y),1.5,1.5);Line(13,y,24,y);}break;
            case "Report":Shape("M5,3 L22,3 L27,8 L27,29 L5,29 Z",paper);d.DrawRectangle(blue,null,new Rect(9,18,3,7));d.DrawRectangle(green,null,new Rect(14,13,3,12));d.DrawRectangle(amber,null,new Rect(19,9,3,16));break;
            case "Comb":Line(4,28,14,14);Line(14,14,29,14);Line(14,14,14,4);Line(14,4,29,4);Line(14,9,29,9);d.DrawEllipse(amber,outline,new Point(4,28),2.5,2.5);break;
            default:Bolt();break;
        }
    }
    /// <summary>Иконки инструментов «Системы» панели ОВ: трубы — синие, действие — зелёное.</summary>
    static void MepIcon(DrawingContext d,string name,Pen outline,Brush blue,Brush paper)
    {
        var green=new SolidColorBrush(Color.FromRgb(62,158,104));
        var pipe=new Pen(blue,4);var act=new Pen(green,2){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
        void Shape(string path,Brush? fill=null,Pen? pen=null)=>d.DrawGeometry(fill,pen??outline,Geometry.Parse(path));
        void Line(Pen pen,double x,double y,double a,double b)=>d.DrawLine(pen,new Point(x,y),new Point(a,b));
        if(name.Contains("Updater"))
        {
            Shape("M5,3 L22,3 L27,8 L27,29 L5,29 Z",paper);Line(outline,9,10,20,10);Line(outline,9,15,17,15);
            Shape("M10,24 C11,19 20,18 22,22 M22,22 L22,18 M22,22 L18,22",null,act);
        }
        else if(name.Contains("Disconnect")||name.Contains("Detach")){Line(pipe,2,16,12,16);Line(pipe,20,16,30,16);Line(new Pen(new SolidColorBrush(Color.FromRgb(214,64,52)),2),14,8,18,24);}
        else if(name.Contains("Diffuser")){Line(pipe,2,7,30,7);Line(new Pen(blue,3),16,9,16,20);d.DrawRectangle(paper,outline,new Rect(8,20,16,6));for(int x=11;x<=21;x+=5)Line(outline,x,22,x,26);}
        else if(name.Contains("Line")){Line(pipe,2,16,13,16);Line(pipe,19,16,30,16);d.DrawRectangle(paper,outline,new Rect(13,11,6,10));Shape("M9,25 L23,25 M19,22 L23,25 L19,28",null,act);}
        else if(name.Contains("Connect")){Line(pipe,2,22,14,22);Line(pipe,18,22,18,4);d.DrawEllipse(Brushes.White,act,new Point(16,22),3.5,3.5);}
        else if(name.Contains("Tray")){d.PushTransform(new RotateTransform(-20,16,16));d.DrawRectangle(paper,outline,new Rect(4,12,24,8));d.Pop();Shape("M8,28 C14,32 22,30 26,24 M26,24 L26,29 M26,24 L21,25",null,act);}
        else if(name.Contains("Orient")){Line(pipe,2,26,30,26);Line(new Pen(blue,3),8,20,22,6);Shape("M26,10 C28,14 27,18 24,20 M24,20 L29,20 M24,20 L25,15",null,act);}
        else {Line(pipe,4,16,28,16);d.DrawEllipse(null,act,new Point(16,16),10,10);Shape("M26,10 L26,16 L20,14",null,act);}
    }
    internal static DrawingImage Icon(string name,int pixels)
    {
        var group=new DrawingGroup();using(var d=group.Open())
        {
            var outline=new Pen(new SolidColorBrush(Color.FromRgb(76,96,117)),1.4){LineJoin=PenLineJoin.Round,StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
            var blue=new SolidColorBrush(Color.FromRgb(51,145,210));var green=new SolidColorBrush(Color.FromRgb(62,158,104));var paper=new SolidColorBrush(Color.FromRgb(233,242,248));
            void Shape(string path,Brush? fill=null,Pen? pen=null)=>d.DrawGeometry(fill,pen??outline,Geometry.Parse(path));
            void Line(double x,double y,double a,double b)=>d.DrawLine(outline,new Point(x,y),new Point(a,b));
            void Arrow()=>Shape("M15,12 L23,12 L23,7 L30,16 L23,25 L23,20 L15,20 Z",green);
            if(name.StartsWith("BotPlusEom"))EomIcon(d,name["BotPlusEom".Length..],outline,blue,green,paper);
            else if(name.StartsWith("OVMep"))MepIcon(d,name,outline,blue,paper);
            else if(name.Contains("Crossings")){d.DrawRoundedRectangle(paper,outline,new Rect(12,3,8,26),1,1);d.DrawRectangle(null,new Pen(blue,3.4),new Rect(2,12,28,8));d.DrawEllipse(Brushes.White,new Pen(green,2),new Point(16,16),4,4);}
            else if(name.Contains("Opening")){d.DrawRoundedRectangle(paper,outline,new Rect(4,4,24,24),1,1);d.DrawEllipse(Brushes.White,new Pen(blue,2),new Point(16,16),7,7);Line(1,16,10,16);Line(22,16,31,16);Line(16,1,16,10);Line(16,22,16,31);}
            else if(name.Contains("Tags"))
            {
                Shape("M3,7 L18,7 L25,14 L18,21 L3,21 Z",paper);d.DrawEllipse(blue,null,new Point(18,14),2,2);Line(6,11,13,11);Line(6,16,12,16);
                if(name.Contains("Copy")){Shape("M11,24 L19,24 L19,20 L28,26 L19,31 L19,28 L11,28 Z",green);}
                else{Line(28,4,28,28);Shape("M18,25 L25,25 M22,22 L25,25 L22,28",null,new Pen(green,2));}
            }
            else if(name.Contains("ScheduleNumbering")){d.DrawRectangle(paper,outline,new Rect(3,4,26,24));Line(10,4,10,28);Line(3,12,29,12);Line(3,20,29,20);Line(5,8,8,8);Line(5,16,8,16);Line(5,24,8,24);Line(13,8,25,8);Line(13,16,25,16);Line(13,24,25,24);}
            else if(name.Contains("Folder")){Shape("M3,8 L12,8 L15,11 L29,11 L27,27 L3,27 Z",new SolidColorBrush(Color.FromRgb(236,184,75)));Line(5,15,26,15);}
            else if(name.Contains("Radiator")){for(int i=0;i<5;i++)d.DrawRoundedRectangle(paper,outline,new Rect(3+i*5,8,4,19),1,1);Line(2,12,29,12);Line(2,23,29,23);Shape("M12,5 L14,2 M20,5 L22,2",null,new Pen(blue,1.6));}
            else if(name.Contains("Library")){Shape("M3,6 L9,6 L9,28 L3,28 Z M11,3 L17,3 L17,28 L11,28 Z M20,7 L25,5 L31,26 L25,28 Z",paper);Line(5,11,8,11);Line(13,8,16,8);}
            else if(name=="OVProjectInformation")
            {
                Shape("M3,15 L16,4 L29,15 M7,13 L7,28 L25,28 L25,13",paper);
                var heat=new Pen(new SolidColorBrush(Color.FromRgb(221,102,48)),2){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
                for(int x=12;x<=20;x+=4)Shape($"M{x},24 C{x-3},21 {x+3},19 {x},16",null,heat);
            }
            else if(name.Contains("Settings")){for(int i=0;i<8;i++){d.PushTransform(new RotateTransform(i*45,16,16));d.DrawRoundedRectangle(paper,outline,new Rect(13,2.5,6,6.5),1,1);d.Pop();}d.DrawEllipse(paper,outline,new Point(16,16),9.5,9.5);d.DrawEllipse(blue,null,new Point(16,16),4.2,4.2);d.DrawEllipse(Brushes.White,null,new Point(16,16),1.7,1.7);}
            else if(name.Contains("Information")||name.Contains("Service")){Shape("M5,3 L22,3 L28,9 L28,29 L5,29 Z",paper);Line(21,3,21,10);Line(21,10,28,10);d.DrawEllipse(blue,null,new Point(17,20),6,6);d.DrawLine(new Pen(Brushes.White,2),new Point(17,19),new Point(17,24));d.DrawEllipse(Brushes.White,null,new Point(17,16),1,1);}
            else if(name.Contains("Update")||name.Contains("Continue")){d.DrawEllipse(paper,outline,new Point(16,16),12,12);Shape("M7,13 C9,5 23,5 25,13 M25,13 L25,6 M25,13 L18,13",null,new Pen(blue,2.2));Shape("M25,20 C22,28 9,27 7,20 M7,20 L7,27 M7,20 L14,20",null,new Pen(green,2.2));}
            else if(name.Contains("Confirm")){d.DrawRoundedRectangle(paper,outline,new Rect(4,4,24,24),3,3);Shape("M8,16 L14,22 L25,9",null,new Pen(green,3));}
            else if(name.Contains("Mcp")){d.DrawRoundedRectangle(paper,outline,new Rect(7,7,18,18),2,2);for(int i=11;i<24;i+=5){Line(i,3,i,7);Line(i,25,i,29);Line(3,i,7,i);Line(25,i,29,i);}d.DrawEllipse(blue,null,new Point(16,16),4,4);}
            else if(name.Contains("Preview")){Shape("M4,3 L23,3 L23,28 L4,28 Z",paper);Line(8,9,19,9);Line(8,14,17,14);d.DrawEllipse(Brushes.White,new Pen(blue,2),new Point(22,22),6,6);d.DrawLine(new Pen(blue,3),new Point(26,26),new Point(30,30));}
            else {Shape("M3,10 L13,5 L23,10 L23,25 L13,30 L3,25 Z M3,10 L13,15 L23,10 M13,15 L13,30",paper);if(name.Contains("Transfer")||name.Contains("Export")||name.Contains("Import")||name.Contains("HeatLoss"))Arrow();else Line(6,21,10,23);}
        }
        group.Transform=new ScaleTransform(pixels/32d,pixels/32d);group.Freeze();var image=new DrawingImage(group);image.Freeze();return image;
    }
}
public sealed class HostAvailable:IExternalCommandAvailability
{public bool IsCommandAvailable(UIApplication app,Autodesk.Revit.DB.CategorySet categories)=>true;}
