using System.Diagnostics;
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

    internal async void Start(string folder, EquipmentLibrary previous, IReadOnlyDictionary<string, int> categories)
    {
        if (IsRunning || disposed) return;
        var token = shutdown.Token;
        IsRunning = true;
        Folder = folder;
        Report("Поиск RFA в папке и подпапках…");
        try
        {
            // Чтение метаданных с диска позволяет индексировать RFA любой читаемой версии без открытия в Revit.
            var result = await Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                var scan = EquipmentLibrary.ReadFolder(folder, previous, categories, (done, total) =>
                {
                    if (clock.ElapsedMilliseconds < 200) return;
                    clock.Restart();
                    dispatcher.BeginInvoke(() =>
                    {
                        if (!disposed) Report($"Чтение RFA… {done} из {total}");
                    });
                }, token);
                token.ThrowIfCancellationRequested();
                scan.Library.Save();
                return scan;
            }, token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (disposed) return;
                Completed?.Invoke(result.Library);
                string message = $"Семейств в индексе: {result.Library.Entries.Length}.";
                if (result.Skipped > 0) message += $" Пропущено файлов: {result.Skipped}.";
                Finish(message);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
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
