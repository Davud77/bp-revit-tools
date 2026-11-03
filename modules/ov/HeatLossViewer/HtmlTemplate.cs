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
        bool native = json.RootElement.TryGetProperty("nativeRadiators", out var mode) && mode.ValueKind == JsonValueKind.True;
        string html = Read("Viewer.html");
        if (!native)
        {
            html = ReplaceBlock(html, "RADIATOR_PANEL", "");
            html = ReplaceBlock(html, "RADIATOR_ROOM", "<details id=\"radiator-room-preview\"><summary>Радиаторы — результат подбора из Revit</summary><p id=\"rad-preview-state\" class=\"note\"></p><div class=\"scroll\"><table><thead><tr><th>Прибор</th><th>Вт</th><th>Размещение</th></tr></thead><tbody id=\"rad-preview-devices\"></tbody></table></div></details>");
            const string button = "<button data-view=\"radiators\"";
            int start = html.IndexOf(button, StringComparison.Ordinal);
            if (start >= 0) html = html.Remove(start, html.IndexOf("</button>", start, StringComparison.Ordinal) + 9 - start);
        }
        return html.Replace("__VIEWER_SCRIPT__", Read("Poteri.js") + "\n" + Read("Calculation.js") + "\n" + Read("LibraryInputs.js") + "\n" + Read("Selection.js") + "\n" + Read("RadiatorSelection.js") + "\n" + Read("Viewer.js") + "\n" + Read("ThermalInputs.js") + "\n" + Read(native ? "RadiatorUI.js" : "RadiatorPreview.js") + "\n" + Read("Report.js") + "\n" + Read("Layout.js") + "\n" + Read("TableSorting.js") + (native ? "\n" + Read("NativeRadiatorBridge.js") : ""))
            .Replace("__RADIATOR_CATALOG__", JsonSerializer.Serialize(catalog.RootElement))
            .Replace("__BUILDING_JSON__", safeJson);
    }
    private static string ReplaceBlock(string html, string key, string replacement)
    {
        string a = "<!--" + key + "_START-->", b = "<!--" + key + "_END-->";
        int start = html.IndexOf(a, StringComparison.Ordinal), end = html.IndexOf(b, StringComparison.Ordinal);
        if (start < 0 || end < start) throw new InvalidOperationException("Missing HTML section: " + key);
        return html[..start] + replacement + html[(end + b.Length)..];
    }

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HeatLossViewer.Viewer." + name)
            ?? throw new InvalidOperationException("Missing embedded resource: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
