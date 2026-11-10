using Autodesk.Revit.DB;
using System.Runtime.CompilerServices;

namespace Ac2Revit;

/// <summary>
/// Writes the Archicad element ID into Revit's «Марка».
/// Archicad IDs are labels and repeat freely (every door of one kind is «Дв-04»); Revit expects «Марка» to be unique
/// inside a category and reports every repeat as a warning. The first element keeps the source value, later ones get
/// «value (2)», «value (3)» …, and the untouched source value is kept in «Комментарии» so nothing is lost.
/// </summary>
public static class SourceMark
{
    const string CommentPrefix = "Исходная марка: ";
    sealed class Registry
    {
        public readonly Dictionary<long, Dictionary<string, ElementId>> ByCategory = new();
    }
    static readonly ConditionalWeakTable<Document, Registry> Registries = new();

    public static void Set(Element element, string? mark)
    {
        if (string.IsNullOrWhiteSpace(mark)) return;
        var parameter = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
        if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String) return;
        var doc = element.Document;
        long category = element.Category?.Id.Value ?? -1;
        var used = Used(doc, category);
        string current = parameter.AsString() ?? "";
        // A repeated run keeps the value this element already carries when it is still one of its own variants.
        string value = current.Length > 0 && SourceMarkRule.IsVariantOf(current, mark) && (!used.TryGetValue(current, out var owner) || owner == element.Id)
            ? current
            : SourceMarkRule.Unique(mark, candidate => used.TryGetValue(candidate, out var other) && other != element.Id);
        if (current.Length > 0 && used.TryGetValue(current, out var previous) && previous == element.Id && current != value) used.Remove(current);
        used[value] = element.Id;
        if (current != value) parameter.Set(value);
        if (value != mark) KeepSource(element, mark);
    }

    static void KeepSource(Element element, string mark)
    {
        var comments = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments == null || comments.IsReadOnly) return;
        string text = comments.AsString() ?? "";
        if (text.Contains(CommentPrefix, StringComparison.Ordinal)) return;
        comments.Set(text.Length == 0 ? CommentPrefix + mark : text + "; " + CommentPrefix + mark);
    }

    static Dictionary<string, ElementId> Used(Document doc, long category)
    {
        var registry = Registries.GetOrCreateValue(doc);
        if (registry.ByCategory.TryGetValue(category, out var known)) return known;
        known = new Dictionary<string, ElementId>(StringComparer.Ordinal);
        // One scan per document and category; a reopened document gets a new registry and scans again.
        var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
        if (category >= 0) collector = collector.OfCategoryId(new ElementId(category));
        foreach (var other in collector)
        {
            var value = other.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            if (!string.IsNullOrEmpty(value)) known.TryAdd(value, other.Id);
        }
        registry.ByCategory[category] = known;
        return known;
    }
}
