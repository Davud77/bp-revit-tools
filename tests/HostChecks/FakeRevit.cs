namespace Autodesk.Revit.UI
{
    public enum Result { Succeeded, Failed, Cancelled }
    public interface IExternalApplication
    {
        Result OnStartup(UIControlledApplication application);
        Result OnShutdown(UIControlledApplication application);
    }
    public class UIControlledApplication
    {
        public List<string> Panels { get; } = [];
        public void CreateRibbonTab(string name) { }
        public RibbonPanel CreateRibbonPanel(string tab, string name) { Panels.Add(name); return new(); }
    }
    public class RibbonPanel { public void AddItem(PushButtonData data) { } }
    public class PushButtonData { public string AvailabilityClassName {get;set;}=""; public string ToolTip {get;set;}=""; public PushButtonData(string id,string text,string assembly,string type) { } }
    public static class TaskDialog { public static void Show(string title, string message) => State.Errors.Add(message); }
}
namespace Autodesk.Revit.Exceptions { public class ArgumentException : System.ArgumentException { } }
namespace BotPlus.Shared
{
    internal static class BotPlusSettings
    {
        internal static HashSet<string> HiddenPanels { get; } = [];
        internal const string VkTab = "BP ВК";
        internal static void Load() { }
    }
}

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
namespace BP.Revit.AI { public sealed class PluginApplication() : FakeModule("mcp"); }
namespace Ac2Revit { public sealed class App() : FakeModule("ac2revit"); }
namespace HeatLossViewer { public sealed class App() : FakeModule("ov"); }
namespace BotPlus.Openings { public sealed class App() : FakeModule("openings"); }
namespace BotPlus.Eom { public sealed class App() : FakeModule("eom"); }
namespace BotPlus.RT { public sealed class SettingsCommand { } }
namespace BotPlus.RT { public sealed class MatchTagCommand { } public sealed class CopyTagCommand { } }
namespace BotPlus.RT { public sealed class ScheduleNumberingCommand { } public sealed class ScheduleNumberingAvailable { } }
namespace BotPlus.RT { internal static class StartupUpdates { public static void Attach(Autodesk.Revit.UI.UIControlledApplication app){} public static void Detach(Autodesk.Revit.UI.UIControlledApplication app){} } }
namespace BotPlus.RT { public sealed class HostAvailable{} }
namespace BotPlus.RT { public sealed class UpdateCommand { } internal static class RibbonStyle { public static void Apply(Autodesk.Revit.UI.UIControlledApplication app){} } }
