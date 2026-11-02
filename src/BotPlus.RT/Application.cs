using System.Reflection;
using Autodesk.Revit.UI;

namespace BotPlus.RT;

/// <summary>One Revit entry point owns the startup and shutdown order of all modules.</summary>
public sealed class Application : IExternalApplication
{
    readonly List<IExternalApplication> started = [];

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            try { application.CreateRibbonTab("BotPlus RT"); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            string root = Path.GetDirectoryName(typeof(Application).Assembly.Location)!;
            foreach (var (file, type) in new[]
            {
                ("modules/codex-mcp/plugin/WEAM.Revit.AI.dll", "WEAM.Revit.AI.PluginApplication"),
                ("modules/ac2revit/dist/Revit2026/Ac2Revit.dll", "Ac2Revit.App"),
                ("modules/ov/HeatLossViewer.dll", "HeatLossViewer.App")
            })
            {
                var assembly = Assembly.LoadFrom(Path.Combine(root, file));
                var module = (IExternalApplication)Activator.CreateInstance(assembly.GetType(type, throwOnError: true)!)!;
                // Include the failing module so partially registered handlers are cleaned up.
                started.Add(module);
                if (module.OnStartup(application) != Result.Succeeded)
                    throw new InvalidOperationException($"Не удалось загрузить модуль {type}.");
            }
            var service = application.CreateRibbonPanel("BotPlus RT", "Сервис");
            service.AddItem(new PushButtonData("BotPlusRTService", "Сервис", typeof(Application).Assembly.Location,
                typeof(ServiceCommand).FullName!));
            return Result.Succeeded;
        }
        catch (Exception error)
        {
            OnShutdown(application);
            TaskDialog.Show("BotPlus RT", "Ошибка загрузки модулей:\n" + error.GetBaseException().Message);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        foreach (var module in started.AsEnumerable().Reverse())
        {
            try { module.OnShutdown(application); }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        }
        started.Clear();
        return Result.Succeeded;
    }
}
