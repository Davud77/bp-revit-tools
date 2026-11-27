using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeatLossViewer.Mep;

/// <summary>One component of the composed name: prefix + value + suffix. Empty values drop the whole component.</summary>
public sealed class NamePart
{
    public string Parameter { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
}

public enum QuantityUnit { Piece, Meter, Millimeter, SquareMeter }
public enum QuantityRounding { Up, Nearest }
/// <summary>Physical kind of the source value after conversion from Revit internal units.</summary>
public enum SourceKind { Count, Length, Area, Number }
public enum SectionShape { Round, Rectangular }

public enum ConditionComparison { Equals, NotEquals, Contains, StartsWith, Empty, NotEmpty }

public sealed class FormulaCondition
{
    public string Parameter { get; set; } = "Комментарии к типоразмеру";
    [JsonConverter(typeof(JsonStringEnumConverter))] public ConditionComparison Comparison { get; set; }
    public string Value { get; set; } = "";
    public EngineeringRule Formula { get; set; } = new();

    public bool Matches(Func<string, string?> text)
    {
        // Missing parameters must not accidentally satisfy «не равно» or an empty value.
        var raw = text(Parameter);
        if (raw is null) return false;
        string actual = raw.Trim(), expected = Value.Trim();
        return Comparison switch
        {
            ConditionComparison.Equals => actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            ConditionComparison.NotEquals => !actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            ConditionComparison.Contains => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            ConditionComparison.StartsWith => actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            ConditionComparison.Empty => actual.Length == 0,
            ConditionComparison.NotEmpty => actual.Length > 0,
            _ => false
        };
    }
}

/// <summary>Ordered conditions for one category and target; first match wins, otherwise the fallback formula.</summary>
public sealed class ConditionalRuleGroup
{
    public string Category { get; set; } = "";
    public bool IsQuantity { get; set; }
    public string Target { get; set; } = EngineeringRulesPolicy.DefaultNameTarget;
    public List<FormulaCondition> Conditions { get; set; } = [];
    public EngineeringRule Otherwise { get; set; } = new();
    [JsonIgnore] public bool IsActive => Conditions.Select(c => c.Formula).Append(Otherwise)
        .Any(f => IsQuantity ? f.QuantityEnabled : f.NameEnabled);

    public EngineeringRule Select(Func<string, string?> text) => Conditions.FirstOrDefault(c => c.Matches(text))?.Formula ?? Otherwise;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Category) || string.IsNullOrWhiteSpace(Target))
            throw new InvalidOperationException("Укажите категорию и заполняемый параметр правила.");
        foreach (var c in Conditions)
        {
            if (string.IsNullOrWhiteSpace(c.Parameter)) throw new InvalidOperationException("Выберите параметр условия.");
            if (!Enum.IsDefined(c.Comparison)) throw new InvalidOperationException("Неизвестное сравнение условия.");
            if (c.Comparison is ConditionComparison.Contains or ConditionComparison.StartsWith && string.IsNullOrWhiteSpace(c.Value))
                throw new InvalidOperationException("Укажите значение условия.");
        }
        foreach (var formula in Conditions.Select(c => c.Formula).Append(Otherwise))
        {
            var f = formula.Clone(); f.Category = Category; f.TypeMark = null;
            f.NameEnabled &= !IsQuantity; f.QuantityEnabled &= IsQuantity;
            f.NameTarget = f.QuantityTarget = Target;
            f.Validate();
        }
    }
}

/// <summary>
/// Rule for one category and one value of the type parameter «Маркировка типоразмера» (as in BIM2B):
/// types with the same mark share the rule, <see cref="TypeMark"/> = null is «Без типа» (types without a mark),
/// the mark «игнорировать» switches the updater off for the type.
/// </summary>
public sealed class EngineeringRule
{
    public string Category { get; set; } = "";
    public string? TypeMark { get; set; }
    /// <summary>Version 1 scoped rules by type UniqueId; read only for migration.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TypeKey { get; set; }
    public bool NameEnabled { get; set; }
    public string NameTarget { get; set; } = EngineeringRulesPolicy.DefaultNameTarget;
    public List<NamePart> Parts { get; set; } = [];
    public bool QuantityEnabled { get; set; }
    public string QuantityTarget { get; set; } = EngineeringRulesPolicy.DefaultQuantityTarget;
    public string QuantitySource { get; set; } = EngineeringRulesPolicy.CountSource;
    /// <summary>General reserve coefficient; 1.1 (10 %) by default.</summary>
    public double Reserve { get; set; } = EngineeringRulesPolicy.DefaultReserve;
    /// <summary>Reserve by size and system (sewerage Ø110 — 10 %, Ø50 — 15 %); falls back to <see cref="Reserve"/>.</summary>
    public List<ReserveRow> ReserveBySize { get; set; } = [];
    [JsonConverter(typeof(JsonStringEnumConverter))] public QuantityUnit Unit { get; set; } = QuantityUnit.Piece;
    public int Decimals { get; set; } = 2;
    [JsonConverter(typeof(JsonStringEnumConverter))] public QuantityRounding Rounding { get; set; } = QuantityRounding.Up;

    [JsonIgnore] public bool IsActive => NameEnabled || QuantityEnabled;
    [JsonIgnore] public string ScopeLabel => TypeMark ?? EngineeringRulesPolicy.NoTypeLabel;

    public EngineeringRule Clone() => JsonSerializer.Deserialize<EngineeringRule>(JsonSerializer.Serialize(this))!;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Category)) throw new InvalidOperationException("Не указана категория правила.");
        if (EngineeringRulesPolicy.IsIgnored(TypeMark)) throw new InvalidOperationException("Для маркировки «игнорировать» правило не задаётся.");
        if (NameEnabled && EngineeringRulesPolicy.SupportsNaming(Category))
        {
            if (string.IsNullOrWhiteSpace(NameTarget)) throw new InvalidOperationException("Укажите параметр для наименования.");
            if (Parts.Count == 0) throw new InvalidOperationException("Добавьте хотя бы один компонент наименования.");
            if (Parts.Any(p => string.IsNullOrWhiteSpace(p.Parameter))) throw new InvalidOperationException("У компонента наименования не выбран параметр.");
        }
        if (QuantityEnabled)
        {
            if (string.IsNullOrWhiteSpace(QuantityTarget)) throw new InvalidOperationException("Укажите параметр для количества.");
            if (string.IsNullOrWhiteSpace(QuantitySource)) throw new InvalidOperationException("Выберите исходный параметр количества.");
            if (!double.IsFinite(Reserve) || Reserve < 1 || Reserve > 10) throw new InvalidOperationException("Коэффициент запаса должен быть от 1 до 10.");
            if (Decimals is < 0 or > 6) throw new InvalidOperationException("Округление — от 0 до 6 знаков после запятой.");
            foreach (var row in ReserveBySize)
            {
                row.Systems = row.Systems.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
                if (!double.IsFinite(row.MaxSizeMm) || row.MaxSizeMm <= 0) throw new InvalidOperationException("Запас по размеру: размер должен быть больше 0.");
                if (!double.IsFinite(row.Reserve) || row.Reserve < 1 || row.Reserve > 10) throw new InvalidOperationException("Запас по размеру: коэффициент от 1 до 10 (0–900 %).");
            }
            if (ReserveBySize.GroupBy(r => (string.Join(",", r.Systems.Order()).ToUpperInvariant(), r.MaxSizeMm)).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Запас по размеру: повторяются строки с одинаковыми системами и размером.");
        }
    }
}

/// <summary>Row of the duct wall table: applies to sizes up to <see cref="MaxSizeMm"/> (diameter or larger side).</summary>
public sealed class WallThicknessRow
{
    [JsonConverter(typeof(JsonStringEnumConverter))] public SectionShape Shape { get; set; }
    public bool FireProtected { get; set; }
    public double MaxSizeMm { get; set; }
    public double ThicknessMm { get; set; }
}

/// <summary>
/// Duct wall thickness by section shape, size and fire protection. Rows apply in ascending size order: the first row
/// whose limit is not smaller than the duct size gives the thickness. Defaults follow the usual steel-sheet table and
/// must be checked against the project's standard.
/// </summary>
public sealed class WallThicknessSettings
{
    public List<WallThicknessRow> Rows { get; set; } = Defaults();
    /// <summary>Text in the insulation type or material name that marks fire protection.</summary>
    public string FireMarker { get; set; } = "огнезащ";
    /// <summary>Instance parameter that receives the thickness of ducts and pipes; empty — not written.</summary>
    public string Target { get; set; } = "";

    public static List<WallThicknessRow> Defaults()
    {
        WallThicknessRow R(SectionShape s, bool fire, double size, double t) => new() { Shape = s, FireProtected = fire, MaxSizeMm = size, ThicknessMm = t };
        return
        [
            R(SectionShape.Round, false, 200, 0.5), R(SectionShape.Round, false, 450, 0.6), R(SectionShape.Round, false, 800, 0.7),
            R(SectionShape.Round, false, 1250, 1.0), R(SectionShape.Round, false, 1600, 1.2), R(SectionShape.Round, false, 2000, 1.4),
            R(SectionShape.Rectangular, false, 250, 0.5), R(SectionShape.Rectangular, false, 1000, 0.7), R(SectionShape.Rectangular, false, 2000, 0.9),
            R(SectionShape.Round, true, 800, 0.8), R(SectionShape.Round, true, 1250, 1.0), R(SectionShape.Round, true, 1600, 1.2), R(SectionShape.Round, true, 2000, 1.4),
            R(SectionShape.Rectangular, true, 1000, 0.8), R(SectionShape.Rectangular, true, 2000, 0.9)
        ];
    }

    public void Validate()
    {
        foreach (var r in Rows)
            if (!double.IsFinite(r.MaxSizeMm) || r.MaxSizeMm <= 0 || !double.IsFinite(r.ThicknessMm) || r.ThicknessMm <= 0 || r.ThicknessMm > 20)
                throw new InvalidOperationException("Таблица толщины стенки: размер больше 0, толщина от 0 до 20 мм.");
        if (Rows.GroupBy(r => (r.Shape, r.FireProtected, r.MaxSizeMm)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Таблица толщины стенки: одинаковые строки для одной формы и размера.");
    }
}

public enum SystemDomain { Pipe, Duct, Any }

/// <summary>Reserve for elements up to <see cref="MaxSizeMm"/> (diameter or larger side), optionally only for some system types.</summary>
public sealed class ReserveRow
{
    public List<string> Systems { get; set; } = [];
    public double MaxSizeMm { get; set; }
    public double Reserve { get; set; } = EngineeringRulesPolicy.DefaultReserve;
}

/// <summary>
/// ADSK_Система_Имя by system type: one text for one or several system types (Т3 and Т4 share a name).
/// An entry matches a type name that equals it or ends with «_entry» (ADSK_Водопровод_В1 ↔ В1), case-insensitive.
/// </summary>
public sealed class SystemNameRule
{
    public List<string> Types { get; set; } = [];
    [JsonConverter(typeof(JsonStringEnumConverter))] public SystemDomain Domain { get; set; } = SystemDomain.Pipe;
    public string Name { get; set; } = "";
}

public sealed class SystemNameSettings : IJsonOnDeserializing
{
    /// <summary>Revision of <see cref="Defaults"/>; stored rules of an older revision get the newly added default types once.</summary>
    public const int CurrentDefaults = 2;
    public int DefaultsVersion { get; set; } = CurrentDefaults;
    void IJsonOnDeserializing.OnDeserializing() => DefaultsVersion = 0;
    public bool Enabled { get; set; }
    public string Target { get; set; } = EngineeringRulesPolicy.DefaultSystemTarget;
    public List<SystemNameRule> Rules { get; set; } = Defaults();

    /// <summary>Designations of ГОСТ 21.601 for water, sewerage, fire fighting and heating; wording is editable.</summary>
    public static List<SystemNameRule> Defaults()
    {
        SystemNameRule R(string types, string name) => new() { Types = [.. types.Split(',')], Domain = SystemDomain.Pipe, Name = name };
        return
        [
            R("В1,Холодное водоснабжение", "В1 - Внутренний хозяйственно-питьевой водопровод"),
            R("В2", "В2 - Внутренний противопожарный водопровод"),
            R("В3", "В3 - Внутренний производственный водопровод"),
            R("Т3,Т4,Горячее водоснабжение", "Т3,Т4 - Внутренний водопровод горячей воды, подающий и циркуляционный"),
            R("К1,Вентиляционное отверстие", "К1 - Внутренняя бытовая канализация"),
            R("К2", "К2 - Внутренние водостоки"),
            R("К3", "К3 - Внутренняя производственная канализация"),
            R("В21,Водяная система пожаротушения", "В21 - Автоматическое водяное пожаротушение"),
            R("В22,Дренчерная система пожаротушения", "В22 - Дренчерное пожаротушение"),
            R("Газовая система пожаротушения", "Автоматическое газовое пожаротушение"),
            R("Другие системы пожаротушения", "Прочие системы пожаротушения"),
            R("Т1,Т2", "Т1,Т2 - Теплоснабжение, подающий и обратный трубопроводы"),
            R("Т11,Т21", "Т11,Т21 - Система отопления, подающий и обратный трубопроводы"),
            R("Х1,Х2", "Х1,Х2 - Холодоснабжение, подающий и обратный трубопроводы"),
            R("Х3,Х4", "Х3,Х4 - Кондиционирование, подающий и обратный трубопроводы"),
            R("Х5", "Х5 - Кондиционирование, отвод конденсата"),
            R("Прочее", "Прочие трубопроводы")
        ];
    }

    /// <summary>
    /// Stored rules of an older defaults revision: default types that no rule mentions are added once — to the rule that already
    /// holds another type of the same default rule, otherwise as a new rule. Names written by the user are not changed.
    /// </summary>
    public bool AddNewDefaults()
    {
        if (DefaultsVersion >= CurrentDefaults) return false;
        DefaultsVersion = CurrentDefaults;
        bool changed = false;
        bool Known(string t) => Rules.Any(r => r.Types.Contains(t, StringComparer.CurrentCultureIgnoreCase));
        foreach (var def in Defaults())
        {
            var missing = def.Types.Where(t => !Known(t)).ToList();
            if (missing.Count == 0) continue;
            var host = Rules.FirstOrDefault(r => r.Domain == def.Domain && def.Types.Any(t => r.Types.Contains(t, StringComparer.CurrentCultureIgnoreCase)));
            if (host != null) host.Types.AddRange(missing);
            else Rules.Add(def);
            changed = true;
        }
        return changed;
    }

    public void Validate()
    {
        if (Enabled && string.IsNullOrWhiteSpace(Target)) throw new InvalidOperationException("Укажите параметр для имени системы.");
        foreach (var r in Rules)
        {
            r.Types = r.Types.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            if (r.Types.Count == 0) throw new InvalidOperationException("Имена систем: у правила не указаны типы систем.");
            if (string.IsNullOrWhiteSpace(r.Name)) throw new InvalidOperationException($"Имена систем: пустое имя для «{string.Join(", ", r.Types)}».");
        }
    }
}

public sealed class EngineeringRuleSet
{
    public const int CurrentVersion = 3;
    public int Version { get; set; } = CurrentVersion;
    public List<EngineeringRule> Rules { get; set; } = [];
    public List<ConditionalRuleGroup> ConditionalGroups { get; set; } = [];
    public bool ConditionalRulesInitialized { get; set; }
    public bool RuleListInitialized { get; set; }
    public int PipeNameDefaultsVersion { get; set; }
    public int PipeQuantityDefaultsVersion { get; set; }
    public int EnabledDefaultsVersion { get; set; }
    public WallThicknessSettings WallThickness { get; set; } = new();
    public SystemNameSettings SystemNames { get; set; } = new();

    public void PrepareDefaults()
    {
        if (!ConditionalRulesInitialized && Rules.Count == 0 && ConditionalGroups.Count == 0 && !RuleListInitialized)
            Rules.AddRange(DefaultRules());
        EnsureConditionalGroups();
        if (!RuleListInitialized)
        {
            foreach (var (category, _) in EngineeringRulesPolicy.Categories)
                foreach (bool quantity in new[] { false, true })
                    if ((quantity || EngineeringRulesPolicy.SupportsNaming(category))
                        && !ConditionalGroups.Any(g => g.Category == category && g.IsQuantity == quantity))
                        ConditionalGroups.Add(new ConditionalRuleGroup { Category = category, IsQuantity = quantity,
                            Target = quantity ? EngineeringRulesPolicy.DefaultQuantityTarget : EngineeringRulesPolicy.DefaultNameTarget,
                            Otherwise = EngineeringRulesPolicy.DefaultFormula(category) });
            RuleListInitialized = true;
        }
        ApplyPipeNameDefaults();
        ApplyPipeQuantityDefaults();
        if (EnabledDefaultsVersion >= 1) return;
        foreach (var group in ConditionalGroups.Where(g => (g.IsQuantity || EngineeringRulesPolicy.SupportsNaming(g.Category))
            && g.Target == (g.IsQuantity ? EngineeringRulesPolicy.DefaultQuantityTarget : EngineeringRulesPolicy.DefaultNameTarget)))
            foreach (var formula in group.Conditions.Select(c => c.Formula).Append(group.Otherwise))
            {
                var standard = EngineeringRulesPolicy.DefaultFormula(group.Category);
                formula.Category = group.Category;
                if (!formula.IsActive && formula.Parts.Count == 0)
                {
                    formula.QuantitySource = standard.QuantitySource; formula.Unit = standard.Unit;
                    formula.Decimals = standard.Decimals; formula.Reserve = standard.Reserve; formula.Rounding = standard.Rounding;
                }
                if (formula.Parts.Count == 0) formula.Parts = standard.Parts;
                if (string.IsNullOrWhiteSpace(formula.QuantitySource)) formula.QuantitySource = standard.QuantitySource;
                if (group.IsQuantity) formula.QuantityEnabled = true; else formula.NameEnabled = true;
            }
        SystemNames.Enabled = true;
        EnabledDefaultsVersion = 1;
    }

    public void ValidateConditionalGroups()
    {
        var groups = ConditionalGroups.Where(g => g.IsQuantity || EngineeringRulesPolicy.SupportsNaming(g.Category)).ToList();
        if (groups.GroupBy(g => (g.Category, g.Target)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("В категории уже есть правило для этого заполняемого параметра.");
        foreach (var group in groups) group.Validate();
    }

    /// <summary>Legacy mark rules become explicit conditions, without changing their formulas or coverage.</summary>
    public void EnsureConditionalGroups()
    {
        if (ConditionalRulesInitialized) return;
        foreach (var category in Rules.Select(r => r.Category).Distinct())
            foreach (bool quantity in new[] { false, true })
            {
                // Once converted, later edits or deletion of conditions must never restore legacy branches.
                if (!quantity && !EngineeringRulesPolicy.SupportsNaming(category)) continue;
                if (ConditionalGroups.Any(g => g.Category == category && g.IsQuantity == quantity)) continue;
                foreach (var legacy in Rules.Where(r => r.Category == category).GroupBy(r => quantity ? r.QuantityTarget : r.NameTarget))
                    ConditionalGroups.Add(new ConditionalRuleGroup
                    {
                        Category = category, IsQuantity = quantity, Target = legacy.Key,
                        Conditions = legacy.Select(r => new FormulaCondition
                        {
                            Parameter = "Маркировка типоразмера", Value = r.TypeMark ?? "", Formula = r.Clone()
                        }).ToList(),
                        Otherwise = new EngineeringRule { Category = category }
                    });
            }
        ConditionalRulesInitialized = true;
    }

    /// <summary>Requested pipe naming scheme, applied once on the editor copy, never on every reopen.</summary>
    public void ApplyPipeNameDefaults()
    {
        if (PipeNameDefaultsVersion >= 1) return;
        const string category = "OST_PipeCurves", comment = "Комментарии к типоразмеру";
        EngineeringRule Formula(string diameter) => new()
        {
            Category = category, NameEnabled = true,
            Parts = [new() { Parameter = comment }, new() { Parameter = diameter, Prefix = " ø" },
                new() { Parameter = EngineeringRulesPolicy.WallThicknessSource, Prefix = "x" }]
        };
        var group = ConditionalGroups.FirstOrDefault(g => g.Category == category && !g.IsQuantity && g.Target == EngineeringRulesPolicy.DefaultNameTarget);
        if (group == null)
        {
            group = new ConditionalRuleGroup { Category = category, Target = EngineeringRulesPolicy.DefaultNameTarget };
            ConditionalGroups.Add(group);
        }
        group.Conditions = [new() { Parameter = comment, Value = "Труба стальная электросварная прямошовная", Formula = Formula("Внешний диаметр") }];
        group.Otherwise = Formula("Диаметр");
        PipeNameDefaultsVersion = 1;
    }

    /// <summary>Pipe length reserve by exact type comments; one-time upgrade, preserving later user edits.</summary>
    public void ApplyPipeQuantityDefaults()
    {
        if (PipeQuantityDefaultsVersion >= 1) return;
        const string category = "OST_PipeCurves";
        EngineeringRule Formula(double reserve) => new()
        {
            Category = category, QuantityEnabled = true, QuantitySource = "Длина", Unit = QuantityUnit.Meter,
            Reserve = reserve, Decimals = 6, Rounding = QuantityRounding.Nearest
        };
        var group = ConditionalGroups.FirstOrDefault(g => g.Category == category && g.IsQuantity && g.Target == EngineeringRulesPolicy.DefaultQuantityTarget);
        if (group == null)
        {
            group = new ConditionalRuleGroup { Category = category, IsQuantity = true, Target = EngineeringRulesPolicy.DefaultQuantityTarget };
            ConditionalGroups.Add(group);
        }
        group.Conditions = new[] { "Труба стальная водогазопроводная", "Труба стальная водогазопроводная оцинкованная",
            "Труба стальная электросварная прямошовная", "Труба чугунная SML" }
            .Select(comment => new FormulaCondition { Parameter = "Комментарии к типоразмеру", Value = comment, Formula = Formula(1.1) }).ToList();
        group.Otherwise = Formula(1.3);
        PipeQuantityDefaultsVersion = 1;
    }

    public IReadOnlyList<EngineeringRule> ResolveConditionals(string category, string? mark, Func<string, string?> text)
    {
        if (EngineeringRulesPolicy.IsIgnored(mark)) return [];
        var groups = ConditionalGroups.Where(g => g.Category == category).ToList();
        if (groups.Count == 0) return !ConditionalRulesInitialized && Resolve(category, mark) is { } legacy ? [legacy] : [];
        var results = new List<EngineeringRule>();
        foreach (var group in groups)
        {
            if (!group.IsQuantity && !EngineeringRulesPolicy.SupportsNaming(category)) continue;
            var f = group.Select(p => p == "Маркировка типоразмера" ? mark ?? "" : text(p));
            var result = new EngineeringRule { Category = category };
            if (!group.IsQuantity)
            {
                result.NameEnabled = f.NameEnabled; result.NameTarget = group.Target; result.Parts = f.Parts;
            }
            else
            {
                result.QuantityEnabled = f.QuantityEnabled; result.QuantityTarget = group.Target;
                result.QuantitySource = f.QuantitySource; result.Unit = f.Unit; result.Reserve = f.Reserve;
                result.Decimals = f.Decimals; result.Rounding = f.Rounding; result.ReserveBySize = f.ReserveBySize;
            }
            if (result.IsActive) results.Add(result);
        }
        return results;
    }

    /// <summary>Rule of the element's category and type mark. Marks without a rule and «игнорировать» are not processed.</summary>
    public EngineeringRule? Resolve(string category, string? typeMark)
    {
        if (EngineeringRulesPolicy.IsIgnored(typeMark)) return null;
        string? mark = EngineeringRulesPolicy.NormalizeMark(typeMark);
        bool names = EngineeringRulesPolicy.SupportsNaming(category);
        var rule = Rules.FirstOrDefault(r => r.Category == category && r.TypeMark == mark && (r.QuantityEnabled || names && r.NameEnabled));
        if (names || rule is not { NameEnabled: true }) return rule;
        var quantity = rule.Clone();
        quantity.NameEnabled = false;
        return quantity;
    }

    public EngineeringRule? Find(string category, string? typeMark)
    {
        string? mark = EngineeringRulesPolicy.NormalizeMark(typeMark);
        return Rules.FirstOrDefault(r => r.Category == category && r.TypeMark == mark);
    }

    /// <summary>Replaces or removes the rule with the same scope. Inactive rules are not stored.</summary>
    public void Put(EngineeringRule rule)
    {
        rule.TypeMark = EngineeringRulesPolicy.NormalizeMark(rule.TypeMark);
        rule.TypeKey = null;
        // Validate first: an invalid edit must not drop the rule that is already stored.
        if (rule.IsActive) rule.Validate();
        Rules.RemoveAll(r => r.Category == rule.Category && r.TypeMark == rule.TypeMark);
        if (rule.IsActive) Rules.Add(rule);
    }

    /// <summary>Import: copies only the selected rules (matched by category and mark); the wall table on request.</summary>
    public int Merge(EngineeringRuleSet incoming, Func<EngineeringRule, bool> selected, bool wallThickness, bool systemNames = false)
    {
        if (systemNames)
        {
            incoming.SystemNames.Validate();
            SystemNames = JsonSerializer.Deserialize<SystemNameSettings>(JsonSerializer.Serialize(incoming.SystemNames))!;
        }
        int count = 0;
        foreach (var rule in incoming.Rules.Where(selected)) { Put(rule.Clone()); count++; }
        if (wallThickness)
        {
            incoming.WallThickness.Validate();
            WallThickness = JsonSerializer.Deserialize<WallThicknessSettings>(JsonSerializer.Serialize(incoming.WallThickness))!;
        }
        return count;
    }

    /// <summary>Version 1 rules: type rules become rules of their type's mark; they win over category rules.</summary>
    public bool Migrate(Func<string, string?> markOfType)
    {
        if (!Rules.Any(r => r.TypeKey != null)) return false;
        var legacy = Rules.ToList();
        Rules.Clear();
        foreach (var rule in legacy.OrderBy(r => r.TypeKey != null))
        {
            if (rule.TypeKey != null) rule.TypeMark = markOfType(rule.TypeKey);
            if (EngineeringRulesPolicy.IsIgnored(rule.TypeMark)) continue;
            Put(rule);
        }
        return true;
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>
    /// Формулы по умолчанию для труб, гибких труб и количества изоляции труб по маркировке типоразмера.
    /// Подставляются в окно, пока в проекте нет сохранённых правил. Запас — «Нет», округление — «Нет».
    /// </summary>
    public static List<EngineeringRule> DefaultRules()
    {
        const string comment = "Комментарии к типоразмеру";
        static NamePart P(string parameter, string prefix = "") => new() { Parameter = parameter, Prefix = prefix };
        static EngineeringRule R(string category, string? mark, string source, QuantityUnit unit, params NamePart[] parts) => new()
        {
            Category = category, TypeMark = mark, NameEnabled = EngineeringRulesPolicy.SupportsNaming(category),
            Parts = [.. parts], QuantityEnabled = true, QuantitySource = source,
            Reserve = 1, Unit = unit, Decimals = 6, Rounding = QuantityRounding.Nearest
        };
        return
        [
            R("OST_PipeCurves", null, "Длина", QuantityUnit.Meter, P(comment), P("Внешний диаметр", " ø"), P(EngineeringRulesPolicy.WallThicknessSource, "x")),
            R("OST_PipeCurves", "Ду х Стенка", "Длина", QuantityUnit.Meter, P(comment), P("Диаметр", " ø"), P(EngineeringRulesPolicy.WallThicknessSource, "x")),
            R("OST_FlexPipeCurves", "Гофры", EngineeringRulesPolicy.CountSource, QuantityUnit.Piece, P(comment)),
            R("OST_FlexPipeCurves", "Подводки", EngineeringRulesPolicy.CountSource, QuantityUnit.Piece, P(comment), P("Диаметр", " ø")),
            R("OST_PipeInsulations", "Трубки", "Длина", QuantityUnit.Meter),
            R("OST_PipeInsulations", "Трубки цветные", "Длина", QuantityUnit.Meter),
            R("OST_PipeInsulations", "Цилиндры", "Объем", QuantityUnit.Piece),
        ];
    }

    public static EngineeringRuleSet FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new EngineeringRuleSet();
        EngineeringRuleSet set;
        try { set = JsonSerializer.Deserialize<EngineeringRuleSet>(json) ?? throw new InvalidOperationException("Повреждены правила апдейтера."); }
        catch (JsonException e) { throw new InvalidOperationException("Повреждены правила апдейтера: " + e.Message); }
        if (set.Version is < 1 or > CurrentVersion) throw new InvalidOperationException($"Неизвестная версия правил апдейтера: {set.Version}.");
        set.Rules ??= [];
        set.ConditionalGroups ??= [];
        set.WallThickness ??= new();
        set.WallThickness.Rows ??= WallThicknessSettings.Defaults();
        set.SystemNames ??= new();
        set.SystemNames.Rules ??= SystemNameSettings.Defaults();
        set.SystemNames.AddNewDefaults();
        if (set.Version == 1)
        {
            // In version 1 a rule without a type key covered the whole category; its closest equivalent is «Без типа».
            foreach (var r in set.Rules) r.TypeMark = null;
            set.Version = CurrentVersion;
        }
        set.Version = CurrentVersion;
        return set;
    }
}

/// <summary>What the rules window needs from the project; Revit implementation — RevitUpdaterModel.</summary>
internal interface IUpdaterModel
{
    (bool Exists, string? Mark) SelectPreview(string category, ConditionalRuleGroup group, EngineeringRule formula, WallThicknessSettings walls)
    {
        foreach (var (mark, _) in Marks(category))
            if (!EngineeringRulesPolicy.IsIgnored(mark) && HasSample(category, mark) && ReferenceEquals(formula,
                group.Select(p => p == "Маркировка типоразмера" ? mark ?? "" : Text(category, mark, p, walls))))
                return (true, mark);
        return (false, null);
    }
    /// <summary>Types of the category grouped by «Маркировка типоразмера» (null — without mark).</summary>
    IReadOnlyList<(string? Mark, List<string> Types)> Marks(string category);
    bool HasSample(string category, string? mark);
    IReadOnlyList<string> Parameters(string category, string? mark);
    IReadOnlyList<string> TextTargets(string category, string? mark);
    IReadOnlyList<string> NumberTargets(string category, string? mark);
    string? Text(string category, string? mark, string parameter, WallThicknessSettings walls);
    (SourceKind Kind, double Value)? Source(string category, string? mark, string parameter);
    /// <summary>Size (mm) and system type names of the example element, for the reserve preview.</summary>
    (double? SizeMm, IReadOnlyList<string> Systems) ReserveContext(string category, string? mark);
    /// <summary>System types of the project for the «Имена систем» preview.</summary>
    IReadOnlyList<(string Type, SystemDomain Domain)> SystemTypes();
}

public static class EngineeringRulesPolicy
{
    /// <summary>Supported categories (BuiltInCategory names) with their labels, in list order.</summary>
    public static readonly (string Key, string Label)[] Categories =
    [
        ("OST_PipeCurves", "Трубы"),
        ("OST_FlexPipeCurves", "Гибкие трубы"),
        ("OST_PipeFitting", "Соединительные детали трубопроводов"),
        ("OST_PipeInsulations", "Материал изоляции трубопровода"),
        ("OST_DuctCurves", "Воздуховоды"),
        ("OST_FlexDuctCurves", "Гибкие воздуховоды"),
        ("OST_DuctFitting", "Соединительные детали воздуховодов"),
        ("OST_DuctInsulations", "Материал изоляции воздуховода"),
        ("OST_CableTray", "Кабельные лотки"),
        ("OST_Conduit", "Короба")
    ];
    public static EngineeringRule DefaultFormula(string category, string? mark = null)
    {
        var (source, unit, decimals) = category switch
        {
            "OST_PipeCurves" or "OST_FlexPipeCurves" or "OST_DuctCurves" or "OST_FlexDuctCurves" or "OST_CableTray" or "OST_Conduit" => ("Длина", QuantityUnit.Meter, 2),
            "OST_DuctFitting" => (EngineeringRulesPolicy.FittingAreaParameter, QuantityUnit.SquareMeter, 2),
            "OST_PipeInsulations" => (EngineeringRulesPolicy.HostLengthSource, QuantityUnit.Meter, 2),
            "OST_DuctInsulations" => (EngineeringRulesPolicy.HostAreaSource, QuantityUnit.SquareMeter, 2),
            _ => (EngineeringRulesPolicy.CountSource, QuantityUnit.Piece, 0)
        };
        string size = category is "OST_DuctFitting" or "OST_PipeFitting" ? EngineeringRulesPolicy.FittingSizeSource : EngineeringRulesPolicy.SortedSizeSource;
        return new EngineeringRule
        {
            Category = category, TypeMark = mark, NameEnabled = SupportsNaming(category), QuantityEnabled = true,
            Parts = [new NamePart { Parameter = EngineeringRulesPolicy.TypeNameSource }, new NamePart { Parameter = size, Prefix = " " }],
            QuantitySource = source, Unit = unit, Decimals = decimals, Reserve = EngineeringRulesPolicy.DefaultReserve
        };
    }

    internal static bool SupportsNaming(string category) => category != "OST_PipeInsulations";

    public static string CategoryLabel(string key) => Categories.FirstOrDefault(c => c.Key == key).Label ?? key;

    public const string DefaultNameTarget = "ADSK_Наименование";
    public const string DefaultQuantityTarget = "ADSK_Количество";
    public const string FittingAreaParameter = "ADSK_Размер_Площадь";
    public const string DefaultSystemTarget = "ADSK_Система_Имя";
    public const double DefaultReserve = 1.1;

    /// <summary>
    /// Reserve of an element: rows for its system first, then rows for any system; within each group the rows apply from the
    /// smaller size up (first limit ≥ size). No size or no matching row — the rule's general reserve.
    /// </summary>
    public static double ReserveFor(EngineeringRule rule, double? sizeMm, IEnumerable<string> systemTypes)
    {
        if (sizeMm is not { } size || size <= 0 || rule.ReserveBySize.Count == 0) return rule.Reserve;
        var types = systemTypes.ToList();
        double? Pick(bool specific) => rule.ReserveBySize
            .Where(r => specific ? r.Systems.Count > 0 && r.Systems.Any(e => types.Any(t => SystemMatches(e, t))) : r.Systems.Count == 0)
            .OrderBy(r => r.MaxSizeMm).FirstOrDefault(r => size <= r.MaxSizeMm + 1e-6)?.Reserve;
        return Pick(true) ?? Pick(false) ?? rule.Reserve;
    }

    /// <summary>Categories that get ADSK_Система_Имя besides the name/quantity categories.</summary>
    public static readonly string[] SystemCategories =
    [
        "OST_PipeCurves", "OST_FlexPipeCurves", "OST_PipeFitting", "OST_PipeAccessory", "OST_PipeInsulations",
        "OST_DuctCurves", "OST_FlexDuctCurves", "OST_DuctFitting", "OST_DuctAccessory", "OST_DuctInsulations", "OST_DuctTerminal",
        "OST_PlumbingFixtures", "OST_MechanicalEquipment", "OST_Sprinklers"
    ];

    public static bool SystemMatches(string entry, string systemType)
    {
        entry = entry.Trim(); systemType = systemType.Trim();
        if (entry.Length == 0) return false;
        return string.Equals(entry, systemType, StringComparison.CurrentCultureIgnoreCase)
            || systemType.EndsWith("_" + entry, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// System name of an element from its system types (several for multi-system equipment): the first matching rule per
    /// type; only the first system is used. Null when it has no matching rule — the parameter is left as is.
    /// </summary>
    public static string? SystemName(SystemNameSettings settings, IEnumerable<(string Type, SystemDomain Domain)> types)
    {
        foreach (var (type, domain) in types)
        {
            var rule = settings.Rules.FirstOrDefault(r => (r.Domain == SystemDomain.Any || r.Domain == domain) && r.Types.Any(t => SystemMatches(t, type)));
            return rule?.Name.Trim();
        }
        return null;
    }
    public const string IgnoreMark = "игнорировать";
    public const string NoTypeLabel = "Без типа";
    /// <summary>Virtual sources. Real Revit parameter names never start with '@'.</summary>
    public const string CountSource = "@Штук";
    public const string SortedSizeSource = "@Размер";
    public const string TypeNameSource = "@Тип";
    public const string FamilyNameSource = "@Семейство";
    public const string WallThicknessSource = "@Толщина стенки";
    public const string FittingSizeSource = "@Размер фитинга";
    public const string HostLengthSource = "@Длина основы";
    public const string HostAreaSource = "@Площадь основы";
    public static readonly string[] VirtualNameSources = [TypeNameSource, FamilyNameSource, SortedSizeSource, WallThicknessSource, FittingSizeSource];
    public static readonly string[] VirtualQuantitySources = [CountSource, HostLengthSource, HostAreaSource];

    public static string? NormalizeMark(string? mark) => string.IsNullOrWhiteSpace(mark) ? null : mark.Trim();
    public static bool IsIgnored(string? mark) => string.Equals(NormalizeMark(mark), IgnoreMark, StringComparison.CurrentCultureIgnoreCase);

    public static string ComposeName(IEnumerable<NamePart> parts, Func<string, string?> value)
    {
        var text = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            string? v = value(part.Parameter)?.Trim();
            if (string.IsNullOrEmpty(v)) continue;
            text.Append(part.Prefix).Append(v).Append(part.Suffix);
        }
        return text.ToString().Trim();
    }

    /// <summary>Units the user may choose for a source of the given kind.</summary>
    public static QuantityUnit[] UnitsFor(SourceKind kind) => kind switch
    {
        SourceKind.Count => [QuantityUnit.Piece],
        SourceKind.Length => [QuantityUnit.Meter, QuantityUnit.Millimeter],
        SourceKind.Area => [QuantityUnit.SquareMeter],
        _ => [QuantityUnit.Piece]
    };

    public static string UnitLabel(QuantityUnit unit) => unit switch
    {
        QuantityUnit.Meter => "м", QuantityUnit.Millimeter => "мм", QuantityUnit.SquareMeter => "м²", _ => "шт"
    };

    /// <summary>
    /// <paramref name="value"/> is in SI (m, m²) for length/area, raw for count/number.
    /// Returns null with a reason when the unit does not match the source kind.
    /// </summary>
    public static double? Quantity(EngineeringRule rule, SourceKind kind, double value, out string? error, double? reserve = null)
    {
        error = null;
        if (!double.IsFinite(value)) { error = "исходное значение не число"; return null; }
        if (kind != SourceKind.Number && !UnitsFor(kind).Contains(rule.Unit))
        { error = $"единица «{UnitLabel(rule.Unit)}» не подходит к источнику"; return null; }
        double converted = rule.Unit == QuantityUnit.Millimeter ? value * 1000 : value;
        return Round(converted * (reserve ?? rule.Reserve), rule.Decimals, rule.Rounding);
    }

    public static double Round(double value, int decimals, QuantityRounding mode)
    {
        double scale = Math.Pow(10, decimals);
        double scaled = value * scale;
        // Floating products such as 1.1 * 10 land just above an integer; do not bump them a whole step.
        double nearest = Math.Round(scaled, MidpointRounding.AwayFromZero);
        if (mode == QuantityRounding.Nearest || Math.Abs(scaled - nearest) < 1e-7) return nearest / scale;
        return Math.Ceiling(scaled) / scale;
    }

    /// <summary>For a rectangular section the larger side goes first: 500×300, never 300×500.</summary>
    public static string SortedSize(double widthMm, double heightMm)
    {
        double a = Math.Max(widthMm, heightMm), b = Math.Min(widthMm, heightMm);
        return Format(a, 0) + "×" + Format(b, 0);
    }

    /// <summary>Pipe wall: half the difference of the actual outer and inner diameters.</summary>
    public static double? PipeWall(double outerMm, double innerMm) =>
        outerMm > 0 && innerMm > 0 && outerMm > innerMm ? Math.Round((outerMm - innerMm) / 2, 3) : null;

    /// <summary>
    /// Duct wall from the table: rows of the shape and protection, ascending by size; the first limit ≥ size wins.
    /// A fire-protected duct without a matching protected row uses the ordinary rows.
    /// </summary>
    public static double? DuctWall(IEnumerable<WallThicknessRow> rows, SectionShape shape, double sizeMm, bool fireProtected)
    {
        double? Pick(bool fire) => rows.Where(r => r.Shape == shape && r.FireProtected == fire).OrderBy(r => r.MaxSizeMm)
            .FirstOrDefault(r => sizeMm <= r.MaxSizeMm + 1e-6)?.ThicknessMm;
        return fireProtected ? Pick(true) ?? Pick(false) : Pick(false);
    }

    /// <summary>
    /// Fitting size from its connectors: round «Ø250», rectangular «500×300» (larger side first); distinct sizes from the
    /// largest section down, joined by «-» — elbow «Ø250», transition «500×300-400×200», tee/takeoff «Ø250-Ø160».
    /// </summary>
    public static string FittingSize(IEnumerable<(double DiameterMm, double WidthMm, double HeightMm)> connectors)
    {
        var sizes = connectors
            .Select(c => c.DiameterMm > 0 ? (Area: Math.PI * c.DiameterMm * c.DiameterMm / 4, Text: "Ø" + Format(c.DiameterMm, 0))
                : (Area: c.WidthMm * c.HeightMm, Text: SortedSize(c.WidthMm, c.HeightMm)))
            .Where(s => s.Area > 0)
            .OrderByDescending(s => s.Area)
            .Select(s => s.Text).Distinct().ToList();
        return string.Join("-", sizes);
    }

    public static string Format(double value, int decimals) =>
        Math.Round(value, decimals).ToString(decimals <= 0 ? "0" : "0." + new string('#', decimals), CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>True when the stored value already equals what would be written — keeps the updater idempotent.</summary>
    public static bool SameNumber(double? current, double next) => current.HasValue && Math.Abs(current.Value - next) < 1e-9;
}
