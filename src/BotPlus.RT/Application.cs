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
                ("modules/codex-mcp/plugin/BP.Revit.AI.dll", "BP.Revit.AI.PluginApplication"),
                ("modules/ac2revit/dist/Revit2026/Ac2Revit.dll", "Ac2Revit.App"),
                ("modules/ov/HeatLossViewer.dll", "HeatLossViewer.App"),
                ("modules/openings/BotPlus.Openings.dll", "BotPlus.Openings.App")
            })
            {
                var assembly = Assembly.LoadFrom(Path.Combine(root, file));
                var module = (IExternalApplication)Activator.CreateInstance(assembly.GetType(type, throwOnError: true)!)!;
                // Include the failing module so partially registered handlers are cleaned up.
                started.Add(module);
                if (module.OnStartup(application) != Result.Succeeded)
                    throw new InvalidOperationException($"Не удалось загрузить модуль {type}.");
            }
            var tags=application.CreateRibbonPanel("BotPlus RT","Марки");
            tags.AddItem(new PushButtonData("BotPlusMatchTags","Сопоставить",typeof(Application).Assembly.Location,typeof(MatchTagCommand).FullName!){ToolTip="Выберите эталонную марку, затем остальные марки и нажмите «Готово». Переносит тип, ориентацию, выноску и положение относительно оборудования."});
            tags.AddItem(new PushButtonData("BotPlusCopyTags","Копировать\nмарки",typeof(Application).Assembly.Location,typeof(CopyTagCommand).FullName!){ToolTip="Выберите эталонную марку и оборудование. Создаёт марки с тем же относительным положением; существующие марки этого типа пропускает."});
            var service = application.CreateRibbonPanel("BotPlus RT", "Настройки");
            service.AddItem(new PushButtonData("BotPlusRTSettings", "Настройки", typeof(Application).Assembly.Location,
                typeof(SettingsCommand).FullName!){ToolTip="Тема окон, состав панелей ленты и сведения о плагине.",AvailabilityClassName=typeof(HostAvailable).FullName!});
            service.AddItem(new PushButtonData("BotPlusRTUpdates","Обновления",typeof(Application).Assembly.Location,typeof(UpdateCommand).FullName!){AvailabilityClassName=typeof(HostAvailable).FullName!});
            RibbonStyle.Apply(application);
            StartupUpdates.Attach(application);
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
        StartupUpdates.Detach(application);
        foreach (var module in started.AsEnumerable().Reverse())
        {
            try { module.OnShutdown(application); }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        }
        started.Clear();
        return Result.Succeeded;
    }
}
