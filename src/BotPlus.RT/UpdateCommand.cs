using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BotPlus.Shared;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TextBox = System.Windows.Controls.TextBox;
using Grid = System.Windows.Controls.Grid;
using Color = System.Windows.Media.Color;

namespace BotPlus.RT;

[Transaction(TransactionMode.ReadOnly)]
public sealed class UpdateCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        if (UpdateWindow.OpenWindow is { } open) { open.Activate(); return Result.Succeeded; }
        var window = new UpdateWindow();
        new WindowInteropHelper(window) { Owner = data.Application.MainWindowHandle };
        window.ShowDialog(); return Result.Succeeded;
    }
}

internal sealed class UpdateWindow : Window
{
    internal static UpdateWindow? OpenWindow { get; private set; }
    readonly TextBlock latest = new(), checkedAt = new(), status = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock stage = new() { Margin = new Thickness(0, 8, 0, 4) };
    readonly TextBox log = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 11,
        Padding = new Thickness(10) };
    readonly Button check = MakeButton("Проверить обновления"), download = MakeButton("Скачать обновление"), install = MakeButton("Запустить установщик");
    readonly ProgressBar progress = new() { Height = 6, Maximum = 100 };
    readonly CancellationTokenSource cancellation = new();
    readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    ReleaseInfo? release;
    string? file;
    bool busy, closed;

    static Button MakeButton(string text) => new() { Content = text, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
    public UpdateWindow()
    {
        Title = "Обновления BotPlus RT"; Width = 740; Height = 580; MinWidth = 660; MinHeight = 480;
        MaxHeight = SystemParameters.WorkArea.Height - 24; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BotPlusTheme.Apply(this);
        var panel = new Grid { Margin = new Thickness(18) }; Content = panel;
        for (int i = 0; i < 7; i++) panel.RowDefinitions.Add(new RowDefinition { Height = i == 5 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        void Add(UIElement element, int row) { Grid.SetRow(element, row); panel.Children.Add(element); }
        Add(new TextBlock { Text = "Обновления BotPlus RT", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) }, 0);
        var versions = new Grid { Margin = new Thickness(0, 0, 0, 16) }.Themed(System.Windows.Controls.Panel.BackgroundProperty, "BP.Panel");
        versions.ColumnDefinitions.Add(new ColumnDefinition()); versions.ColumnDefinitions.Add(new ColumnDefinition());
        void Card(string title, TextBlock value, int column)
        {
            var card = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
            card.Children.Add(new TextBlock { Text = title }.Themed(TextBlock.ForegroundProperty, "BP.Muted"));
            value.FontSize = 20; value.FontWeight = FontWeights.SemiBold; card.Children.Add(value);
            Grid.SetColumn(card, column); versions.Children.Add(card);
        }
        Card("Установленная версия", new TextBlock { Text = UpdateSession.Display(UpdateSession.Current) }, 0);
        Card("Последняя версия на GitHub", latest, 1); Add(versions, 1);
        var summary = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        summary.Children.Add(status); checkedAt.SetResourceReference(TextBlock.ForegroundProperty, "BP.Muted"); checkedAt.Margin = new Thickness(0, 6, 0, 0); summary.Children.Add(checkedAt); Add(summary, 2);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { check, download, install }) buttons.Children.Add(button); Add(buttons, 3);
        var activity = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; activity.Children.Add(stage); activity.Children.Add(progress); Add(activity, 4);
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "Журнал обновления", Content = log }); Add(tabs, 5);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(new TextBlock { Text = "Перед установкой сохраните модели и закройте Revit.", TextWrapping = TextWrapping.Wrap }.Themed(TextBlock.ForegroundProperty, "BP.Muted"));
        var openLog = new Button { Content = "Открыть файл журнала", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        openLog.Click += (_, _) => { try { UpdateSession.Log("INFO", "Открытие журнала."); Process.Start(new ProcessStartInfo(UpdateSession.LogPath) { UseShellExecute = true }); } catch (Exception error) { Log("ERROR", error.Message); } };
        footer.Children.Add(openLog); Add(footer, 6);
        try { if (File.Exists(UpdateSession.LogPath)) log.Text = string.Join(Environment.NewLine, File.ReadLines(UpdateSession.LogPath).TakeLast(200)) + Environment.NewLine; } catch (IOException) { }
        release = UpdateSession.Latest; Refresh();
        check.Click += async (_, _) => await Check();
        download.Click += async (_, _) => await Run("Загрузка установщика", async () =>
        {
            if (release == null) return;
            file = null; Log("INFO", $"Загрузка {release.Name} · {release.Size / 1048576d:F1} МБ.");
            file = await new ReleaseClient(http).Download(release,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotPlus.RT", "Updates"),
                new Progress<int>(n => { if (!closed) { progress.Value = n; stage.Text = $"Загрузка установщика · {n}%"; } }), cancellation.Token);
            Log("INFO", "Размер файла и SHA-256 подтверждены. Установщик готов."); stage.Text = "Установщик проверен и готов к запуску";
        });
        install.Click += (_, _) =>
        {
            try
            {
                if (file == null || release == null || !ReleaseClient.Verify(file, release)) throw new InvalidDataException("Проверка установщика не пройдена. Скачайте файл заново.");
                Log("INFO", "Запуск проверенного установщика " + release.Name);
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); Close();
            }
            catch (Exception error) { file = null; Log("ERROR", error.Message); status.Text = error.Message; RefreshButtons(); }
        };
        Loaded += async (_, _) => { OpenWindow = this; if (release == null) await Check(); };
        Closed += (_, _) => { if (OpenWindow == this) OpenWindow = null; closed = true; cancellation.Cancel(); http.Dispose(); };
    }
    Task Check() => Run("Проверка доступных версий", async () => { release = await UpdateSession.Check(http, cancellation.Token); file = null; Log("INFO", "Последняя версия: " + UpdateSession.Display(release.Version)); });
    void RefreshButtons()
    {
        check.IsEnabled = !busy; download.IsEnabled = !busy && release != null && release.Version > UpdateSession.Current;
        install.IsEnabled = !busy && file != null;
    }
    void Refresh()
    {
        latest.Text = release == null ? "Не проверена" : UpdateSession.Display(release.Version);
        checkedAt.Text = UpdateSession.CheckedAt is DateTime time ? $"Проверено: {time:dd.MM.yyyy HH:mm} · GitHub / {ReleaseClient.Repository}" : "Источник: GitHub / " + ReleaseClient.Repository;
        status.Text = release == null ? "Проверка ещё не выполнена." : release.Version > UpdateSession.Current ? "Доступно обновление. Скачайте установщик, чтобы обновить плагин." : release.Version == UpdateSession.Current ? "Установлена актуальная версия." : "Установленная версия новее опубликованной на GitHub.";
        if (UpdateSession.Error != null) status.Text = "Не удалось проверить обновления: " + UpdateSession.Error;
        RefreshButtons();
    }
    void Log(string level, string text) { log.AppendText(UpdateSession.Log(level, text) + Environment.NewLine); log.ScrollToEnd(); }
    async Task Run(string operation, Func<Task> action)
    {
        if (busy) return;
        busy = true; RefreshButtons(); stage.Text = operation; progress.Value = 0; progress.IsIndeterminate = operation.StartsWith("Проверка");
        Log("INFO", operation + "."); string? failure = null;
        try { await action(); if (file == null) stage.Text = "Проверка завершена"; }
        catch (OperationCanceledException) { failure = "Операция отменена или превышено время ожидания."; }
        catch (Exception error) { failure = UpdateSession.Describe(error); Log("DETAIL", error.ToString()); }
        finally
        {
            busy = false;
            if (!closed)
            {
                progress.IsIndeterminate = false; Refresh();
                if (failure != null) { status.Text = failure; stage.Text = "Операция не завершена"; Log("ERROR", failure); }
            }
        }
    }
}
