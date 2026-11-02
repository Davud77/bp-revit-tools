using System.Reflection;
using System.IO;
using System.Text.Json;

namespace HeatLossViewer;

public static class HtmlTemplate
{
    public static string GetHtml(string buildingJson)
    {
        // Normalize with the safe default encoder even for callers outside the export writer.
        using var json = JsonDocument.Parse(buildingJson);
        string safeJson = JsonSerializer.Serialize(json.RootElement);
        using var catalog = JsonDocument.Parse(Read("RadiatorCatalog.json"));
        return Read("Viewer.html").Replace("__VIEWER_SCRIPT__", Read("Calculation.js") + "\n" + Read("RadiatorSelection.js") + "\n" + Read("Viewer.js") + "\n" + Read("RadiatorUI.js"))
            .Replace("__RADIATOR_CATALOG__", JsonSerializer.Serialize(catalog.RootElement))
            .Replace("__BUILDING_JSON__", safeJson);
    }

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HeatLossViewer.Viewer." + name)
            ?? throw new InvalidOperationException("Missing embedded resource: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
