using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace BotPlus.RT.Setup;

static partial class Deployment
{
    public const string Version = "1.6";
    const string AddinId = "DE07865F-0FBF-4CC3-B71E-AC2B149113E6";
    const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BotPlus.RT";
    static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotPlus.RT");
    static readonly string App = Path.Combine(Data, "Application");
    static readonly string Release = Path.Combine(App, "versions", Version);
    static readonly string Manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2026", "BotPlus.RT.addin");
    static readonly Encoding Utf8 = new UTF8Encoding(false);
    public static string LogPath => Path.Combine(Data, "setup.log");
    public static event Action<string>? Logged;

    public static void Log(string message)
    {
        Directory.CreateDirectory(Data);
        string line = DateTimeOffset.Now.ToString("O") + " " + message;
        File.AppendAllText(LogPath, line + Environment.NewLine, Utf8);
        Logged?.Invoke(line);
    }

    public static string? FindRevit()
    {
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", "Revit 2026");
        if (File.Exists(Path.Combine(standard, "Revit.exe"))) return standard;
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Autodesk\Revit\2026");
        foreach (var name in new[] { "InstallationLocation", "InstallLocation", "InstallPath" })
            if (key?.GetValue(name) is string location && File.Exists(Path.Combine(location, "Revit.exe"))) return location;
        return null;
    }

    static void CheckRevitClosed()
    {
        if (Process.GetProcessesByName("Revit").Length != 0)
            throw new InvalidOperationException("Сохраните проекты и закройте все окна Revit, затем повторите установку или удаление.");
    }

    public static string ReadLicense()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WEAM.Payload.zip")!;
        using var archive = new ZipArchive(stream);
        using var reader = new StreamReader(archive.GetEntry("LICENSE")!.Open());
        return reader.ReadToEnd();
    }

    public static void Install(string? revitDir, IProgress<SetupProgress>? progress = null, int? archicadVersion = 28, string? archicadDir = null)
    {
        void Step(int percent, string text) { Log(text); progress?.Report(new(percent, text)); }
        Step(0, "Начало установки BotPlus RT " + Version);
        Step(2, "Проверка открытых экземпляров Revit");
        CheckRevitClosed();
        Step(5, "Проверка папки и версии Revit 2026");
        revitDir ??= FindRevit();
        if (revitDir is null || !File.Exists(Path.Combine(revitDir, "Revit.exe")) || !File.Exists(Path.Combine(revitDir, "RevitAPI.dll")))
            throw new InvalidOperationException("Не найден Revit 2026. Укажите его папку установки.");
        if (FileVersionInfo.GetVersionInfo(Path.Combine(revitDir, "RevitAPI.dll")).FileMajorPart != 26)
            throw new InvalidOperationException("Этот установщик поддерживает Revit 2026. Выбрана другая версия.");
        CheckLegacyManifests();
        if (archicadVersion is int ac) TapirTarget(ac, archicadDir);

        Step(10, "Распаковка файлов приложения в " + Release);
        Directory.CreateDirectory(Release);
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WEAM.Payload.zip")!)
        using (var archive = new ZipArchive(stream))
        {
            var files = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            int completed = 0;
            foreach (var entry in files)
            {
                var destination = Path.GetFullPath(Path.Combine(Release, entry.FullName));
                RequireChild(destination, Release);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var memory = new MemoryStream(); input.CopyTo(memory);
                var bytes = memory.ToArray();
                // Same-version repair works even when a client still holds the identical node.exe.
                if (!File.Exists(destination) || !SHA256.HashData(File.ReadAllBytes(destination)).SequenceEqual(SHA256.HashData(bytes))) File.WriteAllBytes(destination, bytes);
                completed++;
                if (completed % 100 == 0 || completed == files.Count) Step(10 + (int)(65L * completed / files.Count), $"Файлы: {completed} / {files.Count} — {entry.FullName}");
            }
        }
        if (archicadVersion is int version)
        { Step(76, $"Установка Tapir для Archicad {version}"); InstallTapir(version, archicadDir); }
        else Log("Установка Tapir пропущена по выбору пользователя.");
        Step(78, "Подготовка примеров MCP-конфигураций");
        RunNode(Release, "--examples");
        Step(84, "Регистрация единственной надстройки BotPlus RT для Revit");

        var assembly = SecurityElement.Escape(Path.Combine(Release, "BotPlus.RT.dll"));
        var xml = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><RevitAddIns><AddIn Type=\"Application\"><Name>BotPlus RT</Name><Assembly>{assembly}</Assembly><AddInId>{AddinId}</AddInId><FullClassName>BotPlus.RT.Application</FullClassName><VendorId>BTPL</VendorId><VendorDescription>BotPlus RT: MCP, Ac2Revit, ОВ</VendorDescription></AddIn></RevitAddIns>";
        DisableLegacyManifests();
        AtomicWithBackup(Manifest, xml);
        Step(92, "Регистрация версии и удаления в Windows");
        var setupCopy = Path.Combine(App, "BotPlus.RT.Setup.exe");
        if (!string.Equals(Environment.ProcessPath, setupCopy, StringComparison.OrdinalIgnoreCase)) File.Copy(Environment.ProcessPath!, setupCopy, true);
        using var uninstall = Registry.CurrentUser.CreateSubKey(RegistryKey);
        uninstall.SetValue("DisplayName", "BotPlus RT для Revit 2026");
        uninstall.SetValue("DisplayVersion", Version); uninstall.SetValue("Publisher", "BotPlus");
        uninstall.SetValue("InstallLocation", App); uninstall.SetValue("DisplayIcon", setupCopy);
        uninstall.SetValue("UninstallString", '"' + setupCopy + "\" --uninstall");
        uninstall.SetValue("QuietUninstallString", '"' + setupCopy + "\" --uninstall --silent");
        uninstall.SetValue("NoModify", 1); uninstall.SetValue("NoRepair", 1);
        var state = new JsonObject { ["version"] = Version, ["release"] = Release, ["installedUtc"] = DateTimeOffset.UtcNow.ToString("O") };
        File.WriteAllText(Path.Combine(App, "installation.json"), state.ToJsonString(), Utf8);
        Step(100, "Установка завершена. Подключение AI-клиента и автоподтверждение выбираются на ленте Revit.");
    }

    public static void Uninstall(IProgress<SetupProgress>? progress = null)
    {
        Log("Начало удаления BotPlus RT"); progress?.Report(new(0, "Проверка открытых приложений"));
        CheckRevitClosed();
        if (!File.Exists(Path.Combine(App, "installation.json"))) throw new InvalidOperationException("Не найдена установка WEAM.Revit.AI, управляемая установщиком.");
        foreach (var process in Process.GetProcessesByName("node"))
        {
            try
            {
                if (process.MainModule?.FileName?.StartsWith(App + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true)
                    throw new InvalidOperationException("Закройте AI-клиент, использующий MCP WEAM, перед удалением.");
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        var state = JsonNode.Parse(File.ReadAllText(Path.Combine(App, "installation.json")))!;
        var release = state["release"]!.GetValue<string>(); RequireChild(release, App);
        progress?.Report(new(20, "Удаление MCP-подключений")); Log("Удаление MCP-подключений");
        RunNode(release, "--remove", "--codex", "--claude");
        if (File.Exists(Manifest))
        {
            var xml = System.Xml.Linq.XDocument.Load(Manifest);
            var owns = xml.Descendants("AddIn").Any(a => string.Equals(a.Element("AddInId")?.Value, AddinId, StringComparison.OrdinalIgnoreCase)
                && a.Element("Assembly")?.Value.StartsWith(App + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true);
            if (owns) { Backup(Manifest); File.Delete(Manifest); }
        }
        RestoreLegacyManifests();
        progress?.Report(new(55, "Архивирование сеансов Ac2Revit")); Log("Архивирование сеансов Ac2Revit");
        PreserveSessions();
        Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, false);
        RequireChild(App, Data);
        Directory.Delete(App, true);
        Log("Uninstalled; settings, snapshots, models and configuration backups preserved.");
        progress?.Report(new(100, "Надстройка удалена, данные сохранены"));
    }

    static void RunNode(string release, params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(release, "node", "node.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        info.ArgumentList.Add(Path.Combine(release, "configure-mcp.mjs"));
        info.ArgumentList.Add("--backup-dir=" + Path.Combine(Data, "backups"));
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(); throw new TimeoutException("Не завершена настройка MCP."); }
        if (process.ExitCode != 0) throw new InvalidOperationException("Не удалось настроить MCP: " + stderr.GetAwaiter().GetResult());
        Log(stdout.GetAwaiter().GetResult().Trim());
    }

    static void RequireChild(string path, string root)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь выходит за пределы каталога приложения.");
    }

    static void Backup(string file)
    {
        if (!File.Exists(file)) return;
        var dir = Path.Combine(Data, "backups"); Directory.CreateDirectory(dir);
        File.Copy(file, Path.Combine(dir, Path.GetFileName(file) + "." + Guid.NewGuid().ToString("N") + ".bak"));
    }

    static void AtomicWithBackup(string file, string text)
    {
        if (File.Exists(file) && File.ReadAllText(file) == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); Backup(file);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, text, Utf8); File.Move(temp, file, true);
    }
}
