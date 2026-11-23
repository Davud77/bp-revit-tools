using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

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
        // Version 1 kept type UniqueIds; they become the types' current «Маркировка типоразмера» (saved on the next write).
        set.Migrate(key => doc.GetElement(key) is ElementType type ? EngineeringEngine.TypeMark(type) : null);
        lastJson = json; lastSet = set;
        return set;
    }

    /// <summary>Call in an open transaction.</summary>
    internal static void Write(Document doc, EngineeringRuleSet set)
    {
        foreach (var rule in set.Rules) rule.Validate();
        set.ValidateConditionalGroups();
        set.WallThickness.Validate();
        set.SystemNames.Validate();
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
        EngineeringRulesPolicy.Categories.Select(c => (Enum.Parse<BuiltInCategory>(c.Key), c.Label)).ToArray();

    internal static string? CategoryKey(Element element)
    {
        var bic = element.Category?.BuiltInCategory;
        return bic is { } c && Categories.Any(x => x.Category == c) ? c.ToString() : null;
    }

    /// <summary>«Маркировка типоразмера» of the type (or of the element when it is a type); empty → null.</summary>
    internal static string? TypeMark(Element element)
    {
        var type = element as ElementType ?? element.Document.GetElement(element.GetTypeId()) as ElementType;
        return EngineeringRulesPolicy.NormalizeMark(type?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString());
    }

    static IEnumerable<Element> All(Document doc, string category) =>
        new FilteredElementCollector(doc).OfCategory(Enum.Parse<BuiltInCategory>(category)).WhereElementIsNotElementType();

    /// <summary>Instances whose type carries <paramref name="mark"/> (null — types without a mark).</summary>
    internal static IEnumerable<Element> Instances(Document doc, string category, string? mark) =>
        All(doc, category).Where(e => TypeMark(e) == EngineeringRulesPolicy.NormalizeMark(mark));

    internal static IEnumerable<Element> InstancesOfType(Document doc, ElementType type) =>
        CategoryKey(type) is { } category ? All(doc, category).Where(e => e.GetTypeId() == type.Id) : [];

    /// <summary>Types of the category grouped by mark, for the scope list (BIM2B groups rules the same way).</summary>
    internal static List<(string? Mark, List<string> Types)> Marks(Document doc, string category) =>
        new FilteredElementCollector(doc).OfCategory(Enum.Parse<BuiltInCategory>(category)).WhereElementIsElementType().Cast<ElementType>()
            .GroupBy(TypeMark)
            .Select(g => (g.Key, g.Select(t => string.IsNullOrEmpty(t.FamilyName) ? t.Name : t.FamilyName + ": " + t.Name).OrderBy(n => n).ToList()))
            .OrderBy(g => g.Key != null).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToList();

    static Parameter? Find(Element element, string name) =>
        element.LookupParameter(name) ?? element.Document.GetElement(element.GetTypeId())?.LookupParameter(name);

    static double Mm(Element e, BuiltInParameter id) => e.get_Parameter(id) is { HasValue: true, StorageType: StorageType.Double } p
        ? UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters) : 0;

    internal static string? Text(Element element, string source, WallThicknessSettings? walls = null)
    {
        var type = element.Document.GetElement(element.GetTypeId()) as ElementType;
        switch (source)
        {
            case EngineeringRulesPolicy.TypeNameSource: return type?.Name;
            case EngineeringRulesPolicy.FamilyNameSource: return type?.FamilyName;
            case EngineeringRulesPolicy.SortedSizeSource: return SortedSize(element);
            case EngineeringRulesPolicy.CountSource: return "1";
            case EngineeringRulesPolicy.FittingSizeSource: return FittingSize(element);
            case EngineeringRulesPolicy.WallThicknessSource:
                return WallThickness(element, walls ?? new()) is { } t ? EngineeringRulesPolicy.Format(t, 2) : null;
            case EngineeringRulesPolicy.HostLengthSource:
            case EngineeringRulesPolicy.HostAreaSource:
                return Source(element, source) is { } v ? EngineeringRulesPolicy.Format(v.Value, 2) : null;
        }
        var p = Find(element, source);
        if (p is null) return null;
        if (!p.HasValue) return "";
        return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
    }

    static string? SortedSize(Element element)
    {
        double w = Mm(element, BuiltInParameter.RBS_CURVE_WIDTH_PARAM), h = Mm(element, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
        if (w <= 0) w = Mm(element, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
        if (h <= 0) h = Mm(element, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
        if (w > 0 && h > 0) return EngineeringRulesPolicy.SortedSize(w, h);
        double d = Mm(element, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
        if (d <= 0) d = Mm(element, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
        if (d <= 0) d = Mm(element, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
        if (d > 0) return EngineeringRulesPolicy.Format(d, 1);
        return element.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString();
    }

    /// <summary>Size of a duct or pipe fitting from its connectors: elbow, transition, tee, takeoff.</summary>
    static string? FittingSize(Element element)
    {
        if (element is not FamilyInstance) return null;
        double ToMm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
        var sizes = Mep.Connectors(element).Select(c => c.Shape == ConnectorProfileType.Round
            ? (ToMm(c.Radius * 2), 0d, 0d) : (0d, ToMm(c.Width), ToMm(c.Height))).ToList();
        return sizes.Count == 0 ? null : EngineeringRulesPolicy.FittingSize(sizes);
    }

    /// <summary>Pipes: (outer − inner)/2; ducts: table by shape, size and fire protection (insulation name marker).</summary>
    internal static double? WallThickness(Element element, WallThicknessSettings walls)
    {
        if (element is Pipe)
            return EngineeringRulesPolicy.PipeWall(Mm(element, BuiltInParameter.RBS_PIPE_OUTER_DIAMETER), Mm(element, BuiltInParameter.RBS_PIPE_INNER_DIAM_PARAM));
        if (element is not Duct duct) return null;
        var shape = duct.DuctType.Shape == ConnectorProfileType.Round ? SectionShape.Round : SectionShape.Rectangular;
        double size = shape == SectionShape.Round ? Mm(duct, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
            : Math.Max(Mm(duct, BuiltInParameter.RBS_CURVE_WIDTH_PARAM), Mm(duct, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM));
        if (size <= 0) return null;
        return EngineeringRulesPolicy.DuctWall(walls.Rows, shape, size, FireProtected(duct, walls.FireMarker));
    }

    static bool FireProtected(Duct duct, string marker)
    {
        if (string.IsNullOrWhiteSpace(marker)) return false;
        var doc = duct.Document;
        foreach (var id in InsulationLiningBase.GetInsulationIds(doc, duct.Id))
        {
            var type = doc.GetElement(doc.GetElement(id)?.GetTypeId() ?? ElementId.InvalidElementId) as ElementType;
            if (type != null && (type.Name + " " + type.FamilyName).Contains(marker, StringComparison.CurrentCultureIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Source value in SI and its physical kind. Null when absent.</summary>
    internal static (SourceKind Kind, double Value)? Source(Element element, string source)
    {
        switch (source)
        {
            case EngineeringRulesPolicy.CountSource: return (SourceKind.Count, 1);
            case EngineeringRulesPolicy.HostLengthSource:
            case EngineeringRulesPolicy.HostAreaSource:
                // Insulation counts by its host: pipe length, actual duct surface.
                if (element is not InsulationLiningBase lining || element.Document.GetElement(lining.HostElementId) is not { } host) return null;
                var p0 = host.get_Parameter(source == EngineeringRulesPolicy.HostLengthSource ? BuiltInParameter.CURVE_ELEM_LENGTH : BuiltInParameter.RBS_CURVE_SURFACE_AREA);
                if (p0 is not { HasValue: true, StorageType: StorageType.Double }) return null;
                return source == EngineeringRulesPolicy.HostLengthSource
                    ? (SourceKind.Length, UnitUtils.ConvertFromInternalUnits(p0.AsDouble(), UnitTypeId.Meters))
                    : (SourceKind.Area, UnitUtils.ConvertFromInternalUnits(p0.AsDouble(), UnitTypeId.SquareMeters));
        }
        var p = Find(element, source);
        if (p is null || !p.HasValue) return null;
        if (p.StorageType == StorageType.Integer) return (SourceKind.Number, p.AsInteger());
        if (p.StorageType != StorageType.Double) return null;
        var spec = p.Definition.GetDataType();
        if (spec == SpecTypeId.Length) return (SourceKind.Length, UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Meters));
        if (spec == SpecTypeId.Area) return (SourceKind.Area, UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.SquareMeters));
        // Volume (insulation cylinders) as a number in m³.
        if (spec == SpecTypeId.Volume) return (SourceKind.Number, UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.CubicMeters));
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

    internal static bool SystemCategory(Element element) =>
        element.Category?.BuiltInCategory is { } c && EngineeringRulesPolicy.SystemCategories.Contains(c.ToString());

    /// <summary>All categories the updater watches: name/quantity categories and ADSK_Система_Имя categories.</summary>
    internal static List<BuiltInCategory> Watched() =>
        Categories.Select(c => c.Category).Concat(EngineeringRulesPolicy.SystemCategories.Select(Enum.Parse<BuiltInCategory>)).Distinct().ToList();

    /// <summary>
    /// System types of the element: the system type parameter of pipes, ducts and fittings; for insulation — of its host;
    /// for equipment and fixtures — the systems of its connectors (several for multi-system equipment).
    /// </summary>
    internal static List<(string Type, SystemDomain Domain)> SystemTypes(Element element)
    {
        var doc = element.Document;
        if (element is InsulationLiningBase lining && doc.GetElement(lining.HostElementId) is { } host) element = host;
        var result = new List<(string, SystemDomain)>();
        void Add(ElementId id, SystemDomain domain)
        {
            if (id != ElementId.InvalidElementId && doc.GetElement(id) is ElementType type && !result.Contains((type.Name, domain))) result.Add((type.Name, domain));
        }
        foreach (var (bip, domain) in new[] { (BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM, SystemDomain.Pipe), (BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM, SystemDomain.Duct) })
            if (element.get_Parameter(bip) is { StorageType: StorageType.ElementId } p) Add(p.AsElementId(), domain);
        if (result.Count == 0)
            foreach (var c in Mep.Connectors(element).OrderBy(c => c.Id))
                if (c.MEPSystem is { } system) Add(system.GetTypeId(), system is PipingSystem ? SystemDomain.Pipe : system is MechanicalSystem ? SystemDomain.Duct : SystemDomain.Any);
        return result;
    }

    /// <summary>
    /// Size for the reserve table, mm: pipe nominal diameter, duct/tray diameter or larger side, flexible diameter;
    /// fittings and accessories — the largest connector; insulation — its host.
    /// </summary>
    internal static double? SizeMm(Element element)
    {
        var doc = element.Document;
        if (element is InsulationLiningBase lining && doc.GetElement(lining.HostElementId) is { } host) element = host;
        double d = Mm(element, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
        if (d <= 0) d = Mm(element, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
        if (d <= 0) d = Mm(element, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
        if (d <= 0) d = Math.Max(Mm(element, BuiltInParameter.RBS_CURVE_WIDTH_PARAM), Mm(element, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM));
        if (d <= 0) d = Math.Max(Mm(element, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM), Mm(element, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM));
        if (d <= 0 && element is FamilyInstance)
            d = Mep.Connectors(element).Select(c => UnitUtils.ConvertFromInternalUnits(c.Shape == ConnectorProfileType.Round ? c.Radius * 2 : Math.Max(c.Width, c.Height), UnitTypeId.Millimeters))
                .DefaultIfEmpty(0).Max();
        return d > 0 ? Math.Round(d, 3) : null;
    }

    /// <summary>Rule of the element (by category and type mark), the wall thickness and ADSK_Система_Имя. Never throws.</summary>
    internal static void Process(Element element, EngineeringRuleSet set, EngineeringOutcome outcome)
    {
        string? category = CategoryKey(element);
        bool system = set.SystemNames.Enabled && (SystemCategory(element) || element is FamilyInstance { SuperComponent: not null });
        if (category is null && !system) return;
        string? mark = TypeMark(element);
        // «игнорировать» switches off name, quantity and wall thickness; ADSK_Система_Имя is still filled.
        bool ignored = EngineeringRulesPolicy.IsIgnored(mark);
        IReadOnlyList<EngineeringRule> rules = category is null || ignored ? [] : set.ResolveConditionals(category, mark, p => Text(element, p, set.WallThickness));
        bool walls = !ignored && set.WallThickness.Target.Length > 0 && element is Pipe or Duct;
        if (rules.Count > 0 || walls || system) Apply(element, rules, set, outcome);
    }

    /// <summary>Writes only differing values. Each element is independent; a failure is counted, never thrown.</summary>
    internal static void Apply(Element element, IReadOnlyList<EngineeringRule> rules, EngineeringRuleSet set, EngineeringOutcome outcome)
    {
        var doc = element.Document;
        if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, element.Id) == CheckoutStatus.OwnedByOtherUser)
        { outcome.Skip("занят другим пользователем"); return; }
        bool changed = false, failed = false;
        try
        {
            foreach (var rule in rules)
            {
                if (rule is { NameEnabled: true })
                {
                    string name = EngineeringRulesPolicy.ComposeName(rule.Parts, s => Text(element, s, set.WallThickness));
                    var target = Writable(element, rule.NameTarget, outcome);
                    if (target is null) failed = true;
                    else if (target.StorageType != StorageType.String) { outcome.Skip($"«{rule.NameTarget}» не текстовый"); failed = true; }
                    else if ((target.AsString() ?? "") != name) { target.Set(name); changed = true; }
                }
                if (rule is { QuantityEnabled: true })
                {
                    var source = Source(element, rule.QuantitySource);
                    if (source is null)
                    {
                        // As in BIM2B: a duct fitting family without ADSK_Размер_Площадь is simply not counted.
                        outcome.Skip(rule.QuantitySource == EngineeringRulesPolicy.FittingAreaParameter
                            ? $"семейство без «{EngineeringRulesPolicy.FittingAreaParameter}» — проигнорировано"
                            : $"нет значения «{rule.QuantitySource}»");
                        failed = true;
                    }
                    else
                    {
                        double reserve = rule.Reserve;
                        var q = EngineeringRulesPolicy.Quantity(rule, source.Value.Kind, source.Value.Value, out string? error, reserve);
                        var target = q is null ? null : Writable(element, rule.QuantityTarget, outcome);
                        if (q is null) { outcome.Skip(error!); failed = true; }
                        else if (target is null) failed = true;
                        else if (WriteNumber(target, q.Value, rule.Decimals, outcome, rule.QuantityTarget) is { } wrote) changed |= wrote;
                        else failed = true;
                    }
                }
            }
            if (set.SystemNames.Enabled && (SystemCategory(element) || element is FamilyInstance { SuperComponent: not null }))
            {
                // No matching rule (or no system yet): the value is left as is rather than cleared.
                if (InheritedSystemName(element, set.SystemNames) is { } systemName)
                {
                    if (element is FamilyInstance family) ApplyNestedSystemName(family, set.SystemNames.Target, systemName, outcome, new HashSet<ElementId> { element.Id });
                    string systemTarget = set.SystemNames.Target;
                    var target = Writable(element, systemTarget, outcome);
                    if (target is null) failed = true;
                    else if (target.StorageType != StorageType.String) { outcome.Skip($"«{systemTarget}» не текстовый"); failed = true; }
                    else if ((target.AsString() ?? "") != systemName) { target.Set(systemName); changed = true; }
                }
            }
            string wallTarget = set.WallThickness.Target;
            if (wallTarget.Length > 0 && element is Pipe or Duct)
            {
                var t = WallThickness(element, set.WallThickness);
                var target = t is null ? null : Writable(element, wallTarget, outcome);
                if (t is null) { outcome.Skip("толщина стенки не определена (нет размера или строки таблицы)"); failed = true; }
                else if (target is null) failed = true;
                else if (WriteThickness(target, t.Value, outcome, wallTarget) is { } wrote) changed |= wrote;
                else failed = true;
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

    // Resolve outermost parent first, keeping nested instance updates consistent with propagation.
    static string? InheritedSystemName(Element element, SystemNameSettings settings)
    {
        var chain = new List<Element>();
        var seen = new HashSet<ElementId>();
        for (Element? current = element; current != null && seen.Add(current.Id);
             current = (current as FamilyInstance)?.SuperComponent) chain.Add(current);
        for (int i = chain.Count - 1; i >= 0; i--)
            if (EngineeringRulesPolicy.SystemName(settings, SystemTypes(chain[i])) is { } name) return name;
        return null;
    }

    static void ApplyNestedSystemName(FamilyInstance parent, string targetName, string value, EngineeringOutcome outcome, HashSet<ElementId> seen)
    {
        foreach (var id in parent.GetSubComponentIds())
        {
            if (!seen.Add(id) || parent.Document.GetElement(id) is not { } child) continue;
            // Visit descendants even if an intermediate family has no writable parameter.
            if (child is FamilyInstance family) ApplyNestedSystemName(family, targetName, value, outcome, seen);
            try
            {
                if (child.Document.IsWorkshared && WorksharingUtils.GetCheckoutStatus(child.Document, id) == CheckoutStatus.OwnedByOtherUser)
                { outcome.Skip("вложенный элемент занят другим пользователем"); continue; }
                var p = Writable(child, targetName, outcome);
                if (p == null) continue;
                if (p.StorageType != StorageType.String) { outcome.Skip($"«{targetName}» не текстовый"); continue; }
                if ((p.AsString() ?? "") == value) continue;
                if (p.Set(value)) outcome.Updated++;
                else outcome.Skip("Revit не принял имя системы вложенного элемента");
            }
            catch (Exception error) { outcome.Skip("вложенный элемент: " + error.Message); }
        }
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

    /// <summary>Thickness in mm into a length parameter (converted), a unitless number (mm) or text.</summary>
    static bool? WriteThickness(Parameter p, double mm, EngineeringOutcome outcome, string name)
    {
        if (p.StorageType == StorageType.Double && p.Definition.GetDataType() == SpecTypeId.Length)
        {
            double value = UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
            if (Math.Abs(p.AsDouble() - value) < 1e-9) return false;
            return p.Set(value);
        }
        return WriteNumber(p, mm, 2, outcome, name);
    }

    /// <summary>Applies the rules to every element of the category with the given mark.</summary>
    /// <summary>All elements of the system categories: ADSK_Система_Имя (and their other rules) for the whole project.</summary>
    internal static EngineeringOutcome ApplySystemNames(Document doc, EngineeringRuleSet set, EngineeringOutcome? outcome = null)
    {
        outcome ??= new EngineeringOutcome();
        var done = new HashSet<ElementId>();
        foreach (var category in EngineeringRulesPolicy.SystemCategories.Select(Enum.Parse<BuiltInCategory>))
            foreach (var element in new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType().ToList())
                if (done.Add(element.Id)) Process(element, set, outcome);
        return outcome;
    }

    internal static EngineeringOutcome ApplyAll(Document doc, EngineeringRuleSet set)
    {
        var outcome = new EngineeringOutcome();
        foreach (var category in Watched())
            foreach (var element in new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType().ToList())
                Process(element, set, outcome);
        return outcome;
    }

    internal static EngineeringOutcome ApplyScope(Document doc, EngineeringRuleSet set, string category, string? mark)
    {
        var outcome = new EngineeringOutcome();
        var elements = set.ConditionalGroups.Any(g => g.Category == category) ? All(doc, category) : Instances(doc, category, mark);
        foreach (var element in elements.ToList()) Process(element, set, outcome);
        return outcome;
    }
}

/// <summary>
/// Dynamic model updater: fills values on creation and on any change of the listed categories and their types
/// (a new «Маркировка типоразмера» moves instances to another rule). Writes are idempotent (only differing values),
/// so its own changes cannot loop.
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
        var filter = new LogicalOrFilter(new ElementMulticategoryFilter(EngineeringEngine.Watched()), new ElementClassFilter(typeof(FamilyInstance)));
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
            if (set.Rules.Count == 0 && set.ConditionalGroups.Count == 0 && set.WallThickness.Target.Length == 0 && !set.SystemNames.Enabled) return;
            var ids = new HashSet<ElementId>(data.GetAddedElementIds());
            ids.UnionWith(data.GetModifiedElementIds());
            var outcome = new EngineeringOutcome();
            var done = new HashSet<ElementId>();
            foreach (var id in ids)
            {
                var element = doc.GetElement(id);
                if (element is null) continue;
                // A type edit (size, material, mark) changes values of all its instances.
                var targets = element is ElementType type ? EngineeringEngine.InstancesOfType(doc, type) : [element];
                foreach (var target in targets)
                    if (done.Add(target.Id)) EngineeringEngine.Process(target, set, outcome);
            }
        }
        catch (Exception error) { Trace.WriteLine("BotPlus engineering updater: " + error); }
    }

    public UpdaterId GetUpdaterId() => id;
    public ChangePriority GetChangePriority() => ChangePriority.MEPSystems;
    public string GetUpdaterName() => "BotPlus RT: инженерные параметры";
    public string GetAdditionalInformation() => "Заполняет наименование, количество и толщину стенки инженерных элементов по правилам проекта.";
}

/// <summary>Project data for the rules window: the first element of each scope serves as the example.</summary>
internal sealed class RevitUpdaterModel(Document doc) : IUpdaterModel
{
    readonly Dictionary<(string, string?), List<Element>> samples = [];
    string? previewCategory;
    List<Element> preview = [];

    public (bool Exists, string? Mark) SelectPreview(string category, ConditionalRuleGroup group, EngineeringRule formula, WallThicknessSettings walls)
    {
        previewCategory = category;
        var element = new FilteredElementCollector(doc).OfCategory(Enum.Parse<BuiltInCategory>(category)).WhereElementIsNotElementType()
            .FirstOrDefault(e => !EngineeringRulesPolicy.IsIgnored(EngineeringEngine.TypeMark(e)) && ReferenceEquals(formula,
                group.Select(p => p == "Маркировка типоразмера" ? EngineeringEngine.TypeMark(e) ?? "" : EngineeringEngine.Text(e, p, walls))));
        preview = element is null ? [] : [element];
        return (element != null, element is null ? null : EngineeringEngine.TypeMark(element));
    }

    List<Element> Scoped(string category, string? mark)
    {
        if (previewCategory == category) return preview;
        if (!samples.TryGetValue((category, mark), out var list))
            samples[(category, mark)] = list = EngineeringEngine.Instances(doc, category, mark).Take(30).ToList();
        return list;
    }

    /// <summary>Scope elements, or any elements of the category when the mark has none (names and targets still useful).</summary>
    List<Element> Any(string category, string? mark)
    {
        var scoped = Scoped(category, mark);
        return scoped.Count > 0 ? scoped
            : new FilteredElementCollector(doc).OfCategory(Enum.Parse<BuiltInCategory>(category)).WhereElementIsNotElementType().Take(20).ToList();
    }

    public IReadOnlyList<(string? Mark, List<string> Types)> Marks(string category) => EngineeringEngine.Marks(doc, category);
    public bool HasSample(string category, string? mark) => Scoped(category, mark).Count > 0;
    public IReadOnlyList<string> Parameters(string category, string? mark) => EngineeringEngine.ParameterNames(Any(category, mark));

    IReadOnlyList<string> Targets(string category, string? mark, Func<Parameter, bool> fits)
    {
        var first = Any(category, mark).FirstOrDefault();
        if (first is null) return [];
        return first.Parameters.Cast<Parameter>().Where(p => !p.IsReadOnly && p.Definition?.Name is { Length: > 0 } && fits(p))
            .Select(p => p.Definition.Name).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public IReadOnlyList<string> TextTargets(string category, string? mark) => Targets(category, mark, p => p.StorageType == StorageType.String);
    public IReadOnlyList<string> NumberTargets(string category, string? mark) => Targets(category, mark, p =>
        p.StorageType is StorageType.Integer or StorageType.String || (p.StorageType == StorageType.Double && p.Definition.GetDataType() == SpecTypeId.Number));

    public string? Text(string category, string? mark, string parameter, WallThicknessSettings walls) =>
        Scoped(category, mark).FirstOrDefault() is { } e ? EngineeringEngine.Text(e, parameter, walls) : null;

    public (double? SizeMm, IReadOnlyList<string> Systems) ReserveContext(string category, string? mark) =>
        Scoped(category, mark).FirstOrDefault() is { } e ? (EngineeringEngine.SizeMm(e), EngineeringEngine.SystemTypes(e).Select(t => t.Type).ToList()) : (null, []);

    public IReadOnlyList<(string Type, SystemDomain Domain)> SystemTypes() =>
        new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<ElementType>().Select(t => (t.Name, SystemDomain.Pipe))
            .Concat(new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).Cast<ElementType>().Select(t => (t.Name, SystemDomain.Duct)))
            .OrderBy(t => t.Item2).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public (SourceKind Kind, double Value)? Source(string category, string? mark, string parameter) =>
        Scoped(category, mark).FirstOrDefault() is { } e ? EngineeringEngine.Source(e, parameter) : null;
}
