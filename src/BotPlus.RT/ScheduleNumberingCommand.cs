using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BotPlus.RT;

[Transaction(TransactionMode.Manual)]
public sealed class ScheduleNumberingCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try { return Run(data.Application); }
        catch (Exception error)
        {
            TaskDialog.Show("Нумерация", error.Message);
            return Result.Cancelled;
        }
    }

    /// <summary>
    /// Numbering of the active schedule: rows in the table's order get start, start+1, …; with «Для выделенных элементов» only rows
    /// of the selected elements. A row whose parameter cannot be written is skipped and does not take a number.
    /// </summary>
    internal static Result Run(UIApplication app)
    {
        var ui = app.ActiveUIDocument;
        if (ui?.ActiveView is not ViewSchedule schedule)
        {
            TaskDialog.Show("Нумерация", "Откройте спецификацию, которую нужно пронумеровать.");
            return Result.Cancelled;
        }
        var doc = ui.Document;
        ScheduleNumbering.Validate(doc, schedule);
        var candidates = ScheduleNumbering.Elements(doc, schedule);
        var parameters = ScheduleNumbering.Parameters(doc, schedule, candidates);
        if (parameters.Count == 0) throw new InvalidOperationException("В спецификации нет доступных для записи текстовых или целочисленных полей экземпляра.");
        var selection = ui.Selection.GetElementIds().Select(id => id.Value).ToHashSet();
        var selected = candidates.Select(e => e.Id.Value).Where(selection.Contains).ToHashSet();
        var window = new ScheduleNumberingWindow(parameters, selected.Count > 0, ScheduleNumbering.SortParameters(schedule));
        new WindowInteropHelper(window) { Owner = app.MainWindowHandle };
        if (window.ShowDialog() != true) return Result.Cancelled;
        var chosen = window.Parameter!;
        if (window.Clear)
        {
            ScheduleNumbering.Clear(doc, candidates, chosen, selected);
            return Result.Succeeded;
        }
        var scope = window.SelectedOnly ? selected : null;
        var rows = ScheduleNumbering.ReadRows(doc, schedule, candidates, chosen, window.SortParameter, scope);
        var result = ScheduleNumbering.Apply(doc, rows, chosen, window.Start, scope);
        if (result.Skipped > 0)
            TaskDialog.Show("Нумерация", $"Пронумеровано элементов: {result.Updated}. Пропущено: {result.Skipped}." +
                (result.Errors.Count > 0 ? "\n" + string.Join("\n", result.Errors) : ""));
        return Result.Succeeded;
    }
}

public sealed class ScheduleNumberingAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication app, CategorySet categories) =>
        app.ActiveUIDocument?.ActiveView is ViewSchedule { IsTemplate: false };
}

/// <summary>
/// «Нумерация» on Revit's contextual tab «Изменить спецификацию/количество»: the panel is added while a schedule is the active
/// view and removed when another view becomes active.
/// </summary>
internal static class ScheduleContext
{
    internal const string ButtonName = "BotPlusScheduleNumbering";
    const string PanelId = "BotPlusRT.ScheduleTools";
    static ExternalEvent? run;
    static long nextSync;

    internal static void Attach(UIControlledApplication app)
    {
        run ??= ExternalEvent.Create(new RunHandler());
        app.Idling += OnIdling;
        nextSync = 0;
    }

    internal static void Detach(UIControlledApplication app)
    {
        app.Idling -= OnIdling;
        try { Sync(false); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
    }

    static void OnIdling(object? sender, IdlingEventArgs e)
    {
        // Revit can recreate contextual tabs after ViewActivated, including on row selection.
        // Read the actual active view and keep retrying instead of missing a late-created tab.
        long now = Environment.TickCount64;
        if (now < nextSync) return;
        nextSync = now + 250;
        try { Sync((sender as UIApplication)?.ActiveUIDocument?.ActiveView is ViewSchedule { IsTemplate: false }); }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine("BotPlus schedule tab: " + error); }
    }

    static bool IsScheduleTab(Autodesk.Windows.RibbonTab tab) =>
        tab.Title?.StartsWith("Изменить спецификаци", StringComparison.OrdinalIgnoreCase) == true ||
        tab.Title?.StartsWith("Modify Schedule", StringComparison.OrdinalIgnoreCase) == true ||
        (tab.IsContextualTab && tab.Id?.Contains("Schedule", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>True when the panel is where it should be.</summary>
    static bool Sync(bool show)
    {
        var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
        if (ribbon is null) return false;
        var targets = show ? ribbon.Tabs.Where(IsScheduleTab).ToList() : [];
        foreach (var tab in ribbon.Tabs)
        {
            var mine = tab.Panels.Where(p => p.Source?.Id == PanelId).ToList();
            if (!targets.Contains(tab)) foreach (var p in mine) tab.Panels.Remove(p);
            else if (mine.Count == 0) tab.Panels.Add(Panel());
        }
        return !show || targets.Count > 0;
    }

    static Autodesk.Windows.RibbonPanel Panel()
    {
        var button = new Autodesk.Windows.RibbonButton
        {
            Id = PanelId + ".Numbering", Text = "Нумерация", ShowText = true, ShowImage = true,
            Size = Autodesk.Windows.RibbonItemSize.Large, Orientation = System.Windows.Controls.Orientation.Vertical,
            LargeImage = RibbonStyle.Icon(ButtonName, 32), Image = RibbonStyle.Icon(ButtonName, 16),
            ToolTip = "Пронумеровать строки спецификации по порядку. Выделите строки, чтобы пронумеровать только их.",
            CommandHandler = new RaiseRun()
        };
        var source = new Autodesk.Windows.RibbonPanelSource { Id = PanelId, Title = "BotPlus RT" };
        source.Items.Add(button);
        return new Autodesk.Windows.RibbonPanel { Source = source };
    }

    sealed class RaiseRun : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run?.Raise();
    }

    sealed class RunHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            try { ScheduleNumberingCommand.Run(app); }
            catch (Exception error) { TaskDialog.Show("Нумерация", error.Message); }
        }
        public string GetName() => "BotPlus RT: нумерация спецификации";
    }
}
