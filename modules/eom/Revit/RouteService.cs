using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BotPlus.Eom;

/// <summary>
/// Обозначения проводок ГОСТ 21.210 следуют за трассами без участия пользователя (IUpdater):
/// новому лотку или коробу на активном плане ставится обозначение способа прокладки, выбранного в списке на ленте;
/// при изменении трассы обозначение перестраивается, при удалении — удаляется.
/// </summary>
internal sealed class RouteService : IUpdater
{
    internal static string Current { get; set; } = WiringSymbols.Auto;
    static RouteService? instance;
    readonly UpdaterId id;

    RouteService(AddInId addin) => id = new UpdaterId(addin, new Guid("6F0D3C2B-8E4A-4B7D-A1C9-3E5F7B2D4A10"));

    internal static void Register(UIControlledApplication app)
    {
        instance = new RouteService(app.ActiveAddInId);
        UpdaterRegistry.RegisterUpdater(instance, true);
        var hosts = new ElementMulticategoryFilter(WiringSymbols.HostCategories);
        UpdaterRegistry.AddTrigger(instance.id, hosts, Element.GetChangeTypeElementAddition());
        UpdaterRegistry.AddTrigger(instance.id, hosts, Element.GetChangeTypeGeometry());
        UpdaterRegistry.AddTrigger(instance.id, hosts, Element.GetChangeTypeElementDeletion());
    }

    internal static void Unregister()
    {
        if (instance == null) return;
        try { UpdaterRegistry.UnregisterUpdater(instance.id); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        instance = null;
    }

    public void Execute(UpdaterData data)
    {
        var doc = data.GetDocument();
        var added = data.GetAddedElementIds();
        try { WiringSymbols.Follow(doc, data.GetModifiedElementIds(), data.GetDeletedElementIds()); }
        catch (Exception error) { WiringSymbols.LastError = error.Message; }
        if (Current == WiringSymbols.None || added.Count == 0 || doc.ActiveView is not ViewPlan view || view.IsTemplate) return;
        var types = WiringSymbols.Types(doc); if (types.Count == 0) return;
        foreach (var hostId in added)
        {
            if (doc.GetElement(hostId) is not (CableTray or Conduit) || doc.GetElement(hostId) is not { } host) continue;
            if (WiringSymbols.Resolve(types, WiringSymbols.MethodFor(host, Current)) is not { } symbol) continue;
            // Если Revit не дал поставить обозначение внутри построения трассы, повторяем после команды (Idling) и показываем причину.
            try { WiringSymbols.Place(doc, view, host, symbol); }
            catch (Exception error) { WiringSymbols.LastError = error.Message; pending.Add((doc, view.Id, hostId)); }
        }
    }

    static readonly List<(Document Doc, ElementId View, ElementId Host)> pending = [];
    static (Document Doc, List<ElementId> Ids)? reselect;

    /// <summary>
    /// Щелчок по обозначению проводки выбирает трассу: обозначения лежат поверх лотка и иначе перехватывают выбор.
    /// Сами обозначения закреплены, поэтому перетаскивание их не сдвигает; новый выбор применяется в Idling.
    /// </summary>
    internal static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var doc = e.GetDocument(); var ids = e.GetSelectedElements();
        if (doc == null || ids.Count == 0) return;
        bool redirected = false; var result = new List<ElementId>();
        foreach (var id in ids)
        {
            if (doc.GetElement(id) is FamilyInstance { Symbol.FamilyName: WiringSymbols.Family } symbol && EomStore.WiringHost(symbol) is { } host
                && doc.GetElement(new ElementId(host)) is { } element)
            { redirected = true; if (!result.Contains(element.Id)) result.Add(element.Id); }
            else if (!result.Contains(id)) result.Add(id);
        }
        if (redirected) reselect = (doc, result);
    }


    /// <summary>Повтор неудавшихся обозначений вне построения трассы. Если и так не получилось — одно окно с причиной.</summary>
    internal static void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (reselect is { } r && sender is UIApplication ui && ui.ActiveUIDocument?.Document is { } active && active.Equals(r.Doc))
        {
            reselect = null;
            try { ui.ActiveUIDocument.Selection.SetElementIds(r.Ids); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        }
        if (pending.Count == 0) return;
        var batch = pending.ToList(); pending.Clear();
        var errors = new SortedSet<string>();
        foreach (var group in batch.GroupBy(p => p.Doc))
        {
            var doc = group.Key;
            if (!doc.IsValidObject || doc.IsReadOnly || doc.IsModifiable) continue;
            var types = WiringSymbols.Types(doc);
            using var t = new Transaction(doc, "ЭОМ: обозначения проводок ГОСТ");
            try
            {
                t.Start();
                foreach (var (_, viewId, hostId) in group)
                {
                    if (doc.GetElement(viewId) is not View view || doc.GetElement(hostId) is not { } host) continue;
                    if (WiringSymbols.Resolve(types, WiringSymbols.MethodFor(host, Current)) is not { } symbol) continue;
                    try { WiringSymbols.Place(doc, view, host, symbol); }
                    catch (Exception error) { errors.Add(error.Message); }
                }
                t.Commit();
            }
            catch (Exception error) { errors.Add(error.Message); if (t.HasStarted()) t.RollBack(); }
        }
        if (errors.Count > 0)
        {
            WiringSymbols.LastError = errors.First();
            new TaskDialog("BotPlus ЭОМ") { MainInstruction = "Трасса построена, обозначение проводки не поставлено", MainContent = string.Join("\n", errors) }.Show();
        }
    }

    public UpdaterId GetUpdaterId() => id;
    public ChangePriority GetChangePriority() => ChangePriority.Annotations;
    public string GetUpdaterName() => "BotPlus ЭОМ — обозначения проводок";
    public string GetAdditionalInformation() => "Ставит и перестраивает условные обозначения проводок ГОСТ 21.210 вдоль лотков и коробов.";
}
