using System.Globalization;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using View = Autodesk.Revit.DB.View;

namespace HeatLossViewer.Mep;

/// <summary>
/// «Нумерация стояков» — нативная замена скриптов Dynamo ADSK «Нумерация стояков автоматическая» и «по заданному значению».
/// Трубы активного вида (или выделенные) → вертикальные → стояки по точке плана → номер в параметр каждой трубы стояка.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class RiserNumberingCommand : IExternalCommand
{
    const double MmPerFoot = 304.8;

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var ui = data.Application.ActiveUIDocument;
            if (ui is null) return Result.Cancelled;
            var doc = ui.Document;
            if (doc.IsFamilyDocument) { TaskDialog.Show("Нумерация стояков", "Откройте проект."); return Result.Cancelled; }
            var selected = ui.Selection.GetElementIds().Select(doc.GetElement).OfType<Pipe>().ToList();
            var inView = ViewPipes(doc, ui.ActiveView);
            if (inView.Count == 0 && selected.Count == 0) { TaskDialog.Show("Нумерация стояков", "Трубы не найдены: на активном виде нет труб."); return Result.Cancelled; }

            var sample = selected.FirstOrDefault() ?? inView[0];
            var parameters = sample.Parameters.Cast<Parameter>()
                .Where(p => !p.IsReadOnly && p.StorageType is StorageType.String or StorageType.Integer && p.Definition is { } d && d.GetDataType() != SpecTypeId.Boolean.YesNo)
                .Select(p => p.Definition.Name).Distinct().OrderBy(n => n, StringComparer.CurrentCulture).ToList();
            var window = new RiserNumberingWindow(parameters, selected.Count > 0);
            new WindowInteropHelper(window) { Owner = data.Application.MainWindowHandle };
            if (window.ShowDialog() != true) return Result.Cancelled;

            string name = window.ParameterName;
            var source = window.SelectedOnly ? selected : inView;
            var byId = source.ToDictionary(p => p.Id.Value);
            var risers = RiserNumberingPolicy.Group(source.Select(p => Describe(p, name)).OfType<RiserPipe>(), window.MinLengthMm, window.ConsiderSystem || window.ConsiderNeighbors);
            int individualRisers = risers.Count;
            if (window.ConsiderNeighbors) risers = RiserNumberingPolicy.Neighbors(risers, window.NeighborDistanceMm);
            if (risers.Count == 0)
            {
                TaskDialog.Show("Нумерация стояков", source.Any(p => Describe(p, name) != null)
                    ? $"Стояки не найдены: нет стояков длиной от {window.MinLengthMm:0.#} мм."
                    : "Стояки не найдены: среди труб нет вертикальных.");
                return Result.Cancelled;
            }

            int changed = 0, same = 0, missing = 0;
            var problems = new List<ElementId>();
            RiserCopyPlan? plan = null;
            using (var t = new Transaction(doc, "BotPlus: нумерация стояков"))
            {
                t.Start();
                if (window.Auto)
                {
                    foreach (var (id, number) in RiserNumberingPolicy.Auto(risers, window.Start, window.ConsiderSystem))
                        Write(byId[id], name, number.ToString(CultureInfo.InvariantCulture), ref changed, ref same, ref missing);
                }
                else
                {
                    plan = RiserNumberingPolicy.Copy(risers);
                    foreach (var (id, value) in plan.Writes) Write(byId[id], name, value, ref changed, ref same, ref missing);
                    problems.AddRange(plan.Empty.Concat(plan.Conflicts.Select(c => c.Riser)).SelectMany(r => r.Pipes).Select(p => new ElementId(p.Id)));
                }
                if (changed == 0) t.RollBack();
                else if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit отменил запись номеров.");
            }

            var report = new List<string> { $"Стояков: {individualRisers}", $"Труб изменено: {changed}" };
            if (window.ConsiderNeighbors) report.Add($"Групп соседних стояков: {risers.Count} (между осями до {window.NeighborDistanceMm:0.#} мм)");
            if (window.Auto) report.Add(window.ConsiderSystem ? "Нумерация отдельно по типам систем." : "Сквозная нумерация всех систем.");
            if (same > 0) report.Add($"Уже с этим номером: {same}");
            if (missing > 0) report.Add($"Пропущено труб без параметра «{name}» или с недоступным для записи: {missing}");
            if (plan != null)
            {
                if (plan.Empty.Count > 0) report.Add($"Стояков без номера ни в одном сегменте: {plan.Empty.Count}");
                if (plan.Conflicts.Count > 0)
                    report.Add($"Стояков с разными номерами (не изменены): {plan.Conflicts.Count} — " +
                        string.Join("; ", plan.Conflicts.Take(5).Select(c => string.Join(" / ", c.Values))) + (plan.Conflicts.Count > 5 ? "; …" : ""));
                if (problems.Count > 0) { ui.Selection.SetElementIds(problems); report.Add("Трубы этих стояков выделены."); }
            }
            TaskDialog.Show("Нумерация стояков", string.Join("\n", report));
            return Result.Succeeded;
        }
        catch (Exception error) { message = error.Message; return Result.Failed; }
    }

    /// <summary>Трубы, видимые на активном виде; у неграфического вида (спецификация, лист) — трубы проекта.</summary>
    static List<Pipe> ViewPipes(Document doc, View view)
    {
        bool graphic = view is not ViewSchedule && view.ViewType is not (ViewType.DrawingSheet or ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal or ViewType.Undefined);
        var collector = graphic ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(doc);
        return collector.OfClass(typeof(Pipe)).Cast<Pipe>().ToList();
    }

    /// <summary>Вертикальная труба для группировки; null — не вертикальная или без осевой линии.</summary>
    static RiserPipe? Describe(Pipe pipe, string parameter)
    {
        if (pipe.Location is not LocationCurve { Curve: Line line }) return null;
        XYZ a = line.GetEndPoint(0) * MmPerFoot, b = line.GetEndPoint(1) * MmPerFoot;
        if (!RiserNumberingPolicy.IsVertical(b.X - a.X, b.Y - a.Y, b.Z - a.Z)) return null;
        var typeId = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
        string systemType = typeId != null && typeId != ElementId.InvalidElementId ? typeId.Value.ToString(CultureInfo.InvariantCulture) : "";
        return new RiserPipe(pipe.Id.Value, (a.X + b.X) / 2, (a.Y + b.Y) / 2, Math.Min(a.Z, b.Z), line.Length * MmPerFoot, Read(pipe.LookupParameter(parameter)), systemType);
    }

    static string Read(Parameter? p) => p switch
    {
        null => "",
        { StorageType: StorageType.String } => p.AsString() ?? "",
        { StorageType: StorageType.Integer } => p.HasValue ? p.AsInteger().ToString(CultureInfo.InvariantCulture) : "",
        _ => p.AsValueString() ?? "",
    };

    /// <summary>Записывает номер; неизменённые значения не перезаписываются, недоступный параметр пропускается.</summary>
    static void Write(Pipe pipe, string parameter, string value, ref int changed, ref int same, ref int missing)
    {
        var p = pipe.LookupParameter(parameter);
        if (p is null || p.IsReadOnly) { missing++; return; }
        if (Read(p) == value) { same++; return; }
        bool ok = p.StorageType switch
        {
            StorageType.String => p.Set(value),
            StorageType.Integer => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && p.Set(n),
            _ => false,
        };
        if (ok) changed++; else missing++;
    }
}
