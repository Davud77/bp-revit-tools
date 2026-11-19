using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>
/// Surfaces of the two sides and the edges of a wall (reference / opposite / side), for walls that stay IFC geometry.
/// Each triangle of the IFC body takes the surface of the side it faces; a side without an override keeps the IFC style.
/// Same convention as the native walls: the reference side is the exterior one, the side Revit's wall orientation points to
/// (left of the axis, right if the wall is flipped).
/// </summary>
public sealed class WallSideMaterials
{
    public ElementId Reference { get; init; } = ElementId.InvalidElementId;
    public ElementId Opposite { get; init; } = ElementId.InvalidElementId;
    public ElementId Edges { get; init; } = ElementId.InvalidElementId;
    XYZ? normal; // the exterior direction in model coordinates

    public WallSideMaterials WithAxis(JsonElement record, Geometry geometry)
    {
        var d = record.GetProperty("rawSource").GetProperty("details");
        var a = geometry.Xyz(new Point { X = d.GetProperty("begCoordinate").D("x"), Y = d.GetProperty("begCoordinate").D("y"), Z = 0 });
        var b = geometry.Xyz(new Point { X = d.GetProperty("endCoordinate").D("x"), Y = d.GetProperty("endCoordinate").D("y"), Z = 0 });
        var direction = new XYZ(b.X - a.X, b.Y - a.Y, 0).Normalize();
        var left = XYZ.BasisZ.CrossProduct(direction);
        return new WallSideMaterials { Reference = Reference, Opposite = Opposite, Edges = Edges, normal = d.B("flipped") ? left.Negate() : left };
    }

    public ElementId For(IReadOnlyList<XYZ> triangle, ElementId fallback)
    {
        if (normal == null) return fallback;
        var n = (triangle[1] - triangle[0]).CrossProduct(triangle[2] - triangle[0]);
        if (n.GetLength() < 1e-12) return fallback;
        n = n.Normalize();
        double across = n.DotProduct(normal);
        var chosen = across > 0.9 ? Reference : across < -0.9 ? Opposite : Edges;
        return chosen == ElementId.InvalidElementId ? fallback : chosen;
    }

    /// <summary>Materials of the overridden surfaces of a wall record, created inside the open transaction; null when nothing is overridden.</summary>
    public static WallSideMaterials? Create(Document doc, Manifest manifest, JsonElement details)
    {
        ElementId Of(string field)
        {
            if (!details.TryGetProperty(field, out var m) || !m.TryGetProperty("overridden", out var o) || o.ValueKind != JsonValueKind.True
                || !m.TryGetProperty("attributeId", out var id) || SurfaceMaterials.Find(manifest, id.S("guid")) is not JsonElement surface) return ElementId.InvalidElementId;
            return SurfaceMaterials.GetOrCreate(doc, surface);
        }
        var result = new WallSideMaterials { Reference = Of("referenceMaterial"), Opposite = Of("oppositeMaterial"), Edges = Of("sideMaterial") };
        return result.Reference == ElementId.InvalidElementId && result.Opposite == ElementId.InvalidElementId && result.Edges == ElementId.InvalidElementId ? null : result;
    }
}
