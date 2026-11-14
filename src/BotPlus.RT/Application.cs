using System.Reflection;
using Autodesk.Revit.UI;
using BotPlus.Shared;

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
            BotPlusSettings.Load();
            TagCopyOptions.Reset();
            string root = Path.GetDirectoryName(typeof(Application).Assembly.Location)!;
            foreach (var (panel, file, type) in new[]
            {
                ("MCP", "modules/codex-mcp/plugin/BP.Revit.AI.dll", "BP.Revit.AI.PluginApplication"),
                ("Ac2Revit", "modules/ac2revit/dist/Revit2026/Ac2Revit.dll", "Ac2Revit.App"),
                ("ОВ", "modules/ov/HeatLossViewer.dll", "HeatLossViewer.App"),
                ("Отверстия", "modules/openings/BotPlus.Openings.dll", "BotPlus.Openings.App"),
                ("ЭОМ", "modules/eom/BotPlus.Eom.dll", "BotPlus.Eom.App")
            })
            {
                if (panel == "ЭОМ" && !BotPlusSettings.HiddenPanels.Contains("ВК")) CreateVkTab(application);
                if (BotPlusSettings.HiddenPanels.Contains(panel)) continue;
                var assembly = Assembly.LoadFrom(Path.Combine(root, file));
                var module = (IExternalApplication)Activator.CreateInstance(assembly.GetType(type, throwOnError: true)!)!;
                // Include the failing module so partially registered handlers are cleaned up.
                started.Add(module);
                if (module.OnStartup(application) != Result.Succeeded)
                    throw new InvalidOperationException($"Не удалось загрузить модуль {type}.");
            }
            if (!BotPlusSettings.HiddenPanels.Contains("Марки"))
            {
                var tags=application.CreateRibbonPanel("BotPlus RT","Марки");
                tags.AddItem(new PushButtonData("BotPlusMatchTags","Сопоставить",typeof(Application).Assembly.Location,typeof(MatchTagCommand).FullName!){ToolTip="Выберите эталонную марку, затем обведите марки рамкой. Каждая группа сопоставляется сразу. Esc — завершить."});
                tags.AddItem(new PushButtonData("BotPlusCopyTags","Копировать\nмарки",typeof(Application).Assembly.Location,typeof(CopyTagCommand).FullName!){ToolTip="Выберите эталонную марку, затем обведите оборудование рамкой. Марки создаются сразу. Область копирования — в строке под лентой. Esc — завершить."});
            }
            if (!BotPlusSettings.HiddenPanels.Contains("Спецификации"))
            {
                var schedules = application.CreateRibbonPanel("BotPlus RT", "Спецификации");
                schedules.AddItem(new PushButtonData("BotPlusScheduleNumbering", "Нумерация", typeof(Application).Assembly.Location,
                    typeof(ScheduleNumberingCommand).FullName!) { ToolTip = "Пронумеровать элементы в порядке строк активной спецификации.",
                    AvailabilityClassName = typeof(ScheduleNumberingAvailable).FullName! });
            }
            var service = application.CreateRibbonPanel("BotPlus RT", "Настройки");
            service.AddItem(new PushButtonData("BotPlusRTSettings", "Настройки", typeof(Application).Assembly.Location,
                typeof(SettingsCommand).FullName!){ToolTip="Тема окон, включение модулей и сведения о плагине.",AvailabilityClassName=typeof(HostAvailable).FullName!});
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

    /// <summary>Вкладка ВК пока без команд: создаётся одна пустая панель, чтобы вкладка была видна и готова к наполнению.</summary>
    static void CreateVkTab(UIControlledApplication application)
    {
        try { application.CreateRibbonTab(BotPlusSettings.VkTab); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        application.CreateRibbonPanel(BotPlusSettings.VkTab, "ВК");
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
