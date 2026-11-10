using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BotPlus.RT.Setup;

static partial class Deployment
{
    const string ProjectTemplatesFolder = "project-templates";

    sealed record ProjectTemplate(string Name, string File);

    static ProjectTemplate[] ReadProjectTemplates(string release)
    {
        var folder = Path.Combine(release, ProjectTemplatesFolder);
        var templates = JsonSerializer.Deserialize<ProjectTemplate[]>(File.ReadAllText(Path.Combine(folder, "templates.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (templates is not { Length: > 0 })
            throw new InvalidDataException("В поставке нет шаблонов проекта Revit.");
        foreach (var template in templates)
        {
            if (string.IsNullOrWhiteSpace(template.Name) || template.Name.IndexOfAny([',', '=', '\r', '\n']) >= 0 ||
                string.IsNullOrWhiteSpace(template.File) || template.File != Path.GetFileName(template.File) ||
                !template.File.EndsWith(".rte", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Некорректная запись в списке шаблонов проекта Revit.");
            var file = Path.Combine(folder, template.File);
            if (!File.Exists(file))
                throw new FileNotFoundException("В поставке нет шаблона проекта " + template.Name, file);
        }
        return templates;
    }

    static void ConfigureProjectTemplates(string? release)
    {
        var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Autodesk Revit 2026", "Revit.ini");
        var defaults = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Autodesk", "RVT 2026", "UserDataCache", "Revit.ini");
        UpdateProjectTemplates(ini, defaults, Path.Combine(App, "versions"), release);
    }

    internal static void UpdateProjectTemplates(string ini, string defaults, string versions, string? release)
    {
        if (release is null && !File.Exists(ini)) return;

        var entries = new List<(string Name, string File)>();
        if (release is not null)
        {
            foreach (var template in ReadProjectTemplates(release))
            {
                var file = Path.Combine(release, ProjectTemplatesFolder, template.File);
                if (file.IndexOfAny([',', '=']) >= 0)
                    throw new InvalidOperationException("Путь к шаблонам Revit содержит запятую или знак равенства: " + file);
                entries.Add((template.Name, file));
            }
        }

        string original;
        Encoding encoding;
        using (var reader = new StreamReader(File.Exists(ini) ? ini : defaults, new UTF8Encoding(false, true)))
        {
            original = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
        }
        var directories = new Regex(@"^\[Directories[A-Z]{3}\][^\S\r\n]*\r?\n(?:^(?!\[)[^\r\n]*\r?\n)*(?:^(?!\[)[^\r\n]+\z)?",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var templateSetting = new Regex(@"^(?<key>[ \t]*DefaultTemplate[ \t]*=)(?<value>[^\r\n]*)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool found = false;
        string updated = directories.Replace(original, section => templateSetting.Replace(section.Value, setting =>
        {
            found = true;
            var items = Regex.Split(setting.Groups["value"].Value, @",[ \t]*(?=[^,=]+=)")
                .Where(item => !string.IsNullOrWhiteSpace(item) && !IsInstalledTemplate(item, ini, versions)).ToList();
            var names = items.Select(TemplateName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, file) in entries)
            {
                var label = name;
                for (int suffix = 2; !names.Add(label); suffix++)
                    label = $"{name} ({suffix})";
                items.Add(label + "=" + file);
            }
            return setting.Groups["key"].Value + string.Join(", ", items);
        }));
        if (release is not null && !found)
            throw new InvalidDataException("В Revit.ini не найден список шаблонов проекта.");
        if (updated != original)
            AtomicWithBackup(ini, updated, encoding);
    }

    static string TemplateName(string entry) => entry.Split('=', 2)[0].Trim();

    static bool IsInstalledTemplate(string entry, string ini, string versions)
    {
        int separator = entry.IndexOf('=');
        if (separator < 0) return false;
        var file = entry[(separator + 1)..].Trim();
        if (!file.EndsWith(".rte", StringComparison.OrdinalIgnoreCase)) return false;
        var fullPath = Path.GetFullPath(file, Path.GetDirectoryName(ini)!);
        var relativePath = Path.GetRelativePath(versions, fullPath);
        if (Path.IsPathRooted(relativePath)) return false;
        var relative = relativePath.Split(Path.DirectorySeparatorChar);
        return relative.Length == 3 && relative[0] != ".." &&
            string.Equals(relative[1], ProjectTemplatesFolder, StringComparison.OrdinalIgnoreCase);
    }
}
