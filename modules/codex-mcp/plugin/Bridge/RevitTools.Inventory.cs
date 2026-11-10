using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace BP.Revit.AI;

internal static partial class RevitTools
{
    private static object ListInventoryViews(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var viewType = JsonTools.GetString(args, "viewType");
        if (viewType.Length > 0 && !Enum.GetNames<ViewType>().Contains(viewType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("viewType must be a Revit ViewType name.");
        var includeTemplates = args.TryGetProperty("includeTemplates", out var flag) && flag.ValueKind == JsonValueKind.True;
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(view => (includeTemplates || !view.IsTemplate) && view.ViewType != ViewType.Internal &&
                (viewType.Length == 0 || view.ViewType.ToString().Equals(viewType, StringComparison.OrdinalIgnoreCase)) &&
                InventoryNameMatches(view.Name, args)).OrderBy(view => view.Id.Value);
        return InventoryPage(views, args, view => new { id = view.Id.Value, view.Name, viewType = view.ViewType.ToString(),
            view.IsTemplate, view.Scale, levelId = view.GenLevel?.Id.Value, templateId = view.ViewTemplateId.Value });
    }

    private static IEnumerable<ViewSchedule> ProjectSchedules(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .Where(IsProjectSchedule);

    private static bool IsProjectSchedule(ViewSchedule view) =>
        !view.IsTemplate && !view.IsInternalKeynoteSchedule && !view.IsTitleblockRevisionSchedule;

    private static ViewSchedule RequireProjectSchedule(Document doc, JsonElement args) =>
        doc.GetElement(new ElementId(HostedReadId(args, "scheduleId"))) is ViewSchedule schedule && IsProjectSchedule(schedule)
            ? schedule : throw new ArgumentException("scheduleId must identify a project schedule.");

    private static object ListInventorySchedules(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var categoryId = args.TryGetProperty("categoryId", out _) ? ScheduleSignedId(args, "categoryId") : (long?)null;
        var schedules = ProjectSchedules(doc).Where(schedule => InventoryNameMatches(schedule.Name, args) &&
            (categoryId is null || schedule.Definition.CategoryId.Value == categoryId)).OrderBy(schedule => schedule.Id.Value);
        return InventoryPage(schedules, args, schedule => new { id = schedule.Id.Value, schedule.Name,
            categoryId = schedule.Definition.CategoryId.Value, schedule.Definition.IsKeySchedule, schedule.Definition.IsMaterialTakeoff });
    }

    private static object ReadSchedule(UIApplication app, JsonElement args)
    {
        var schedule = RequireProjectSchedule(RequireDocument(app), args);
        var section = JsonTools.GetString(args, "section", "body") switch
        {
            "body" => SectionType.Body,
            "header" => SectionType.Header,
            _ => throw new ArgumentException("section must be body or header.")
        };
        var rowOffset = InventoryNumber(args, "rowOffset", 0, 0, 1_000_000);
        var columnOffset = InventoryNumber(args, "columnOffset", 0, 0, 1_000_000);
        var rowLimit = InventoryNumber(args, "rowLimit", 25, 1, 50);
        var columnLimit = InventoryNumber(args, "columnLimit", 20, 1, 20);
        using var data = schedule.GetTableData();
        using var grid = data.GetSectionData(section);
        var rowCount = Math.Clamp(grid.NumberOfRows - rowOffset, 0, rowLimit);
        var columnCount = Math.Clamp(grid.NumberOfColumns - columnOffset, 0, columnLimit);
        var cells = new string[rowCount][];
        var truncatedCells = 0;
        for (var r = 0; r < rowCount; r++)
        {
            cells[r] = new string[columnCount];
            for (var c = 0; c < columnCount; c++)
            {
                var value = schedule.GetCellText(section, grid.FirstRowNumber + rowOffset + r, grid.FirstColumnNumber + columnOffset + c) ?? "";
                if (value.Length > 512)
                {
                    value = value[..(char.IsHighSurrogate(value[511]) ? 511 : 512)];
                    truncatedCells++;
                }
                cells[r][c] = value;
            }
        }
        var definition = schedule.Definition;
        return new { scheduleId = schedule.Id.Value, schedule.Name, section = section.ToString().ToLowerInvariant(),
            categoryId = definition.CategoryId.Value, definition.IsItemized,
            fields = definition.GetFieldOrder().Select(id => DescribeScheduleField(definition.GetField(id))).ToArray(),
            rows = grid.NumberOfRows, columns = grid.NumberOfColumns, rowOffset, columnOffset,
            nextRowOffset = NextInventoryOffset(rowOffset, rowCount, grid.NumberOfRows),
            nextColumnOffset = NextInventoryOffset(columnOffset, columnCount, grid.NumberOfColumns), truncatedCells, cells };
    }

    private static object ListScheduleCategories(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var categories = ViewSchedule.GetValidCategoriesForSchedule()
            .Select(id => Category.GetCategory(doc, id)).OfType<Category>()
            .Where(category => InventoryNameMatches(category.Name, args)).OrderBy(category => category.Id.Value);
        return InventoryPage(categories, args, category => new { id = category.Id.Value, category.Name });
    }

    private static object ListScheduleFields(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        return ScheduleFieldsPage(doc, RequireProjectSchedule(doc, args).Definition, args);
    }

    private static object PreviewScheduleFields(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        RequireWritableProject(doc);
        var categoryId = RequireSchedulableCategory(args);
        var preview = SimulateOperation(doc, args, "Проверить поля спецификации", (document, input) =>
            ScheduleFieldsPage(document, ViewSchedule.CreateSchedule(document, categoryId).Definition, input));
        return new { modelChanged = false, fields = preview.GetProperty("result"), warnings = preview.GetProperty("warnings") };
    }

    private static object ScheduleFieldsPage(Document doc, ScheduleDefinition definition, JsonElement args)
    {
        var fields = definition.GetSchedulableFields().Select(field => new { parameterId = field.ParameterId.Value,
            fieldType = field.FieldType.ToString(), name = field.GetName(doc) })
            .Where(field => InventoryNameMatches(field.name, args)).OrderBy(field => field.parameterId).ThenBy(field => field.fieldType, StringComparer.Ordinal);
        return InventoryPage(fields, args, field => field);
    }

    private static object PreviewCreateSchedule(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "createSchedule", "Создать спецификацию", CreateInventorySchedule);

    private static object ApplyCreateSchedule(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "createSchedule", "Создать проверенную спецификацию?", CreateInventorySchedule, cancelled);

    private static object CreateInventorySchedule(Document doc, JsonElement args)
    {
        var categoryId = RequireSchedulableCategory(args);
        var name = JsonTools.GetString(args, "name").Trim();
        if (name.Length is < 1 or > 200) throw new ArgumentException("Schedule name must contain 1 to 200 characters.");
        if (ProjectSchedules(doc).Any(schedule => schedule.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A schedule with this name already exists.");
        if (!args.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() is < 1 or > 30)
            throw new ArgumentException("Provide 1 to 30 schedule fields.");

        var schedule = ViewSchedule.CreateSchedule(doc, categoryId);
        schedule.Name = name;
        var definition = schedule.Definition;
        var available = definition.GetSchedulableFields();
        var used = new HashSet<(long, ScheduleFieldType)>();
        var added = new List<object>();
        foreach (var field in fields.EnumerateArray())
        {
            var parameterId = ScheduleSignedId(field, "parameterId");
            var fieldType = JsonTools.GetString(field, "fieldType");
            var matches = available.Where(candidate => candidate.ParameterId.Value == parameterId &&
                (fieldType.Length == 0 || candidate.FieldType.ToString().Equals(fieldType, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length != 1) throw new ArgumentException($"Parameter {parameterId} is missing or ambiguous. Use an available parameterId and fieldType.");
            var selected = matches[0];
            if (!used.Add((parameterId, selected.FieldType))) throw new ArgumentException($"Duplicate schedule field {parameterId} ({selected.FieldType}).");
            var addedField = definition.AddField(selected);
            var heading = JsonTools.GetString(field, "heading").Trim();
            if (heading.Length > 200) throw new ArgumentException("heading is limited to 200 characters.");
            if (heading.Length > 0) addedField.ColumnHeading = heading;
            added.Add(DescribeScheduleField(addedField));
        }
        return new { scheduleId = schedule.Id.Value, schedule.Name, categoryId = categoryId.Value, fields = added };
    }

    private static ElementId RequireSchedulableCategory(JsonElement args)
    {
        var id = new ElementId(ScheduleSignedId(args, "categoryId"));
        return ViewSchedule.IsValidCategoryForSchedule(id) ? id : throw new ArgumentException("categoryId cannot be scheduled.");
    }

    private static object ReadModelWarnings(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        var elementId = args.TryGetProperty("elementId", out _) ? new ElementId(HostedReadId(args, "elementId")) : null;
        var warnings = doc.GetWarnings().Where(warning => elementId is null ||
            warning.GetFailingElements().Contains(elementId) || warning.GetAdditionalElements().Contains(elementId));
        return InventoryPage(warnings, args, warning =>
        {
            var affected = warning.GetFailingElements();
            var additional = warning.GetAdditionalElements();
            return new { failureDefinitionId = warning.GetFailureDefinitionId().Guid, description = warning.GetDescriptionText(),
                failingElementIds = affected.Select(id => id.Value).Order().Take(100).ToArray(), failingElementCount = affected.Count,
                additionalElementIds = additional.Select(id => id.Value).Order().Take(100).ToArray(), additionalElementCount = additional.Count };
        });
    }

    private static object ListInventoryWorksets(UIApplication app, JsonElement args)
    {
        var doc = RequireDocument(app);
        if (!doc.IsWorkshared) return new { isWorkshared = false, activeWorksetId = (int?)null,
            worksets = InventoryPage(Array.Empty<Workset>(), args, DescribeWorkset) };
        using var collector = new FilteredWorksetCollector(doc);
        var worksets = collector.OfKind(WorksetKind.UserWorkset).Where(workset => InventoryNameMatches(workset.Name, args))
            .OrderBy(workset => workset.Id.IntegerValue);
        return new { isWorkshared = true, activeWorksetId = (int?)doc.GetWorksetTable().GetActiveWorksetId().IntegerValue,
            worksets = InventoryPage(worksets, args, DescribeWorkset) };
    }

    private static object DescribeWorkset(Workset workset) => new { id = workset.Id.IntegerValue, workset.Name, workset.IsOpen, workset.IsEditable };

    private static bool InventoryNameMatches(string name, JsonElement args) =>
        name.Contains(JsonTools.GetString(args, "nameContains"), StringComparison.OrdinalIgnoreCase);

    private static object InventoryPage<T, TResult>(IEnumerable<T> items, JsonElement args, Func<T, TResult> describe)
    {
        var offset = InventoryNumber(args, "offset", 0, 0, 1_000_000);
        var limit = InventoryNumber(args, "limit", 50, 1, 100);
        var all = items.ToArray();
        var page = all.Skip(offset).Take(limit).Select(describe).ToArray();
        return new { offset, limit, total = all.Length, nextOffset = NextInventoryOffset(offset, page.Length, all.Length), items = page };
    }

    private static int? NextInventoryOffset(int offset, int count, int total) =>
        count > 0 && offset + count < total ? offset + count : null;

    private static int InventoryNumber(JsonElement args, string name, int fallback, int min, int max)
    {
        if (!args.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
            throw new ArgumentException($"{name} must be an integer between {min} and {max}.");
        return number;
    }

    private static long ScheduleSignedId(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) ||
            id == 0 || id < -9_007_199_254_740_991L || id > 9_007_199_254_740_991L)
            throw new ArgumentException($"{name} must be a nonzero JavaScript-safe Revit ID.");
        return id;
    }
}
