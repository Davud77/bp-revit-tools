using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace BP.Revit.AI;

internal static partial class RevitTools
{
    private static object PreviewCreateEmptySheets(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "createEmptySheets", "Создать пустые листы", CreateEmptySheets);
    private static object ApplyCreateEmptySheets(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "createEmptySheets", "Создать проверенные листы?", CreateEmptySheets, cancelled);

    private static object CreateEmptySheets(Document doc, JsonElement args)
    {
        var numbers = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Select(sheet => sheet.SheetNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new { sheets = ExtensionItems(args, "sheets", 20).Select(item =>
        {
            var name = PublishingName(item, "name", 200);
            var number = PublishingName(item, "number", 50);
            if (!numbers.Add(number)) throw new ArgumentException($"Sheet number '{number}' already exists or is repeated.");
            var titleBlockTypeId = ElementId.InvalidElementId;
            if (item.TryGetProperty("titleBlockTypeId", out _))
            {
                titleBlockTypeId = new ElementId(HostedReadId(item, "titleBlockTypeId"));
                if (doc.GetElement(titleBlockTypeId) is not FamilySymbol symbol || symbol.Category?.Id.Value != (long)BuiltInCategory.OST_TitleBlocks)
                    throw new ArgumentException("titleBlockTypeId must identify a loaded title block type.");
            }
            var sheet = ViewSheet.Create(doc, titleBlockTypeId);
            sheet.SheetNumber = number;
            sheet.Name = name;
            return new { id = sheet.Id.Value, sheet.SheetNumber, sheet.Name, titleBlockTypeId = titleBlockTypeId.Value };
        }).ToArray() };
    }

    private static object PreviewPlaceSheetItems(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "placeSheetItems", "Разместить элементы на листе", PlaceSheetItems);
    private static object ApplyPlaceSheetItems(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "placeSheetItems", "Разместить проверенные элементы на листе?", PlaceSheetItems, cancelled);

    private static object PlaceSheetItems(Document doc, JsonElement args)
    {
        if (doc.GetElement(new ElementId(HostedReadId(args, "sheetId"))) is not ViewSheet { IsPlaceholder: false } sheet)
            throw new ArgumentException("sheetId must identify an actual sheet.");
        var placedSchedules = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).OwnedByView(sheet.Id)
            .Cast<ScheduleSheetInstance>().Select(instance => instance.ScheduleId).ToHashSet();
        var items = new List<object>();
        foreach (var request in ExtensionItems(args, "items", 20))
        {
            var viewId = new ElementId(HostedReadId(request, "viewId"));
            if (doc.GetElement(viewId) is not View { IsTemplate: false } view || view is ViewSheet)
                throw new ArgumentException("viewId must identify a non-template view or a schedule.");
            var outline = sheet.Outline;
            var x = outline.Min.U + SheetUnits(BuildingNumber(request, "xMm", 0, 10_000));
            var y = outline.Min.V + SheetUnits(BuildingNumber(request, "yMm", 0, 10_000));
            if ((outline.Max.U > outline.Min.U && x > outline.Max.U + 1e-6) ||
                (outline.Max.V > outline.Min.V && y > outline.Max.V + 1e-6))
                throw new ArgumentException("The insertion point is outside the sheet bounds.");
            var point = new XYZ(x, y, 0);
            if (view is ViewSchedule schedule)
            {
                if (!IsProjectSchedule(schedule) || !placedSchedules.Add(viewId))
                    throw new ArgumentException("The schedule is internal or already placed on this sheet.");
                var instance = ScheduleSheetInstance.Create(doc, sheet.Id, viewId, point);
                items.Add(new { id = instance.Id.Value, viewId = viewId.Value, kind = "schedule" });
            }
            else
            {
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, viewId))
                    throw new ArgumentException($"View {viewId.Value} cannot be placed on this sheet.");
                var viewport = Viewport.Create(doc, sheet.Id, viewId, point);
                items.Add(new { id = viewport.Id.Value, viewId = viewId.Value, kind = "viewport" });
            }
        }
        return new { sheetId = sheet.Id.Value, items };
    }

    private static object PreviewDuplicateViews(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "duplicateViews", "Дублировать виды", DuplicateViews);
    private static object ApplyDuplicateViews(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "duplicateViews", "Дублировать проверенные виды?", DuplicateViews, cancelled);

    private static object DuplicateViews(Document doc, JsonElement args) => new { views = ExtensionItems(args, "views", 20).Select(item =>
    {
        var view = PublishingView(doc, item);
        var option = JsonTools.GetString(item, "mode", "duplicate") switch
        {
            "duplicate" => ViewDuplicateOption.Duplicate,
            "withDetailing" => ViewDuplicateOption.WithDetailing,
            "asDependent" => ViewDuplicateOption.AsDependent,
            _ => throw new ArgumentException("Unknown duplication mode.")
        };
        if (!view.CanViewBeDuplicated(option)) throw new ArgumentException($"View {view.Id.Value} cannot be duplicated with this mode.");
        var name = PublishingName(item, "name", 200);
        var duplicate = (View)doc.GetElement(view.Duplicate(option));
        duplicate.Name = name;
        return new { sourceViewId = view.Id.Value, id = duplicate.Id.Value, duplicate.Name, mode = option.ToString() };
    }).ToArray() };

    private static object PreviewUpdateViews(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "updateViews", "Изменить свойства видов", UpdatePublishingViews);
    private static object ApplyUpdateViews(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "updateViews", "Применить проверенные изменения видов?", UpdatePublishingViews, cancelled);

    private static object UpdatePublishingViews(Document doc, JsonElement args)
    {
        var processed = new HashSet<ElementId>();
        return new { views = ExtensionItems(args, "views", 20).Select(item =>
        {
            var view = PublishingView(doc, item);
            if (!processed.Add(view.Id)) throw new ArgumentException("Each view may be updated only once per batch.");
            var previous = new { view.Name, view.Scale, templateId = view.ViewTemplateId.Value };
            var hasName = item.TryGetProperty("name", out _);
            var hasScale = item.TryGetProperty("scale", out _);
            var hasTemplate = item.TryGetProperty("templateId", out var template);
            if (!hasName && !hasScale && !hasTemplate) throw new ArgumentException("Provide at least one view change.");
            if (hasName) view.Name = PublishingName(item, "name", 200);
            if (hasTemplate)
            {
                var id = template.ValueKind == JsonValueKind.Null ? ElementId.InvalidElementId : new ElementId(HostedReadId(item, "templateId"));
                if (id != ElementId.InvalidElementId && !view.IsValidViewTemplate(id)) throw new ArgumentException("templateId is incompatible with the view.");
                view.ViewTemplateId = id;
            }
            if (hasScale)
            {
                var scale = InventoryNumber(item, "scale", 1, 1, 1000);
                view.Scale = scale;
                doc.Regenerate();
                if (view.Scale != scale) throw new InvalidOperationException("Revit did not retain the requested scale. Check the view template and projection.");
            }
            return new { id = view.Id.Value, previous, view.Name, view.Scale, templateId = view.ViewTemplateId.Value };
        }).ToArray() };
    }

    private static View PublishingView(Document doc, JsonElement item) =>
        doc.GetElement(new ElementId(HostedReadId(item, "viewId"))) is View { IsTemplate: false } view && view is not ViewSheet
            ? view : throw new ArgumentException("viewId must identify a non-template view other than a sheet.");

    private static string PublishingName(JsonElement args, string property, int limit)
    {
        var name = JsonTools.GetString(args, property).Trim();
        return name.Length > 0 && name.Length <= limit ? name : throw new ArgumentException($"{property} must contain 1 to {limit} characters.");
    }

    private static object ExportScheduleCsv(UIApplication app, JsonElement args)
    {
        var schedule = RequireProjectSchedule(RequireDocument(app), args);
        var destination = AuthoringFilePath(args, "path", ".csv", false);
        if (File.Exists(destination)) throw new ArgumentException("The destination CSV already exists.");
        var folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        var temporaryName = "bp-schedule-" + Guid.NewGuid().ToString("N") + ".csv";
        var temporary = Path.Combine(folder, temporaryName);
        using var options = new ViewScheduleExportOptions { FieldDelimiter = ",", TextQualifier = ExportTextQualifier.DoubleQuote,
            Title = false, ColumnHeaders = ExportColumnHeaders.OneRow, HeadersFootersBlanks = true };
        try
        {
            schedule.Export(folder, temporaryName, options);
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new { scheduleId = schedule.Id.Value, path = destination, bytes = new FileInfo(destination).Length, modelChanged = false };
    }
}
