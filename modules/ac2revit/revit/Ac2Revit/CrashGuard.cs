using System.Text.Json;

namespace Ac2Revit;

/// <summary>
/// A native Revit crash on one element ("АР ветхое": opening cut 3153 / 3642, ucrtbase 0xc0000409) would repeat on
/// every retry. Before each risky element the key is written to a marker next to the session; after the element it
/// is cleared. A marker left by a crash makes the next run skip that element and report it, so the rest is built.
/// </summary>
public sealed class CrashGuard
{
    readonly string path; readonly string stage;
    public IReadOnlySet<string> Quarantined { get; }

    public CrashGuard(string folder, string stage)
    {
        this.stage = stage; Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "crash-guard-" + stage + ".json");
        var quarantined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string history = path + ".quarantine";
        if (File.Exists(history)) foreach (string line in File.ReadAllLines(history)) if (line.Length > 0) quarantined.Add(line);
        if (File.Exists(path))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                string key = json.RootElement.GetProperty("key").GetString() ?? "";
                if (key.Length > 0 && quarantined.Add(key)) File.AppendAllLines(history, [key]);
            }
            catch (JsonException) { }
            File.Delete(path);
        }
        Quarantined = quarantined;
    }

    public void Enter(string key) => File.WriteAllText(path, JsonSerializer.Serialize(new { stage, key, utc = DateTime.UtcNow }));
    public void Exit() { if (File.Exists(path)) File.Delete(path); }
}
