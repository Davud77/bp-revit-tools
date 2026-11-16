using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotPlus.RT;
using BotPlus.Shared;

internal static class Program
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var x in All<T>(child)) yield return x;
        }
    }

    // Levels as in the user's model: AC 1st–3rd floor, 3 m apart.
    static readonly LevelInfo L1 = new(1, "АС «1-й этаж»", 0), L2 = new(2, "АС «2-й этаж»", 3000), L3 = new(3, "АС «3-й этаж»", 6000);
    static readonly List<LevelInfo> Levels = [L3, L1, L2];

    static List<RangeRow> Standard() =>
    [
        new() { Plane = RangePlane.Top, Level = new(LevelKind.Above, 0, ""), OffsetMm = -250 },
        new() { Plane = RangePlane.Cut, Level = new(LevelKind.Associated, 0, ""), OffsetMm = 1200 },
        new() { Plane = RangePlane.Bottom, Level = new(LevelKind.Associated, 0, ""), OffsetMm = 0 },
        new() { Plane = RangePlane.Depth, Level = new(LevelKind.Associated, 0, ""), OffsetMm = 0 }
    ];

    [STAThread] static void Main(string[] args)
    {
        // ---------- Policy ----------
        var top = ViewRangePolicy.Choices(RangePlane.Top, Levels, L2);
        Check(top[0].Label == "Связанный уровень (АС «2-й этаж»)" && top[1].Label == "Уровень выше (АС «3-й этаж»)" && top[2].Label == "Уровень ниже (АС «1-й этаж»)", "Level choices as in Revit");
        Check(top.Any(c => c.Kind == LevelKind.Unlimited) && ViewRangePolicy.Choices(RangePlane.Depth, Levels, L2).Any(c => c.Kind == LevelKind.Unlimited), "Unlimited for top and depth");
        Check(ViewRangePolicy.Choices(RangePlane.Bottom, Levels, L2).All(c => c.Kind != LevelKind.Unlimited), "No unlimited bottom");
        Check(ViewRangePolicy.Choices(RangePlane.Cut, Levels, L2).Count == 1, "Cut plane: associated level only");
        Check(ViewRangePolicy.Choices(RangePlane.Top, Levels, L3)[1].Label == "Уровень выше (нет)", "Top floor has no level above");
        Check(ViewRangePolicy.Validate(Standard(), Levels, L2) is null, "Standard range (top −250 of the level above, cut 1200) is valid");
        var bad = Standard(); bad[1].OffsetMm = 3000;
        Check(ViewRangePolicy.Validate(bad, Levels, L2) is { Plane: RangePlane.Cut } p1 && p1.Message.Contains("выше верхней"), "Cut above top rejected");
        bad = Standard(); bad[2].OffsetMm = 1500;
        Check(ViewRangePolicy.Validate(bad, Levels, L2)?.Plane == RangePlane.Bottom, "Bottom above cut rejected");
        bad = Standard(); bad[3].OffsetMm = 10;
        Check(ViewRangePolicy.Validate(bad, Levels, L2)?.Plane == RangePlane.Depth, "Depth above bottom rejected");
        var unlimited = Standard(); unlimited[0].Level = new(LevelKind.Unlimited, 0, ""); unlimited[3].Level = new(LevelKind.Unlimited, 0, "");
        Check(ViewRangePolicy.Validate(unlimited, Levels, L2) is null, "Unlimited top and depth");
        var missing = Standard();
        Check(ViewRangePolicy.Validate(missing, Levels, L3) is { Plane: RangePlane.Top } p2 && p2.Message.Contains("уровня выше нет"), "Missing level above reported");
        var levelRef = Standard(); levelRef[3].Level = new(LevelKind.Level, 1, ""); levelRef[3].OffsetMm = 500;
        Check(ViewRangePolicy.Validate(levelRef, Levels, L2) is null && ViewRangePolicy.Elevation(levelRef[3], Levels, L2, out _) == 500, "Specific level");
        var (min, max) = ViewRangePolicy.SliderRange(new(LevelKind.Associated, 2, ""), 1200, Levels, L2);
        Check(min == -4000 && max == 4000, "Slider: to the neighbouring levels with 1 m margin");
        var (min3, max3) = ViewRangePolicy.SliderRange(new(LevelKind.Associated, 3, ""), 9000, Levels, L3);
        Check(min3 == -4000 && max3 == 9000, "Slider: 6 m without a level above, current value kept in range");
        Check(ViewRangePolicy.Snap(1234) == 1230 && ViewRangePolicy.Snap(-1235) == -1240, "10 mm step");
        Check(ViewRangePolicy.TryParseOffset(" -250,5 ", out double off) && off == -250.5 && !ViewRangePolicy.TryParseOffset("abc", out _), "Offset input");
        Console.WriteLine("PASS view range policy: Revit level choices, plane order, unlimited, missing levels, slider limits, input");

        // ---------- Window ----------
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (bool dark in new[] { false, true })
        {
            BotPlusTheme.Override = dark;
            var applied = new List<IReadOnlyList<RangeRow>>();
            var window = new ViewRangeWindow(Standard(), Levels, L2, null) { ShowActivated = false, ShowInTaskbar = false };
            window.Apply = rows => { applied.Add(rows.Select(r => r.Clone()).ToList()); return null; };
            window.Show(); window.UpdateLayout();
            var combos = All<ComboBox>(window).ToList();
            Check(combos.Count == 4 && !combos[1].IsEnabled && combos[0].SelectedItem!.ToString() == "Уровень выше (АС «3-й этаж»)", "Four level lists, cut list locked");
            var boxes = All<TextBox>(window).ToList();
            Check(boxes.Select(b => b.Text).SequenceEqual(["-250", "1200", "0", "0"]), "Offsets shown");
            var sliders = All<Slider>(window).ToList();
            Check(sliders.Count == 4 && sliders[1].Value == 1200, "Sliders follow offsets");
            var buttons = All<Button>(window).Select(b => b.Content as string).ToList();
            Check(buttons.SequenceEqual(["Принять", "Отмена"]), "Accept and cancel");
            if (args.Length > 0)
            {
                Directory.CreateDirectory(args[0]);
                var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(args[0], dark ? "view-range-dark.png" : "view-range-light.png")); encoder.Save(stream);
            }
            sliders[1].Value = 1500;
            Check(boxes[1].Text == "1500" && applied.Count == 1 && applied[0][1].OffsetMm == 1500, "Slider move applied to the view at once, no wait for release");
            sliders[1].Value = 1510; sliders[1].Value = 1520;
            Check(applied.Count == 1, "Rapid moves within 30 ms are coalesced");
            Check(window.Push() && applied.Count == 2 && applied[1][1].OffsetMm == 1520, "Latest value applied");
            sliders[1].Value = 1500; window.Push();
            boxes[2].Text = "2000";
            Check(!window.Push() && applied.Count == 3, "Bottom above cut is not applied");
            Check(All<TextBlock>(window).Any(t => t.Text.Contains("Нижняя граница") && t.Text.Contains("не применено")), "Reason shown");
            boxes[2].Text = "0";
            Check(window.Push() && applied.Count == 3 && window.Applied == 3, "Back to the applied state: nothing new to apply");
            window.Close();
        }
        app.Shutdown();
        Console.WriteLine("PASS view range window: four planes, locked cut level, slider ↔ offset, live apply, invalid order blocked, light/dark layout");
    }
}

namespace Autodesk.Revit.UI
{
    public enum UITheme { Light, Dark }
    public static class UIThemeManager { public static UITheme CurrentTheme => UITheme.Light; }
}
