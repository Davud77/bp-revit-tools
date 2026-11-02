using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace BotPlus.RT.Setup;

static partial class Deployment
{
    static readonly HashSet<string> LegacyClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WEAM.Revit.AI.PluginApplication", "Ac2Revit.App", "HeatLossViewer.App"
    };

    static IEnumerable<string> LegacyManifests(string baseDir)
    {
        if (!Directory.Exists(baseDir)) yield break;
        foreach (var file in Directory.EnumerateFiles(baseDir, "*.addin"))
        {
            XDocument xml;
            try { xml = XDocument.Load(file); }
            catch (System.Xml.XmlException) { continue; }
            if (xml.Descendants("AddIn").Any(a => LegacyClasses.Contains(a.Element("FullClassName")?.Value ?? "")))
                yield return file;
        }
    }

    static void CheckLegacyManifests()
    {
        var machine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "Revit", "Addins", "2026");
        var conflicts = LegacyManifests(machine).ToList();
        var combined = Path.Combine(machine, "BotPlus.RT.addin");
        if (File.Exists(combined)) conflicts.Add(combined);
        if (conflicts.Count != 0)
            throw new InvalidOperationException("Удалите прежние установки для всех пользователей, затем повторите: " + string.Join(", ", conflicts));
        foreach (var file in LegacyManifests(Path.GetDirectoryName(Manifest)!))
        {
            if (XDocument.Load(file).Descendants("AddIn").Any(a => !LegacyClasses.Contains(a.Element("FullClassName")?.Value ?? "")))
                throw new InvalidOperationException("Общая регистрация содержит сторонние плагины. Разделите её перед установкой: " + file);
        }
    }

    static string LegacyState => Path.Combine(Data, "legacy-manifests.json");

    static void DisableLegacyManifests()
    {
        var saved = File.Exists(LegacyState)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LegacyState))!
            : new Dictionary<string, string>();
        foreach (var file in LegacyManifests(Path.GetDirectoryName(Manifest)!).ToList())
        {
            var backup = file + ".botplus-disabled-" + Guid.NewGuid().ToString("N");
            File.Move(file, backup);
            saved[file] = backup;
            File.WriteAllText(LegacyState, JsonSerializer.Serialize(saved), Utf8);
        }
    }

    static void RestoreLegacyManifests()
    {
        if (!File.Exists(LegacyState)) return;
        var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LegacyState))!;
        foreach (var (file, backup) in saved)
            if (File.Exists(backup) && !File.Exists(file)) File.Move(backup, file);
        if (saved.Values.All(file => !File.Exists(file))) File.Delete(LegacyState);
    }

    static void PreserveSessions()
    {
        foreach (var version in Directory.GetDirectories(Path.Combine(App, "versions")))
        {
            var artifacts = Path.Combine(version, "modules", "ac2revit", "artifacts");
            if (!Directory.Exists(artifacts)) continue;
            var saved = Path.Combine(Data, "saved-sessions", Path.GetFileName(version) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            RequireChild(artifacts, App);
            RequireChild(saved, Data);
            Directory.Move(artifacts, saved);
        }
    }

    public static void VerifyPayload(string destination)
    {
        Directory.CreateDirectory(destination);
        if (Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidOperationException("Для проверки нужна пустая папка.");
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WEAM.Payload.zip")!;
        using var archive = new ZipArchive(stream);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            RequireChild(target, destination);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
        foreach (var file in new[] { "BotPlus.RT.dll", "modules/codex-mcp/plugin/WEAM.Revit.AI.dll",
            "modules/ac2revit/dist/Revit2026/Ac2Revit.dll", "modules/ov/HeatLossViewer.dll",
            "node/node.exe", "server/src/index.js", "modules/ac2revit/runtime/python/python.exe" })
            if (!File.Exists(Path.Combine(destination, file))) throw new FileNotFoundException(file);
    }
}
