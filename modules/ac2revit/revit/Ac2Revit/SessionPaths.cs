namespace Ac2Revit;

/// <summary>
/// Session folder names that keep every generated file within MAX_PATH. Revit (SaveAs, LoadFamily) and the bundled
/// Python fail beyond 259 characters unless the machine enables Windows long paths, which is not a safe assumption.
/// </summary>
public static class SessionPaths
{
    /// <summary>Deepest path a session creates below its folder: prep-…\run-…\opening-families\AC2R_…rfa or a temporary JSON.</summary>
    public const int DepthReserve = 120;
    public const int MaxPath = 259;

    public static string NewFolder(string sessions, string projectName, DateTime now)
    {
        string folder = Path.Combine(sessions, FolderName(projectName, sessions.Length, now));
        string unique = folder;
        for (int attempt = 2; Directory.Exists(unique); attempt++) unique = folder + "-" + attempt;
        return unique;
    }

    /// <summary>File name of the imported model: «A2R_» + the name of the Archicad file; a timestamp is added when that name is taken.</summary>
    public static string OutputFileName(string projectName, DateTime now, Func<string, bool> taken)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new string((projectName ?? "").Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        foreach (string extension in new[] { ".pln", ".pla", ".bpn", ".tpl" })
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name = name[..^extension.Length];
        if (name.Length > 120) name = name[..120].TrimEnd(' ', '.');
        string plain = "A2R_" + (name.Length == 0 ? "Archicad" : name) + ".rvt";
        return taken(plain) ? "A2R_" + (name.Length == 0 ? "Archicad" : name) + "_" + now.ToString("yyyyMMdd-HHmmss") + ".rvt" : plain;
    }

    public static string FolderName(string projectName, int sessionsLength, DateTime now)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new string((projectName ?? "").Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.', '_');
        string stamp = now.ToString("yyyyMMdd-HHmmss");
        int budget = Math.Clamp(MaxPath - DepthReserve - sessionsLength - 1 - stamp.Length - 1, 8, 32);
        if (name.Length > budget) name = name[..budget].TrimEnd(' ', '.', '_');
        return (name.Length == 0 ? "Archicad" : name) + "-" + stamp;
    }
}
