using System.Text;
using System.IO;
using System.Text.Json;

namespace HeatLossViewer.Export;

public sealed class BuildingExportWriter
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
        // Default encoder escapes HTML, including </script> in room names.
    };

    public (string Json, string Html) Write(BuildingData building, string directory)
    {
        OvExportScope.ExcludeUnboundOpenings(building);
        Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(building, JsonOptions);
        string html = HtmlTemplate.GetHtml(json);
        string jsonPath = Path.Combine(directory, "building.json");
        string htmlPath = Path.Combine(directory, "heat_loss_viewer.html");
        WriteAtomic(jsonPath, json);
        WriteAtomic(htmlPath, html);
        return (jsonPath, htmlPath);
    }

    private static void WriteAtomic(string path, string content)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
