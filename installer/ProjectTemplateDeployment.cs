using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BotPlus.RT.Setup;

static partial class Deployment
{
    const string ProjectTemplatesFolder = "project-templates";

    sealed record ProjectTemplate(string Name, string File);

    static ProjectTemplate[] ReadProjectTemplates(string release, bool requireFiles = true)
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
            if (requireFiles && !File.Exists(file))
                throw new FileNotFoundException("В поставке нет шаблона проекта " + template.Name, file);
        }
        return templates;
    }

    static void ConfigureProjectTemplates(string release, bool install)
    {
        var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Autodesk Revit 2026", "Revit.ini");
        var content = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Autodesk", "RVT 2026");
        UpdateProjectTemplates(ini, Path.Combine(content, "UserDataCache", "Revit.ini"),
            Path.Combine(App, "versions"), release, Path.Combine(content, "Templates", "Russian"), install,
            Path.Combine(Data, "Ac2Revit", "settings.json"));
    }

    internal static void UpdateProjectTemplates(string ini, string defaults, string versions, string release,
        string destination, bool install, string? ac2RevitSettings = null)
    {
        if (!install && !File.Exists(ini)) return;
        var templates = install || File.Exists(Path.Combine(release, ProjectTemplatesFolder, "templates.json"))
            ? ReadProjectTemplates(release, requireFiles: install) : [];
        var fileNames = templates.Select(t => t.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var templateNames = templates.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (install && destination.IndexOfAny([',', '=']) >= 0)
            throw new InvalidOperationException("Путь к шаблонам Revit содержит запятую или знак равенства: " + destination);

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
                .Where(item => !string.IsNullOrWhiteSpace(item) &&
                    !IsSupersededTemplate(item, ini, versions, destination, fileNames, install)).ToList();
            if (install)
                AddTemplateEntries(items, templates, destination, templateNames);
            return setting.Groups["key"].Value + string.Join(", ", items);
        }));
        if (install && !found)
            throw new InvalidDataException("В Revit.ini не найден список шаблонов проекта.");
        if (install)
            CopyProjectTemplates(release, destination, templates);
        if (updated != original)
            AtomicWithBackup(ini, updated, encoding);
        if (install && ac2RevitSettings is not null)
            MigrateAc2RevitTemplate(ac2RevitSettings, versions, destination, fileNames);
    }

    static string TemplateName(string entry) => entry.Split('=', 2)[0].Trim();

    static void AddTemplateEntries(List<string> items, ProjectTemplate[] templates, string destination,
        HashSet<string> templateNames)
    {
        var names = items.Select(TemplateName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Count; i++)
        {
            string name = TemplateName(items[i]);
            int separator = items[i].IndexOf('=');
            if (separator < 0 || !templateNames.Contains(name)) continue;
            string label = $"{name} (пользовательский)";
            for (int suffix = 2; !names.Add(label); suffix++)
                label = $"{name} (пользовательский {suffix})";
            items[i] = label + items[i][separator..];
        }
        foreach (var template in templates)
            items.Add(template.Name + "=" + Path.Combine(destination, template.File));
    }

    static bool IsSupersededTemplate(string entry, string ini, string versions, string destination,
        HashSet<string> fileNames, bool migrate)
    {
        int separator = entry.IndexOf('=');
        if (separator < 0) return false;
        var file = entry[(separator + 1)..].Trim();
        if (!file.EndsWith(".rte", StringComparison.OrdinalIgnoreCase)) return false;
        var fullPath = Path.GetFullPath(file, Path.GetDirectoryName(ini)!);
        var folder = Path.GetDirectoryName(fullPath)!;
        var fileName = Path.GetFileName(fullPath);
        if (fileNames.Contains(fileName) &&
            (string.Equals(folder, destination, StringComparison.OrdinalIgnoreCase) ||
             (migrate && IsInputTemplatesFolder(folder))))
            return true;
        return IsVersionTemplate(versions, fullPath);
    }

    static bool IsInputTemplatesFolder(string folder) =>
        folder.EndsWith(Path.DirectorySeparatorChar + Path.Combine("Входные данные", "Шаблоны"),
            StringComparison.OrdinalIgnoreCase);

    static bool IsVersionTemplate(string versions, string file)
    {
        var relativePath = Path.GetRelativePath(versions, file);
        if (Path.IsPathRooted(relativePath)) return false;
        var relative = relativePath.Split(Path.DirectorySeparatorChar);
        return relative.Length == 3 && relative[0] != ".." &&
            string.Equals(relative[1], ProjectTemplatesFolder, StringComparison.OrdinalIgnoreCase);
    }

    static void MigrateAc2RevitTemplate(string settingsFile, string versions, string destination, HashSet<string> fileNames)
    {
        if (!File.Exists(settingsFile)) return;
        JsonObject? settings;
        try { settings = JsonNode.Parse(File.ReadAllText(settingsFile)) as JsonObject; }
        catch (JsonException)
        {
            Log("Не удалось прочитать сохранённый путь шаблона Ac2Revit в settings.json.");
            return;
        }
        if (settings?["projectTemplate"] is not JsonValue value || !value.TryGetValue<string>(out var selected) ||
            !Path.IsPathFullyQualified(selected))
            return;
        var fileName = Path.GetFileName(selected);
        if (!fileNames.Contains(fileName)) return;
        var folder = Path.GetDirectoryName(selected)!;
        bool missingInput = !File.Exists(selected) && (IsInputTemplatesFolder(folder) ||
            string.Equals(Path.GetFileName(folder), "Входные данные", StringComparison.OrdinalIgnoreCase));
        if (!missingInput && !IsVersionTemplate(versions, selected)) return;

        settings["projectTemplate"] = Path.Combine(destination, fileName);
        AtomicWithBackup(settingsFile, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    static void CopyProjectTemplates(string release, string destination, ProjectTemplate[] templates)
    {
        try
        {
            Directory.CreateDirectory(destination);
            foreach (var template in templates)
            {
                var source = Path.Combine(release, ProjectTemplatesFolder, template.File);
                var target = Path.Combine(destination, template.File);
                if (VerifyExistingTemplate(source, target)) continue;

                var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(source, temp);
                    try { File.Move(temp, target); }
                    catch (IOException) when (VerifyExistingTemplate(source, target)) { }
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Нет доступа для записи шаблонов Revit в " + destination +
                ". Запустите установщик от имени администратора.", ex);
        }
    }

    static bool VerifyExistingTemplate(string source, string target)
    {
        if (!File.Exists(target)) return false;
        using var installed = File.OpenRead(target);
        using var packaged = File.OpenRead(source);
        if (installed.Length != packaged.Length || !SHA256.HashData(installed).SequenceEqual(SHA256.HashData(packaged)))
            throw new IOException("В папке шаблонов Revit уже есть файл с таким именем и другим содержимым: " + target);
        return true;
    }
}
