using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private static bool IsMepTagSymbol(FamilySymbol symbol) =>
        MepTagCategories.Any(p => symbol.Category?.Id.Value == (long)p.Tag);

    private static Parameter ShelfParameter(FamilySymbol symbol, string name)
    {
        var matches = symbol.GetParameters(name);
        if (matches.Count != 1 || matches[0].StorageType != StorageType.Double
            || !matches[0].Definition.GetDataType().Equals(SpecTypeId.Length))
            throw new ArgumentException($"Type {symbol.Id.Value} needs one length parameter '{name}'.");
        return matches[0];
    }

    private static object PreviewDuplicateTagTypes(UIApplication app, JsonElement args) =>
        PreviewOperation(app, args, "duplicateTagTypes", "Типоразмеры полок марок", DuplicateTagTypes);
    private static object ApplyDuplicateTagTypes(UIApplication app, JsonElement args, Func<bool>? cancelled) =>
        ApplyOperation(app, args, "duplicateTagTypes", "Создать типоразмеры полок марок", DuplicateTagTypes, cancelled);

    private static object DuplicateTagTypes(Document document, JsonElement args)
    {
        var items = args.GetProperty("types");
        if (items.GetArrayLength() is < 1 or > 50) throw new ArgumentException("Provide 1..50 types.");
        var names = new HashSet<string>();
        var result = new List<object>();
        foreach (var item in items.EnumerateArray())
        {
            var sourceId = AnnotationId(item, "sourceSymbolId");
            if (document.GetElement(new ElementId(sourceId)) is not FamilySymbol source || !IsMepTagSymbol(source))
                throw new ArgumentException("sourceSymbolId must identify a native MEP tag type.");
            var name = JsonTools.GetString(item, "name").Trim();
            if (name.Length is < 1 or > 200 || !names.Add(source.Family.Id.Value + ":" + name.ToUpperInvariant()))
                throw new ArgumentException("Provide unique nonempty family/type names.");
            var length = HostedReadNumber(item, "shelfLengthMillimeters", 5, 500);
            if (Math.Abs(length / 5 - Math.Round(length / 5)) > 1e-8)
                throw new ArgumentException("Shelf lengths must be multiples of 5 paper millimetres.");
            var parameterName = JsonTools.GetString(item, "lengthParameterName", "Длина полки");
            var originalLength = ShelfParameter(source, parameterName);
            var existing = source.Family.GetFamilySymbolIds().Select(id => document.GetElement(id))
                .OfType<FamilySymbol>().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            FamilySymbol target;
            if (existing is not null)
            {
                var existingLength = ShelfParameter(existing, parameterName);
                if (Math.Abs(existingLength.AsDouble() * 304.8 - length) > .001)
                    throw new ArgumentException($"Existing type '{name}' has a different shelf length.");
                foreach (var p in source.GetOrderedParameters().Where(p => !p.IsReadOnly && p.HasValue && p.Id != originalLength.Id
                    && p.Id.Value != (long)BuiltInParameter.SYMBOL_NAME_PARAM))
                {
                    var other = existing.GetOrderedParameters().FirstOrDefault(q => q.Id == p.Id);
                    var equal = other is not null && other.StorageType == p.StorageType && p.StorageType switch
                    {
                        StorageType.Double => Math.Abs(p.AsDouble() - other.AsDouble()) < 1e-10,
                        StorageType.Integer => p.AsInteger() == other.AsInteger(),
                        StorageType.ElementId => p.AsElementId() == other.AsElementId(),
                        StorageType.String => p.AsString() == other.AsString(),
                        _ => p.AsValueString() == other.AsValueString()
                    };
                    if (!equal)
                        throw new ArgumentException($"Existing type '{name}' differs in '{p.Definition.Name}'.");
                }
                target = existing;
            }
            else
            {
                target = (FamilySymbol)source.Duplicate(name);
                var parameter = ShelfParameter(target, parameterName);
                if (parameter.IsReadOnly || !parameter.Set(length / 304.8))
                    throw new ArgumentException("The shelf parameter is not writable.");
            }
            result.Add(new { id = target.Id.Value, sourceSymbolId = sourceId, family = source.FamilyName,
                name = target.Name, shelfLengthMillimeters = length, reused = existing is not null });
        }
        document.Regenerate();
        return new { count = result.Count, types = result };
    }

    private static object ReadMepTagLayout(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        var viewId = AnnotationId(args, "viewId");
        if (document.GetElement(new ElementId(viewId)) is not View view || view.IsTemplate)
            throw new ArgumentException("Provide a non-template view.");
        var ids = args.GetProperty("tagIds").EnumerateArray().Select(i => i.GetInt64()).ToArray();
        if (ids.Length is < 1 or > 100 || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("Provide 1..100 unique tag IDs.");
        var targets = new Dictionary<long, object>();
        var tags = new List<object>();
        foreach (var id in ids)
        {
            if (document.GetElement(new ElementId(id)) is not IndependentTag tag || tag.OwnerViewId != view.Id
                || document.GetElement(tag.GetTypeId()) is not FamilySymbol symbol || !IsMepTagSymbol(symbol))
                throw new ArgumentException($"Tag {id} is not a MEP tag in this view.");
            var refs = tag.GetTaggedReferences();
            if (refs.Count is < 1 or > 20 || refs.Any(r=>r.LinkedElementId != ElementId.InvalidElementId))
                throw new ArgumentException("Provide 1..20 local targets per tag.");
            foreach (var reference in refs)
            {
            var target = document.GetElement(reference.ElementId);
            if (!targets.ContainsKey(target.Id.Value))
            {
                var samples = new List<XYZ>();
                if (target.Location is LocationCurve location)
                    foreach(var fraction in new[]{.2,.5,.8})samples.Add(location.Curve.Evaluate(fraction,true));
                void Collect(GeometryElement? geometry)
                {
                    if (geometry is null || samples.Count >= 2048) return;
                    foreach (var g in geometry)
                    {
                        if (samples.Count >= 2048) break;
                        if (g is GeometryInstance instance) Collect(instance.GetInstanceGeometry());
                        else if (g is Solid solid && solid.Volume > 1e-10)
                            foreach (Edge edge in solid.Edges)
                            {
                                var edgeCurve=edge.AsCurve();
                                samples.Add(edgeCurve.Evaluate(.5,true));
                                if(edgeCurve.IsBound){samples.Add(edgeCurve.GetEndPoint(0));samples.Add(edgeCurve.GetEndPoint(1));}
                                if (samples.Count >= 2048) break;
                            }
                        else if (g is Curve curve && curve.IsBound) samples.Add(curve.Evaluate(.5, true));
                    }
                }
                string? geometryError = null;
                try { Collect(target.get_Geometry(new Options { View = view, IncludeNonVisibleObjects = false })); }
                catch (Exception e) { geometryError = e.Message; }
                var box = target.get_BoundingBox(view);
                var centre = target.Location is LocationPoint point ? point.Point
                    : box is null ? tag.TagHeadPosition : box.Transform.OfPoint((box.Min + box.Max) / 2);
                var candidates = samples.GroupBy(p => $"{Math.Round(p.X,6)}:{Math.Round(p.Y,6)}:{Math.Round(p.Z,6)}")
                    .Select(g => g.First()).ToArray();
                // Include visible boundary points in every paper direction, not only points nearest the centre.
                var boundary=new List<XYZ>();
                for(var i=0;i<16 && candidates.Length>0;i++)
                {
                    var direction=view.RightDirection*Math.Cos(i*Math.PI/8)+view.UpDirection*Math.Sin(i*Math.PI/8);
                    boundary.Add(candidates.MaxBy(p=>p.DotProduct(direction))!);
                }
                candidates=boundary.Concat(candidates.OrderBy(p=>p.DistanceTo(centre)))
                    .DistinctBy(p=>$"{Math.Round(p.X,6)}:{Math.Round(p.Y,6)}:{Math.Round(p.Z,6)}").Take(96).ToArray();
                targets[target.Id.Value] = new { id = target.Id.Value, category = target.Category?.Name,
                    categoryId = target.Category?.Id.Value, className = target.GetType().Name,
                    anchorCandidates = candidates.Select(AnnotationPointMeters).ToArray(), geometryError,
                    bounds = box is null ? null : new { min = AnnotationPointMeters(box.Transform.OfPoint(box.Min)),
                        max = AnnotationPointMeters(box.Transform.OfPoint(box.Max)) } };
            }
            }
            Parameter? shelf = null;
            try { shelf = ShelfParameter(symbol, "Длина полки"); } catch (ArgumentException) { }
            tags.Add(new { id, typeId = symbol.Id.Value, family = symbol.FamilyName, typeName = symbol.Name,
                targetId = refs[0].ElementId.Value, targetIds = refs.Select(r=>r.ElementId.Value).ToArray(),
                tagText = tag.TagText, hasLeader = tag.HasLeader, mergeElbows = tag.MergeElbows,
                leaders = refs.Select(r=>new { targetId = r.ElementId.Value,
                    end = tag.HasLeader && tag.LeaderEndCondition == LeaderEndCondition.Free ? AnnotationPointMeters(tag.GetLeaderEnd(r)) : null,
                    elbow = tag.HasLeader && tag.HasLeaderElbow(r) ? AnnotationPointMeters(tag.GetLeaderElbow(r)) : null }).ToArray(),
                orientation = tag.TagOrientation.ToString(), head = AnnotationPointMeters(tag.TagHeadPosition),
                elbow = tag.HasLeader && tag.HasLeaderElbow(refs[0]) ? AnnotationPointMeters(tag.GetLeaderElbow(refs[0])) : null,
                end = tag.HasLeader && tag.LeaderEndCondition == LeaderEndCondition.Free ? AnnotationPointMeters(tag.GetLeaderEnd(refs[0])) : null,
                shelfLengthMillimeters = shelf?.AsDouble() * 304.8 });
        }
        return new { viewId, scale = view.Scale, tags, targets = targets.Values,
            axes = new { origin = AnnotationPointMeters(view.Origin), right = new { x = view.RightDirection.X, y = view.RightDirection.Y, z = view.RightDirection.Z },
                up = new { x = view.UpDirection.X, y = view.UpDirection.Y, z = view.UpDirection.Z } } };
    }

    private static object InspectTagFamily(UIApplication app, JsonElement args)
    {
        var document = RequireDocument(app);
        if (document.GetElement(new ElementId(AnnotationId(args, "symbolId"))) is not FamilySymbol symbol
            || !IsMepTagSymbol(symbol) || !symbol.Family.IsEditable)
            throw new ArgumentException("Provide an editable MEP tag family type.");
        var familyDocument = document.EditFamily(symbol.Family);
        try
        {
            using var transaction = new Transaction(familyDocument, "Inspect annotation type without saving");
            transaction.Start();
            var manager = familyDocument.FamilyManager;
            var type = manager.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == symbol.Name);
            if (type is null) throw new ArgumentException("Family type was not found in the editable definition.");
            manager.CurrentType = type;
            familyDocument.Regenerate();
            object Point(XYZ p) => new { xMm = p.X * 304.8, yMm = p.Y * 304.8, zMm = p.Z * 304.8 };
            var curves = new FilteredElementCollector(familyDocument).OfClass(typeof(CurveElement))
                .Cast<CurveElement>().Where(c => c.GeometryCurve.IsBound).Select(c => new
                { id = c.Id.Value, style = c.LineStyle?.Name, start = Point(c.GeometryCurve.GetEndPoint(0)), end = Point(c.GeometryCurve.GetEndPoint(1)) }).Take(100).ToArray();
            transaction.RollBack();
            return new { symbolId = symbol.Id.Value, family = symbol.FamilyName, typeName = symbol.Name,
                curves, projectModified = document.IsModified, familySaved = false };
        }
        finally { familyDocument.Close(false); }
    }
}
