using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>
/// Colours and transparency that the IFC gave to the triangles of an element (glass, facade finishes, furniture).
/// Every distinct style becomes one Revit material that is reused by name, and each triangle of the DirectShape
/// receives it, so the copy looks like the source instead of Revit's default grey. Triangles without a style stay
/// without a material.
/// </summary>
public static class IfcSurfaceStyles
{
    public readonly record struct Style(int R, int G, int B, int Transparency)
    {
        public string MaterialName => $"АС стиль IFC #{R:X2}{G:X2}{B:X2} прозр. {Transparency}%";
    }

    /// <summary>The styles of one record and the style index of each triangle (-1 = none); null when the record has none.</summary>
    public static (IReadOnlyList<Style> Styles, IReadOnlyList<int> Faces)? Read(JsonElement record, int triangleCount)
    {
        if (!record.TryGetProperty("surfaceStyles", out var styles) || styles.ValueKind != JsonValueKind.Array
            || !record.TryGetProperty("faceStyles", out var faces) || faces.ValueKind != JsonValueKind.Array) return null;
        var list = new List<Style>();
        foreach (var style in styles.EnumerateArray())
        {
            var rgb = style.GetProperty("rgb").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            int transparency = style.GetProperty("transparency").GetInt32();
            if (rgb.Length != 3 || rgb.Any(v => v < 0 || v > 255) || transparency < 0 || transparency > 100)
                throw new InvalidDataException("Invalid IFC surface style");
            list.Add(new Style(rgb[0], rgb[1], rgb[2], transparency));
        }
        var perFace = faces.EnumerateArray().Select(v => v.GetInt32()).ToList();
        if (perFace.Count != triangleCount || perFace.Any(i => i < -1 || i >= list.Count))
            throw new InvalidDataException("IFC surface styles do not match the triangles");
        return (list, perFace);
    }

    /// <summary>Creates (or finds by name) the material of every style used by <paramref name="records"/>.</summary>
    public static Dictionary<Style, ElementId> Ensure(Document doc, IEnumerable<JsonElement> records)
    {
        var wanted = new HashSet<Style>();
        foreach (var record in records)
        {
            if (!record.TryGetProperty("surfaceStyles", out var styles) || styles.ValueKind != JsonValueKind.Array) continue;
            foreach (var style in styles.EnumerateArray())
            {
                var rgb = style.GetProperty("rgb").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                wanted.Add(new Style(rgb[0], rgb[1], rgb[2], style.GetProperty("transparency").GetInt32()));
            }
        }
        var result = new Dictionary<Style, ElementId>();
        if (wanted.Count == 0) return result;
        var existing = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
            .GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First().Id);
        using var transaction = new Transaction(doc, "IFC surface style materials");
        transaction.Start();
        foreach (var style in wanted.OrderBy(s => s.MaterialName, StringComparer.Ordinal))
        {
            if (!existing.TryGetValue(style.MaterialName, out var id))
            {
                id = Material.Create(doc, style.MaterialName);
                var material = (Material)doc.GetElement(id);
                material.Color = new Color((byte)style.R, (byte)style.G, (byte)style.B);
                material.Transparency = style.Transparency;
            }
            result[style] = id;
        }
        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("IFC surface style materials were not committed");
        return result;
    }
}
