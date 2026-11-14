using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace BotPlus.Eom;

/// <summary>Прибор или щит: категория, точка вставки и габарит в плане (для индикаторов).</summary>
internal sealed record DeviceInfo(ElementId Id, BuiltInCategory Cat, string Category, string Name, XYZ? Point, XYZ? Min, XYZ? Max)
{
    internal bool IsPanel => Cat == BuiltInCategory.OST_ElectricalEquipment;
}
/// <summary>Назначение группы для схемы и цвета.</summary>
internal enum CircuitKind { Lighting, Sockets, Power, Feeder }
internal sealed record CircuitInfo(ElementId Id, CircuitInput Input, double RatingA, string Cable, IReadOnlyList<DeviceInfo> Devices)
{
    internal CircuitKind Kind =>
        Devices.Any(d => d.IsPanel) ? CircuitKind.Feeder :
        Devices.Count > 0 && Devices.All(d => d.Cat is BuiltInCategory.OST_LightingFixtures or BuiltInCategory.OST_LightingDevices) ? CircuitKind.Lighting :
        Devices.Count > 0 && Devices.All(d => d.Cat == BuiltInCategory.OST_ElectricalFixtures) ? CircuitKind.Sockets : CircuitKind.Power;
}
/// <summary>Щит: свои группы и группа вышестоящего щита, от которой он питается (null — ввод/источник).</summary>
internal sealed record PanelInfo(ElementId Id, string Name, string Type, IReadOnlyList<CircuitInfo> Circuits, ElementId? FeedCircuit, ElementId? FeedPanel);

/// <summary>Снимок электрической сети проекта: щиты, группы (цепи Revit) и неподключённые приборы.</summary>
internal sealed class Network
{
    internal readonly List<PanelInfo> Panels = [];
    internal readonly List<CircuitInfo> Orphans = [];
    internal readonly List<DeviceInfo> Unconnected = [];

    internal static readonly BuiltInCategory[] DeviceCategories =
    [
        BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingDevices,
        BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_CommunicationDevices, BuiltInCategory.OST_DataDevices,
        BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_SecurityDevices, BuiltInCategory.OST_MechanicalEquipment
    ];

    static double Value(Element e, BuiltInParameter id, ForgeTypeId unit)
    {
        var p = e.get_Parameter(id);
        return p is { StorageType: StorageType.Double, HasValue: true } ? UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit) : 0;
    }
    static string Text(Element e, BuiltInParameter id) => e.get_Parameter(id)?.AsString() ?? e.get_Parameter(id)?.AsValueString() ?? "";

    internal static DeviceInfo Device(Element e)
    {
        var box = e.get_BoundingBox(null);
        return new(e.Id, e.Category?.BuiltInCategory ?? BuiltInCategory.INVALID, e.Category?.Name ?? "", e is FamilyInstance fi ? $"{fi.Symbol.FamilyName}: {fi.Name}" : e.Name, Point(e), box?.Min, box?.Max);
    }

    internal static XYZ? Point(Element e) => e.Location switch
    {
        LocationPoint lp => lp.Point,
        LocationCurve lc => lc.Curve.Evaluate(0.5, true),
        _ => e.get_BoundingBox(null) is { } box ? (box.Min + box.Max) / 2 : null
    };

    /// <summary>Соединитель семейства сообщает «Силовой — симметричный/несимметричный», а цепь Revit — PowerCircuit; все три считаются силовыми.</summary>
    internal static bool IsPower(ElectricalSystemType type) =>
        type is ElectricalSystemType.PowerCircuit or ElectricalSystemType.PowerBalanced or ElectricalSystemType.PowerUnBalanced;

    internal static bool HasPowerConnector(FamilyInstance fi)
    {
        var connectors = fi.MEPModel?.ConnectorManager?.Connectors;
        if (connectors == null) return false;
        foreach (Connector c in connectors)
            if (c.Domain == Domain.DomainElectrical && IsPower(c.ElectricalSystemType)) return true;
        return false;
    }

    /// <summary>Прибор уже входит в силовую группу (как потребитель, а не как щит этой группы).</summary>
    internal static bool InPowerCircuit(FamilyInstance fi)
    {
        try { return fi.MEPModel?.GetElectricalSystems()?.Any(s => s.SystemType == ElectricalSystemType.PowerCircuit && s.BaseEquipment?.Id != fi.Id) == true; }
        catch { return false; }
    }

    internal static Network Read(Document doc, EomSettings settings)
    {
        var net = new Network();
        var byPanel = new Dictionary<ElementId, List<CircuitInfo>>();
        var connected = new HashSet<ElementId>();
        var feeds = new Dictionary<ElementId, (ElementId Circuit, ElementId? Panel)>();
        foreach (var system in new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>())
        {
            if (system.SystemType != ElectricalSystemType.PowerCircuit) continue;
            var devices = new List<DeviceInfo>();
            foreach (Element e in system.Elements)
            {
                devices.Add(Device(e)); connected.Add(e.Id);
                if (e.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalEquipment) feeds[e.Id] = (system.Id, system.BaseEquipment?.Id);
            }
            string panel = system.BaseEquipment != null ? system.PanelName : "";
            double power = Value(system, BuiltInParameter.RBS_ELEC_TRUE_LOAD, UnitTypeId.Watts);
            double pf = system.PowerFactor;
            int poles = system.PolesNumber;
            // Revit считает длину только для групп, подключённых к щиту; у остальных Length бросает исключение.
            double length = 0;
            if (system.BaseEquipment != null)
                try { length = UnitUtils.ConvertFromInternalUnits(system.Length, UnitTypeId.Meters); }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            var input = new CircuitInput(panel, system.CircuitNumber, system.LoadName ?? "", power, pf, poles, length);
            var choice = EomStore.CircuitChoice(system);
            var info = new CircuitInfo(system.Id, input, Value(system, BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM, UnitTypeId.Amperes), choice?.Cable ?? "", devices);
            if (system.BaseEquipment == null) net.Orphans.Add(info);
            else (byPanel.TryGetValue(system.BaseEquipment.Id, out var list) ? list : byPanel[system.BaseEquipment.Id] = []).Add(info);
        }
        foreach (var panel in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
        {
            byPanel.TryGetValue(panel.Id, out var circuits);
            string name = Text(panel, BuiltInParameter.RBS_ELEC_PANEL_NAME); if (name.Length == 0) name = panel.Name;
            var feed = feeds.TryGetValue(panel.Id, out var f) ? f : default;
            net.Panels.Add(new(panel.Id, name, $"{panel.Symbol.FamilyName}: {panel.Name}", (circuits ?? []).OrderBy(c => c.Input.Number, NaturalComparer.Instance).ToList(),
                feed.Circuit, feed.Panel));
        }
        net.Panels.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        var filter = new ElementMulticategoryFilter(DeviceCategories.Where(c => c != BuiltInCategory.OST_ElectricalEquipment).ToList());
        foreach (var fi in new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType().OfType<FamilyInstance>())
            if (!connected.Contains(fi.Id) && HasPowerConnector(fi)) net.Unconnected.Add(Device(fi));
        return net;
    }

    internal IEnumerable<CircuitInfo> AllCircuits => Panels.SelectMany(p => p.Circuits).Concat(Orphans);
}

/// <summary>Сортировка «Гр.2» раньше «Гр.10».</summary>
internal sealed class NaturalComparer : IComparer<string>
{
    internal static readonly NaturalComparer Instance = new();
    public int Compare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                int c = long.Parse(a[si..Math.Min(i, si + 18)]).CompareTo(long.Parse(b[sj..Math.Min(j, sj + 18)]));
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
