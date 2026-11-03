using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace BotPlus.RT.Setup;

static partial class Deployment
{
    public static readonly int[] TapirVersions = [25, 26, 27, 28, 29, 30];
    public static string? FindArchicad(int version)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Graphisoft", "Archicad " + version);
        return File.Exists(Path.Combine(path, "Archicad.exe")) ? path : null;
    }
    public static string TapirTarget(int version, string? directory)
    {
        if (!TapirVersions.Contains(version)) throw new ArgumentException("Неподдерживаемая версия Archicad.");
        directory = Path.GetFullPath(directory ?? FindArchicad(version) ?? throw new InvalidOperationException($"Не найден Archicad {version}. Укажите его папку или выберите «Не устанавливать Tapir»."));
        string exe = Path.Combine(directory, "Archicad.exe");
        if (!File.Exists(exe) || FileVersionInfo.GetVersionInfo(exe).FileMajorPart != version)
            throw new InvalidOperationException($"Выберите папку Archicad {version}, содержащую Archicad.exe этой версии.");
        foreach (var process in Process.GetProcessesByName("Archicad"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Сохраните проект и закройте Archicad {version} перед установкой Tapir.");
            }
            catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Закройте Archicad перед установкой Tapir: не удалось проверить его версию."); }
        }
        string[] names = ["Add-Ons", "Расширения Archicad", "Extensões", "Doplnky ArchiCADu", "Dodatki", "Extensiones ArchiCAD", "Add-On'lar"];
        var addons = Directory.GetDirectories(directory).FirstOrDefault(p => names.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Не найдена папка расширений Archicad: " + directory);
        return Path.Combine(addons, "Tapir", $"TapirAddOn_AC{version}_Win.apx");
    }
    public static void InstallTapir(int version, string? directory, bool allowElevation = true)
    {
        string target = TapirTarget(version, directory);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WEAM.Payload.zip")!;
        using var archive = new ZipArchive(stream);
        using var manifest = JsonDocument.Parse(archive.GetEntry("tapir/releases.json")!.Open());
        var asset = manifest.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("version").GetInt32() == version);
        using var input = archive.GetEntry("tapir/" + asset.GetProperty("file").GetString())!.Open();
        using var memory = new MemoryStream(); input.CopyTo(memory); byte[] bytes = memory.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(asset.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Не совпадает SHA-256 Tapir.");
        if (File.Exists(target) && SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(SHA256.HashData(bytes)))
        { Log($"Tapir для Archicad {version} уже установлен: {target}"); return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = target + ".botplus-new";
            try { File.WriteAllBytes(temp, bytes); File.Move(temp, target, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            Log($"Tapir {manifest.RootElement.GetProperty("release").GetString()} установлен для Archicad {version}: {target}");
        }
        catch (UnauthorizedAccessException) when (allowElevation)
        {
            Log($"Для установки Tapir в Archicad {version} Windows запросит права администратора.");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--silent"); start.ArgumentList.Add("--tapir-worker=" + version); start.ArgumentList.Add("--archicad=" + (directory ?? FindArchicad(version)));
            using var helper = Process.Start(start) ?? throw new InvalidOperationException("Не запущена установка Tapir.");
            helper.WaitForExit();
            if (helper.ExitCode != 0 || !File.Exists(target) || !SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(SHA256.HashData(bytes)))
                throw new InvalidOperationException("Tapir не установлен. Проверьте журнал и права доступа к папке Archicad.");
        }
    }
}
