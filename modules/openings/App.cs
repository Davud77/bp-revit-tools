using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BotPlus.Openings;

public sealed class App:IExternalApplication
{
    internal static readonly List<HoleController> Controllers=[];
    public Result OnStartup(UIControlledApplication app)
    {
        var panel=app.CreateRibbonPanel("BotPlus RT","Отверстия");
        string path=typeof(App).Assembly.Location;
        panel.AddItem(new PushButtonData("BotPlusOpeningCrossings","Пересечения",path,typeof(CrossingsCommand).FullName!)
            {ToolTip="Открывает вкладку «Пересечения»: поиск пересечений инженерных систем со стенами и сетями из связей. Ничего не создаёт."});
        panel.AddItem(new PushButtonData("BotPlusOpeningManager","Менеджер\nотверстий",path,typeof(ManagerCommand).FullName!)
            {ToolTip="Открывает вкладку «Отверстия»: задания, согласование и вырезы."});
        HoleMonitor.Start(app);
        return Result.Succeeded;
    }
    public Result OnShutdown(UIControlledApplication app)
    {HoleMonitor.Stop(app);foreach(var controller in Controllers.ToArray())controller.Close();Controllers.Clear();return Result.Succeeded;}
}
public abstract class OpeningCommand:IExternalCommand
{
    /// <summary>true — открыть вкладку «Пересечения», false — «Отверстия».</summary>
    protected abstract bool Crossings {get;}
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var document=data.Application.ActiveUIDocument?.Document;
        if(document==null||document.IsFamilyDocument||document.IsReadOnly){message="Откройте редактируемый проект Revit.";return Result.Cancelled;}
        var existing=App.Controllers.FirstOrDefault(c=>c.Document.Equals(document));
        if(existing!=null){existing.Window.ShowTab(Crossings);existing.Window.Activate();return Result.Succeeded;}
        try{var controller=new HoleController(data.Application);App.Controllers.Add(controller);controller.Show(Crossings);return Result.Succeeded;}
        catch(Exception error){message=error.Message;return Result.Failed;}
    }
}
[Transaction(TransactionMode.Manual)]
public sealed class CrossingsCommand:OpeningCommand {protected override bool Crossings=>true;}
[Transaction(TransactionMode.Manual)]
public sealed class ManagerCommand:OpeningCommand {protected override bool Crossings=>false;}
