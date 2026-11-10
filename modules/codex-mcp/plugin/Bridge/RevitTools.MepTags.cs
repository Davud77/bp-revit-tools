using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace BP.Revit.AI;

internal static partial class RevitTools
{
    private static readonly (BuiltInCategory Element, BuiltInCategory Tag)[] MepTagCategories =
    [
        (BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctTags),
        (BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_FlexDuctTags),
        (BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctFittingTags),
        (BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_DuctAccessoryTags),
        (BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_DuctTerminalTags),
        (BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_MechanicalEquipmentTags),
        (BuiltInCategory.OST_DuctInsulations, BuiltInCategory.OST_DuctInsulationsTags),
        (BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeTags),
        (BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_FlexPipeTags),
        (BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeFittingTags),
        (BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_PipeAccessoryTags),
        (BuiltInCategory.OST_PipeInsulations, BuiltInCategory.OST_PipeInsulationsTags)
    ];

    private static object PreviewTagMep(UIApplication app, JsonElement args) =>
        PreviewOperation(app, BindAnnotationView(app, args), "tagMep", "Марки инженерных элементов", TagMepElements);

    private static object ApplyTagMep(UIApplication app, JsonElement args, Func<bool>? isCancelled) =>
        ApplyOperation(app, args, "tagMep", "Разместить инженерные марки", TagMepElements, isCancelled);

    private static object PreviewPositionMepTags(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "positionMepTags", "Размещение инженерных марок", PositionMepTags);
    private static object ApplyPositionMepTags(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "positionMepTags", "Переместить инженерные марки", PositionMepTags, cancelled);

    private static object PositionMepTags(Document document, JsonElement args)
    {
        var viewId = AnnotationId(args, "viewId");
        if (document.GetElement(new ElementId(viewId)) is not View view || view.IsTemplate)
            throw new ArgumentException("viewId must identify a non-template view.");
        var items = args.GetProperty("tags");
        if (items.GetArrayLength() is < 1 or > 100) throw new ArgumentException("Provide 1 to 100 tags.");
        var ids = new HashSet<long>();
        var tags = new List<IndependentTag>();
        foreach (var item in items.EnumerateArray())
        {
            var id = AnnotationId(item, "tagId");
            if (!ids.Add(id) || document.GetElement(new ElementId(id)) is not IndependentTag tag
                || tag.OwnerViewId != view.Id || !MepTagCategories.Any(p => tag.Category?.Id.Value == (long)p.Tag))
                throw new ArgumentException($"Tag {id} must be a unique native MEP tag owned by this view.");
            if (item.TryGetProperty("tagTypeId", out _))
            {
                var typeId = AnnotationId(item, "tagTypeId");
                if (document.GetElement(new ElementId(typeId)) is not FamilySymbol symbol || symbol.Category?.Id != tag.Category?.Id)
                    throw new ArgumentException("tagTypeId must be a compatible native type in the same tag category.");
                if (!symbol.IsActive) symbol.Activate();
                var replacement = tag.ChangeTypeId(symbol.Id);
                if (replacement != ElementId.InvalidElementId && replacement != tag.Id)
                    throw new InvalidOperationException("Changing this type replaced the tag identity; the batch was rolled back.");
            }
            if (item.TryGetProperty("leader", out _) && !AnnotationBoolean(item, "leader"))
                throw new ArgumentException("Positioned MEP tags must retain a leader.");
            tag.HasLeader = true;
            var requestedHead=ToXYZ(ReadPoint3D(item,"headPosition"));
            tag.TagHeadPosition=requestedHead;
            if (item.TryGetProperty("leaders", out var leaders))
            {
                var references = tag.GetTaggedReferences();
                var points = leaders.EnumerateArray().ToArray();
                if (points.Length != references.Count || points.Select(p=>AnnotationId(p,"elementId")).Distinct().Count()!=points.Length
                    || references.Any(r=>r.LinkedElementId!=ElementId.InvalidElementId))
                    throw new ArgumentException("Provide one endpoint for every local target, without duplicates.");
                tag.LeaderEndCondition=LeaderEndCondition.Free;
                foreach (var entry in points)
                {
                    var targetId=AnnotationId(entry,"elementId");
                    var reference=references.FirstOrDefault(r=>r.ElementId.Value==targetId)
                        ?? throw new ArgumentException("An endpoint does not belong to this tag.");
                    var endpoint=ToXYZ(ReadPoint3D(entry,"endPosition"));
                    ValidateTagEndpoint(document,view,reference,endpoint);
                    tag.SetLeaderEnd(reference,endpoint);
                }
            }
            if (item.TryGetProperty("leaderEndPosition", out _))
            {
                var references = tag.GetTaggedReferences();
                if (references.Count != 1 || references[0].LinkedElementId != ElementId.InvalidElementId)
                    throw new ArgumentException("An explicit endpoint requires one local target.");
                var endpoint = ToXYZ(ReadPoint3D(item, "leaderEndPosition"));
                ValidateTagEndpoint(document,view,references[0],endpoint);
                tag.LeaderEndCondition = LeaderEndCondition.Free;
                tag.SetLeaderEnd(references[0], endpoint);
            }
            // End-condition changes can move the head. Restore the requested head before aligning the knee.
            tag.TagHeadPosition=requestedHead;
            if(item.TryGetProperty("elbowPosition",out _))
            {
                var references=tag.GetTaggedReferences();
                if(!tag.HasLeader||references.Count==0||(references.Count>1&&!tag.MergeElbows))
                    throw new ArgumentException("An explicit common elbow requires a single leader or merged elbows.");
                var elbow = ToXYZ(ReadPoint3D(item,"elbowPosition"));
                if (!item.TryGetProperty("alignElbowToShelf", out _) || AnnotationBoolean(item, "alignElbowToShelf"))
                {
                    if (tag.TagOrientation != TagOrientation.Horizontal)
                        throw new ArgumentException("Shelf alignment requires a horizontal tag.");
                    var offset = item.TryGetProperty("shelfOffsetUpPaperMillimeters", out var offsetValue) ? offsetValue.GetDouble() : 0;
                    if (!double.IsFinite(offset) || Math.Abs(offset) > 100) throw new ArgumentException("Invalid shelf baseline offset.");
                    var expected = offset * view.Scale / 304.8;
                    elbow -= view.UpDirection * ((elbow-tag.TagHeadPosition).DotProduct(view.UpDirection)-expected);
                }
                tag.SetLeaderElbow(references[0],elbow);
            }
            tags.Add(tag);
        }
        document.Regenerate();
        var moved = new List<object>();
        foreach (var tag in tags)
        {
            if (tag.IsOrphaned || string.IsNullOrWhiteSpace(tag.TagText) || tag.TagText.Contains('?'))
                throw new InvalidOperationException($"Tag {tag.Id.Value} has an invalid label.");
            var references = tag.GetTaggedReferences();
            moved.Add(new { id = tag.Id.Value, typeId = tag.GetTypeId().Value, tagText = tag.TagText, hasLeader = tag.HasLeader,
                headPosition = AnnotationPointMeters(tag.TagHeadPosition),
                elbowPosition = tag.HasLeader && references.Count == 1 && tag.HasLeaderElbow(references[0])
                    ? AnnotationPointMeters(tag.GetLeaderElbow(references[0])) : null,
                leaderEndPosition = tag.HasLeader && references.Count == 1 && tag.LeaderEndCondition == LeaderEndCondition.Free
                    ? AnnotationPointMeters(tag.GetLeaderEnd(references[0])) : null });
        }
        return new { viewId, count = moved.Count, moved };
    }

    private static void ValidateTagEndpoint(Document document, View view, Reference reference, XYZ endpoint)
    {
        var box=document.GetElement(reference.ElementId).get_BoundingBox(view)
            ?? throw new ArgumentException("The target has no geometry bounds in this view.");
        var local=box.Transform.Inverse.OfPoint(endpoint);
        const double tolerance=1e-6;
        if(local.X<box.Min.X-tolerance||local.X>box.Max.X+tolerance||local.Y<box.Min.Y-tolerance||local.Y>box.Max.Y+tolerance
            ||local.Z<box.Min.Z-tolerance||local.Z>box.Max.Z+tolerance)
            throw new ArgumentException("The requested endpoint is outside its target geometry bounds.");
    }

    private static object TagMepElements(Document document, JsonElement args)
    {
        var viewId = AnnotationId(args, "viewId");
        if (document.GetElement(new ElementId(viewId)) is not View view || view.IsTemplate)
            throw new ArgumentException("viewId must identify a non-template model view.");
        if (view is View3D threeDimensional)
        {
            if (threeDimensional.IsPerspective || !threeDimensional.IsLocked)
                throw new ArgumentException("MEP tags require a locked orthographic 3D view. Lock its orientation first.");
        }
        else if (view.ViewType is not (ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan
            or ViewType.Section or ViewType.Elevation or ViewType.Detail))
            throw new ArgumentException("MEP tags support model plans, sections, elevations, details and locked orthographic 3D views.");
        if (!args.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() is < 1 or > 50)
            throw new ArgumentException("tags must contain between 1 and 50 items.");

        var mappings = new Dictionary<long, long>();
        if (args.TryGetProperty("categoryTypes", out var categoryTypes))
        {
            if (categoryTypes.ValueKind != JsonValueKind.Array || categoryTypes.GetArrayLength() is < 1 or > 12)
                throw new ArgumentException("categoryTypes must contain between 1 and 12 mappings.");
            foreach (var mapping in categoryTypes.EnumerateArray())
            {
                var categoryName = JsonTools.GetString(mapping, "category");
                if (!Enum.TryParse<BuiltInCategory>(categoryName, out var category)
                    || !MepTagCategories.Any(pair => pair.Element == category)
                    || !mappings.TryAdd((long)category, AnnotationId(mapping, "tagTypeId")))
                    throw new ArgumentException("categoryTypes must contain unique supported MEP categories.");
            }
        }
        var visible = new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType()
            .ToElementIds().Select(id => id.Value).ToHashSet();
        var existing = new HashSet<(long Element, long Type)>();
        foreach (var tag in new FilteredElementCollector(document, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            foreach (var target in tag.GetTaggedLocalElementIds()) existing.Add((target.Value, tag.GetTypeId().Value));
        var created = new List<object>();
        var pending = new List<(IndependentTag Tag, Reference Reference, long ElementId, long TypeId)>();
        var skipped = new List<object>();
        var requested = new HashSet<long>();
        foreach (var item in tags.EnumerateArray())
        {
            var elementId = AnnotationId(item, "elementId");
            if (!requested.Add(elementId)) throw new ArgumentException("Each element may appear only once in a tag batch.");
            var element = document.GetElement(new ElementId(elementId)) ?? throw new ArgumentException($"Element {elementId} does not exist.");
            var categoryId = element.Category?.Id.Value ?? 0;
            var pair = MepTagCategories.FirstOrDefault(pair => (long)pair.Element == categoryId);
            if (pair == default || element is ElementType || element is RevitLinkInstance)
                throw new ArgumentException($"Element {elementId} must belong to a supported local MEP category.");
            var typeId = item.TryGetProperty("tagTypeId", out _) ? AnnotationId(item, "tagTypeId")
                : mappings.TryGetValue(categoryId, out var mapped) ? mapped
                : throw new ArgumentException($"No tag type supplied for element {elementId}; provide tagTypeId or categoryTypes.");
            if (document.GetElement(new ElementId(typeId)) is not FamilySymbol type || type.Category?.Id.Value != (long)pair.Tag)
                throw new ArgumentException($"Type {typeId} must be an existing {pair.Tag} family type for element {elementId}.");
            if (!visible.Contains(elementId) || element.IsHidden(view))
            { skipped.Add(new { elementId, reason = "notVisibleInView" }); continue; }
            if (existing.Contains((elementId, typeId)))
            { skipped.Add(new { elementId, reason = "alreadyTaggedWithType" }); continue; }
            var leader = !item.TryGetProperty("leader", out _) || AnnotationBoolean(item, "leader");
            if (!leader && (item.TryGetProperty("leaderEndPosition", out _) || item.TryGetProperty("elbowPosition", out _)))
                throw new ArgumentException("Leader points require leader=true.");
            var anchor = element.Location switch
            {
                LocationPoint position => position.Point,
                LocationCurve curve => curve.Curve.Evaluate(0.5, true),
                _ => MepTagBoundsAnchor(element, view)
            };
            var head = item.TryGetProperty("headPosition", out _) ? ToXYZ(ReadPoint3D(item, "headPosition"))
                : anchor + AnnotationTagOffset(item, view, 5);
            if (!type.IsActive) { type.Activate(); document.Regenerate(); }
            var reference = new Reference(element);
            var tag = IndependentTag.Create(document, type.Id, view.Id, reference, leader,
                JsonTools.GetString(item, "orientation", "horizontal") switch
                {
                    "horizontal" => TagOrientation.Horizontal,
                    "vertical" => TagOrientation.Vertical,
                    _ => throw new ArgumentException("orientation must be horizontal or vertical.")
                }, leader ? anchor : head);
            tag.TagHeadPosition = head;
            if (leader && item.TryGetProperty("leaderEndPosition", out _))
            {
                tag.LeaderEndCondition = LeaderEndCondition.Free;
                tag.SetLeaderEnd(reference, ToXYZ(ReadPoint3D(item, "leaderEndPosition")));
            }
            if (leader && item.TryGetProperty("elbowPosition", out _))
                tag.SetLeaderElbow(reference, ToXYZ(ReadPoint3D(item, "elbowPosition")));
            pending.Add((tag, reference, elementId, typeId));
        }
        document.Regenerate();
        foreach (var item in pending)
        {
            var (tag, reference, elementId, typeId) = item;
            var text = tag.TagText.Trim();
            if (tag.IsOrphaned || !tag.GetTaggedLocalElementIds().Any(id => id.Value == elementId)
                || string.IsNullOrWhiteSpace(text) || text.Contains('?'))
                throw new InvalidOperationException($"Tag for element {elementId} has no valid label. Check family label bindings and element parameters.");
            created.Add(new { id = tag.Id.Value, elementId, tagTypeId = typeId, tagText = text,
                headPosition = AnnotationPointMeters(tag.TagHeadPosition), hasLeader = tag.HasLeader,
                leaderEndCondition = tag.LeaderEndCondition.ToString(),
                elbowPosition = tag.HasLeader && tag.HasLeaderElbow(reference) ? AnnotationPointMeters(tag.GetLeaderElbow(reference)) : null,
                leaderEndPosition = tag.HasLeader && tag.LeaderEndCondition == LeaderEndCondition.Free ? AnnotationPointMeters(tag.GetLeaderEnd(reference)) : null });
        }
        return new { viewId, count = created.Count, skippedCount = skipped.Count, created, skipped,
            note = "Native dependent tags; types are chosen by actual category and explicit mapping. Layout collisions require visual review; no automatic collision solving." };
    }

    private static XYZ MepTagBoundsAnchor(Element element, View view)
    {
        var bounds = element.get_BoundingBox(view) ?? element.get_BoundingBox(null)
            ?? throw new ArgumentException($"Element {element.Id.Value} has no placement geometry.");
        return bounds.Transform.OfPoint((bounds.Min + bounds.Max) / 2);
    }
}
