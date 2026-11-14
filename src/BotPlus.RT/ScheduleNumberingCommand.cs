using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BotPlus.RT;

[Transaction(TransactionMode.Manual)]
public sealed class ScheduleNumberingCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var ui = data.Application.ActiveUIDocument;
            if (ui?.ActiveView is not ViewSchedule schedule)
            {
                TaskDialog.Show("Нумерация", "Откройте спецификацию, которую нужно пронумеровать.");
                return Result.Cancelled;
            }
            var doc = ui.Document;
            ScheduleNumbering.Validate(doc, schedule);
            var candidates = ScheduleNumbering.Elements(doc, schedule);
            var parameters = ScheduleNumbering.Parameters(candidates);
            if (parameters.Count == 0) throw new InvalidOperationException("Нет текстовых или целочисленных параметров экземпляров для нумерации.");
            var selected = ui.Selection.GetElementIds().Select(id => id.Value).ToHashSet();
            var window = new ScheduleNumberingWindow(parameters, selected.Count > 0);
            new WindowInteropHelper(window) { Owner = data.Application.MainWindowHandle };
            if (window.ShowDialog() != true) return Result.Cancelled;
            var chosen = window.Parameter!;
            var rows = ScheduleNumbering.ReadRows(doc, schedule, candidates);
            var result = ScheduleNumbering.Apply(doc, rows, chosen, window.Start, window.SelectedOnly ? selected : null);
            TaskDialog.Show("Нумерация", $"Обновлено элементов: {result.Updated}\nПронумеровано строк: {result.Rows}\nПропущено недоступных параметров: {result.Skipped}" +
                (result.Errors.Count == 0 ? "" : "\n\n" + string.Join("\n", result.Errors)));
            return Result.Succeeded;
        }
        catch (Exception error) { message = error.Message; return Result.Failed; }
    }
}

public sealed class ScheduleNumberingAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication app, CategorySet categories) =>
        app.ActiveUIDocument?.ActiveView is ViewSchedule { IsTemplate: false };
}
