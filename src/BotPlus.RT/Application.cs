using System.Reflection;
using Autodesk.Revit.UI;
using BotPlus.Shared;

namespace BotPlus.RT;

/// <summary>One Revit entry point owns the startup and shutdown order of all modules.</summary>
public sealed class Application : IExternalApplication
{
    readonly List<IExternalApplication> started = [];
    RibbonNavigation? ribbon;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            try { application.CreateRibbonTab("BotPlus RT"); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            BotPlusSettings.Load();
            TagCopyOptions.Reset();
            ribbon = new RibbonNavigation(application);
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
                if (BotPlusSettings.HiddenPanels.Contains(panel)) continue;
                var assembly = Assembly.LoadFrom(Path.Combine(root, file));
                var module = (IExternalApplication)Activator.CreateInstance(assembly.GetType(type, throwOnError: true)!)!;
                // Include the failing module so partially registered handlers are cleaned up.
                started.Add(module);
                if (module.OnStartup(application) != Result.Succeeded)
                    throw new InvalidOperationException($"Не удалось загрузить модуль {type}.");
                ribbon.CapturePanels(application, panel);
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
                // The same command on Revit's contextual tab of an open schedule.
                ScheduleContext.Attach(application);
            }
            if (!BotPlusSettings.HiddenPanels.Contains("Виды"))
            {
                var views = application.CreateRibbonPanel("BotPlus RT", "Виды");
                // «Открыть вид» живёт только на контекстной вкладке: появляется при выборе видового экрана или разреза.
                views.AddItem(new PushButtonData("BotPlusViewRange", "Секущий\nдиапазон", typeof(Application).Assembly.Location, typeof(ViewRangeCommand).FullName!)
                    { ToolTip = "Управление секущим диапазоном плана с помощью ползунков: уровень и смещение каждой плоскости, изменения видны сразу.",
                      AvailabilityClassName = typeof(ViewRangeAvailable).FullName! });
                ViewportContext.Attach(application);
            }
            if (!BotPlusSettings.HiddenPanels.Contains("Копирование"))
            {
                var copying = application.CreateRibbonPanel("BotPlus RT", "Копирование");
                copying.AddItem(new PushButtonData("BotPlusCopyFromLink", "Копировать\nиз связи", typeof(Application).Assembly.Location,
                    typeof(CopyFromLinkCommand).FullName!));
                copying.AddItem(new PushButtonData("BotPlusCopyFromProject", "Копировать\nиз проекта", typeof(Application).Assembly.Location,
                    typeof(CopyFromProjectCommand).FullName!));
            }
            ribbon.CapturePanels(application);
            ribbon.Complete();
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
        ribbon?.Dispose();
        ribbon = null;
        StartupUpdates.Detach(application);
        ViewportContext.Detach(application);
        ScheduleContext.Detach(application);
        foreach (var module in started.AsEnumerable().Reverse())
        {
            try { module.OnShutdown(application); }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        }
        started.Clear();
        return Result.Succeeded;
    }
}
