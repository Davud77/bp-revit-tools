using System.Text.Json;

namespace BotPlus.Eom.Core;

/// <summary>Один переносимый файл: заголовок, JSON с относительными путями, затем блоки PNG.</summary>
internal sealed record EquipmentLibraryIndex(string Path, Guid Revision, long PreviewsStart)
{
    internal const string FileName = "BotPlus.UGO.index";
    const long Signature = 0x315844494F475542; // BUGOIDX1
    static readonly char[] InvalidNameChars = System.IO.Path.GetInvalidFileNameChars();
    const int MaxMetadata = 64 * 1024 * 1024;
    const int MaxPreview = 4 * 1024 * 1024;

    internal static EquipmentLibrary? Open(string folder, EquipmentLibrary? current = null)
    {
        folder = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        string path = System.IO.Path.Combine(folder, FileName);
        FileStream stream;
        try { stream = OpenRead(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (stream)
        using (var reader = new BinaryReader(stream))
        {
            Guid revision = ReadRevision(reader);
            if (StringComparer.OrdinalIgnoreCase.Equals(current?.Index?.Path, path) && current?.Index?.Revision == revision)
                return current;
            int bytes = reader.ReadInt32();
            if (bytes <= 0 || bytes > MaxMetadata || bytes > stream.Length - stream.Position)
                throw new InvalidDataException("Повреждён заголовок индекса Базы УГО.");
            var entries = JsonSerializer.Deserialize<IndexedFamily[]>(reader.ReadBytes(bytes))
                ?? throw new InvalidDataException("Пустой индекс Базы УГО.");
            long previewBytes = stream.Length - stream.Position;
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.File) || string.IsNullOrWhiteSpace(entry.Name) ||
                    string.IsNullOrWhiteSpace(entry.CategoryName) || entry.Category == 0 ||
                    System.IO.Path.IsPathRooted(entry.File) ||
                    entry.File.Split('\\', '/').Any(part => part.Length == 0 || part[^1] is '.' or ' ' || part.IndexOfAny(InvalidNameChars) >= 0) ||
                    !string.Equals(System.IO.Path.GetExtension(entry.File), ".rfa", StringComparison.OrdinalIgnoreCase) ||
                    !unique.Add(EquipmentLibrary.IndexKey(entry.File)) || entry.Preview != null ||
                    entry.PreviewSize < 0 || entry.PreviewSize > MaxPreview || entry.PreviewOffset < 0 ||
                    entry.PreviewOffset > previewBytes - entry.PreviewSize)
                    throw new InvalidDataException("Недопустимая запись в индексе Базы УГО.");
            }
            return new(folder, entries) { Index = new(path, revision, stream.Position) };
        }
    }

    internal static EquipmentLibrary Publish(EquipmentLibrary library, EquipmentLibrary? previous, CancellationToken cancellation)
    {
        string path = System.IO.Path.Combine(library.Folder, FileName);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Guid revision = Guid.NewGuid();
        long offset = 0;
        var entries = library.Entries.Select(entry =>
        {
            int size = entry.Preview?.Length ?? entry.PreviewSize;
            var stored = entry with { Preview = null, PreviewOffset = size == 0 ? 0 : offset, PreviewSize = size };
            offset += size;
            return stored;
        }).ToArray();
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(entries);
        if (metadata.Length > MaxMetadata) throw new InvalidDataException("Индекс Базы УГО превышает доступный размер.");

        long previewsStart;
        try
        {
            using (var output = new BinaryWriter(File.Create(temp)))
            using (var cached = library.Entries.Any(entry => entry.Preview == null && entry.PreviewSize > 0)
                ? previous?.Index?.OpenPreviews() : null)
            {
                output.Write(Signature);
                output.Write(revision.ToByteArray());
                output.Write(metadata.Length);
                output.Write(metadata);
                previewsStart = output.BaseStream.Position;
                foreach (var entry in library.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (entry.HasPreview)
                        output.Write(entry.Preview ?? previous!.Index!.ReadPreview(cached!, entry));
                }
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
        return new(library.Folder, entries) { Index = new(path, revision, previewsStart) };
    }

    internal FileStream OpenPreviews()
    {
        var stream = OpenRead(Path);
        try
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (ReadRevision(reader) != Revision) throw new IOException("Индекс обновился. Перечитайте библиотеку.");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal byte[] ReadPreview(Stream stream, IndexedFamily entry)
    {
        stream.Position = PreviewsStart + entry.PreviewOffset;
        var bytes = new byte[entry.PreviewSize];
        stream.ReadExactly(bytes);
        return bytes;
    }

    static Guid ReadRevision(BinaryReader reader)
    {
        if (reader.ReadInt64() != Signature) throw new InvalidDataException("Неподдерживаемый формат индекса Базы УГО.");
        byte[] revision = reader.ReadBytes(16);
        if (revision.Length != 16) throw new EndOfStreamException("Неполный заголовок индекса Базы УГО.");
        return new Guid(revision);
    }

    static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
