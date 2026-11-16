using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>One Revit material per Archicad surface (colour and transparency). Call inside an open transaction.</summary>
public static class SurfaceMaterials
{
    public static ElementId GetOrCreate(Document doc, JsonElement surface)
    {
        var c = surface.GetProperty("surfaceColor");
        byte Channel(string name) => (byte)Math.Clamp((int)Math.Round(c.D(name) * 255, MidpointRounding.AwayFromZero), 0, 255);
        var color = new Color(Channel("red"), Channel("green"), Channel("blue"));
        int transparency = (int)Math.Clamp(Math.Round(surface.D("transparency")), 0, 100);
        string name = SourceLabel.Format($"Поверхность {surface.S("name")} #{color.Red:X2}{color.Green:X2}{color.Blue:X2}" + (transparency > 0 ? $" прозр. {transparency}%" : ""));
        var existing = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m => m.Name == name);
        if (existing != null) return existing.Id;
        var id = Material.Create(doc, name);
        var material = (Material)doc.GetElement(id);
        material.Color = color; material.Transparency = transparency;
        return id;
    }

    /// <summary>The manifest surface with this attribute GUID, or null.</summary>
    public static JsonElement? Find(Manifest manifest, string guid)
    {
        foreach (var s in manifest.Surfaces)
            if (s.TryGetProperty("attributeId", out var id) && s.TryGetProperty("surfaceColor", out _) && string.Equals(id.S("guid"), guid, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }
}
