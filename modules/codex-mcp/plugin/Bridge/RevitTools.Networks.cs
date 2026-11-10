using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace BP.Revit.AI;

internal static partial class RevitTools
{
    private static ConnectorManager? ElementConnectors(Element element) => element switch
    {
        MEPCurve curve => curve.ConnectorManager,
        FamilyInstance family => family.MEPModel?.ConnectorManager,
        FabricationPart part => part.ConnectorManager,
        _ => null
    };

    private static bool PhysicalConnector(Connector connector) => connector.ConnectorType is
        ConnectorType.End or ConnectorType.Curve or ConnectorType.Physical;

    private static IEnumerable<Connector> ConnectorPeers(Connector connector) => connector.AllRefs.Cast<Connector>()
        .Where(peer => PhysicalConnector(peer) && peer.Owner.Id != connector.Owner.Id);

    private static object ReadMepConnectors(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        return new { elements = ExtensionIds(args, "elementIds", 20).Select(id =>
        {
            var element = doc.GetElement(id) ?? throw new ArgumentException($"Unknown element {id.Value}.");
            var ports = ElementConnectors(element)?.Connectors.Cast<Connector>().OrderBy(port => port.Id).ToArray() ?? [];
            return new { elementId = id.Value, connectorCount = ports.Length,
                connectors = ports.Take(100).Select(DescribeConnector).ToArray() };
        }).ToArray() };
    }

    private static object DescribeConnector(Connector port)
    {
        var physical = PhysicalConnector(port);
        var shape = physical ? port.Shape : ConnectorProfileType.Invalid;
        var peers = physical ? ConnectorPeers(port).OrderBy(peer => peer.Owner.Id.Value).ThenBy(peer => peer.Id).ToArray() : [];
        return new { connectorId = port.Id, connectorType = port.ConnectorType.ToString(), domain = port.Domain.ToString(),
            physical, isConnected = physical ? (bool?)port.IsConnected : null, shape = shape.ToString(),
            originMeters = physical ? DocPoint(port.Origin) : null,
            axis = physical ? DocVector(port.CoordinateSystem.BasisZ) : null,
            radiusMeters = shape == ConnectorProfileType.Round ? ToMeters(port.Radius) : (double?)null,
            widthMeters = shape is ConnectorProfileType.Rectangular or ConnectorProfileType.Oval ? ToMeters(port.Width) : (double?)null,
            heightMeters = shape is ConnectorProfileType.Rectangular or ConnectorProfileType.Oval ? ToMeters(port.Height) : (double?)null,
            systemId = physical ? port.MEPSystem?.Id.Value : null, peerCount = peers.Length,
            peers = peers.Take(32).Select(peer => new { elementId = peer.Owner.Id.Value, connectorId = peer.Id }).ToArray() };
    }

    private static double ToMeters(double value) => UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Meters);

    private static string MepSystemKind(MEPSystem system) => system switch
    {
        MechanicalSystem => "mechanical", PipingSystem => "piping", ElectricalSystem => "electrical", _ => "other"
    };

    private static object ListMepSystems(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var kind = JsonTools.GetString(args, "kind");
        var filter = new ElementMulticlassFilter(new List<Type> { typeof(MechanicalSystem), typeof(PipingSystem), typeof(ElectricalSystem) });
        var systems = new FilteredElementCollector(doc).WherePasses(filter).Cast<MEPSystem>()
            .Where(system => (kind.Length == 0 || MepSystemKind(system) == kind) && InventoryNameMatches(system.Name, args))
            .OrderBy(system => system.Id.Value);
        return InventoryPage(systems, args, system => new { id = system.Id.Value, system.Name, kind = MepSystemKind(system),
            typeId = system.GetTypeId().Value });
    }

    private static object ReadMepSystem(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        if (doc.GetElement(new ElementId(HostedReadId(args, "systemId"))) is not MEPSystem system)
            throw new ArgumentException("systemId must identify a MEP system.");
        var terminals = system.Elements.Cast<Element>().ToDictionary(element => element.Id.Value);
        var network = system switch
        {
            MechanicalSystem mechanical => mechanical.DuctNetwork.Cast<Element>(),
            PipingSystem piping => piping.PipingNetwork.Cast<Element>(),
            _ => Enumerable.Empty<Element>()
        };
        var members = terminals.Values.Concat(network).DistinctBy(element => element.Id.Value).OrderBy(element => element.Id.Value);
        return new { systemId = system.Id.Value, system.Name, kind = MepSystemKind(system), equipmentId = system.BaseEquipment?.Id.Value,
            terminalCount = terminals.Count, elements = InventoryPage(members, args, element => new { id = element.Id.Value,
                element.Name, category = element.Category?.Name, isTerminal = terminals.ContainsKey(element.Id.Value) }) };
    }

    private static object ListMepRoutingPreferences(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        return new { types = ExtensionIds(args, "typeIds", 10).Select(id =>
        {
            if (doc.GetElement(id) is not MEPCurveType type || type is not (DuctType or PipeType))
                throw new ArgumentException($"Type {id.Value} must be a duct or pipe type.");
            using var manager = type.RoutingPreferenceManager;
            var groups = Enum.GetValues<RoutingPreferenceRuleGroupType>().Where(group => group != RoutingPreferenceRuleGroupType.Undefined)
                .Select(group => new { group = group.ToString(), count = manager.GetNumberOfRules(group),
                    rules = Enumerable.Range(0, Math.Min(manager.GetNumberOfRules(group), 30)).Select(i =>
                    {
                        using var rule = manager.GetRule(group, i);
                        return new { index = i, partId = rule.MEPPartId.Value, name = doc.GetElement(rule.MEPPartId)?.Name, rule.Description };
                    }).ToArray() }).Where(group => group.count > 0).ToArray();
            return new { typeId = id.Value, type.Name, groups };
        }).ToArray() };
    }

    private static object PreviewConnectMep(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "connectMep", "Соединить коннекторы", ConnectMep);
    private static object ApplyConnectMep(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "connectMep", "Соединить проверенные коннекторы?", ConnectMep, cancelled);

    private static object ConnectMep(Document doc, JsonElement args)
    {
        var mode = JsonTools.GetString(args, "mode");
        var count = mode switch { "takeoff" => 1, "tee" => 3, "cross" => 4,
            "direct" or "elbow" or "union" or "transition" => 2, _ => throw new ArgumentException("Unknown fitting mode.") };
        var keys = ConnectorKeys(args, count);
        var ports = keys.Select(key => FindConnector(doc, key)).ToArray();
        var domain = ports[0].Domain;
        if (domain is not (Domain.DomainHvac or Domain.DomainPiping) || ports.Any(port =>
                port.ConnectorType != ConnectorType.End || port.IsConnected || port.Domain != domain))
            throw new ArgumentException("Provide free end connectors in the same HVAC or piping domain.");
        var before = ports.Select(port => DocPoint(port.Origin)).ToArray();
        FamilyInstance? fitting = mode switch
        {
            "elbow" => doc.Create.NewElbowFitting(ports[0], ports[1]),
            "union" => doc.Create.NewUnionFitting(ports[0], ports[1]),
            "transition" => doc.Create.NewTransitionFitting(ports[0], ports[1]),
            "tee" => doc.Create.NewTeeFitting(ports[0], ports[1], ports[2]),
            "cross" => doc.Create.NewCrossFitting(ports[0], ports[1], ports[2], ports[3]),
            "takeoff" => doc.Create.NewTakeoffFitting(ports[0], RequireTakeoffHost(doc, args, keys[0].elementId, domain)),
            _ => null
        };
        if (mode == "direct") ports[0].ConnectTo(ports[1]);
        else if (fitting is null) throw new InvalidOperationException("Revit did not create the requested fitting.");
        doc.Regenerate();
        var refreshed = keys.Select(key => FindConnector(doc, key)).ToArray();
        var peerIds = refreshed.Select(port => ConnectorPeers(port).Select(peer => peer.Owner.Id.Value).Distinct().Order().ToArray()).ToArray();
        if (fitting is not null && peerIds.Any(peers => !peers.Contains(fitting.Id.Value)))
            throw new InvalidOperationException("The fitting did not connect to every requested connector.");
        if (mode == "direct" && !refreshed[0].IsConnectedTo(refreshed[1]) && !peerIds[0].Intersect(peerIds[1]).Any())
            throw new InvalidOperationException("Revit did not create a connection between the requested connectors.");
        return new { mode, fittingId = fitting?.Id.Value, connectors = keys.Select((key, i) => new { key.elementId, key.connectorId,
            beforeMeters = before[i], afterMeters = DocPoint(refreshed[i].Origin), peerElementIds = peerIds[i] }).ToArray() };
    }

    private static MEPCurve RequireTakeoffHost(Document doc, JsonElement args, long branchId, Domain domain)
    {
        var hostId = HostedReadId(args, "hostElementId");
        var host = doc.GetElement(new ElementId(hostId));
        if (hostId == branchId || (domain == Domain.DomainHvac ? host is not Duct : host is not Pipe))
            throw new ArgumentException("Takeoff host must be a different duct or pipe in the same domain.");
        return (MEPCurve)host;
    }

    private static object PreviewDisconnectMep(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "disconnectMep", "Разъединить коннекторы", DisconnectMep);
    private static object ApplyDisconnectMep(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "disconnectMep", "Разъединить проверенные коннекторы?", DisconnectMep, cancelled);

    private static object DisconnectMep(Document doc, JsonElement args)
    {
        var keys = ConnectorKeys(args, 2);
        var first = FindConnector(doc, keys[0]);
        var second = FindConnector(doc, keys[1]);
        if (!PhysicalConnector(first) || !PhysicalConnector(second) || !first.IsConnectedTo(second))
            throw new ArgumentException("The two physical connectors must be directly connected.");
        first.DisconnectFrom(second);
        doc.Regenerate();
        if (FindConnector(doc, keys[0]).IsConnectedTo(FindConnector(doc, keys[1])))
            throw new InvalidOperationException("The connectors are still connected.");
        return new { connectors = keys.Select(key => new { key.elementId, key.connectorId }).ToArray(), disconnected = true };
    }

    private static (long elementId, int connectorId)[] ConnectorKeys(JsonElement args, int count)
    {
        var keys = args.GetProperty("connectors").EnumerateArray().Select(item =>
            (elementId: HostedReadId(item, "elementId"), connectorId: InventoryNumber(item, "connectorId", -1, 0, int.MaxValue))).ToArray();
        if (keys.Length != count || keys.Any(key => key.connectorId < 0) || keys.Select(key => key.elementId).Distinct().Count() != count)
            throw new ArgumentException($"Provide {count} connector IDs from distinct elements.");
        return keys;
    }

    private static Connector FindConnector(Document doc, (long elementId, int connectorId) key)
    {
        var element = doc.GetElement(new ElementId(key.elementId));
        return (element is null ? null : ElementConnectors(element)?.Lookup(key.connectorId))
            ?? throw new ArgumentException($"Unknown connector {key.connectorId} on element {key.elementId}.");
    }

    private static object ListRacewayTypes(UIApplication app)
    {
        var doc = RequireDocument(app);
        return new { conduitTypes = RacewayTypes(doc, typeof(ConduitType)), cableTrayTypes = RacewayTypes(doc, typeof(CableTrayType)) };
    }

    private static object[] RacewayTypes(Document doc, Type type) => new FilteredElementCollector(doc).OfClass(type)
        .Cast<ElementType>().OrderBy(item => item.Id.Value).Select(item => (object)new { id = item.Id.Value, item.Name }).ToArray();

    private static object PreviewCreateRaceways(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "createRaceways", "Создать электрические трассы", CreateRaceways);
    private static object ApplyCreateRaceways(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "createRaceways", "Создать проверенные электрические трассы?", CreateRaceways, cancelled);

    private static object CreateRaceways(Document doc, JsonElement args) => new { segments = ExtensionItems(args, "segments", 30).Select(item =>
    {
        var kind = JsonTools.GetString(item, "kind");
        var typeId = new ElementId(HostedReadId(item, "typeId"));
        var levelId = new ElementId(HostedReadId(item, "levelId"));
        if (doc.GetElement(levelId) is not Level) throw new ArgumentException("levelId must identify a level.");
        var start = MepPoint(item.GetProperty("start"));
        var end = MepPoint(item.GetProperty("end"));
        var length = ToMeters(start.DistanceTo(end));
        if (length is < 0.01 or > 300) throw new ArgumentException("Segment length must be between 0.01 and 300 m.");
        Element segment;
        if (kind == "conduit" && doc.GetElement(typeId) is ConduitType)
        {
            segment = Conduit.Create(doc, typeId, start, end, levelId);
            SetMepSize(segment, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, BuildingNumber(item, "diameterMeters", 0.001, 3));
        }
        else if (kind == "cableTray" && doc.GetElement(typeId) is CableTrayType)
        {
            segment = CableTray.Create(doc, typeId, start, end, levelId);
            SetMepSize(segment, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, BuildingNumber(item, "widthMeters", 0.001, 3));
            SetMepSize(segment, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, BuildingNumber(item, "heightMeters", 0.001, 3));
        }
        else throw new ArgumentException("kind and typeId must identify a conduit or cable tray type.");
        return new { id = segment.Id.Value, kind, lengthMeters = length };
    }).ToArray() };
}
