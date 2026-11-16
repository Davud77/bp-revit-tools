using System.Diagnostics;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace HeatLossViewer.Mep;

/// <summary>Настройки изоляции хранятся в проекте (ProjectInformation), как и правила апдейтера.</summary>
internal static class InsulationStore
{
    static readonly Guid SchemaId = new("8c2d6a14-3f7b-4e19-a5d0-6b9e2f41c7a8");
    static string? lastJson;
    static InsulationSettings last = new();

    internal static InsulationSettings Read(Document doc)
    {
        var schema = Schema.Lookup(SchemaId);
        if (schema is null) return new InsulationSettings();
        var entity = doc.ProjectInformation.GetEntity(schema);
        string? json = entity.IsValid() ? entity.Get<string>(schema.GetField("Json")) : null;
        if (json != lastJson) { last = InsulationSettings.FromJson(json); lastJson = json; }
        return last;
    }

    /// <summary>Call in an open transaction.</summary>
    internal static void Write(Document doc, InsulationSettings settings)
    {
        settings.Validate();
        var schema = Schema.Lookup(SchemaId);
        if (schema is null)
        {
            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName("BotPlusRTPipeInsulationV1");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("Json", typeof(string));
            schema = builder.Finish();
        }
        var entity = new Entity(schema);
        entity.Set(schema.GetField("Json"), settings.ToJson());
        doc.ProjectInformation.SetEntity(entity);
    }
}

internal sealed class InsulationOutcome
{
    internal int Created, Updated, Unchanged;
    internal readonly Dictionary<string, int> Skipped = [];
    internal void Skip(string reason) => Skipped[reason] = Skipped.GetValueOrDefault(reason) + 1;

    internal string Summary()
    {
        var text = $"Построено изоляции: {Created}\nИзменён тип или толщина: {Updated}\nБез изменений: {Unchanged}";
        if (Skipped.Count > 0)
            text += "\n\nПропущено:\n" + string.Join("\n", Skipped.OrderByDescending(p => p.Value).Select(p => $"• {p.Key}: {p.Value}"));
        return text;
    }
}

internal static class InsulationEngine
{
    internal const string NameParameter = "ADSK_Наименование";
    internal static readonly BuiltInCategory[] Categories = [BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory];

    /// <summary>Ключ строки окна: ADSK_Наименование трубы, без него — «Семейство: Тип».</summary>
    internal static string Key(Element element)
    {
        var type = element.Document.GetElement(element.GetTypeId()) as ElementType;
        string? name = (element.LookupParameter(NameParameter) ?? type?.LookupParameter(NameParameter))?.AsString()?.Trim();
        if (!string.IsNullOrEmpty(name)) return name;
        return type is null ? element.Name : string.IsNullOrEmpty(type.FamilyName) ? type.Name : type.FamilyName + ": " + type.Name;
    }

    internal static string? SystemType(Element element) =>
        element.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM) is { StorageType: StorageType.ElementId } p && element.Document.GetElement(p.AsElementId()) is ElementType t ? t.Name : null;

    internal static double DiameterMm(Element element) =>
        element.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM) is { HasValue: true } p ? UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters) : 0;

    internal static PipeInsulation? Existing(Document doc, ElementId host) =>
        InsulationLiningBase.GetInsulationIds(doc, host).Select(doc.GetElement).OfType<PipeInsulation>().FirstOrDefault();

    internal static IEnumerable<Pipe> Pipes(Document doc) => new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>();

    static bool IsFitting(Element e) => e.Category?.BuiltInCategory is BuiltInCategory.OST_PipeFitting or BuiltInCategory.OST_PipeAccessory;

    /// <summary>
    /// Правило элемента. Труба — по своей системе и наименованию; фитинг и арматура — по соединённой трубе
    /// (через соседние фитинги, до 4 шагов), при нескольких — по трубе наибольшего диаметра.
    /// </summary>
    internal static InsulationRule? RuleFor(Element element, InsulationSettings settings)
    {
        if (element is Pipe) return settings.Resolve(SystemType(element), Key(element));
        if (!settings.Fittings || !IsFitting(element)) return null;
        var seen = new HashSet<ElementId> { element.Id };
        var front = new List<Element> { element };
        InsulationRule? best = null; double bestD = -1;
        for (int depth = 0; depth < 4 && front.Count > 0 && best is null; depth++)
        {
            var next = new List<Element>();
            foreach (var e in front)
                foreach (var c in Mep.Connectors(e))
                    foreach (var n in Mep.Neighbors(c))
                    {
                        var owner = n.Owner;
                        if (!seen.Add(owner.Id)) continue;
                        if (owner is Pipe pipe)
                        {
                            double d = DiameterMm(pipe);
                            if (d > bestD && settings.Resolve(SystemType(pipe), Key(pipe)) is { } rule) { best = rule; bestD = d; }
                        }
                        else if (IsFitting(owner)) next.Add(owner);
                    }
            front = next;
        }
        return best;
    }

    /// <summary>Типы изоляции труб проекта по имени.</summary>
    internal static Dictionary<string, ElementId> Types(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(PipeInsulationType)).Cast<ElementType>()
            .GroupBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Диаметр для наименования изоляции: у трубы — её диаметр, у фитинга и арматуры — наибольший коннектор.</summary>
    static double NameDiameter(Element element) => element is Pipe ? DiameterMm(element) : EngineeringEngine.SizeMm(element) ?? 0;

    /// <summary>ADSK_Наименование изоляции (параметр экземпляра); true — значение изменено.</summary>
    static bool WriteName(PipeInsulation insulation, Element host, InsulationRule rule, InsulationOutcome outcome)
    {
        if (InsulationPolicy.InsulationName(rule.NameTemplate, NameDiameter(host)) is not { } name) return false;
        var p = insulation.LookupParameter(NameParameter);
        if (p is not { StorageType: StorageType.String } || p.IsReadOnly) { outcome.Skip("у изоляции нет изменяемого параметра экземпляра " + NameParameter); return false; }
        if (p.AsString() == name) return false;
        p.Set(name);
        return true;
    }

    internal static void Process(Element element, InsulationSettings settings, Dictionary<string, ElementId> types, InsulationOutcome outcome)
    {
        var rule = RuleFor(element, settings);
        if (rule?.InsulationType is null) return;
        if (!types.TryGetValue(rule.InsulationType, out var typeId)) { outcome.Skip($"нет типа изоляции «{rule.InsulationType}»"); return; }
        var doc = element.Document;
        var existing = Existing(doc, element.Id);
        string? currentType = existing is null ? null : doc.GetElement(existing.GetTypeId())?.Name;
        double? currentMm = existing is null ? null : UnitUtils.ConvertFromInternalUnits(existing.Thickness, UnitTypeId.Millimeters);
        double ft = UnitUtils.ConvertToInternalUnits(rule.ThicknessMm, UnitTypeId.Millimeters);
        try
        {
            switch (InsulationPolicy.Decide(rule, currentType, currentMm))
            {
                case InsulationAction.Create:
                    WriteName(PipeInsulation.Create(doc, element.Id, typeId, ft), element, rule, outcome);
                    outcome.Created++;
                    break;
                case InsulationAction.Update:
                    if (existing!.GetTypeId() != typeId) existing.ChangeTypeId(typeId);
                    if (Math.Abs(existing.Thickness - ft) > 1e-6) existing.Thickness = ft;
                    WriteName(existing, element, rule, outcome);
                    outcome.Updated++;
                    break;
                case InsulationAction.Keep:
                    if (WriteName(existing!, element, rule, outcome)) outcome.Updated++; else outcome.Unchanged++;
                    break;
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException error) { outcome.Skip("Revit не построил изоляцию: " + error.Message.Trim()); }
    }

    /// <summary>Весь проект: трубы, затем фитинги и арматура (их правило зависит от уже обработанных труб).</summary>
    internal static InsulationOutcome ApplyAll(Document doc, InsulationSettings settings)
    {
        var outcome = new InsulationOutcome();
        var types = Types(doc);
        foreach (var category in Categories)
        {
            if (category != BuiltInCategory.OST_PipeCurves && !settings.Fittings) continue;
            foreach (var e in new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType().ToList())
                Process(e, settings, types, outcome);
        }
        return outcome;
    }
}

/// <summary>Новые и изменённые трубы, фитинги и арматура сразу получают изоляцию по правилам проекта.</summary>
internal sealed class InsulationUpdater : IUpdater
{
    static readonly Guid UpdaterGuid = new("3e71c0a9-6d24-4b8f-92a5-7f0d4c1e6b53");
    internal static bool Suspended;
    readonly UpdaterId id;

    InsulationUpdater(AddInId addIn) { id = new UpdaterId(addIn, UpdaterGuid); }

    internal static void Register(AddInId addIn)
    {
        var updater = new InsulationUpdater(addIn);
        if (UpdaterRegistry.IsUpdaterRegistered(updater.id)) return;
        UpdaterRegistry.RegisterUpdater(updater, isOptional: true);
        var filter = new ElementMulticategoryFilter(InsulationEngine.Categories);
        UpdaterRegistry.AddTrigger(updater.id, filter, Element.GetChangeTypeElementAddition());
        UpdaterRegistry.AddTrigger(updater.id, filter, Element.GetChangeTypeAny());
    }

    internal static void Unregister(AddInId addIn)
    {
        var updaterId = new UpdaterId(addIn, UpdaterGuid);
        if (UpdaterRegistry.IsUpdaterRegistered(updaterId)) UpdaterRegistry.UnregisterUpdater(updaterId);
    }

    public void Execute(UpdaterData data)
    {
        if (Suspended) return;
        try
        {
            var doc = data.GetDocument();
            var settings = InsulationStore.Read(doc);
            if (!settings.Auto || settings.IsEmpty) return;
            var types = InsulationEngine.Types(doc);
            var outcome = new InsulationOutcome();
            var pipes = new List<Element>();
            var fittings = new Dictionary<ElementId, Element>();
            foreach (var elementId in data.GetAddedElementIds().Concat(data.GetModifiedElementIds()))
            {
                if (doc.GetElement(elementId) is not { } element) continue;
                if (element is Pipe)
                {
                    pipes.Add(element);
                    // A pipe that changed system or name changes the rule of its fittings.
                    if (settings.Fittings)
                        foreach (var c in Mep.Connectors(element))
                            foreach (var n in Mep.Neighbors(c))
                                if (n.Owner is FamilyInstance f) fittings.TryAdd(f.Id, f);
                }
                else fittings.TryAdd(element.Id, element);
            }
            foreach (var pipe in pipes) InsulationEngine.Process(pipe, settings, types, outcome);
            foreach (var fitting in fittings.Values) InsulationEngine.Process(fitting, settings, types, outcome);
        }
        catch (Exception error) { Trace.WriteLine("BotPlus insulation updater: " + error); }
    }

    public UpdaterId GetUpdaterId() => id;
    // After the engineering updater (MEPSystems), so ADSK_Наименование is already filled.
    public ChangePriority GetChangePriority() => ChangePriority.Annotations;
    public string GetUpdaterName() => "BotPlus RT: изоляция труб";
    public string GetAdditionalInformation() => "Строит изоляцию новых и изменённых труб, фитингов и арматуры по правилам проекта.";
}

internal sealed class RevitInsulationModel(Document doc) : IInsulationModel
{
    readonly List<(string? System, string Name, double D, bool Insulated)> pipes = InsulationEngine.Pipes(doc)
        .Select(p => (InsulationEngine.SystemType(p), InsulationEngine.Key(p), InsulationEngine.DiameterMm(p), InsulationEngine.Existing(doc, p.Id) != null)).ToList();

    /// <summary>Только системы, в которых есть трубы.</summary>
    public IReadOnlyList<string> Systems() =>
        pipes.Select(p => p.System).OfType<string>().Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    public IReadOnlyList<PipeGroup> Pipes(IReadOnlyCollection<string> systems) =>
        pipes.Where(p => systems.Contains("") || p.System is { } s && systems.Contains(s, StringComparer.CurrentCultureIgnoreCase))
            .GroupBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new PipeGroup(g.Key, g.Count(), InsulationPolicy.Sizes(g.Select(p => p.D)), g.Count(p => p.Insulated)))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public IReadOnlyList<string> InsulationTypes() => InsulationEngine.Types(doc).Keys.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
}

[Transaction(TransactionMode.Manual)]
public sealed class InsulationCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var ui = data.Application.ActiveUIDocument;
            if (ui is null) return Result.Cancelled;
            var doc = ui.Document;
            if (doc.IsFamilyDocument) { TaskDialog.Show("Изоляция", "Откройте проект."); return Result.Cancelled; }
            // Selected pipes preselect their systems.
            var picked = ui.Selection.GetElementIds().Select(doc.GetElement).OfType<Pipe>().Select(InsulationEngine.SystemType).OfType<string>().Distinct().ToList();
            var window = new InsulationWindow(new RevitInsulationModel(doc), InsulationStore.Read(doc).Clone(), picked);
            new WindowInteropHelper(window) { Owner = data.Application.MainWindowHandle };
            if (window.ShowDialog() != true || window.Result is not { } settings) return Result.Cancelled;

            InsulationOutcome outcome;
            using (var t = new Transaction(doc, "BotPlus: изоляция труб"))
            {
                t.Start();
                InsulationStore.Write(doc, settings);
                outcome = InsulationEngine.ApplyAll(doc, settings);
                // Every element is already processed; the updater would only re-check it on commit.
                InsulationUpdater.Suspended = true;
                try { if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit отменил построение изоляции."); }
                finally { InsulationUpdater.Suspended = false; }
            }
            TaskDialog.Show("Изоляция", outcome.Summary() + (settings.Auto ? "\n\nНовые трубы получат изоляцию автоматически." : ""));
            return Result.Succeeded;
        }
        catch (Exception error) { message = error.Message; return Result.Failed; }
    }
}
