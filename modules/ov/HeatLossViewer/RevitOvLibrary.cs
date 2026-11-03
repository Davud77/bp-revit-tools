using Autodesk.Revit.DB;
using HeatLossViewer.Export;

namespace HeatLossViewer;

public static class RevitOvLibrary
{
    public static string TypeKey(Element? element, string elementKey)
    {
        if (element is null || element.GetTypeId() == ElementId.InvalidElementId || !elementKey.Contains('/')) return "";
        var type = element.Document.GetElement(element.GetTypeId());
        return type is null ? "" : elementKey[..elementKey.LastIndexOf('/')] + "/" + type.UniqueId;
    }
    private static double? Si(double value, ForgeTypeId unit) => value > 0 && double.IsFinite(value) ? UnitUtils.ConvertFromInternalUnits(value, unit) : null;
    public static OvLibraryData Read(Document host)
    {
        var stored = OvLibraryStore.Read(host);
        var result = new OvLibraryData { Radiators = stored.Radiators };
        var savedMaterials = stored.Materials.ToDictionary(m => m.Key);
        var savedTypes = stored.Constructions.ToDictionary(t => t.Key);
        void ReadDocument(Document doc, string prefix, HashSet<Document> parents)
        {
            bool linked = !ReferenceEquals(doc, host);
            var wallMaterials = new Dictionary<ElementId, int>();
            var layerMaterials = new HashSet<ElementId>();
            var instances = new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e => e.Category is not null &&
                new[] { (long)BuiltInCategory.OST_Walls, (long)BuiltInCategory.OST_Floors, (long)BuiltInCategory.OST_Windows, (long)BuiltInCategory.OST_Doors }.Contains(e.Category.Id.Value)).ToList();
            foreach (var type in new FilteredElementCollector(doc).WhereElementIsElementType())
            {
                string? kind = type is WallType ? "wall" : type is FloorType ? "floor" : type.Category?.Id.Value == (long)BuiltInCategory.OST_Windows ? "window" : type.Category?.Id.Value == (long)BuiltInCategory.OST_Doors ? "door" : null;
                if (kind is null) continue;
                string key = prefix + "/" + type.UniqueId;
                savedTypes.TryGetValue(key, out var old);
                var row = new OvConstruction { Key = key, ElementId = type.Id.Value.ToString(), Name = (linked ? doc.Title + " · " : "") + (type is FamilySymbol fs ? fs.FamilyName + " · " : "") + type.Name,
                    Kind = kind, Instances = instances.Count(e => e.GetTypeId() == type.Id), ReadOnly = linked,
                    N = old?.N ?? 1, Homogeneity = old?.Homogeneity ?? 1, Gn = old?.Gn ?? (kind == "door" ? 7 : 5), F = old?.F ?? (kind == "door" ? .5 : .67), Ai = old?.Ai ?? 1,
                    GroundFloor = old?.GroundFloor ?? false };
                var structure = (type as HostObjAttributes)?.GetCompoundStructure();
                if (structure is not null)
                    foreach (var layer in structure.GetLayers())
                    {
                        var material = doc.GetElement(layer.MaterialId) as Material;
                        row.Layers.Add(new OvLayer { MaterialKey = material is null ? "" : prefix + "/" + material.UniqueId, MaterialName = material?.Name ?? "По категории / не задан", Thickness = UnitUtils.ConvertFromInternalUnits(layer.Width, UnitTypeId.Meters), Function = layer.Function.ToString() });
                        if (material is not null)
                        {
                            layerMaterials.Add(material.Id);
                            if (kind == "wall") wallMaterials[material.Id] = wallMaterials.GetValueOrDefault(material.Id) + 1;
                        }
                    }
                double? nativeR = null;
                var thermal = type is WallType wt ? wt.ThermalProperties : type is FloorType ft ? ft.ThermalProperties : null;
                if (thermal is not null) nativeR = Si(thermal.ThermalResistance, UnitTypeId.SquareMeterKelvinsPerWatt);
                if (type is FamilySymbol symbol)
                {
                    var familyThermal = symbol.GetThermalProperties();
                    if (familyThermal is not null)
                    {
                        var u = Si(familyThermal.HeatTransferCoefficient, UnitTypeId.WattsPerSquareMeterKelvin);
                        if (u > 0) nativeR = 1 / u;
                    }
                }
                row.R0 = old?.R0 ?? nativeR; row.UseLayers = old?.UseLayers ?? row.Layers.Count > 0;
                row.UseInCalculation = old?.UseInCalculation ?? nativeR.HasValue;
                result.Constructions.Add(row);
            }
            foreach (var wall in instances.Where(e => e.Category!.Id.Value == (long)BuiltInCategory.OST_Walls))
                foreach (var id in wall.GetMaterialIds(false)) { layerMaterials.Add(id); wallMaterials.TryAdd(id, 0); }
            foreach (var id in layerMaterials)
            {
                if (doc.GetElement(id) is not Material material) continue;
                string key = prefix + "/" + material.UniqueId; savedMaterials.TryGetValue(key, out var old);
                using var asset = (doc.GetElement(material.ThermalAssetId) as PropertySetElement)?.GetThermalAsset();
                result.Materials.Add(new OvMaterial { Key = key, ElementId = id.Value.ToString(), Name = (linked ? doc.Title + " · " : "") + material.Name, WallTypes = wallMaterials.GetValueOrDefault(id), ReadOnly = linked,
                    Lambda = linked && old is not null ? old.Lambda : asset is null ? null : Si(asset.ThermalConductivity, UnitTypeId.WattsPerMeterKelvin),
                    Density = linked && old is not null ? old.Density : asset is null ? null : Si(asset.Density, UnitTypeId.KilogramsPerCubicMeter),
                    SpecificHeat = linked && old is not null ? old.SpecificHeat : asset is null ? null : Si(asset.SpecificHeat, UnitTypeId.JoulesPerKilogramDegreeCelsius) });
            }
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var child = link.GetLinkDocument(); if (child is null || parents.Contains(child)) continue;
                parents.Add(child); ReadDocument(child, prefix + "/" + link.UniqueId, parents); parents.Remove(child);
            }
        }
        ReadDocument(host, "host", [host]);
        result.Materials = result.Materials.OrderBy(m => m.Name).ToList(); result.Constructions = result.Constructions.OrderBy(t => t.Name).ToList(); return result;
    }
    public static void Write(Document doc, OvLibraryData edited, OvLibraryData original)
    {
        edited.Validate();
        var before = original.Materials.ToDictionary(m => m.Key);
        foreach (var row in edited.Materials.Where(m => !m.ReadOnly))
        {
            if (!before.TryGetValue(row.Key, out var old) || (row.Lambda == old.Lambda && row.Density == old.Density && row.SpecificHeat == old.SpecificHeat)) continue;
            var material = doc.GetElement(row.Key["host/".Length..]) as Material ?? throw new InvalidOperationException("Материал удалён из проекта: " + row.Name);
            var property = doc.GetElement(material.ThermalAssetId) as PropertySetElement;
            using var asset = property?.GetThermalAsset() ?? new ThermalAsset("ОВ «" + material.Name + "»", ThermalMaterialType.Solid);
            asset.ThermalConductivity = UnitUtils.ConvertToInternalUnits(row.Lambda ?? 0, UnitTypeId.WattsPerMeterKelvin);
            asset.Density = UnitUtils.ConvertToInternalUnits(row.Density ?? 0, UnitTypeId.KilogramsPerCubicMeter);
            asset.SpecificHeat = UnitUtils.ConvertToInternalUnits(row.SpecificHeat ?? 0, UnitTypeId.JoulesPerKilogramDegreeCelsius);
            // A thermal asset can be shared. Give this material its own copy instead of editing other materials.
            bool shared = property is not null && new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().Any(m => m.Id != material.Id && m.ThermalAssetId == property.Id);
            if (property is null || shared)
            {
                asset.Name = AvailableAssetName(doc, "ОВ «" + material.Name + "»");
                var created = PropertySetElement.Create(doc, asset); material.SetMaterialAspectByPropertySet(MaterialAspect.Thermal, created.Id);
            }
            else
            {
                // Revit rejects SetThermalAsset while its copied name is still occupied
                // by the current property set. Free the name inside this transaction.
                string name = property.Name;
                property.get_Parameter(BuiltInParameter.PROPERTY_SET_NAME).Set(AvailableAssetName(doc, name + " (обновление ОВ)"));
                asset.Name = name;
                property.SetThermalAsset(asset);
            }
        }
        OvLibraryStore.Write(doc, edited);
    }
    private static string AvailableAssetName(Document doc, string name)
    {
        var names = new FilteredElementCollector(doc).OfClass(typeof(PropertySetElement)).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string candidate = name; int suffix = 2;
        while (names.Contains(candidate)) candidate = name + " (" + suffix++ + ")";
        return candidate;
    }
}
