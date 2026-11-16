using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.UI.Selection;

namespace BotPlus.RT;

/// <summary>«Секущий диапазон» активного плана: уровень и смещение четырёх плоскостей, применение сразу, «Принять» — один Undo.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class ViewRangeCommand : IExternalCommand
{
    const string Title = "Секущий диапазон";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var ui = data.Application.ActiveUIDocument;
            if (ui?.ActiveView is not ViewPlan view || view.IsTemplate) { TaskDialog.Show(Title, "Откройте план."); return Result.Cancelled; }
            var doc = ui.Document;
            string? note = null;
            ViewPlan target = view;
            // A dependent view shares the range of its primary view.
            if (view.GetPrimaryViewId() is { } primaryId && primaryId != ElementId.InvalidElementId && doc.GetElement(primaryId) is ViewPlan primary)
            { target = primary; note = $"Зависимый вид: диапазон меняется у основного вида «{primary.Name}»."; }
            if (target.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(target.ViewTemplateId) is ViewPlan template
                && !template.GetNonControlledTemplateParameterIds().Contains(new ElementId(BuiltInParameter.PLAN_VIEW_RANGE)))
            {
                var ask = new TaskDialog(Title)
                {
                    MainInstruction = $"Секущим диапазоном управляет шаблон «{template.Name}»",
                    MainContent = "Изменение шаблона затронет все виды, которые его используют.",
                    CommonButtons = TaskDialogCommonButtons.Cancel
                };
                ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Изменить диапазон в шаблоне");
                if (ask.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;
                target = template; note = $"Изменяется шаблон «{template.Name}».";
            }
            var genLevel = view.GenLevel ?? throw new InvalidOperationException("У плана нет связанного уровня.");
            double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(l => new LevelInfo(l.Id.Value, l.Name, Mm(l.Elevation))).ToList();
            var associated = levels.First(l => l.Id == genLevel.Id.Value);

            var range = target.GetViewRange();
            var rows = ViewRangePolicy.Planes.Select(plane =>
            {
                var p = Plane(plane);
                var id = range.GetLevelId(p);
                var kind = id == PlanViewRange.Current ? LevelKind.Associated : id == PlanViewRange.LevelAbove ? LevelKind.Above
                    : id == PlanViewRange.LevelBelow ? LevelKind.Below : id == PlanViewRange.Unlimited ? LevelKind.Unlimited
                    : id == genLevel.Id ? LevelKind.Associated : LevelKind.Level;
                return new RangeRow { Plane = plane, Level = new LevelChoice(kind, kind == LevelKind.Level ? id.Value : 0, ""), OffsetMm = Math.Round(Mm(range.GetOffset(p)), 2) };
            }).ToList();

            using var group = new TransactionGroup(doc, "BotPlus: секущий диапазон");
            group.Start();
            var window = new ViewRangeWindow(rows, levels, associated, note);
            new WindowInteropHelper(window) { Owner = data.Application.MainWindowHandle };
            window.Apply = changed =>
            {
                using var t = new Transaction(doc, "BotPlus: секущий диапазон");
                try
                {
                    t.Start();
                    var r = target.GetViewRange();
                    foreach (var row in changed)
                    {
                        var p = Plane(row.Plane);
                        r.SetLevelId(p, row.Level.Kind switch
                        {
                            LevelKind.Associated => row.Plane == RangePlane.Cut ? PlanViewRange.Current : genLevel.Id,
                            LevelKind.Above => PlanViewRange.LevelAbove,
                            LevelKind.Below => PlanViewRange.LevelBelow,
                            LevelKind.Unlimited => PlanViewRange.Unlimited,
                            _ => new ElementId(row.Level.LevelId)
                        });
                        if (row.Level.Kind != LevelKind.Unlimited) r.SetOffset(p, UnitUtils.ConvertToInternalUnits(row.OffsetMm, UnitTypeId.Millimeters));
                    }
                    target.SetViewRange(r);
                    if (t.Commit() != TransactionStatus.Committed) return "Revit отклонил диапазон.";
                    ui.RefreshActiveView();
                    return null;
                }
                catch (Exception error)
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    return "Revit отклонил диапазон: " + error.Message;
                }
            };
            bool accepted = window.ShowDialog() == true;
            if (accepted && window.Applied > 0) group.Assimilate(); else group.RollBack();
            return accepted ? Result.Succeeded : Result.Cancelled;
        }
        catch (Exception error) { message = error.Message; return Result.Failed; }
    }

    static PlanViewPlane Plane(RangePlane plane) => plane switch
    {
        RangePlane.Top => PlanViewPlane.TopClipPlane,
        RangePlane.Cut => PlanViewPlane.CutPlane,
        RangePlane.Bottom => PlanViewPlane.BottomClipPlane,
        _ => PlanViewPlane.ViewDepthPlane
    };
}

public sealed class ViewRangeAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication app, CategorySet categories) => app.ActiveUIDocument?.ActiveView is ViewPlan { IsTemplate: false };
}

/// <summary>«Открыть вид»: открывает виды выбранных видовых экранов листа (и спецификаций на листе) отдельными вкладками.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class OpenViewCommand : IExternalCommand
{
    const string Title = "Открыть вид";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        if (ui is null) return Result.Cancelled;
        var doc = ui.Document;
        var views = Views(doc, ui.Selection.GetElementIds().Select(doc.GetElement));
        if (views.Count == 0)
        {
            if (ui.ActiveView is not ViewSheet) { TaskDialog.Show(Title, "Выберите видовой экран на листе."); return Result.Cancelled; }
            try
            {
                var picked = ui.Selection.PickObject(ObjectType.Element, new ViewportFilter(), "Выберите видовой экран");
                views = Views(doc, [doc.GetElement(picked)]);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        }
        if (views.Count == 0) { TaskDialog.Show(Title, "У выбранного видового экрана нет вида."); return Result.Cancelled; }
        foreach (var view in views)
        {
            try { ui.ActiveView = view; }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { TaskDialog.Show(Title, $"Вид «{view.Name}» нельзя открыть."); }
        }
        return Result.Succeeded;
    }

    /// <summary>Distinct views behind viewports and schedule instances; already open views are simply activated.</summary>
    internal static List<View> Views(Document doc, IEnumerable<Element?> selection)
    {
        var result = new List<View>();
        foreach (var e in selection)
        {
            var id = e switch { Viewport v => v.ViewId, ScheduleSheetInstance s => s.ScheduleId, _ => ElementId.InvalidElementId };
            if (id != ElementId.InvalidElementId && doc.GetElement(id) is View view && result.All(v => v.Id != view.Id)) result.Add(view);
        }
        return result;
    }

    sealed class ViewportFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is Viewport or ScheduleSheetInstance;
        public bool AllowReference(Reference reference, XYZ position) => true;
    }
}

public sealed class OpenViewAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication app, CategorySet categories) => app.ActiveUIDocument != null;
}

/// <summary>
/// Puts «Открыть вид» on Revit's contextual tab «Изменить | Видовые экраны» (as ModPlus does). The Revit API has no access to
/// contextual tabs, so the button is added through AdWindows when such a tab appears; it posts the regular ribbon command.
/// </summary>
internal static class ViewportContext
{
    internal const string ButtonName = "BotPlusOpenView";
    const string PanelId = "BotPlusRT.ViewportTools";
    static UIApplication? ui;
    static bool pending = true;

    internal static void Attach(UIControlledApplication app)
    {
        app.Idling += OnIdling;
        app.SelectionChanged += OnSelectionChanged;
    }

    internal static void Detach(UIControlledApplication app)
    {
        app.Idling -= OnIdling;
        app.SelectionChanged -= OnSelectionChanged;
    }

    static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try { if (e.GetSelectedElements().Select(e.GetDocument().GetElement).Any(x => x is Viewport or ScheduleSheetInstance)) pending = true; }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
    }

    static void OnIdling(object? sender, IdlingEventArgs e)
    {
        ui ??= sender as UIApplication;
        if (!pending) return;
        pending = false;
        try { Ensure(); }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine("BotPlus viewport tab: " + error); }
    }

    static bool ViewportTab(Autodesk.Windows.RibbonTab tab)
    {
        string text = $"{tab.Title} {tab.Name} {tab.Id}";
        return tab.IsContextualTab && (text.Contains("Видовые экраны", StringComparison.CurrentCultureIgnoreCase) || text.Contains("Viewport", StringComparison.OrdinalIgnoreCase));
    }

    static void Ensure()
    {
        var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
        if (ribbon is null) return;
        foreach (var tab in ribbon.Tabs.Where(ViewportTab))
        {
            if (tab.Panels.Any(p => p.Source?.Id == PanelId)) continue;
            var button = new Autodesk.Windows.RibbonButton
            {
                Id = PanelId + ".OpenView", Text = "Открыть\nвид", ShowText = true, ShowImage = true,
                Size = Autodesk.Windows.RibbonItemSize.Large, Orientation = System.Windows.Controls.Orientation.Vertical,
                LargeImage = RibbonStyle.Icon(ButtonName, 32), Image = RibbonStyle.Icon(ButtonName, 16),
                ToolTip = "Открыть вид выбранного видового экрана в отдельной вкладке.",
                CommandHandler = new PostOpenView()
            };
            var source = new Autodesk.Windows.RibbonPanelSource { Id = PanelId, Title = "BotPlus RT" };
            source.Items.Add(button);
            tab.Panels.Add(new Autodesk.Windows.RibbonPanel { Source = source });
        }
    }

    /// <summary>Runs the ribbon command through Revit's own queue: the external command gets a proper API context.</summary>
    sealed class PostOpenView : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            var id = RevitCommandId.LookupCommandId($"CustomCtrl_%CustomCtrl_%BotPlus RT%Виды%{ButtonName}");
            if (ui != null && id != null && ui.CanPostCommand(id)) ui.PostCommand(id);
            else TaskDialog.Show("Открыть вид", "Команда недоступна: включите панель «Виды» в настройках BotPlus RT.");
        }
    }
}
