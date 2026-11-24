using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BotPlus.Eom.Core;

internal sealed record IndexedFamily(string File, string Name, int Category, string CategoryName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? Preview, long Length, DateTime Modified)
{
    public long PreviewOffset { get; init; }
    public int PreviewSize { get; init; }
    internal bool HasPreview => Preview != null || PreviewSize > 0;
}

internal sealed record EquipmentLibrary(string Folder, IndexedFamily[] Entries)
{
    internal EquipmentLibraryIndex? Index { get; init; }
    static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotPlus.RT", "eom");
    static readonly Regex BackupName = new(@"\.\d{4}\.rfa$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly byte[] PngHeader = [137, 80, 78, 71, 13, 10, 26, 10];

    internal sealed record Scan(EquipmentLibrary Library, int Skipped);

    internal static EquipmentLibrary Load()
    {
        // Старый индекс содержал только пути. Папка сохранена, метаданные заполняет новая индексация.
        var value = Read<EquipmentLibrary>("equipment-library.json");
        return new(value?.Folder ?? "", value?.Entries ?? []);
    }

    internal static int[]? LoadCategories() => Read<int[]>("equipment-categories.json");
    internal static void SaveCategories(int[] categories) => Write("equipment-categories.json", categories);
    internal static string? LoadFolder() => Read<string>("equipment-library-folder.json");
    internal static void SaveFolder(string folder) => Write("equipment-library-folder.json", folder);

    [SupportedOSPlatform("windows")]
    internal static Scan ReadFolder(string folder, EquipmentLibrary previous, IReadOnlyDictionary<string, int> categories,
        Action<int, int> progress, CancellationToken cancellation)
    {
        var basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive
        };
        var files = Directory.EnumerateFiles(basePath, "*.rfa", options).Where(path => !BackupName.IsMatch(path)).ToArray();
        return ReadFiles(basePath, files, previous, categories, progress, cancellation);
    }

    [SupportedOSPlatform("windows")]
    internal static Scan ReadFiles(string basePath, string[] files, EquipmentLibrary previous,
        IReadOnlyDictionary<string, int> categories, Action<int, int> progress, CancellationToken cancellation)
    {
        Array.Sort(files, StringComparer.Ordinal);
        var cached = new Dictionary<string, IndexedFamily?>(StringComparer.OrdinalIgnoreCase);
        if (StringComparer.OrdinalIgnoreCase.Equals(basePath, previous.Folder))
        {
            foreach (var entry in previous.Entries)
            {
                var key = IndexKey(entry.File);
                // Для неоднозначного пути перечитываем сам RFA вместо выбора произвольной записи.
                if (!cached.TryAdd(key, entry)) cached[key] = null;
            }
        }
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexed = new List<IndexedFamily>();
        int skipped = 0;
        for (int i = 0; i < files.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(files[i]);
                string relativePath = Path.GetRelativePath(basePath, info.FullName);
                string key = IndexKey(relativePath);
                if (accepted.Contains(key)) skipped++;
                else
                {
                    var entry = cached.TryGetValue(key, out var old) && old != null && old.File == relativePath &&
                        old.Length == info.Length && old.Modified == info.LastWriteTimeUtc
                        ? old : ReadFamily(info, relativePath, categories);
                    if (entry == null) skipped++;
                    else
                    {
                        accepted.Add(key);
                        indexed.Add(entry);
                    }
                }
            }
            catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException
                or XmlException or ArgumentException or NotSupportedException)
            {
                skipped++;
            }
            progress(i + 1, files.Length);
        }
        return new Scan(new(basePath, indexed.ToArray()), skipped);
    }

    internal static string IndexKey(string path) => path.Replace('/', '\\');

    [SupportedOSPlatform("windows")]
    static IndexedFamily? ReadFamily(FileInfo info, string relativePath, IReadOnlyDictionary<string, int> categories)
    {
        using var storage = new FamilyFileStreams(info.FullName);
        if (storage.Read("PartAtom", 16 * 1024 * 1024) is not { } rawMetadata) return null;
        using var metadata = new MemoryStream(rawMetadata);
        using var reader = XmlReader.Create(metadata, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var entry = XElement.Load(reader);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        string? category = entry.Elements(atom + "category")
            .FirstOrDefault(element => (string?)element.Element(atom + "scheme") == "adsk:revit:grouping")
            ?.Element(atom + "term")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(category) || !categories.TryGetValue(category, out int categoryId)) return null;

        string? title = entry.Element(atom + "title")?.Value.Trim();
        string name = string.IsNullOrWhiteSpace(title) || Guid.TryParse(title, out _)
            ? Path.GetFileNameWithoutExtension(info.Name) : title;
        var rawImage = storage.Read("RevitPreview4.0", 4 * 1024 * 1024) ?? storage.Read("RevitPreview", 4 * 1024 * 1024);
        int pngOffset = rawImage == null ? -1 : rawImage.AsSpan().IndexOf(PngHeader);
        var preview = pngOffset < 0 ? null : rawImage![pngOffset..];
        return new(relativePath, name, categoryId, category, preview, info.Length, info.LastWriteTimeUtc);
    }

    static T? Read<T>(string name)
    {
        try
        {
            var path = Path.Combine(SettingsFolder, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.WriteLine(error);
            return default;
        }
    }

    static void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(SettingsFolder);
        string path = Path.Combine(SettingsFolder, name);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, value);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
