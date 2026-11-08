global using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotPlus.RT;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "Выходные данные/Проверки/Обновления"); Directory.CreateDirectory(output);
        UpdateSession.LogPath = Path.Combine(output, "ui-checks.log");
        if (UpdateSession.Display(new Version(1, 6, 1)) != "1.6.1") throw new Exception("Patch version hidden");
        if (UpdateSession.Normalize(new Version(1, 6, 0, 7)) != new Version(1, 6, 0)) throw new Exception("Revision comparison");
        using var http = new HttpClient(new Failure());
        UpdateSession.Latest = new(new Version(1, 7, 0), "BotPlus.RT-1.7-Setup.exe", new Uri("https://github.com"), 100, "", "");
        try { UpdateSession.Check(http, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Failure swallowed"); }
        catch (HttpRequestException) { }
        if (UpdateSession.Error == null || UpdateSession.Latest.Version != new Version(1, 7, 0)) throw new Exception("Network failure lost previous successful result");
        UpdateSession.Error = null; UpdateSession.CheckedAt = DateTime.Now;
        Render("available", output, "Доступно обновление");
        UpdateSession.Latest = UpdateSession.Latest with { Version = UpdateSession.Current };
        Render("current", output, "Установлена актуальная версия");
        UpdateSession.Latest = null; UpdateSession.Error = "Нет соединения с GitHub";
        Render("offline", output, "Не удалось проверить обновления");
        RenderSettings(output); CheckSettings(output);
        Console.WriteLine("PASS patch version, failed check preserves cached release, three window states rendered without clipped buttons");
    }
    static void CheckSettings(string output)
    {
        var file = Path.Combine(output, "settings-check.json"); if (File.Exists(file)) File.Delete(file);
        BotPlus.Shared.BotPlusSettings.Load(file);
        if (BotPlus.Shared.BotPlusSettings.ThemeMode != "follow" || BotPlus.Shared.BotPlusSettings.HiddenPanels.Count != 0) throw new Exception("Defaults must follow Revit with every panel visible");
        BotPlus.Shared.BotPlusSettings.Save("dark", ["ОВ", "Настройки", "Марки"], file);
        BotPlus.Shared.BotPlusSettings.Load(file);
        if (BotPlus.Shared.BotPlusSettings.ThemeMode != "dark" || !BotPlus.Shared.BotPlusSettings.HiddenPanels.SetEquals(["ОВ", "Марки"])) throw new Exception("Settings round trip lost data or hid the Settings panel");
        File.WriteAllText(file, "{broken"); BotPlus.Shared.BotPlusSettings.Load(file);
        if (BotPlus.Shared.BotPlusSettings.ThemeMode != "follow") throw new Exception("Broken settings file must fall back to defaults");
        File.Delete(file);
    }
    static void RenderSettings(string output)
    {
        foreach (var dark in new[] { false, true })
        {
            BotPlus.Shared.BotPlusTheme.Override = dark;
            var window = new SettingsWindow(); var content = (FrameworkElement)window.Content;
            content.Measure(new Size(520, 400)); content.Arrange(new Rect(0, 0, 520, 400)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(520, 400, 96, 96, PixelFormats.Pbgra32);
            var back = new Border { Width = 520, Height = 400, Background = window.Background, Child = null }; back.Measure(new Size(520, 400)); back.Arrange(new Rect(0, 0, 520, 400)); bitmap.Render(back); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, "settings-" + (dark ? "dark" : "light") + ".png")); encoder.Save(stream); window.Close();
        }
        BotPlus.Shared.BotPlusTheme.Override = null;
    }
    static void Render(string name, string output, string expected)
    {
        var window = new UpdateWindow(); var content = (FrameworkElement)window.Content;
        content.Measure(new Size(700, 500)); content.Arrange(new Rect(0, 0, 700, 500)); content.UpdateLayout();
        var elements = Descendants(content).ToArray();
        if (!elements.OfType<TextBlock>().Any(t => t.Text.Contains(expected))) throw new Exception("Missing status: " + expected);
        if (!elements.OfType<TextBlock>().Any(t => t.Text == "1.6.1")) throw new Exception("Missing installed version");
        foreach (var button in elements.OfType<Button>().Where(b => b.Content is string))
        {
            var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
            if (bounds.Right > 701 || bounds.Bottom > 501 || button.ActualWidth + button.Margin.Left + button.Margin.Right < button.DesiredSize.Width - 1) throw new Exception("Clipped action: " + button.Content);
        }
        var bitmap = new RenderTargetBitmap(700, 500, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream); window.Close();
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    sealed class Failure : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
}
namespace BotPlus.RT { public sealed class Application { } }
