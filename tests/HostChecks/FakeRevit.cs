namespace Autodesk.Revit.UI
{
    public enum Result { Succeeded, Failed, Cancelled }
    public interface IExternalApplication
    {
        Result OnStartup(UIControlledApplication application);
        Result OnShutdown(UIControlledApplication application);
    }
    public class UIControlledApplication { public void CreateRibbonTab(string name) { } public RibbonPanel CreateRibbonPanel(string tab,string name)=>new(); }
    public class RibbonPanel { public void AddItem(PushButtonData data) { } }
    public class PushButtonData { public string AvailabilityClassName {get;set;}=""; public PushButtonData(string id,string text,string assembly,string type) { } }
    public static class TaskDialog { public static void Show(string title, string message) => State.Errors.Add(message); }
}
namespace Autodesk.Revit.Exceptions { public class ArgumentException : System.ArgumentException { } }

public static class State
{
    public static readonly List<string> Calls = [];
    public static readonly List<string> Errors = [];
    public static bool FailAc2Revit;
}
public abstract class FakeModule(string name) : Autodesk.Revit.UI.IExternalApplication
{
    public Autodesk.Revit.UI.Result OnStartup(Autodesk.Revit.UI.UIControlledApplication app)
    {
        State.Calls.Add("start:" + name);
        if (name == "ac2revit" && State.FailAc2Revit) throw new InvalidOperationException("startup test failure");
        return Autodesk.Revit.UI.Result.Succeeded;
    }
    public Autodesk.Revit.UI.Result OnShutdown(Autodesk.Revit.UI.UIControlledApplication app)
    {
        State.Calls.Add("stop:" + name);
        return Autodesk.Revit.UI.Result.Succeeded;
    }
}
namespace WEAM.Revit.AI { public sealed class PluginApplication() : FakeModule("mcp"); }
namespace Ac2Revit { public sealed class App() : FakeModule("ac2revit"); }
namespace HeatLossViewer { public sealed class App() : FakeModule("ov"); }
namespace BotPlus.RT { public sealed class ServiceCommand { } }
namespace BotPlus.RT { public sealed class HostAvailable{} }
namespace BotPlus.RT { public sealed class UpdateCommand { } internal static class RibbonStyle { public static void Apply(Autodesk.Revit.UI.UIControlledApplication app){} } }
