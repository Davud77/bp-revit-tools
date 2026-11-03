using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TextBox=System.Windows.Controls.TextBox;

namespace BotPlus.RT;

[Transaction(TransactionMode.ReadOnly)]
public sealed class UpdateCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {var window=new UpdateWindow();new WindowInteropHelper(window){Owner=data.Application.MainWindowHandle};window.ShowDialog();return Result.Succeeded;}
}
internal sealed class UpdateWindow:Window
{
    readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)};
    readonly TextBox log=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Height=140};
    readonly Button check=new(){Content="Проверить",Padding=new Thickness(12,6,12,6)};
    readonly Button download=new(){Content="Скачать",IsEnabled=false,Padding=new Thickness(12,6,12,6),Margin=new Thickness(8,0,0,0)};
    readonly Button install=new(){Content="Открыть установщик",IsEnabled=false,Padding=new Thickness(12,6,12,6),Margin=new Thickness(8,0,0,0)};
    readonly ProgressBar progress=new(){Height=8,Margin=new Thickness(0,10,0,12),Maximum=100};
    readonly CancellationTokenSource cancellation=new();
    readonly HttpClient http=new(){Timeout=TimeSpan.FromMinutes(10)};
    ReleaseInfo? release;string? file;bool available;
    public UpdateWindow()
    {
        Title="BotPlus RT — Обновления";Width=640;Height=380;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(20)};Content=panel;
        status.Text="Установлена версия "+typeof(Application).Assembly.GetName().Version?.ToString(2)+". Источник: GitHub / Davud77/bp-revit-tools.";
        panel.Children.Add(status);var buttons=new StackPanel{Orientation=Orientation.Horizontal};buttons.Children.Add(check);buttons.Children.Add(download);buttons.Children.Add(install);panel.Children.Add(buttons);panel.Children.Add(progress);panel.Children.Add(log);
        panel.Children.Add(new TextBlock{Text="Перед установкой сохраните модели и закройте Revit. Установщик показывает ход установки и журнал.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)});
        check.Click+=async(_,_)=>await Run(async()=>{file=null;release=await new ReleaseClient(http).Latest(cancellation.Token);var current=typeof(Application).Assembly.GetName().Version!;available=download.IsEnabled=release.Version>new Version(current.Major,current.Minor,Math.Max(0,current.Build));status.Text=download.IsEnabled?"Доступна версия "+release.Version:"Установлена актуальная версия. Последняя на GitHub: "+release.Version;});
        download.Click+=async(_,_)=>await Run(async()=>{if(release==null)return;file=await new ReleaseClient(http).Download(release,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BotPlus.RT","Updates"),new Progress<int>(n=>progress.Value=n),cancellation.Token);install.IsEnabled=true;Log("SHA-256 подтверждён: "+release.Sha256);});
        install.Click+=(_,_)=>{try{if(file==null||release==null||!ReleaseClient.Verify(file,release))throw new InvalidDataException("Установщик изменён; скачайте его заново.");Process.Start(new ProcessStartInfo(file){UseShellExecute=true});Close();}catch(Exception error){Log(error.Message);}};
        Closed+=(_,_)=>{cancellation.Cancel();http.Dispose();};
    }
    void Log(string value){log.AppendText(DateTime.Now.ToString("HH:mm:ss")+" "+value+Environment.NewLine);log.ScrollToEnd();}
    async Task Run(Func<Task> action)
    {
        check.IsEnabled=false;download.IsEnabled=false;install.IsEnabled=false;
        try{Log("Начало операции");await action();Log("Готово.");}
        catch(OperationCanceledException){Log("Операция отменена.");}catch(Exception error){Log(error.Message);}
        finally{check.IsEnabled=true;if(file!=null)install.IsEnabled=true;else if(release!=null)download.IsEnabled=available;}
    }
}
