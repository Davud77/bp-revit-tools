using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace BotPlus.Eom;

/// <summary>
/// Связь светильника БД с семейством Revit. В связанном семействе для светильника создаётся тип с его именем,
/// и в параметры типа записываются данные БД: габариты, мощность, поток, cos φ, напряжение, наименование, марка,
/// код, изготовитель, масса, IP. Так данные семейства и БД совпадают, а спецификация берёт их из модели.
/// </summary>
internal static class FixtureSync
{
    enum Kind { Text, Number, Integer, Length, Power, ApparentPower, Flux, Voltage, Mass }

    sealed record Field(string Label, Kind Kind, Func<LuminaireItem, object?> Value, string[] Names, BuiltInParameter[] BuiltIns);

    internal static readonly string[] LengthNames = ["ADSK_Размер_Длина", "Длина", "Длина светильника", "Length"];
    internal static readonly string[] WidthNames = ["ADSK_Размер_Ширина", "Ширина", "Ширина светильника", "Width"];
    internal static readonly string[] HeightNames = ["ADSK_Размер_Высота", "Высота светильника", "Высота корпуса"];

    static readonly Field[] Fields =
    [
        new("Длина", Kind.Length, l => l.LengthMm, LengthNames, []),
        new("Ширина", Kind.Length, l => l.WidthMm, WidthNames, []),
        new("Высота", Kind.Length, l => l.HeightMm, HeightNames, []),
        new("Наименование", Kind.Text, l => l.Description.Length > 0 ? l.Description : l.Name, ["ADSK_Наименование"], [BuiltInParameter.ALL_MODEL_DESCRIPTION]),
        new("Марка", Kind.Text, l => l.Name, ["ADSK_Марка"], [BuiltInParameter.ALL_MODEL_MODEL]),
        new("Код изделия", Kind.Text, l => l.Code, ["ADSK_Код изделия"], []),
        new("Изготовитель", Kind.Text, l => l.Manufacturer, ["ADSK_Завод-изготовитель", "ADSK_Изготовитель"], [BuiltInParameter.ALL_MODEL_MANUFACTURER]),
        new("Масса", Kind.Mass, l => l.MassKg, ["ADSK_Масса", "Масса"], []),
        new("Мощность", Kind.Power, l => l.Power, ["ADSK_Номинальная мощность", "Номинальная мощность", "Мощность", "Активная мощность"], [BuiltInParameter.FBX_LIGHT_WATTAGE]),
        new("Полная мощность", Kind.ApparentPower, l => l.PowerFactor > 0 ? l.Power / l.PowerFactor : 0, ["ADSK_Полная мощность", "Полная мощность", "Apparent Load"], []),
        new("Коэффициент мощности", Kind.Number, l => l.PowerFactor, ["ADSK_Коэффициент мощности", "Коэффициент мощности", "Power Factor"], []),
        new("Напряжение", Kind.Voltage, l => l.Voltage, ["ADSK_Напряжение", "Напряжение", "Voltage"], []),
        new("Световой поток", Kind.Flux, l => l.Flux * Math.Clamp(l.Efficiency, 0.01, 1), ["ADSK_Световой поток", "Световой поток"], [BuiltInParameter.FBX_LIGHT_LIMUNOUS_FLUX]),
        new("Степень защиты", Kind.Text, l => l.IP, ["ADSK_Степень защиты", "Степень защиты IP", "Степень защиты"], []),
        new("Количество ламп", Kind.Integer, l => l.LampCount, ["ADSK_Количество ламп", "Количество ламп"], []),
    ];

    /// <summary>Имя типа Revit для светильника: недопустимые в именах типов символы заменяются.</summary>
    internal static string TypeName(LuminaireItem lum)
    {
        string name = Regex.Replace(lum.Name, @"[\\:{}\[\]|;<>?`~]", "-").Trim();
        return name.Length > 0 ? name : "Светильник";
    }

    /// <summary>Значение параметра длины типа, мм (0 — параметра нет).</summary>
    internal static double ReadMm(Element type, string[] names)
    {
        foreach (var n in names)
            if (type.LookupParameter(n) is { StorageType: StorageType.Double, HasValue: true } p && p.Definition.GetDataType() == SpecTypeId.Length)
                return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters);
        return 0;
    }

    /// <summary>
    /// Тип для расстановки светильника: тип с именем светильника в связанном семействе (создаётся копированием связанного типа)
    /// с записанными данными БД. Требует транзакции. Описание результата добавляется в report.
    /// </summary>
    internal static FamilySymbol Ensure(Document doc, LuminaireItem lum, List<string> report)
    {
        var all = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
        var linked = all.FirstOrDefault(s => $"{s.FamilyName}: {s.Name}" == lum.RevitType)
            ?? throw new InvalidOperationException($"Семейство «{lum.RevitType}» светильника «{lum.Name}» не найдено в проекте. Свяжите светильник заново.");
        string name = TypeName(lum);
        var symbol = all.FirstOrDefault(s => s.Family.Id == linked.Family.Id && s.Name == name);
        bool created = symbol == null;
        symbol ??= (FamilySymbol)linked.Duplicate(name);

        var written = new List<string>(); var missing = new List<string>(); var failed = new List<string>();
        foreach (var field in Fields)
        {
            var value = field.Value(lum);
            if (value is null or "" || value is double d && d <= 0 || value is int i && i <= 0) continue;
            var parameters = field.Names.Select(symbol.LookupParameter).Concat(field.BuiltIns.Select(symbol.get_Parameter)).OfType<Parameter>()
                .DistinctBy(p => p.Id.Value).ToList();
            if (parameters.Count == 0) { missing.Add(field.Label); continue; }
            bool any = false; string? error = null;
            foreach (var p in parameters)
            {
                if (p.IsReadOnly) { error ??= "только чтение"; continue; }
                try { if (Set(p, field.Kind, value)) any = true; else error ??= "тип параметра не подходит"; }
                catch (Exception e) { error ??= e.Message; }
            }
            if (any) written.Add(field.Label); else failed.Add($"{field.Label} ({error})");
        }
        report.Add($"«{lum.Name}» → тип «{symbol.FamilyName}: {name}» {(created ? "создан" : "обновлён")}." +
            (written.Count > 0 ? $" Записано: {string.Join(", ", written)}." : "") +
            (missing.Count > 0 ? $" Нет параметров в семействе: {string.Join(", ", missing)}." : "") +
            (failed.Count > 0 ? $" Не записано: {string.Join(", ", failed)}." : ""));
        lum.RevitType = $"{symbol.FamilyName}: {symbol.Name}";
        return symbol;
    }

    static bool Set(Parameter p, Kind kind, object value)
    {
        switch (p.StorageType)
        {
            case StorageType.String:
                return p.Set(value is double d ? d.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")) : value.ToString());
            case StorageType.Integer:
                return value is double or int && p.Set((int)Math.Round(Convert.ToDouble(value)));
            case StorageType.Double:
                if (value is not (double or int)) return false;
                double v = Convert.ToDouble(value);
                var spec = p.Definition.GetDataType();
                if (!UnitUtils.IsMeasurableSpec(spec)) return p.Set(v);
                ForgeTypeId[] units = kind switch
                {
                    Kind.Length => [UnitTypeId.Millimeters],
                    Kind.Power or Kind.ApparentPower => [UnitTypeId.Watts, UnitTypeId.VoltAmperes],
                    Kind.Flux => [UnitTypeId.Lumens],
                    Kind.Voltage => [UnitTypeId.Volts],
                    Kind.Mass => [UnitTypeId.Kilograms],
                    _ => []
                };
                var unit = units.FirstOrDefault(u => UnitUtils.IsValidUnit(spec, u));
                if (unit == null) return kind is Kind.Number or Kind.Integer && p.Set(v);
                return p.Set(UnitUtils.ConvertToInternalUnits(v, unit));
            default:
                return false;
        }
    }
}
