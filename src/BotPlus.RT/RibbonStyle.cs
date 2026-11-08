using Autodesk.Revit.UI;
using System.Windows;
using System.Windows.Media;

namespace BotPlus.RT;

internal static class RibbonStyle
{
    public static void Apply(UIControlledApplication app)
    {
        foreach(var panel in app.GetRibbonPanels("BotPlus RT"))foreach(var item in panel.GetItems().OfType<PushButton>())
        {item.Image=Icon(item.Name,16);item.LargeImage=Icon(item.Name,32);}
        ApplyVisibility(app.GetRibbonPanels("BotPlus RT"));
    }
    /// <summary>Скрывает панели, снятые в настройках. Панель «Настройки» не входит в список и остаётся всегда.</summary>
    internal static void ApplyVisibility(IEnumerable<RibbonPanel> panels)
    {
        BotPlus.Shared.BotPlusSettings.Load();
        foreach(var panel in panels)
        {
            if(!BotPlus.Shared.BotPlusSettings.Panels.Contains(panel.Name))continue;
            bool visible=!BotPlus.Shared.BotPlusSettings.HiddenPanels.Contains(panel.Name);
            if(panel.Visible!=visible)panel.Visible=visible;
        }
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
            if(name.Contains("Crossings")){d.DrawRoundedRectangle(paper,outline,new Rect(12,3,8,26),1,1);d.DrawRectangle(null,new Pen(blue,3.4),new Rect(2,12,28,8));d.DrawEllipse(Brushes.White,new Pen(green,2),new Point(16,16),4,4);}
            else if(name.Contains("Opening")){d.DrawRoundedRectangle(paper,outline,new Rect(4,4,24,24),1,1);d.DrawEllipse(Brushes.White,new Pen(blue,2),new Point(16,16),7,7);Line(1,16,10,16);Line(22,16,31,16);Line(16,1,16,10);Line(16,22,16,31);}
            else if(name.Contains("Tags"))
            {
                Shape("M3,7 L18,7 L25,14 L18,21 L3,21 Z",paper);d.DrawEllipse(blue,null,new Point(18,14),2,2);Line(6,11,13,11);Line(6,16,12,16);
                if(name.Contains("Copy")){Shape("M11,24 L19,24 L19,20 L28,26 L19,31 L19,28 L11,28 Z",green);}
                else{Line(28,4,28,28);Shape("M18,25 L25,25 M22,22 L25,25 L22,28",null,new Pen(green,2));}
            }
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
            else if(name.Contains("Weam")||name.Contains("Mcp")){d.DrawRoundedRectangle(paper,outline,new Rect(7,7,18,18),2,2);for(int i=11;i<24;i+=5){Line(i,3,i,7);Line(i,25,i,29);Line(3,i,7,i);Line(25,i,29,i);}d.DrawEllipse(blue,null,new Point(16,16),4,4);}
            else if(name.Contains("Preview")){Shape("M4,3 L23,3 L23,28 L4,28 Z",paper);Line(8,9,19,9);Line(8,14,17,14);d.DrawEllipse(Brushes.White,new Pen(blue,2),new Point(22,22),6,6);d.DrawLine(new Pen(blue,3),new Point(26,26),new Point(30,30));}
            else {Shape("M3,10 L13,5 L23,10 L23,25 L13,30 L3,25 Z M3,10 L13,15 L23,10 M13,15 L13,30",paper);if(name.Contains("Transfer")||name.Contains("Export")||name.Contains("Import")||name.Contains("HeatLoss"))Arrow();else Line(6,21,10,23);}
        }
        group.Transform=new ScaleTransform(pixels/32d,pixels/32d);group.Freeze();var image=new DrawingImage(group);image.Freeze();return image;
    }
}
public sealed class HostAvailable:IExternalCommandAvailability
{public bool IsCommandAvailable(UIApplication app,Autodesk.Revit.DB.CategorySet categories)=>true;}
