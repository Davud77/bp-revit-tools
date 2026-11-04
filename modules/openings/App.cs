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
        panel.AddItem(new PushButtonData("BotPlusOpeningManager","Менеджер\nотверстий",typeof(App).Assembly.Location,typeof(ManagerCommand).FullName!));
        HoleMonitor.Start(app);
        return Result.Succeeded;
    }
    public Result OnShutdown(UIControlledApplication app)
    {HoleMonitor.Stop(app);foreach(var controller in Controllers.ToArray())controller.Close();Controllers.Clear();return Result.Succeeded;}
}
[Transaction(TransactionMode.Manual)]
public sealed class ManagerCommand:IExternalCommand
{
    public Result Execute(ExternalCommandData data,ref string message,ElementSet elements)
    {
        var document=data.Application.ActiveUIDocument?.Document;
        if(document==null||document.IsFamilyDocument||document.IsReadOnly){message="Откройте редактируемый проект Revit.";return Result.Cancelled;}
        var existing=App.Controllers.FirstOrDefault(c=>c.Document.Equals(document));
        if(existing!=null){existing.Window.Activate();return Result.Succeeded;}
        try{var controller=new HoleController(data.Application);App.Controllers.Add(controller);controller.Show();return Result.Succeeded;}
        catch(Exception error){message=error.Message;return Result.Failed;}
    }
}
