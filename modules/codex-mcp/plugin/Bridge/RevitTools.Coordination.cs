using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace BP.Revit.AI;

internal static partial class RevitTools
{
    private static JsonElement[] ExtensionItems(JsonElement args, string property, int max)
    {
        if (!args.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() == 0 || value.GetArrayLength() > max)
            throw new ArgumentException($"{property} must contain 1 to {max} items.");
        return value.EnumerateArray().ToArray();
    }

    private static ElementId[] ExtensionIds(JsonElement args, string property, int max)
    {
        var values = ExtensionItems(args, property, max);
        if (values.Any(value => value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || id <= 0 || id > 9_007_199_254_740_991L))
            throw new ArgumentException($"{property} must contain positive JavaScript-safe Revit IDs.");
        var ids = values.Select(value => new ElementId(value.GetInt64())).ToArray();
        if (ids.Distinct().Count() != ids.Length) throw new ArgumentException($"{property} must contain distinct IDs.");
        return ids;
    }

    private static object ListModelLinks(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .OrderBy(link => link.Id.Value);
        return InventoryPage(links, args, link =>
        {
            var linked = link.GetLinkDocument();
            var transform = link.GetTotalTransform();
            return new { id = link.Id.Value, link.Name, typeId = link.GetTypeId().Value, loaded = linked is not null,
                linkedTitle = linked?.Title, linkedToHost = new { originMeters = DocPoint(transform.Origin),
                    xAxis = DocVector(transform.BasisX), yAxis = DocVector(transform.BasisY), zAxis = DocVector(transform.BasisZ) } };
        });
    }

    private static object ListLinkElements(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        if (doc.GetElement(new ElementId(HostedReadId(args, "linkId"))) is not RevitLinkInstance link)
            throw new ArgumentException("linkId must identify a Revit link instance.");
        var linked = link.GetLinkDocument() ?? throw new InvalidOperationException("The linked model is unloaded.");
        var categoryId = new ElementId(ScheduleSignedId(args, "categoryId"));
        if (Category.GetCategory(linked, categoryId) is null) throw new ArgumentException("Unknown categoryId in the linked model.");
        var transform = link.GetTotalTransform();
        var elements = new FilteredElementCollector(linked).OfCategoryId(categoryId).WhereElementIsNotElementType()
            .Where(element => InventoryNameMatches(element.Name, args)).OrderBy(element => element.Id.Value);
        return new { linkId = link.Id.Value, linkedTitle = linked.Title, elements = InventoryPage(elements, args, element =>
        {
            var bounds = ExtensionBounds(element.get_BoundingBox(null), transform);
            return new { linkedElementId = element.Id.Value, element.UniqueId, element.Name, linkedTypeId = element.GetTypeId().Value,
                categoryId = element.Category?.Id.Value, hostBoundsMeters = bounds is null ? null : new { min = DocPoint(bounds.Value.min), max = DocPoint(bounds.Value.max) } };
        }) };
    }

    private static (XYZ min, XYZ max)? ExtensionBounds(BoundingBoxXYZ? box, Transform? toHost = null)
    {
        if (box is null) return null;
        using var local = box.Transform;
        var points = from x in new[] { box.Min.X, box.Max.X }
                     from y in new[] { box.Min.Y, box.Max.Y }
                     from z in new[] { box.Min.Z, box.Max.Z }
                     let point = local.OfPoint(new XYZ(x, y, z))
                     select toHost?.OfPoint(point) ?? point;
        var corners = points.ToArray();
        return (new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z)),
            new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z)));
    }

    private static object CheckElementIntersections(UIApplication app, JsonElement args, Func<bool>? cancelled)
    {
        var doc = RequireDocument(app);
        var mode = JsonTools.GetString(args, "mode");
        if (mode is not ("bounds" or "interference")) throw new ArgumentException("mode must be bounds or interference.");
        var limit = InventoryNumber(args, "limit", 100, 1, 500);
        var first = ExtensionIds(args, "firstIds", 50).OrderBy(id => id.Value).ToArray();
        var second = ExtensionIds(args, "secondIds", 50).OrderBy(id => id.Value).ToArray();
        var candidates = first.Concat(second).Distinct().ToDictionary(id => id.Value, id =>
        {
            var element = doc.GetElement(id);
            return element is null or ElementType or RevitLinkInstance ? throw new ArgumentException($"Element {id.Value} is not a host model instance.") : element;
        });
        var bounds = candidates.ToDictionary(entry => entry.Key, entry => ExtensionBounds(entry.Value.get_BoundingBox(null)));
        var seen = new HashSet<(long, long)>();
        var hits = new List<object>();
        var unsupported = new List<object>();
        var hitCount = 0;
        var unsupportedCount = 0;
        foreach (var a in first)
        {
            if (cancelled?.Invoke() == true) throw new OperationCanceledException("MCP intersection query cancelled.");
            var left = candidates[a.Value];
            using var native = mode == "interference" && ElementIntersectsFilter.IsElementSupported(left)
                ? new ElementIntersectsElementFilter(left) : null;
            foreach (var b in second)
            {
                if (a == b || !seen.Add((Math.Min(a.Value, b.Value), Math.Max(a.Value, b.Value)))) continue;
                var pair = new { firstId = a.Value, secondId = b.Value };
                var leftBounds = bounds[a.Value];
                var rightBounds = bounds[b.Value];
                if ((mode == "bounds" && (leftBounds is null || rightBounds is null)) ||
                    (mode == "interference" && (native is null || !ElementIntersectsFilter.IsCategorySupported(candidates[b.Value]))))
                {
                    if (++unsupportedCount <= limit) unsupported.Add(pair);
                    continue;
                }
                var intersects = native is not null ? native.PassesFilter(candidates[b.Value]) :
                    BoundsOverlap(leftBounds!.Value, rightBounds!.Value);
                if (intersects && ++hitCount <= limit) hits.Add(pair);
            }
        }
        return new { mode, pairsChecked = seen.Count, hitCount, unsupportedCount,
            truncated = hitCount > limit || unsupportedCount > limit, hits, unsupported };
    }

    private static bool BoundsOverlap((XYZ min, XYZ max) a, (XYZ min, XYZ max) b) =>
        a.min.X <= b.max.X && b.min.X <= a.max.X && a.min.Y <= b.max.Y && b.min.Y <= a.max.Y && a.min.Z <= b.max.Z && b.min.Z <= a.max.Z;

    private static object ListProjectParameters(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        if (doc.IsFamilyDocument) throw new InvalidOperationException("Open a project document to list parameter bindings.");
        using var iterator = doc.ParameterBindings.ForwardIterator();
        var bindings = new List<(InternalDefinition definition, ElementBinding binding)>();
        while (iterator.MoveNext())
            if (iterator.Key is InternalDefinition definition && iterator.Current is ElementBinding binding && InventoryNameMatches(definition.Name, args))
                bindings.Add((definition, binding));
        return InventoryPage(bindings.OrderBy(item => item.definition.Id.Value), args, item => new
        {
            id = item.definition.Id.Value, item.definition.Name, group = item.definition.GetGroupTypeId().TypeId,
            dataType = item.definition.GetDataType().TypeId, isInstance = item.binding is InstanceBinding,
            sharedGuid = (doc.GetElement(item.definition.Id) as SharedParameterElement)?.GuidValue,
            categories = item.binding.Categories.Cast<Category>().OrderBy(category => category.Id.Value)
                .Select(category => new { id = category.Id.Value, category.Name }).ToArray()
        });
    }

    private static object ListViewFilters(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        View? view = null;
        if (args.TryGetProperty("viewId", out _))
            view = doc.GetElement(new ElementId(HostedReadId(args, "viewId"))) as View ?? throw new ArgumentException("viewId must identify a view.");
        var filters = view is null
            ? new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<FilterElement>()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(SelectionFilterElement)).Cast<FilterElement>()).OrderBy(filter => filter.Id.Value)
            : view.GetOrderedFilters().Select(doc.GetElement).OfType<FilterElement>();
        return InventoryPage(filters.Where(filter => InventoryNameMatches(filter.Name, args)), args, filter => new
        {
            id = filter.Id.Value, filter.Name, kind = filter.GetType().Name,
            categories = (filter as ParameterFilterElement)?.GetCategories().Select(category => category.Value).ToArray(),
            visible = view?.GetFilterVisibility(filter.Id), enabled = view?.GetIsFilterEnabled(filter.Id)
        });
    }

    private static object ListExtensionLevels(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .Where(level => InventoryNameMatches(level.Name, args)).OrderBy(level => level.Id.Value);
        return InventoryPage(levels, args, level => new { id = level.Id.Value, level.Name,
            elevationMeters = ToMeters(level.Elevation), projectElevationMeters = ToMeters(level.ProjectElevation) });
    }

    private static object ListExtensionGrids(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
            .Where(grid => InventoryNameMatches(grid.Name, args)).OrderBy(grid => grid.Id.Value);
        return InventoryPage(grids, args, grid =>
        {
            using var curve = grid.Curve;
            return new { id = grid.Id.Value, grid.Name, curveType = curve.GetType().Name,
                startMeters = curve.IsBound ? DocPoint(curve.GetEndPoint(0)) : null,
                endMeters = curve.IsBound ? DocPoint(curve.GetEndPoint(1)) : null,
                midpointMeters = curve.IsBound ? DocPoint(curve.Evaluate(0.5, true)) : null };
        });
    }

    private static object PreviewSetElementWorksets(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "setElementWorksets", "Перенести элементы на рабочий набор", SetElementWorksets);
    private static object ApplySetElementWorksets(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "setElementWorksets", "Перенести проверенные элементы на рабочий набор?", SetElementWorksets, cancelled);

    private static object SetElementWorksets(Document doc, JsonElement args)
    {
        if (!doc.IsWorkshared) throw new InvalidOperationException("The project does not use worksharing.");
        var targetId = InventoryNumber(args, "worksetId", -1, 0, int.MaxValue);
        if (targetId < 0 || doc.GetWorksetTable().GetWorkset(new WorksetId(targetId)) is not { Kind: WorksetKind.UserWorkset } target)
            throw new ArgumentException("worksetId must identify a user workset.");
        var elements = ExtensionIds(args, "elementIds", 100).Select(id =>
        {
            var element = doc.GetElement(id);
            if (element is null or ElementType or RevitLinkInstance) throw new ArgumentException($"Element {id.Value} must be a host model instance.");
            var previous = element.WorksetId.IntegerValue;
            if (previous != targetId)
            {
                var parameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (parameter is not { IsReadOnly: false, StorageType: StorageType.Integer } || !parameter.Set(targetId))
                    throw new InvalidOperationException($"Cannot change workset for element {id.Value}.");
            }
            return new { id = id.Value, previousWorksetId = previous, changed = previous != targetId };
        }).ToArray();
        return new { worksetId = targetId, worksetName = target.Name, elements };
    }
}
