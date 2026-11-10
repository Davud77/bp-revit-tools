using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace BP.Revit.AI;
internal static partial class RevitTools
{
    private static object PreviewMergeMepTags(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "mergeMepTags", "Общие подписи одинаковых элементов", MergeMepTags);
    private static object ApplyMergeMepTags(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "mergeMepTags", "Объединить одинаковые марки", MergeMepTags, cancelled);

    private static object MergeMepTags(Document document, JsonElement args)
    {
        var viewId = AnnotationId(args, "viewId");
        var groups = args.GetProperty("groups");
        if (groups.GetArrayLength() is < 1 or > 20) throw new ArgumentException("Provide 1..20 groups.");
        var used = new HashSet<long>();
        var result = new List<object>();
        foreach (var group in groups.EnumerateArray())
        {
            var ids = group.GetProperty("tagIds").EnumerateArray().Select(v => v.GetInt64()).ToArray();
            if (ids.Length is < 2 or > 20 || ids.Any(id => !used.Add(id)))
                throw new ArgumentException("Groups require 2..20 distinct, non-overlapping tag IDs.");
            var tags = ids.Select(id => document.GetElement(new ElementId(id)) as IndependentTag).ToArray();
            if (tags.Any(t => t is null || t.OwnerViewId.Value != viewId || !t.HasLeader
                || t.GetTaggedReferences().Count != 1 || t.LeaderEndCondition != LeaderEndCondition.Free))
                throw new ArgumentException("Only single-target tags with free leaders in the same view can be merged.");
            var survivor = tags[0]!;
            var text = survivor.TagText;
            if (document.GetElement(survivor.GetTypeId()) is not FamilySymbol symbol || !IsMepTagSymbol(symbol)
                || tags.Any(t => t!.GetTypeId() != survivor.GetTypeId() || t.TagText != text))
                throw new ArgumentException("Types and complete displayed labels must match exactly.");
            var references = tags.Select(t => t!.GetTaggedReferences()[0]).ToArray();
            if (references.Any(r => r.LinkedElementId != ElementId.InvalidElementId)
                || references.Select(r => r.ElementId.Value).Distinct().Count() != references.Length)
                throw new ArgumentException("Each tag must identify a different local element.");
            var ends = tags.Select((t,i) => t!.GetLeaderEnd(references[i])).ToArray();
            var head = survivor.TagHeadPosition;
            var knee = survivor.HasLeaderElbow(references[0]) ? survivor.GetLeaderElbow(references[0]) : head;
            survivor.AddReferences(references.Skip(1).ToList());
            survivor.MergeElbows = true;
            survivor.LeadersPresentationMode = LeadersPresentationMode.ShowAll;
            survivor.LeaderEndCondition = LeaderEndCondition.Free;
            var combined = survivor.GetTaggedReferences();
            foreach (var reference in combined)
                survivor.SetLeaderEnd(reference, ends[Array.FindIndex(references,r=>r.ElementId==reference.ElementId)]);
            survivor.SetLeaderElbow(combined[0], knee);
            var deleted = document.Delete(ids.Skip(1).Select(id => new ElementId(id)).ToList());
            if (deleted.Any(id => !ids.Skip(1).Contains(id.Value)))
                throw new InvalidOperationException("Deleting redundant annotations affected an unexpected element; the batch was rolled back.");
            document.Regenerate();
            if (survivor.IsOrphaned || survivor.TagText != text || survivor.GetTaggedReferences().Count != ids.Length)
                throw new InvalidOperationException("The merged tag did not preserve one identical label and all targets.");
            result.Add(new { id = survivor.Id.Value, removedTagIds = ids.Skip(1).ToArray(), tagText = text,
                targetIds = references.Select(r=>r.ElementId.Value).ToArray(), mergeElbows = survivor.MergeElbows });
        }
        return new { viewId, count = result.Count, groups = result };
    }
}
