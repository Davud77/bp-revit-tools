using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace BotPlus.Eom;

internal static class SwitchBoxPlacement
{
    const double Tolerance = 0.0001;
    internal sealed record WallPoint(Reference Reference, XYZ Hit, XYZ Origin, XYZ Inward);

    internal static XYZ Find(UIDocument ui, FamilyInstance device, double offset)
    {
        var doc = ui.Document;
        var point = (device.Location as LocationPoint)?.Point
            ?? throw new InvalidOperationException("Для размещения коробки нужна точка установки выключателя.");

        var face = device.HostFace is { } hostFace ? WallFace(doc, hostFace) : null;
        if (face == null && device.Host is Wall wall)
        {
            var sides = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Interior)
                .Concat(HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior));
            face = sides.Select(reference => WallFace(doc, reference))
                .OfType<(XYZ Origin, XYZ Normal)>()
                .OrderBy(side => Math.Abs((point - side.Origin).DotProduct(side.Normal)))
                .ThenByDescending(side => side.Normal.DotProduct(device.FacingOrientation))
                .Select(side => ((XYZ Origin, XYZ Normal)?)side)
                .FirstOrDefault();
        }
        if (face == null)
        {
            var reference = ui.Selection.PickObject(ObjectType.PointOnElement, new WallFaceFilter(doc),
                "Укажите грань стены со стороны помещения для отступа потолочной коробки.");
            face = WallFace(doc, reference)
                ?? throw new InvalidOperationException("Укажите плоскую вертикальную грань стены.");
        }

        var (origin, inward) = face.Value;
        return FindOverhead(doc, point, origin, inward, offset);
    }

    internal static WallPoint PickWall(UIDocument ui)
    {
        var doc = ui.Document;
        var reference = ui.Selection.PickObject(ObjectType.PointOnElement, new WallFaceFilter(doc),
            "Укажите точку на внутренней грани стены, где нужно поставить выключатель.");
        var (origin, inward) = WallFace(doc, reference)
            ?? throw new InvalidOperationException("Укажите плоскую вертикальную грань стены.");
        return new(reference, reference.GlobalPoint, origin, inward);
    }

    internal static FamilyInstance PlaceSwitch(Document doc, FamilySymbol symbol, Level level, WallPoint wall, double height)
    {
        if (!symbol.IsActive) symbol.Activate();
        var hit = new XYZ(wall.Hit.X, wall.Hit.Y, level.ProjectElevation + height);
        var point = hit - wall.Inward * (hit - wall.Origin).DotProduct(wall.Inward);
        var device = symbol.Family.FamilyPlacementType switch
        {
            FamilyPlacementType.WorkPlaneBased => doc.Create.NewFamilyInstance(wall.Reference, point, XYZ.BasisZ, symbol),
            FamilyPlacementType.OneLevelBasedHosted when doc.GetElement(wall.Reference.ElementId) is Wall host =>
                doc.Create.NewFamilyInstance(point, symbol, host, level, StructuralType.NonStructural),
            _ => doc.Create.NewFamilyInstance(new XYZ(point.X, point.Y, level.ProjectElevation), symbol, level, StructuralType.NonStructural)
        };
        doc.Regenerate();
        if (device.Location is not LocationPoint location)
            throw new InvalidOperationException("Revit не определил точку установки выключателя.");
        var delta = point - location.Point;
        if (symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
        {
            if (delta.GetLength() > Tolerance) ElementTransformUtils.MoveElement(doc, device.Id, delta);
            var facing = device.FacingOrientation;
            var angle = Math.Atan2(facing.CrossProduct(wall.Inward).Z, facing.DotProduct(wall.Inward));
            if (Math.Abs(angle) > Tolerance)
                ElementTransformUtils.RotateElement(doc, device.Id, Line.CreateUnbound(point, XYZ.BasisZ), angle);
        }
        doc.Regenerate();
        if (location.Point.DistanceTo(point) > Tolerance)
            throw new InvalidOperationException("Основа семейства ограничивает установку выключателя на заданной высоте. Выберите совместимый настенный тип.");
        return device;
    }

    internal static XYZ Find(UIDocument ui, FamilyInstance device, WallPoint wall, double offset) =>
        FindOverhead(ui.Document, ((LocationPoint)device.Location).Point, wall.Origin, wall.Inward, offset);

    static XYZ FindOverhead(Document doc, XYZ point, XYZ origin, XYZ inward, double offset)
    {
        var overSwitch = point + inward * (offset - (point - origin).DotProduct(inward));
        if (!XYZ.IsWithinLengthLimits(overSwitch))
            throw new InvalidOperationException("Точка коробки за пределами допустимых координат Revit. Проверьте отступ от стены.");

        double? elevation = FindOverheadFace(doc, Transform.Identity, overSwitch);
        foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            if (link.GetLinkDocument() is not { } linked) continue;
            var hit = FindOverheadFace(linked, link.GetTotalTransform(), overSwitch);
            if (hit != null && (elevation == null || hit.Value < elevation.Value)) elevation = hit;
        }
        return elevation is { } z ? new XYZ(overSwitch.X, overSwitch.Y, z)
            : throw new InvalidOperationException("Над точкой с заданным отступом нет потолка или перекрытия в модели и загруженных связях. Проверьте положение выключателя и отступ.");
    }

    static double? FindOverheadFace(Document doc, Transform toModel, XYZ point)
    {
        var inverse = toModel.Inverse;
        var local = inverse.OfPoint(point);
        var up = inverse.OfVector(XYZ.BasisZ);
        var ray = Line.CreateUnbound(local, up);
        double? nearest = null;
        var overhead = new ElementMulticlassFilter([typeof(Ceiling), typeof(Floor)]);
        foreach (var ceiling in new FilteredElementCollector(doc).WherePasses(overhead).Cast<HostObject>())
        {
            var box = ceiling.get_BoundingBox(null);
            if (box != null && Math.Abs(up.Z) > 0.99999 && (local.X < box.Min.X - Tolerance || local.X > box.Max.X + Tolerance
                || local.Y < box.Min.Y - Tolerance || local.Y > box.Max.Y + Tolerance || local.Z >= box.Max.Z))
                continue;
            foreach (var reference in HostObjectUtils.GetBottomFaces(ceiling))
            {
                if (ceiling.GetGeometryObjectFromReference(reference) is not Face face
                    || face.Intersect(ray, out var intersections) != SetComparisonResult.Overlap || intersections == null)
                    continue;
                foreach (IntersectionResult hit in intersections)
                {
                    double z = toModel.OfPoint(hit.XYZPoint).Z;
                    if (z > point.Z + Tolerance && (nearest == null || z < nearest.Value)) nearest = z;
                }
            }
        }
        return nearest;
    }

    static (XYZ Origin, XYZ Normal)? WallFace(Document doc, Reference reference)
    {
        var owner = doc.GetElement(reference.ElementId);
        var transform = Transform.Identity;
        if (owner is RevitLinkInstance link)
        {
            var linked = link.GetLinkDocument();
            if (linked == null) return null;
            owner = linked.GetElement(reference.LinkedElementId);
            reference = reference.CreateReferenceInLink();
            transform = link.GetTotalTransform();
        }
        if (owner is not Wall wall || wall.GetGeometryObjectFromReference(reference) is not PlanarFace face)
            return null;
        var normal = transform.OfVector(face.FaceNormal);
        return Math.Abs(normal.Z) <= Tolerance
            ? (transform.OfPoint(face.Origin), new XYZ(normal.X, normal.Y, 0).Normalize())
            : null;
    }

    sealed class WallFaceFilter(Document doc) : ISelectionFilter
    {
        public bool AllowElement(Element element) => element is Wall or RevitLinkInstance;
        public bool AllowReference(Reference reference, XYZ position) => WallFace(doc, reference) != null;
    }
}
