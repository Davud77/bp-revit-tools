using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BotPlus.Eom;

/// <summary>Общий режим для панелей трассировки и размещения оборудования.</summary>
internal static class SystemModeToggle
{
    static readonly string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotPlus RT", "eom-emergency-mode.txt");
    static event Action? Changed;
    internal static bool IsEmergency { get; private set; } = Load();

    static bool Load()
    {
        try { return File.Exists(file) && File.ReadAllText(file).Trim() == "1"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.WriteLine(e); return false; }
    }

    static void Set(bool value)
    {
        if (IsEmergency == value) return;
        IsEmergency = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, value ? "1" : "0"); File.Move(temp, file, overwrite: true); }
            finally { File.Delete(temp); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine(e); }
        Changed?.Invoke();
    }

    internal static ToggleButton Create(Action returnFocus)
    {
        var button = new ToggleButton { Content = FloatingToolbarIcons.Draw(FloatingToolbarIcons.Emergency), Width = 34, Height = 32, Padding = new Thickness(4) };
        AutomationProperties.SetName(button, "Аварийная система");
        void Update()
            => ShowMode(button, IsEmergency);
        Update();
        button.Loaded += (_, _) => { Changed += Update; Update(); };
        button.Unloaded += (_, _) => Changed -= Update;
        button.Click += (_, _) => { Set(button.IsChecked == true); returnFocus(); };
        return button;
    }

    internal static void ShowMode(ToggleButton button, bool emergency)
    {
        button.IsChecked = emergency;
        var (r, g, b) = RouteService.SystemColor(emergency);
        button.Foreground = new SolidColorBrush(Color.FromRgb(r, g, b));
        button.ToolTip = emergency ? "Аварийная система · красная" : "Обычная система · синяя. Нажмите «А» для аварийной.";
    }
}
