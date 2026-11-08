using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HeatLossViewer.Export;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HeatLossViewer;

public sealed class NativeRadiatorPane : Grid, IDisposable
{
    private readonly WebView2 browser = new();
    private readonly TextBlock status = new() { Text = "Подготовка подбора радиаторов…", Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap };
    private readonly BuildingData building;
    private readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotPlus.RT", "ov", "sessions", Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? load;
    private Uri? page;
    public NativeRadiatorPane(BuildingData building)
    {
        this.building = building; Children.Add(browser); Children.Add(status);
        Loaded += (_, _) => { load ??= InitialiseAsync(); };
    }
    private async Task InitialiseAsync()
    {
        try
        {
            Directory.CreateDirectory(directory); building.NativeRadiators = true;
            string html = HtmlTemplate.GetHtml(JsonSerializer.Serialize(building, BuildingExportWriter.JsonOptions));
            string path = Path.Combine(directory, "radiators.html"); File.WriteAllText(path, html); page = new Uri(path);
            string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotPlus.RT", "ov", "webview");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await browser.EnsureCoreWebView2Async(environment);
            browser.CoreWebView2.NavigationStarting += (_, args) => { if (!string.Equals(args.Uri, page.AbsoluteUri, StringComparison.OrdinalIgnoreCase)) args.Cancel = true; };
            browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            };
            browser.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                if (!string.Equals(args.Source, page.AbsoluteUri, StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    using var json = JsonDocument.Parse(args.WebMessageAsJson);
                    string? type = json.RootElement.GetProperty("type").GetString();
                    if (type == "ready") { status.Visibility = Visibility.Collapsed; ready.TrySetResult(); }
                    else if (type == "export-file")
                    {
                        string name = Path.GetFileName(json.RootElement.GetProperty("name").GetString() ?? "export.json");
                        string text = json.RootElement.GetProperty("text").GetString() ?? "";
                        if (text.Length > 20_000_000 || !(name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Недопустимый файл экспорта.");
                        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = name, Filter = name.EndsWith(".csv") ? "Таблица CSV|*.csv" : "Данные JSON|*.json" };
                        if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, text, new System.Text.UTF8Encoding(name.EndsWith(".csv")));
                    }
                }
                catch (Exception ex) { System.Windows.MessageBox.Show("Радиаторы: " + ex.Message); }
            };
            browser.CoreWebView2.NavigationCompleted += (_, args) => { if (!args.IsSuccess) ready.TrySetException(new InvalidOperationException("Не удалось открыть вкладку: " + args.WebErrorStatus)); };
            // Страница подбора следует теме плагина: тема читается из localStorage до отрисовки.
            bool dark = BotPlus.Shared.BotPlusTheme.DetectDark();
            browser.CoreWebView2.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("try{localStorage.setItem('heatloss-theme','" + (dark ? "dark" : "light") + "')}catch(e){}");
            browser.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(13, 13, 13) : System.Drawing.Color.White;
            browser.Source = page;
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(45));
        }
        catch (Exception ex) { status.Text = "Не удалось открыть радиаторы: " + ex.Message; throw; }
    }
    public async Task UpdateLibraryAsync(OvLibraryData library)
    {
        building.OvLibrary = library;
        if (load is null) return;
        await load;
        await browser.ExecuteScriptAsync("globalThis.botplusUpdateLibrary(" + JsonSerializer.Serialize(library, BuildingExportWriter.JsonOptions) + ")");
    }
    public async Task<OvRadiatorState> CollectAsync()
    {
        if (load is null) return building.OvLibrary?.Radiators ?? new();
        await load;
        string raw = await browser.ExecuteScriptAsync("(()=>{try{return {ok:true,value:globalThis.botplusRadiatorSnapshot()}}catch(e){return {ok:false,error:e.message}}})()");
        using var reply = JsonDocument.Parse(raw);
        if (!reply.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) throw new ArgumentException(reply.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "Вкладка радиаторов ещё не готова.");
        var state = reply.RootElement.GetProperty("value").Deserialize<OvRadiatorState>(BuildingExportWriter.JsonOptions) ?? throw new InvalidDataException("Нет настроек радиаторов.");
        state.Validate(); return state;
    }
    public void Dispose()
    {
        browser.Dispose();
        try { string file = Path.Combine(directory, "radiators.html"); if (File.Exists(file)) File.Delete(file); if (Directory.Exists(directory)) Directory.Delete(directory); }
        catch (IOException) { }
    }
}
