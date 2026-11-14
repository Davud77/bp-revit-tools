using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotPlus.RT;
using BotPlus.Shared;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (bool dark in new[] { false, true })
        {
            BotPlusTheme.Override = dark;
            var window = new ScheduleNumberingWindow([new(1,"Комментарии"), new(2,"ADSK_Позиция")], !dark)
                { ShowActivated = false, ShowInTaskbar = false };
            window.Show(); window.UpdateLayout();
            var root = (StackPanel)window.Content;
            var combo = root.Children.OfType<ComboBox>().Single();
            if (((NumberingParameter)combo.SelectedItem).Id != 2) throw new Exception("Default parameter");
            if (root.Children.OfType<TextBox>().Single().Text != "1") throw new Exception("Initial number");
            if (root.Children.OfType<CheckBox>().Single().IsEnabled == dark) throw new Exception("Selection availability");
            var buttons = root.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
            if (buttons.Length != 2 || !buttons[0].IsDefault || !buttons[1].IsCancel) throw new Exception("Dialog buttons");
            if (root.DesiredSize.Width > window.ActualWidth || root.DesiredSize.Height > window.ActualHeight) throw new Exception("Clipped dialog");
            if (args.Length > 0)
            {
                Directory.CreateDirectory(args[0]);
                var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(args[0], dark ? "numbering-dark.png" : "numbering-light.png")); encoder.Save(stream);
            }
            window.Close();
        }
        app.Shutdown(); Console.WriteLine("PASS numbering window: default parameter/value, selection availability, buttons and light/dark layout");
    }
}
namespace BotPlus.RT { internal sealed record NumberingParameter(long Id, string Name) { public string Label => Name; public override string ToString() => Label; } }
namespace Autodesk.Revit.UI
{
    public enum UITheme { Light, Dark }
    public static class UIThemeManager { public static UITheme CurrentTheme => UITheme.Light; }
}
