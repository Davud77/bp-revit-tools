using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    // Source lifecycle is outside the target transaction. Never save a reference.
    private sealed class ResourceSource : IDisposable
    {
        public Document Document { get; }
        public string Path { get; }
        public string Sha256 { get; }
        public ResourceSource(UIApplication app, JsonElement args)
        {
            Path = AuthoringFilePath(args, "sourcePath", ".rvt", true);
            if (app.Application.Documents.Cast<Document>().Any(doc => string.Equals(doc.PathName, Path, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The reference is already open in Revit. Close it first; unsaved or stale reference definitions cannot be transferred.");
            using (var info = BasicFileInfo.Extract(Path))
                if (info.IsWorkshared) throw new ArgumentException("Use a standalone local reference copy; central/local workshared references are not supported.");
            Sha256 = ResourceFileHash(Path);
            var expected = JsonTools.GetString(args, "sourceSha256", "");
            if (expected.Length > 0 && !string.Equals(expected, Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The reference file changed. Read its resources and prepare a new preview.");
            Document = app.Application.OpenDocumentFile(Path);
            if (Document.IsFamilyDocument || Document.IsWorkshared)
            {
                Document.Close(false);
                throw new ArgumentException("The reference must be a standalone Revit project.");
            }
        }
        public void CheckFile()
        {
            if (ResourceFileHash(Path) != Sha256) throw new InvalidOperationException("The reference file changed during the operation. Prepare a new preview.");
        }
        public void Dispose() { if (Document.IsValidObject) Document.Close(false); }
    }

    private static string ResourceFileHash(string path)
    {
        // Revit retains a writable file handle while the background document is open.
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string? DocumentationResourceKind(Element element) => element switch
    {
        View view when view.IsTemplate => "viewTemplates",
        ParameterFilterElement => "filters",
        SelectionFilterElement => "filters",
        SharedParameterElement => "sharedParameters",
        FamilySymbol symbol when symbol.Category?.Id.Value == (long)BuiltInCategory.OST_TitleBlocks => "titleBlockTypes",
        ElementType type when type.Category?.CategoryType == CategoryType.Annotation
            || type is TextNoteType || type is DimensionType => "annotationTypes",
        _ => null
    };

    private static List<Element> DocumentationResources(Document document) => new FilteredElementCollector(document)
        .WhereElementIsElementType().Where(element => DocumentationResourceKind(element) is not null)
        .Concat(new FilteredElementCollector(document).OfClass(typeof(View)).Where(element => element is View view && view.IsTemplate))
        .Concat(new FilteredElementCollector(document).OfClass(typeof(FilterElement)))
        .Concat(new FilteredElementCollector(document).OfClass(typeof(SharedParameterElement)))
        .Where(element => DocumentationResourceKind(element) is not null).ToList();

    private static string ResourceKey(Element element) => JsonSerializer.Serialize(new
    {
        kind = DocumentationResourceKind(element),
        @class = element.GetType().FullName,
        category = element.Category?.Id.Value,
        family = element is ElementType type ? type.FamilyName : "",
        name = element.Name
    });

    private static object ResourceDescription(Element element) => new
    {
        id = element.Id.Value, uniqueId = element.UniqueId, kind = DocumentationResourceKind(element),
        name = element.Name, familyName = element is ElementType type ? type.FamilyName : null,
        revitClass = element.GetType().Name, category = element.Category?.Name,
        categoryId = element.Category?.Id.Value,
        copyAllowed = DocumentationResourceKind(element) != "sharedParameters" && element is not SelectionFilterElement,
        sharedParameterGuid = element is SharedParameterElement shared ? shared.GuidValue.ToString() : null,
        projectBinding = element is SharedParameterElement parameterElement ? ResourceParameterBinding(parameterElement) : null,
        sharedParameters = element.Parameters.Cast<Parameter>().Where(parameter => parameter.IsShared)
            .Select(parameter => new { name = parameter.Definition.Name, guid = parameter.GUID.ToString(), storageType = parameter.StorageType.ToString() })
            .OrderBy(parameter => parameter.guid).ToArray(),
        viewType = element is View view ? view.ViewType.ToString() : null,
        filterIds = element is View template && template.IsTemplate && template.AreGraphicsOverridesAllowed()
            ? template.GetFilters().Select(id => id.Value).ToArray() : null,
        filterCategoryIds = element is ParameterFilterElement filter ? filter.GetCategories().Select(id => id.Value).ToArray() : null
    };

    private static object ResourceParameterBinding(SharedParameterElement parameter)
    {
        var iterator = parameter.Document.ParameterBindings.ForwardIterator();
        while (iterator.MoveNext())
            if (iterator.Key is InternalDefinition definition && definition.Id == parameter.Id && iterator.Current is ElementBinding binding)
                return new { scope = binding is InstanceBinding ? "instance" : "type",
                    categoryIds = binding.Categories.Cast<Category>().Select(category => category.Id.Value).Order().ToArray() };
        return new { scope = "notBound", categoryIds = Array.Empty<long>() };
    }

    private static object ListDocumentationResources(UIApplication app, JsonElement args)
    {
        var target = RequireDocument(app);
        using var source = args.TryGetProperty("sourcePath", out _) ? new ResourceSource(app, args) : null;
        var document = source?.Document ?? target;
        var kind = JsonTools.GetString(args, "kind", "all");
        if (kind is not ("all" or "annotationTypes" or "titleBlockTypes" or "viewTemplates" or "filters" or "sharedParameters"))
            throw new ArgumentException("Unsupported documentation resource kind.");
        var offset = JsonTools.GetInt(args, "offset", 0);
        var limit = JsonTools.GetInt(args, "limit", 50);
        var query = JsonTools.GetString(args, "query", "");
        if (offset is < 0 or > 100000 || limit is < 1 or > 100 || query.Length > 200) throw new ArgumentException("Invalid resource page.");
        var all = DocumentationResources(document);
        var matches = all.Where(element => (kind == "all" || DocumentationResourceKind(element) == kind)
            && (element.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (element is ElementType type && type.FamilyName.Contains(query, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(DocumentationResourceKind).ThenBy(ResourceKey).ToArray();
        var resources = matches.Skip(offset).Take(limit).Select(ResourceDescription).ToArray();
        source?.CheckFile();
        return new { sourcePath = document.PathName, sourceSha256 = source?.Sha256,
            sourceIsCurrentDocument = source is null, total = matches.Length, offset, returnedCount = resources.Length,
            hasMore = offset + resources.Length < matches.Length,
            counts = all.GroupBy(DocumentationResourceKind).ToDictionary(group => group.Key!, group => group.Count()), resources,
            note = "Names are identity candidates, not proof of equivalent definitions. Shared parameter bindings and family label definitions require separate validation." };
    }

    private static object PreviewCopyDocumentationResources(UIApplication app, JsonElement args)
    {
        RequireWritableProject(RequireDocument(app));
        if (JsonTools.GetString(args, "sourceSha256", "").Length != 64) throw new ArgumentException("sourceSha256 from resource inventory is required.");
        using var source = new ResourceSource(app, args);
        var bound = JsonNode.Parse(args.GetRawText())!.AsObject();
        bound["sourcePath"] = source.Path;
        bound["sourceSha256"] = source.Sha256;
        return PreviewOperation(app, JsonSerializer.SerializeToElement(bound), "copyDocumentationResources", "Перенос ресурсов оформления",
            (target, arguments) => CopyDocumentationResources(target, arguments, source));
    }

    private static object ApplyCopyDocumentationResources(UIApplication app, JsonElement args, Func<bool>? cancelled)
    {
        var plan = GetOperationPlan(RequireDocument(app), JsonTools.GetString(args, "planId"), "copyDocumentationResources");
        using var source = new ResourceSource(app, plan.Arguments);
        return ApplyOperation(app, args, "copyDocumentationResources", "Перенести проверенные ресурсы оформления?",
            (target, arguments) => CopyDocumentationResources(target, arguments, source), cancelled);
    }

    private sealed class ResourceDuplicates(string policy) : IDuplicateTypeNamesHandler
    {
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
        {
            foreach (var id in args.GetTypeIds()) Names.Add(args.Document.GetElement(id)?.Name ?? id.Value.ToString());
            return policy == "reuseDestination" ? DuplicateTypeAction.UseDestinationTypes : DuplicateTypeAction.Abort;
        }
    }

    private static object CopyDocumentationResources(Document target, JsonElement args, ResourceSource source)
    {
        var ids = AnnotationIds(args, "sourceElementIds", 50, required: true);
        if (ids.Count != args.GetProperty("sourceElementIds").GetArrayLength()) throw new ArgumentException("Source IDs must be unique.");
        var policy = JsonTools.GetString(args, "duplicatePolicy", "abort");
        if (policy is not ("abort" or "reuseDestination")) throw new ArgumentException("Invalid duplicate policy.");
        var selected = ids.Select(id => source.Document.GetElement(new ElementId(id)) ?? throw new ArgumentException($"Source element {id} does not exist.")).ToArray();
        foreach (var element in selected)
            if (DocumentationResourceKind(element) is null or "sharedParameters" || element is SelectionFilterElement)
                throw new ArgumentException($"Source element {element.Id.Value} is not a transferable documentation resource. Model instances and shared parameter bindings are excluded.");
        var duplicates = new ResourceDuplicates(policy);
        using var options = new CopyPasteOptions();
        options.SetDuplicateTypeNamesHandler(duplicates);
        var mappings = new List<object>();
        var createdIds = new HashSet<long>();
        var reusedFamilies = new HashSet<string>();
        var initial = DocumentationResources(target).GroupBy(ResourceKey).ToDictionary(group => group.Key, group => group.ToArray());
        var initialFamilies = new FilteredElementCollector(target).OfClass(typeof(Family)).Cast<Family>()
            .Select(family => (family.Name, Category: family.FamilyCategory?.Id.Value)).ToHashSet();
        var toCopy = new List<ElementId>();
        foreach (var element in selected)
        {
            var key = ResourceKey(element);
            var matches = initial.GetValueOrDefault(key) ?? [];
            if (matches.Length > 1) throw new InvalidOperationException($"Ambiguous destination resource '{element.Name}'.");
            var existingFamily = element is FamilySymbol symbol
                && initialFamilies.Contains((symbol.FamilyName, symbol.Category?.Id.Value));
            if (matches.Length > 0 || existingFamily)
            {
                if (policy == "abort") throw new InvalidOperationException($"Destination already contains resource/family '{element.Name}'. No definitions were overwritten; use an explicit reuse policy only after comparison.");
                if (existingFamily) reusedFamilies.Add(((FamilySymbol)element).FamilyName);
            }
            if (matches.Length == 0) toCopy.Add(element.Id);
        }
        // One native batch avoids treating dependencies created earlier in this package as pre-existing conflicts.
        if (toCopy.Count > 0)
        {
            try
            {
                var copied = ElementTransformUtils.CopyElements(source.Document, toCopy, target, Transform.Identity, options);
                foreach (var id in copied) createdIds.Add(id.Value);
            }
            catch (Exception error) when (duplicates.Names.Count > 0)
            { throw new InvalidOperationException($"Resource copy cancelled; duplicate dependency types: {string.Join(", ", duplicates.Names)}. {error.Message}", error); }
            target.Regenerate();
        }
        var final = DocumentationResources(target).GroupBy(ResourceKey).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var element in selected)
        {
            var key = ResourceKey(element);
            var matches = final.GetValueOrDefault(key) ?? [];
            if (matches.Length != 1) throw new InvalidOperationException($"Cannot map copied resource '{element.Name}' uniquely. The entire package is rolled back.");
            var mapped = matches[0];
            var status = initial.ContainsKey(key) ? "reusedDestination" : "copied";
            mappings.Add(new { sourceId = element.Id.Value, sourceUniqueId = element.UniqueId,
                targetId = mapped.Id.Value, targetUniqueId = mapped.UniqueId, kind = DocumentationResourceKind(element),
                name = element.Name, status, definitionEquivalenceVerified = false });
        }
        source.CheckFile();
        var created = createdIds.Select(id => target.GetElement(new ElementId(id))).Where(element => element is not null).ToArray();
        if (created.Any(element => element is FamilyInstance or ViewSheet or ProjectInfo or Level or Grid
            || (element is View view && !view.IsTemplate)))
            throw new InvalidOperationException("Copy produced project/model instances outside the resource scope. The entire package is rolled back.");
        return new { sourcePath = source.Path, sourceSha256 = source.Sha256, duplicatePolicy = policy,
            count = mappings.Count, mappings, createdElementIds = createdIds.Order().ToArray(),
            dependencyClasses = created.GroupBy(element => element.GetType().Name).ToDictionary(group => group.Key, group => group.Count()),
            reusedDependencyTypeNames = duplicates.Names.Order().ToArray(), reusedFamilyNames = reusedFamilies.Order().ToArray(),
            projectParameterBindingsExplicitlyManaged = false, requiresParameterBindingReview = true,
            note = "Native dependencies are copied by Revit. Reused destination definitions were not compared or replaced. Validate template filters, tag labels and shared parameter GUID/bindings in the target before documentation." };
    }
}
