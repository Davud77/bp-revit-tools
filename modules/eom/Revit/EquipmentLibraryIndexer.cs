using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;

namespace BotPlus.Eom;

internal sealed class EquipmentLibraryIndexer(Dispatcher dispatcher) : IDisposable
{
    readonly CancellationTokenSource shutdown = new();
    bool disposed;
    internal bool IsRunning { get; private set; }
    internal string Status { get; private set; } = "";
    internal string? Folder { get; private set; }
    internal event Action? Changed;
    internal event Action<EquipmentLibrary>? Completed;

    internal void Open(EquipmentLibrary current, string? folder = null) =>
        Run(folder, "Чтение готового индекса…", token =>
        {
            string? savedFolder = EquipmentLibrary.LoadFolder();
            string? path = folder ?? savedFolder;
            var previous = current;
            if (path == null)
            {
                if (previous.Folder.Length == 0) previous = EquipmentLibrary.Load();
                path = previous.Folder;
                if (path.Length == 0) return (null, "");
            }
            token.ThrowIfCancellationRequested();
            var library = EquipmentLibraryIndex.Open(path, previous);
            if (library != null)
            {
                if (folder != null || savedFolder == null) EquipmentLibrary.SaveFolder(library.Folder);
                return (library, $"Семейств в индексе: {library.Entries.Length}.");
            }
            if (folder == null && previous.Index == null &&
                StringComparer.OrdinalIgnoreCase.Equals(previous.Folder, path))
                return (previous, $"Семейств в локальном индексе: {previous.Entries.Length}. Для общего доступа запустите индексацию.");
            return (null, $"В папке нет файла {EquipmentLibraryIndex.FileName}. Создайте индекс или скопируйте готовый.");
        }, current);

    internal void Start(string folder, EquipmentLibrary previous, IReadOnlyDictionary<string, int> categories) =>
        Run(folder, "Поиск RFA в папке и подпапках…", token =>
        {
            EquipmentLibrary? available;
            try { available = EquipmentLibraryIndex.Open(folder, previous); }
            catch (Exception error) when (error is InvalidDataException or EndOfStreamException or JsonException)
            {
                available = null; // Явная индексация может восстановить повреждённый общий индекс.
            }
            available ??= previous.Index == null ? previous : new EquipmentLibrary("", []);
            var clock = Stopwatch.StartNew();
            var scan = EquipmentLibrary.ReadFolder(folder, available, categories, (done, total) =>
            {
                if (clock.ElapsedMilliseconds < 200) return;
                clock.Restart();
                dispatcher.BeginInvoke(() =>
                {
                    if (!disposed) Report($"Чтение RFA… {done} из {total}");
                });
            }, token);
            var library = EquipmentLibraryIndex.Publish(scan.Library, available, token);
            EquipmentLibrary.SaveFolder(library.Folder);
            string message = $"Семейств в индексе: {library.Entries.Length}.";
            if (scan.Skipped > 0) message += $" Пропущено файлов: {scan.Skipped}.";
            return (library, message);
        }, previous);

    async void Run(string? folder, string status, Func<CancellationToken, (EquipmentLibrary? Library, string Message)> work,
        EquipmentLibrary current)
    {
        if (IsRunning || disposed) return;
        var token = shutdown.Token;
        IsRunning = true;
        Folder = folder;
        Report(status);
        try
        {
            var result = await Task.Run(() => work(token), token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (disposed) return;
                if (result.Library is { } library && !ReferenceEquals(library, current)) Completed?.Invoke(library);
                Finish(result.Message);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException
            or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            await dispatcher.InvokeAsync(() => Finish($"Ошибка индексации. {error.Message}"));
        }
    }

    void Finish(string status)
    {
        if (disposed) return;
        IsRunning = false;
        Report(status);
    }

    void Report(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        disposed = true;
        shutdown.Cancel();
        shutdown.Dispose();
    }
}
