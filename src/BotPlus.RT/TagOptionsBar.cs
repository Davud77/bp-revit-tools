using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using BotPlus.Shared;

namespace BotPlus.RT;

/// <summary>Command-owned strip anchored to Revit's options bar, never to screen coordinates.</summary>
internal sealed class TagOptionsBar:IDisposable
{
    readonly FrameworkElement anchor;
    readonly object mainWindow;
    readonly MethodInfo showOptionBar;
    readonly bool wasVisible;
    readonly Window window;
    readonly DispatcherTimer timer;
    bool disposed;

    TagOptionsBar(UIApplication ui,bool copy)
    {
        // Revit has no public options-bar extension API. Keep the 2026 adapter here;
        // no Autodesk assemblies are copied or changed, nor are native controls removed.
        var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("UIFramework.MainWindow")).FirstOrDefault(t=>t!=null)
            ??throw new InvalidOperationException("Не найдена строка параметров Revit 2026.");
        mainWindow=type.GetMethod("getMainWnd",BindingFlags.Public|BindingFlags.Static)?.Invoke(null,null)
            ??throw new InvalidOperationException("Не найдено главное окно Revit.");
        anchor=type.GetField("m_optionBar",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(mainWindow) as FrameworkElement
            ??throw new InvalidOperationException("Не найдена строка параметров под лентой Revit.");
        showOptionBar=type.GetMethod("ShowOptionBar",BindingFlags.Public|BindingFlags.Instance,null,[typeof(bool)],null)
            ??throw new InvalidOperationException("Строка параметров этой версии Revit не поддерживается.");
        wasVisible=anchor.Visibility==Visibility.Visible;
        var content=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        if(copy)
        {
            var selector=new System.Windows.Controls.ComboBox
            {
                ItemsSource=TagCopyOptions.Labels,SelectedIndex=TagCopyOptions.SelectedIndex,Width=170,
                VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(3,0,8,0),
                ToolTip="Область копирования по оборудованию под эталонной маркой"
            };
            selector.SelectionChanged+=(_,_)=>TagCopyOptions.SelectedIndex=selector.SelectedIndex;
            content.Children.Add(selector);
        }
        content.Children.Add(new TextBlock{Text=copy?"Копирование рамкой · Esc — выход":"Сопоставление рамкой · Esc — выход",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,6,0)});
        window=new Window
        {
            WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,
            ShowActivated=false,Background=SystemColors.ControlBrush,Foreground=SystemColors.ControlTextBrush,
            Content=content,Width=copy?475:300,Height=25,FontSize=12
        };
        new WindowInteropHelper(window){Owner=ui.MainWindowHandle};
        BotPlusTheme.Apply(window);
        timer=new DispatcherTimer(DispatcherPriority.Loaded){Interval=TimeSpan.FromMilliseconds(100)};
        timer.Tick+=OnTick;
        try
        {
            showOptionBar.Invoke(mainWindow,[true]);
            anchor.UpdateLayout();
            window.Show();
            Position();
            timer.Start();
        }
        catch{Dispose();throw;}
    }

    internal static TagOptionsBar Show(UIApplication ui,bool copy)=>new(ui,copy);

    void OnTick(object? sender,EventArgs args)
    {
        try{Position();}
        catch(Exception error){System.Diagnostics.Trace.WriteLine(error);Dispose();}
    }

    void Position()
    {
        if(!anchor.IsVisible||anchor.ActualWidth<1||anchor.ActualHeight<1)
        {
            window.Hide();return;
        }
        var point=anchor.PointToScreen(new Point(0,0));
        var source=PresentationSource.FromVisual(window);
        if(source?.CompositionTarget is not { } target)return;
        var dip=target.TransformFromDevice.Transform(point);
        window.Left=dip.X;window.Top=dip.Y;
        window.Height=anchor.ActualHeight;
        window.Width=Math.Min(475,anchor.ActualWidth);
        if(!window.IsVisible)window.Show();
    }

    public void Dispose()
    {
        if(disposed)return;disposed=true;
        timer.Stop();timer.Tick-=OnTick;
        window.Close();
        if(!wasVisible)
        {
            try{showOptionBar.Invoke(mainWindow,[false]);}
            catch(Exception error){System.Diagnostics.Trace.WriteLine(error);}
        }
    }
}
