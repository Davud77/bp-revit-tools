using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System.Net.Http;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BotPlus.RT;

internal static class UpdateSession
{
    internal static Version Current { get; } = Normalize(typeof(Application).Assembly.GetName().Version!);
    internal static ReleaseInfo? Latest { get; set; }
    internal static DateTime? CheckedAt { get; set; }
    internal static string? Error { get; set; }
    internal static string LogPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BotPlus.RT", "Logs", "updates-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
    internal static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
    internal static string Display(Version v) => v.Build > 0 ? v.ToString(3) : v.ToString(2);
    internal static string Describe(Exception error) => error switch
    {
        OperationCanceledException => "Проверка прервана или превышено время ожидания.",
        HttpRequestException { StatusCode: { } code } => $"Сервер обновлений недоступен (HTTP {(int)code}). Повторите попытку позже.",
        HttpRequestException => "Нет соединения с GitHub. Проверьте подключение к сети и повторите попытку.",
        _ => error.Message
    };
    internal static string Log(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        try { lock (LogPath) { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); File.AppendAllText(level == "DETAIL" ? Path.ChangeExtension(LogPath, ".details.log") : LogPath, line + Environment.NewLine); } }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        return line;
    }
    internal static async Task<ReleaseInfo> Check(HttpClient http, CancellationToken token)
    {
        Log("INFO", $"Проверка GitHub / {ReleaseClient.Repository}. Установлена {Display(Current)}.");
        try
        {
            var release = await new ReleaseClient(http).Latest(token);
            Latest = release; CheckedAt = DateTime.Now; Error = null;
            Log("INFO", $"Последняя версия {Display(release.Version)}. " +
                (release.Version > Current ? "Доступно обновление." : release.Version == Current ? "Установлена актуальная версия." : "Установленная версия новее опубликованной."));
            return release;
        }
        catch (Exception error)
        {
            Error = Describe(error);
            Log("ERROR", Error); Log("DETAIL", error.ToString()); throw;
        }
    }
}

/// <summary>Checks once after startup without blocking Revit or displaying network errors.</summary>
internal static class StartupUpdates
{
    static CancellationTokenSource? cancellation;
    static UIControlledApplication? host;
    internal static void Attach(UIControlledApplication app)
    { host = app; cancellation = new(); app.Idling += OnIdle; }
    internal static void Detach(UIControlledApplication app)
    { app.Idling -= OnIdle; cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; host = null; }
    static async void OnIdle(object? sender, IdlingEventArgs args)
    {
        var app = host;
        if (app == null || cancellation == null) return;
        app.Idling -= OnIdle;
        var token = cancellation.Token;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var owner = (sender as UIApplication)?.MainWindowHandle ?? IntPtr.Zero;
        var button = app.GetRibbonPanels("BotPlus RT").SelectMany(p => p.GetItems()).OfType<PushButton>().FirstOrDefault(b => b.Name == "BotPlusRTUpdates");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var release = await UpdateSession.Check(http, token);
            await dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                if (button != null) button.ToolTip = $"Установлена: {UpdateSession.Display(UpdateSession.Current)}. На GitHub: {UpdateSession.Display(release.Version)}.";
                if (release.Version <= UpdateSession.Current || UpdateWindow.OpenWindow != null) return;
                var window = new UpdateWindow { ShowActivated = false };
                if (owner != IntPtr.Zero) new WindowInteropHelper(window) { Owner = owner };
                window.Show();
            });
        }
        catch (Exception) { /* Check records errors in the persistent log. */ }
    }
}
