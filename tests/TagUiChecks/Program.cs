using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using BotPlus.RT;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var owner=new UIFramework.MainWindow{ShowActivated=false,ShowInTaskbar=false,Left=100,Top=100,Width=650,Height=200};
        owner.Show();owner.UpdateLayout();
        var ui=new Autodesk.Revit.UI.UIApplication{MainWindowHandle=new WindowInteropHelper(owner).Handle};
        using(var strip=TagOptionsBar.Show(ui,true))
        {
            Pump();
            var window=app.Windows.Cast<Window>().Single(w=>w!=owner);
            var selector=((StackPanel)window.Content).Children.OfType<ComboBox>().Single();
            if(selector.SelectedIndex!=0||!window.IsVisible)throw new Exception("Scope strip did not open with family selected");
            selector.SelectedIndex=1;
            if(TagCopyOptions.Current!=TagCopyScope.Category)throw new Exception("Category change not applied immediately");
            selector.SelectedIndex=2;
            if(TagCopyOptions.Current!=TagCopyScope.All)throw new Exception("All change not applied immediately");
            var previous=window.Left;owner.Left+=40;Pump();
            if(Math.Abs(window.Left-previous-40)>1)throw new Exception("Scope strip did not follow owner movement");
            if(((StackPanel)window.Content).Children.OfType<Button>().Any()||((StackPanel)window.Content).Children.OfType<CheckBox>().Any())
                throw new Exception("Unexpected confirmation or selection controls");
        }
        if(app.Windows.Count!=1||owner.BarVisible)throw new Exception("Scope strip or temporary options bar left open");
        owner.ShowOptionBar(true);
        using(var strip=TagOptionsBar.Show(ui,false))
        {
            Pump();var window=app.Windows.Cast<Window>().Single(w=>w!=owner);
            if(((StackPanel)window.Content).Children.OfType<ComboBox>().Any())throw new Exception("Copy scope leaked into matching");
        }
        if(!owner.BarVisible)throw new Exception("Previously visible options bar was hidden");
        owner.Close();app.Shutdown();
        Console.WriteLine("PASS options strip: immediate scope changes, owner movement, no confirmation controls, matching and cleanup");
    }
    static void Pump()
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(180)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
}

namespace Autodesk.Revit.UI
{
    public sealed class UIApplication { public IntPtr MainWindowHandle {get;init;} }
    public enum UITheme { Light,Dark }
    public static class UIThemeManager { public static UITheme CurrentTheme=>UITheme.Light; }
}
namespace UIFramework
{
    public sealed class MainWindow:Window
    {
        static MainWindow? current;
        readonly Border m_optionBar=new(){Height=25,VerticalAlignment=VerticalAlignment.Top,Visibility=Visibility.Collapsed};
        public MainWindow(){current=this;Content=m_optionBar;}
        public static MainWindow getMainWnd()=>current!;
        public bool BarVisible=>m_optionBar.Visibility==Visibility.Visible;
        public void ShowOptionBar(bool visible){m_optionBar.Visibility=visible?Visibility.Visible:Visibility.Collapsed;UpdateLayout();}
    }
}
