using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace HeatLossViewer.Mep;

[Transaction(TransactionMode.Manual)]
public sealed class EngineeringRulesCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var ui = data.Application.ActiveUIDocument;
            if (ui is null) return Result.Cancelled;
            var doc = ui.Document;
            if (doc.IsFamilyDocument) { TaskDialog.Show("Апдейтер", "Откройте проект."); return Result.Cancelled; }
            var rules = EngineeringRulesStore.Read(doc);
            var picked = ui.Selection.GetElementIds().Select(doc.GetElement).FirstOrDefault(e => e != null && EngineeringEngine.CategoryKey(e) != null);
            var window = new EngineeringRulesWindow(doc, rules, picked is null ? null : EngineeringEngine.CategoryKey(picked),
                picked is null ? null : EngineeringEngine.TypeKey(picked));
            new WindowInteropHelper(window) { Owner = data.Application.MainWindowHandle };
            if (window.ShowDialog() != true) return Result.Cancelled;

            EngineeringOutcome? outcome = null;
            using (var t = new Transaction(doc, "BotPlus: правила инженерных параметров"))
            {
                t.Start();
                EngineeringRulesStore.Write(doc, window.Rules);
                if (window.UpdateScope is { } scope)
                    outcome = EngineeringEngine.ApplyScope(doc, window.Rules, scope.Category, scope.TypeKey);
                // The command already wrote every value; the updater would only re-check them on commit.
                EngineeringUpdater.Suspended = true;
                try { if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit отменил сохранение правил."); }
                finally { EngineeringUpdater.Suspended = false; }
            }
            TaskDialog.Show("Апдейтер", outcome is null ? "Правила сохранены." : "Правила сохранены.\n\n" + outcome.Summary());
            return Result.Succeeded;
        }
        catch (Exception error) { message = error.Message; return Result.Failed; }
    }
}

/// <summary>Re-applies saved rules to the selection, or to the whole project when nothing is selected.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class EngineeringRefreshCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var ui = data.Application.ActiveUIDocument;
            if (ui is null) return Result.Cancelled;
            var doc = ui.Document;
            var rules = EngineeringRulesStore.Read(doc);
            if (rules.Rules.Count == 0) { TaskDialog.Show("Апдейтер", "В проекте нет правил. Настройте их командой «Апдейтер»."); return Result.Cancelled; }
            var selected = ui.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null && EngineeringEngine.CategoryKey(e) != null).ToList();
            IEnumerable<Element> targets = selected.Count > 0 ? selected
                : rules.Rules.Select(r => r.Category).Distinct().SelectMany(c => EngineeringEngine.Instances(doc, c, null));
            var outcome = new EngineeringOutcome();
            using (var t = new Transaction(doc, "BotPlus: обновить инженерные параметры"))
            {
                t.Start();
                foreach (var element in targets.ToList())
                    if (rules.Resolve(EngineeringEngine.CategoryKey(element)!, EngineeringEngine.TypeKey(element)) is { } rule)
                        EngineeringEngine.Apply(element, rule, outcome);
                EngineeringUpdater.Suspended = true;
                try { t.Commit(); } finally { EngineeringUpdater.Suspended = false; }
            }
            TaskDialog.Show("Апдейтер", (selected.Count > 0 ? "Выделенные элементы.\n" : "Весь проект.\n") + outcome.Summary());
            return Result.Succeeded;
        }
        catch (Exception error) { message = error.Message; return Result.Failed; }
    }
}
