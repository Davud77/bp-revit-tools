using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace BP.Revit.AI;

internal static partial class RevitTools
{
    private static object PreviewConfigureSchedule(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "configureSchedule", "Настроить спецификацию", ConfigureSchedule);
    private static object ApplyConfigureSchedule(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "configureSchedule", "Применить проверенную конфигурацию спецификации?", ConfigureSchedule, cancelled);

    private static object ConfigureSchedule(Document doc, JsonElement args)
    {
        var schedule = RequireProjectSchedule(doc, args);
        var definition = schedule.Definition;
        var hasFilters = args.TryGetProperty("filters", out var filters);
        var hasSort = args.TryGetProperty("sort", out var sort);
        var hasItemized = args.TryGetProperty("itemized", out var itemized);
        if (!hasFilters && !hasSort && !hasItemized) throw new ArgumentException("Provide a schedule configuration change.");
        if (hasFilters)
        {
            if (filters.ValueKind != JsonValueKind.Array || filters.GetArrayLength() > 8) throw new ArgumentException("Use at most eight filters.");
            definition.SetFilters(filters.EnumerateArray().Select(item => ParseScheduleFilter(definition, item)).ToArray());
        }
        if (hasSort)
        {
            if (sort.ValueKind != JsonValueKind.Array || sort.GetArrayLength() > 4) throw new ArgumentException("Use at most four sort fields.");
            var used = new HashSet<int>();
            var fields = sort.EnumerateArray().Select(item =>
            {
                var field = RequireScheduleField(definition, item);
                if (!used.Add(field.IntegerValue) || !definition.CanSortByField(field))
                    throw new ArgumentException("Use distinct sortable fields.");
                var order = JsonTools.GetString(item, "order") switch
                {
                    "ascending" => ScheduleSortOrder.Ascending, "descending" => ScheduleSortOrder.Descending,
                    _ => throw new ArgumentException("Unknown sort order.")
                };
                return new ScheduleSortGroupField(field, order) { ShowBlankLine = item.TryGetProperty("blankLine", out var blank) && blank.GetBoolean() };
            }).ToArray();
            definition.SetSortGroupFields(fields);
        }
        if (hasItemized) definition.IsItemized = itemized.GetBoolean();
        return new { scheduleId = schedule.Id.Value, definition.IsItemized, filterCount = definition.GetFilterCount(),
            sort = definition.GetSortGroupFields().Select(field => new { fieldId = field.FieldId.IntegerValue,
                order = field.SortOrder.ToString(), field.ShowBlankLine }).ToArray() };
    }

    private static ScheduleFieldId RequireScheduleField(ScheduleDefinition definition, JsonElement item)
    {
        var id = InventoryNumber(item, "fieldId", -1, 0, int.MaxValue);
        return definition.GetFieldOrder().FirstOrDefault(field => field.IntegerValue == id)
            ?? throw new ArgumentException($"Unknown schedule field {id}.");
    }

    private static ScheduleFilter ParseScheduleFilter(ScheduleDefinition definition, JsonElement item)
    {
        var field = RequireScheduleField(definition, item);
        var comparison = JsonTools.GetString(item, "comparison") switch
        {
            "equal" => ScheduleFilterType.Equal, "notEqual" => ScheduleFilterType.NotEqual,
            "greaterThan" => ScheduleFilterType.GreaterThan, "greaterThanOrEqual" => ScheduleFilterType.GreaterThanOrEqual,
            "lessThan" => ScheduleFilterType.LessThan, "lessThanOrEqual" => ScheduleFilterType.LessThanOrEqual,
            "contains" => ScheduleFilterType.Contains, "beginsWith" => ScheduleFilterType.BeginsWith,
            "endsWith" => ScheduleFilterType.EndsWith, _ => throw new ArgumentException("Unknown filter comparison.")
        };
        var value = item.GetProperty("value");
        var kind = JsonTools.GetString(value, "kind");
        var substring = comparison is ScheduleFilterType.Contains or ScheduleFilterType.BeginsWith or ScheduleFilterType.EndsWith;
        if (substring ? kind != "text" || !definition.CanFilterBySubstring(field) : !definition.CanFilterByValue(field))
            throw new ArgumentException("The schedule field does not support this filter comparison and value kind.");
        var raw = value.GetProperty("value");
        return kind switch
        {
            "text" when raw.ValueKind == JsonValueKind.String && raw.GetString() is { Length: <= 500 } text => new ScheduleFilter(field, comparison, text),
            "integer" => new ScheduleFilter(field, comparison, raw.GetInt32()),
            "internalDouble" when raw.ValueKind == JsonValueKind.Number && raw.TryGetDouble(out var number) && double.IsFinite(number) => new ScheduleFilter(field, comparison, number),
            "elementId" => new ScheduleFilter(field, comparison, new ElementId(ScheduleSignedId(value, "value"))),
            _ => throw new ArgumentException("Invalid schedule filter value.")
        };
    }

    private static object PreviewConfigureViewFilters(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "configureViewFilters", "Настроить фильтры вида", ConfigureViewFilters);
    private static object ApplyConfigureViewFilters(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "configureViewFilters", "Применить проверенные настройки фильтров?", ConfigureViewFilters, cancelled);

    private static object ConfigureViewFilters(Document doc, JsonElement args)
    {
        if (doc.GetElement(new ElementId(HostedReadId(args, "viewId"))) is not View view || !view.AreGraphicsOverridesAllowed())
            throw new ArgumentException("viewId must identify a view supporting visibility and graphics overrides.");
        var applied = view.GetFilters().ToHashSet();
        var seen = new HashSet<ElementId>();
        foreach (var item in ExtensionItems(args, "filters", 30))
        {
            var id = new ElementId(HostedReadId(item, "filterId"));
            if (doc.GetElement(id) is not FilterElement || !seen.Add(id)) throw new ArgumentException("Use distinct existing filter IDs.");
            switch (JsonTools.GetString(item, "action"))
            {
                case "remove":
                    if (applied.Remove(id)) view.RemoveFilter(id);
                    break;
                case "set":
                    if (applied.Add(id)) view.AddFilter(id);
                    if (item.TryGetProperty("visible", out var visible)) view.SetFilterVisibility(id, visible.GetBoolean());
                    if (item.TryGetProperty("enabled", out var enabled)) view.SetIsFilterEnabled(id, enabled.GetBoolean());
                    break;
                default: throw new ArgumentException("Unknown view filter action.");
            }
        }
        return new { viewId = view.Id.Value, filters = view.GetOrderedFilters().Select(id => new { filterId = id.Value,
            visible = view.GetFilterVisibility(id), enabled = view.GetIsFilterEnabled(id) }).ToArray() };
    }

    private static object ListViewFamilyTypes(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var types = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .Where(type => InventoryNameMatches(type.Name, args)).OrderBy(type => type.Id.Value);
        return InventoryPage(types, args, type => new { id = type.Id.Value, type.Name, family = type.ViewFamily.ToString() });
    }

    private static object PreviewCreate3dViews(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "create3dViews", "Создать 3D-виды", Create3dViews);
    private static object ApplyCreate3dViews(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "create3dViews", "Создать проверенные 3D-виды?", Create3dViews, cancelled);

    private static object Create3dViews(Document doc, JsonElement args) => new { views = ExtensionItems(args, "views", 20).Select(item =>
    {
        var typeId = new ElementId(HostedReadId(item, "typeId"));
        if (doc.GetElement(typeId) is not ViewFamilyType { ViewFamily: ViewFamily.ThreeDimensional })
            throw new ArgumentException("typeId must identify a three-dimensional view family type.");
        var view = View3D.CreateIsometric(doc, typeId);
        view.Name = PublishingName(item, "name", 200);
        if (item.TryGetProperty("sectionBox", out var box))
        {
            var min = MepPoint(box.GetProperty("min"));
            var max = MepPoint(box.GetProperty("max"));
            if (min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z) throw new ArgumentException("The section box must have positive extents.");
            using var bounds = new BoundingBoxXYZ { Min = min, Max = max };
            view.SetSectionBox(bounds);
            view.IsSectionBoxActive = true;
        }
        return new { id = view.Id.Value, view.Name, view.IsSectionBoxActive };
    }).ToArray() };

    private static object PreviewTransformElements(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "transformElements", "Преобразовать элементы", TransformElements);
    private static object ApplyTransformElements(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "transformElements", "Применить проверенное преобразование элементов?", TransformElements, cancelled);

    private static object TransformElements(Document doc, JsonElement args)
    {
        var ids = ExtensionIds(args, "elementIds", 50);
        var mode = JsonTools.GetString(args, "mode");
        foreach (var id in ids)
        {
            var element = doc.GetElement(id);
            if (element is null or ElementType or RevitLinkInstance or View || (mode != "copy" && element.Pinned))
                throw new ArgumentException($"Element {id.Value} is missing, pinned, or unsupported for this transformation.");
        }
        ICollection<ElementId> copies = Array.Empty<ElementId>();
        if (mode is "move" or "copy")
        {
            var translation = MepPoint(args.GetProperty("translation"));
            if (mode == "copy") copies = ElementTransformUtils.CopyElements(doc, ids, translation);
            else
            {
                if (translation.GetLength() <= 1e-9) throw new ArgumentException("The move translation is zero.");
                ElementTransformUtils.MoveElements(doc, ids, translation);
            }
        }
        else if (mode == "rotate")
        {
            var angle = BuildingNumber(args, "degrees", -360, 360);
            if (angle == 0) throw new ArgumentException("The rotation angle is zero.");
            using var axis = Line.CreateBound(MepPoint(args.GetProperty("axisStart")), MepPoint(args.GetProperty("axisEnd")));
            ElementTransformUtils.RotateElements(doc, ids, axis, angle * Math.PI / 180);
        }
        else throw new ArgumentException("Unknown transformation mode.");
        return new { mode, sourceIds = ids.Select(id => id.Value).ToArray(), createdIds = copies.Select(id => id.Value).Order().ToArray() };
    }

    private static object ListDwgExportSetups(UIApplication app) => new
    {
        names = BaseExportOptions.GetPredefinedSetupNames(RequireDocument(app)).Order(StringComparer.OrdinalIgnoreCase).ToArray()
    };

    private static object ExportViewsDwg(UIApplication app, JsonElement args, Func<bool>? cancelled)
    {
        var doc = RequireDocument(app);
        var ids = ExtensionIds(args, "viewIds", 30);
        if (ids.Any(id => doc.GetElement(id) is not View { IsTemplate: false, CanBePrinted: true }))
            throw new ArgumentException("All viewIds must identify printable views or sheets.");
        var setup = JsonTools.GetString(args, "setupName");
        using var options = setup.Length == 0 ? new DWGExportOptions() : DWGExportOptions.GetPredefinedOptions(doc, setup)
            ?? throw new ArgumentException("Unknown DWG export setup.");
        return PublishExport(args, "dwg", cancelled, stage => doc.Export(stage, "views", ids, options));
    }

    private static object ExportProjectIfc(UIApplication app, JsonElement args, Func<bool>? cancelled)
    {
        var doc = RequireDocument(app);
        RequireWritableProject(doc);
        var version = JsonTools.GetString(args, "version", "IFC4") switch
        {
            "IFC2x3" => IFCVersion.IFC2x3, "IFC4" => IFCVersion.IFC4, "IFC4RV" => IFCVersion.IFC4RV,
            _ => throw new ArgumentException("Unknown IFC version.")
        };
        var viewId = ElementId.InvalidElementId;
        if (args.TryGetProperty("viewId", out _))
        {
            viewId = new ElementId(HostedReadId(args, "viewId"));
            if (doc.GetElement(viewId) is not View3D { IsTemplate: false }) throw new ArgumentException("viewId must identify a 3D view.");
        }
        using var options = new IFCExportOptions { FileVersion = version, FilterViewId = viewId,
            ExportBaseQuantities = args.TryGetProperty("baseQuantities", out var quantities) && quantities.GetBoolean() };
        return PublishExport(args, "ifc", cancelled, stage =>
        {
            using var transaction = new Transaction(doc, "BP AI: экспорт IFC");
            if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Cannot start the IFC exporter transaction.");
            try { return doc.Export(stage, "model.ifc", options); }
            finally
            {
                if (transaction.GetStatus() == TransactionStatus.Started && transaction.RollBack() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException("Revit did not roll back the IFC exporter transaction.");
            }
        });
    }

    private static object PublishExport(JsonElement args, string extension, Func<bool>? cancelled, Func<string, bool> export)
    {
        var directory = JsonTools.GetString(args, "directory");
        if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal) || directory.Length > 185)
            throw new ArgumentException("Use a fully qualified local directory of at most 185 characters.");
        var folder = Path.GetFullPath(directory);
        Directory.CreateDirectory(folder);
        var job = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(folder, ".bp-export-" + job);
        var output = Path.Combine(folder, extension + "-" + job);
        Directory.CreateDirectory(stage);
        try
        {
            if (cancelled?.Invoke() == true) throw new OperationCanceledException("Export cancelled.");
            if (!export(stage)) throw new InvalidOperationException($"Revit did not complete the {extension.ToUpperInvariant()} export.");
            var files = Directory.GetFiles(stage);
            if (!files.Any(file => Path.GetExtension(file).Equals("." + extension, StringComparison.OrdinalIgnoreCase) && new FileInfo(file).Length > 0))
                throw new InvalidOperationException("The exporter did not produce the requested output.");
            if (cancelled?.Invoke() == true) throw new OperationCanceledException("Export cancelled before publishing files.");
            Directory.Move(stage, output);
            return new { directory = output, files = files.Select(file => Path.Combine(output, Path.GetFileName(file))).Order().ToArray(), modelChanged = false };
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }
}
