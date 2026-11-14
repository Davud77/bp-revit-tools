using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace HeatLossViewer.Mep;

/// <summary>Rules live in the project (ProjectInformation entity) so they travel with the RVT.</summary>
internal static class EngineeringRulesStore
{
    static readonly Guid SchemaId = new("5f0b3c62-4d2e-4f52-9a77-0e6c4a1b9d31");
    static string? lastJson;
    static EngineeringRuleSet lastSet = new();

    internal static EngineeringRuleSet Read(Document doc)
    {
        var schema = Schema.Lookup(SchemaId);
        if (schema is null) return new EngineeringRuleSet();
        var entity = doc.ProjectInformation.GetEntity(schema);
        string? json = entity.IsValid() ? entity.Get<string>(schema.GetField("Json")) : null;
        if (json == lastJson) return lastSet;
        var set = EngineeringRuleSet.FromJson(json);
        lastJson = json; lastSet = set;
        return set;
    }

    /// <summary>Call in an open transaction.</summary>
    internal static void Write(Document doc, EngineeringRuleSet set)
    {
        foreach (var rule in set.Rules) rule.Validate();
        var schema = Schema.Lookup(SchemaId);
        if (schema is null)
        {
            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName("BotPlusRTEngineeringRulesV1");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("Json", typeof(string));
            schema = builder.Finish();
        }
        var entity = new Entity(schema);
        entity.Set(schema.GetField("Json"), set.ToJson());
        doc.ProjectInformation.SetEntity(entity);
    }
}

internal sealed class EngineeringOutcome
{
    internal int Updated, Unchanged;
    internal readonly Dictionary<string, int> Skipped = [];
    internal void Skip(string reason) => Skipped[reason] = Skipped.GetValueOrDefault(reason) + 1;

    internal string Summary()
    {
        var text = $"Обновлено элементов: {Updated}\nБез изменений: {Unchanged}";
        if (Skipped.Count > 0)
            text += "\n\nПропущено:\n" + string.Join("\n", Skipped.OrderByDescending(p => p.Value).Select(p => $"• {p.Key}: {p.Value}"));
        return text;
    }
}

internal static class EngineeringEngine
{
    internal static readonly (BuiltInCategory Category, string Label)[] Categories =
    [
        (BuiltInCategory.OST_PipeCurves, "Трубы"),
        (BuiltInCategory.OST_FlexPipeCurves, "Гибкие трубы"),
        (BuiltInCategory.OST_PipeFitting, "Соединительные детали трубопроводов"),
        (BuiltInCategory.OST_DuctCurves, "Воздуховоды"),
        (BuiltInCategory.OST_FlexDuctCurves, "Гибкие воздуховоды"),
        (BuiltInCategory.OST_DuctFitting, "Соединительные детали воздуховодов"),
        (BuiltInCategory.OST_CableTray, "Кабельные лотки"),
        (BuiltInCategory.OST_Conduit, "Короба")
    ];

    internal static string? CategoryKey(Element element)
    {
        var bic = element.Category?.BuiltInCategory;
        return bic is { } c && Categories.Any(x => x.Category == c) ? c.ToString() : null;
    }

    internal static string? TypeKey(Element element) => element.Document.GetElement(element.GetTypeId())?.UniqueId;

    internal static IEnumerable<Element> Instances(Document doc, string category, string? typeKey)
    {
        var bic = Enum.Parse<BuiltInCategory>(category);
        var all = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType();
        return typeKey == null ? all : all.Where(e => TypeKey(e) == typeKey);
    }

    static Parameter? Find(Element element, string name) =>
        element.LookupParameter(name) ?? element.Document.GetElement(element.GetTypeId())?.LookupParameter(name);

    internal static string? Text(Element element, string source)
    {
        var type = element.Document.GetElement(element.GetTypeId()) as ElementType;
        switch (source)
        {
            case EngineeringRulesPolicy.TypeNameSource: return type?.Name;
            case EngineeringRulesPolicy.FamilyNameSource: return type?.FamilyName;
            case EngineeringRulesPolicy.SortedSizeSource: return SortedSize(element);
            case EngineeringRulesPolicy.CountSource: return "1";
        }
        var p = Find(element, source);
        if (p is null || !p.HasValue) return null;
        return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
    }

    static string? SortedSize(Element element)
    {
        double Mm(BuiltInParameter id) => element.get_Parameter(id) is { HasValue: true, StorageType: StorageType.Double } p
            ? UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters) : 0;
        double w = Mm(BuiltInParameter.RBS_CURVE_WIDTH_PARAM), h = Mm(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
        if (w <= 0) w = Mm(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
        if (h <= 0) h = Mm(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
        if (w > 0 && h > 0) return EngineeringRulesPolicy.SortedSize(w, h);
        double d = Mm(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
        if (d <= 0) d = Mm(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
        if (d <= 0) d = Mm(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
        if (d > 0) return EngineeringRulesPolicy.Format(d, 1);
        return element.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString();
    }

    /// <summary>Source value in SI and its physical kind. Null when absent.</summary>
    internal static (SourceKind Kind, double Value)? Source(Element element, string source)
    {
        if (source == EngineeringRulesPolicy.CountSource) return (SourceKind.Count, 1);
        var p = Find(element, source);
        if (p is null || !p.HasValue) return null;
        if (p.StorageType == StorageType.Integer) return (SourceKind.Number, p.AsInteger());
        if (p.StorageType != StorageType.Double) return null;
        var spec = p.Definition.GetDataType();
        if (spec == SpecTypeId.Length) return (SourceKind.Length, UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Meters));
        if (spec == SpecTypeId.Area) return (SourceKind.Area, UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.SquareMeters));
        return (SourceKind.Number, p.AsDouble());
    }

    internal static SourceKind? Kind(Element element, string source) => Source(element, source)?.Kind;

    /// <summary>Numeric and text parameter names for the chooser. Virtual sources are added by the window.</summary>
    internal static List<string> ParameterNames(IEnumerable<Element> sample)
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var element in sample.Take(20))
        {
            void Add(Element? e)
            {
                if (e is null) return;
                foreach (Parameter p in e.Parameters)
                    if (p.Definition?.Name is { Length: > 0 } n && p.StorageType != StorageType.None) names.Add(n);
            }
            Add(element); Add(element.Document.GetElement(element.GetTypeId()));
        }
        return [.. names];
    }

    /// <summary>Writes only differing values. Each element is independent; a failure is counted, never thrown.</summary>
    internal static void Apply(Element element, EngineeringRule rule, EngineeringOutcome outcome)
    {
        var doc = element.Document;
        if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, element.Id) == CheckoutStatus.OwnedByOtherUser)
        { outcome.Skip("занят другим пользователем"); return; }
        bool changed = false, failed = false;
        try
        {
            if (rule.NameEnabled)
            {
                string name = EngineeringRulesPolicy.ComposeName(rule.Parts, s => Text(element, s));
                var target = Writable(element, rule.NameTarget, outcome);
                if (target is null) failed = true;
                else if (target.StorageType != StorageType.String) { outcome.Skip($"«{rule.NameTarget}» не текстовый"); failed = true; }
                else if ((target.AsString() ?? "") != name) { target.Set(name); changed = true; }
            }
            if (rule.QuantityEnabled)
            {
                var source = Source(element, rule.QuantitySource);
                if (source is null) { outcome.Skip($"нет значения «{rule.QuantitySource}»"); failed = true; }
                else
                {
                    var q = EngineeringRulesPolicy.Quantity(rule, source.Value.Kind, source.Value.Value, out string? error);
                    var target = q is null ? null : Writable(element, rule.QuantityTarget, outcome);
                    if (q is null) { outcome.Skip(error!); failed = true; }
                    else if (target is null) failed = true;
                    else if (WriteNumber(target, q.Value, rule.Decimals, outcome, rule.QuantityTarget) is { } wrote) changed |= wrote;
                    else failed = true;
                }
            }
        }
        catch (Exception error)
        {
            outcome.Skip("ошибка Revit: " + error.Message);
            return;
        }
        if (changed) outcome.Updated++;
        else if (!failed) outcome.Unchanged++;
    }

    static Parameter? Writable(Element element, string name, EngineeringOutcome outcome)
    {
        var p = element.LookupParameter(name);
        if (p is null)
        {
            outcome.Skip(Find(element, name) is null ? $"нет параметра «{name}»" : $"«{name}» — параметр типа");
            return null;
        }
        if (p.IsReadOnly) { outcome.Skip($"«{name}» только для чтения или задан формулой"); return null; }
        return p;
    }

    static bool? WriteNumber(Parameter p, double value, int decimals, EngineeringOutcome outcome, string name)
    {
        switch (p.StorageType)
        {
            case StorageType.Double:
                var spec = p.Definition.GetDataType();
                if (spec != SpecTypeId.Number) { outcome.Skip($"«{name}» не числовой параметр без единиц"); return null; }
                if (EngineeringRulesPolicy.SameNumber(p.AsDouble(), value)) return false;
                return p.Set(value);
            case StorageType.Integer:
                int i = checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
                if (p.AsInteger() == i) return false;
                return p.Set(i);
            case StorageType.String:
                string text = EngineeringRulesPolicy.Format(value, decimals);
                if (p.AsString() == text) return false;
                return p.Set(text);
            default:
                outcome.Skip($"«{name}» несовместимого типа"); return null;
        }
    }

    internal static EngineeringOutcome ApplyScope(Document doc, EngineeringRuleSet set, string category, string? typeKey)
    {
        var outcome = new EngineeringOutcome();
        foreach (var element in Instances(doc, category, typeKey).ToList())
        {
            var rule = set.Resolve(category, TypeKey(element));
            if (rule is null) continue;
            // A «Без типа» save does not touch elements whose type has its own rule: they resolve to that rule.
            Apply(element, rule, outcome);
        }
        return outcome;
    }
}

/// <summary>
/// Dynamic model updater: fills values on creation and on any change of the listed categories.
/// Writes are idempotent (only differing values), so its own changes cannot loop.
/// </summary>
internal sealed class EngineeringUpdater : IUpdater
{
    static readonly Guid UpdaterGuid = new("0d6f4b8e-5e3a-4f9c-9b62-2b8d71e4c215");
    internal static bool Suspended;
    readonly UpdaterId id;

    EngineeringUpdater(AddInId addIn) { id = new UpdaterId(addIn, UpdaterGuid); }

    internal static void Register(AddInId addIn)
    {
        var updater = new EngineeringUpdater(addIn);
        if (UpdaterRegistry.IsUpdaterRegistered(updater.id)) return;
        UpdaterRegistry.RegisterUpdater(updater, isOptional: true);
        var filter = new ElementMulticategoryFilter(EngineeringEngine.Categories.Select(c => c.Category).ToList());
        UpdaterRegistry.AddTrigger(updater.id, filter, Element.GetChangeTypeAny());
        UpdaterRegistry.AddTrigger(updater.id, filter, Element.GetChangeTypeElementAddition());
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
            var set = EngineeringRulesStore.Read(doc);
            if (set.Rules.Count == 0) return;
            var ids = new HashSet<ElementId>(data.GetAddedElementIds());
            ids.UnionWith(data.GetModifiedElementIds());
            var outcome = new EngineeringOutcome();
            foreach (var id in ids)
            {
                var element = doc.GetElement(id);
                if (element is null) continue;
                if (element is ElementType type)
                {
                    // A type edit (size, material) changes values of all its instances.
                    string? category = EngineeringEngine.CategoryKey(type);
                    if (category is null || set.Rules.All(r => r.Category != category)) continue;
                    foreach (var instance in EngineeringEngine.Instances(doc, category, type.UniqueId))
                        if (set.Resolve(category, type.UniqueId) is { } typeRule) EngineeringEngine.Apply(instance, typeRule, outcome);
                    continue;
                }
                string? key = EngineeringEngine.CategoryKey(element);
                if (key is null) continue;
                if (set.Resolve(key, EngineeringEngine.TypeKey(element)) is { } rule) EngineeringEngine.Apply(element, rule, outcome);
            }
        }
        catch (Exception error) { Trace.WriteLine("BotPlus engineering updater: " + error); }
    }

    public UpdaterId GetUpdaterId() => id;
    public ChangePriority GetChangePriority() => ChangePriority.MEPSystems;
    public string GetUpdaterName() => "BotPlus RT: инженерные параметры";
    public string GetAdditionalInformation() => "Заполняет наименование и количество инженерных элементов по правилам проекта.";
}
