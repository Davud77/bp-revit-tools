using System.Diagnostics;

namespace BotPlus.RT.Setup;

/// <summary>Найденная установка Archicad, для которой есть сборка Tapir.</summary>
sealed record ArchicadInstall(int Version, string Directory, bool Running);

static partial class Deployment
{
    /// <summary>
    /// Ищет Archicad: сначала запущенные процессы (самый надёжный признак рабочей версии), затем стандартные папки
    /// Graphisoft на всех фиксированных дисках. Версия берётся из Archicad.exe, а не из имени папки.
    /// </summary>
    public static IReadOnlyList<ArchicadInstall> DetectArchicad()
    {
        var found = new Dictionary<int, ArchicadInstall>();
        void Add(string? exe, bool running)
        {
            try
            {
                if (exe is null || !File.Exists(exe)) return;
                int major = FileVersionInfo.GetVersionInfo(exe).FileMajorPart;
                if (!TapirVersions.Contains(major)) return;
                var directory = Path.GetDirectoryName(exe)!;
                if (!found.TryGetValue(major, out var known) || running && !known.Running) found[major] = new(major, directory, running);
            }
            catch (Exception) { }
        }
        foreach (var process in Process.GetProcessesByName("Archicad"))
        {
            try { Add(process.MainModule?.FileName, true); } catch (Exception) { }
            finally { process.Dispose(); }
        }
        var roots = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files"));
        foreach (var root in roots.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (int version in TapirVersions)
                Add(Path.Combine(root, "Graphisoft", "Archicad " + version, "Archicad.exe"), false);
        return found.Values.OrderByDescending(a => a.Running).ThenByDescending(a => a.Version).ToList();
    }
}
