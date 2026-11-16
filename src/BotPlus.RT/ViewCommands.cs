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

/// <summary>
/// «Открыть вид»: открывает виды выбранных видовых экранов листа, спецификаций на листе, разрезов, фасадов и выносок
/// отдельными вкладками. Кнопка появляется на контекстной вкладке только при таком выборе (<see cref="ViewportContext"/>).
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class OpenViewCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        return ui is null ? Result.Cancelled : OpenViews.Run(ui) ? Result.Succeeded : Result.Cancelled;
    }
}

public sealed class OpenViewAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication app, CategorySet categories) => app.ActiveUIDocument != null;
}

internal static class OpenViews
{
    const string Title = "Открыть вид";

    /// <summary>Opens the views behind the selection; on a sheet with nothing suitable selected, asks to pick a viewport.</summary>
    internal static bool Run(UIDocument ui)
    {
        var doc = ui.Document;
        var views = Views(doc, ui.Selection.GetElementIds().Select(doc.GetElement));
        if (views.Count == 0)
        {
            if (ui.ActiveView is not ViewSheet) { TaskDialog.Show(Title, "Выберите видовой экран на листе, разрез, фасад или выносной элемент."); return false; }
            try
            {
                var picked = ui.Selection.PickObject(ObjectType.Element, new ViewportFilter(), "Выберите видовой экран");
                views = Views(doc, [doc.GetElement(picked)]);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return false; }
        }
        if (views.Count == 0) { TaskDialog.Show(Title, "У выбранного элемента нет вида."); return false; }
        foreach (var view in views)
        {
            try { ui.ActiveView = view; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { ui.RequestViewChange(view); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { TaskDialog.Show(Title, $"Вид «{view.Name}» нельзя открыть."); }
        }
        return true;
    }

    internal static bool Suitable(Element? e) => e is Viewport or ScheduleSheetInstance or ElevationMarker || IsViewer(e);

    static bool IsViewer(Element? e) => e?.Category?.BuiltInCategory == BuiltInCategory.OST_Viewers && e is not View;

    /// <summary>
    /// A section, callout or elevation line is a «viewer» element next to its view: Revit creates the view right before it,
    /// so the id one less is checked first, then a view of the same name.
    /// </summary>
    static View? ViewOfViewer(Document doc, Element viewer)
    {
        if (doc.GetElement(new ElementId(viewer.Id.Value - 1)) is View near && !near.IsTemplate && near.Name == viewer.Name) return near;
        return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .FirstOrDefault(v => !v.IsTemplate && v.Name == viewer.Name && v is not ViewSheet && v.ViewType is ViewType.Section or ViewType.Elevation or ViewType.Detail
                or ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.DraftingView or ViewType.ThreeD);
    }

    /// <summary>Distinct views behind the selection; already open views are simply activated.</summary>
    internal static List<View> Views(Document doc, IEnumerable<Element?> selection)
    {
        var result = new List<View>();
        void Add(View? view) { if (view is { IsTemplate: false } && result.All(v => v.Id != view.Id)) result.Add(view); }
        foreach (var e in selection)
        {
            switch (e)
            {
                case Viewport v: Add(doc.GetElement(v.ViewId) as View); break;
                case ScheduleSheetInstance s: Add(doc.GetElement(s.ScheduleId) as View); break;
                case ElevationMarker m:
                    for (int i = 0; i < m.MaximumViewCount; i++)
                        if (!m.IsAvailableIndex(i)) Add(doc.GetElement(m.GetViewId(i)) as View);
                    break;
                case not null when IsViewer(e): Add(ViewOfViewer(doc, e)); break;
            }
        }
        return result;
    }

    sealed class ViewportFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is Viewport or ScheduleSheetInstance;
        public bool AllowReference(Reference reference, XYZ position) => true;
    }
}

/// <summary>
/// «Открыть вид» на контекстной вкладке, как у ModPlus: панель «BotPlus RT» добавляется на открытую контекстную вкладку,
/// только когда выбран видовой экран, спецификация на листе, разрез, фасад или выноска, и убирается при другом выборе.
/// Revit API не даёт доступа к контекстным вкладкам — панель добавляется через AdWindows, команда выполняется через ExternalEvent.
/// </summary>
internal static class ViewportContext
{
    internal const string ButtonName = "BotPlusOpenView";
    const string PanelId = "BotPlusRT.ViewportTools";
    static ExternalEvent? open;
    static bool dirty, wanted;

    internal static void Attach(UIControlledApplication app)
    {
        open ??= ExternalEvent.Create(new OpenHandler());
        app.Idling += OnIdling;
        app.SelectionChanged += OnSelectionChanged;
    }

    internal static void Detach(UIControlledApplication app)
    {
        app.Idling -= OnIdling;
        app.SelectionChanged -= OnSelectionChanged;
        try { Sync(false); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
    }

    static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            var doc = e.GetDocument();
            bool now = e.GetSelectedElements().Select(doc.GetElement).Any(OpenViews.Suitable);
            if (now != wanted || now) { wanted = now; dirty = true; }
        }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
    }

    static void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (!dirty) return;
        dirty = false;
        try { Sync(wanted); }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine("BotPlus viewport tab: " + error); }
    }

    /// <summary>Panel on the visible contextual tabs while the selection is suitable, removed everywhere otherwise.</summary>
    static void Sync(bool show)
    {
        var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
        if (ribbon is null) return;
        var targets = show ? ribbon.Tabs.Where(t => t.IsContextualTab && t.IsVisible).ToList() : [];
        if (show && targets.Count == 0 && ribbon.ActiveTab is { } active) targets.Add(active);
        foreach (var tab in ribbon.Tabs)
        {
            var mine = tab.Panels.Where(p => p.Source?.Id == PanelId).ToList();
            bool keep = targets.Contains(tab);
            if (!keep) foreach (var p in mine) tab.Panels.Remove(p);
            else if (mine.Count == 0) tab.Panels.Add(Panel());
        }
    }

    static Autodesk.Windows.RibbonPanel Panel()
    {
        var button = new Autodesk.Windows.RibbonButton
        {
            Id = PanelId + ".OpenView", Text = "Открыть\nвид", ShowText = true, ShowImage = true,
            Size = Autodesk.Windows.RibbonItemSize.Large, Orientation = System.Windows.Controls.Orientation.Vertical,
            LargeImage = RibbonStyle.Icon(ButtonName, 32), Image = RibbonStyle.Icon(ButtonName, 16),
            ToolTip = "Открыть вид выбранного видового экрана, разреза, фасада или выноски в отдельной вкладке.",
            CommandHandler = new RaiseOpen()
        };
        var source = new Autodesk.Windows.RibbonPanelSource { Id = PanelId, Title = "BotPlus RT" };
        source.Items.Add(button);
        return new Autodesk.Windows.RibbonPanel { Source = source };
    }

    sealed class RaiseOpen : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => open?.Raise();
    }

    sealed class OpenHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            try { if (app.ActiveUIDocument is { } ui) OpenViews.Run(ui); }
            catch (Exception error) { TaskDialog.Show("Открыть вид", error.Message); }
        }
        public string GetName() => "BotPlus RT: открыть вид";
    }
}
