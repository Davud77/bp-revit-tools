using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Autodesk.Revit.UI;
using TextBox = System.Windows.Controls.TextBox;

namespace BotPlus.Eom;

internal sealed class PlacementHeightWindow : Window
{
    readonly IntPtr owner;
    IntPtr previousFocus;

    internal double? OverrideZ { get; private set; }

    PlacementHeightWindow(IntPtr owner, int left, int top, double? defaultHeight, Func<double, double?> elevation)
    {
        this.owner = owner;
        Title = FloatingToolbar.Title;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;
        ShowInTaskbar = false;
        new WindowInteropHelper(this).Owner = owner;
        BotPlusTheme.Apply(this);

        var height = new TextBox { Width = 68, VerticalContentAlignment = VerticalAlignment.Center };
        const string fieldName = "Высота от уровня вида, мм";
        AutomationProperties.SetName(height, fieldName);
        var hint = new TextBlock
        {
            Text = defaultHeight?.ToString("0.###", CultureInfo.CurrentCulture), Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        hint.Themed(TextBlock.ForegroundProperty, "BP.Muted");
        var field = new System.Windows.Controls.Grid();
        field.Children.Add(height);
        field.Children.Add(hint);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var mode = SystemModeToggle.Create(ReturnRevitFocus);
        mode.Margin = new Thickness(0, 0, 6, 0);
        content.Children.Add(mode);
        content.Children.Add(new TextBlock { Text = "H=", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(field);
        Content = FloatingToolbar.Layout(content, () =>
        {
            Close();
            ReturnRevitFocus();
        });

        void ReadInput()
        {
            var value = height.Text.Trim();
            bool empty = value.Length == 0;
            OverrideZ = double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mm)
                ? elevation(mm) : null;
            bool invalid = !empty && OverrideZ == null;
            hint.Visibility = empty && !height.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
            if (invalid) height.Themed(Control.BorderBrushProperty, "BP.Error");
            else height.ClearValue(Control.BorderBrushProperty);
            height.ToolTip = invalid ? "Введите допустимую неотрицательную высоту в миллиметрах." : fieldName;
        }

        height.TextChanged += (_, _) => ReadInput();
        height.IsKeyboardFocusedChanged += (_, _) => ReadInput();
        height.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            ReturnRevitFocus();
        };
        ReadInput();

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, 0x0015); // без изменения размера, Z-порядка и активации
            HwndSource.FromHwnd(handle)?.AddHook(RememberRevitFocus);
        };
    }

    void ReturnRevitFocus()
    {
        SetActiveWindow(owner);
        if (previousFocus != IntPtr.Zero && IsChild(owner, previousFocus)) SetFocus(previousFocus);
    }

    internal static PlacementHeightWindow? TryShow(UIDocument ui, double? defaultHeight, Func<double, double?> elevation)
    {
        var view = ui.GetOpenUIViews().FirstOrDefault(view => view.ViewId == ui.ActiveView.Id);
        if (view == null) return null;
        using (view)
        {
            var bounds = view.GetWindowRectangle();
            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return null;
            var window = new PlacementHeightWindow(ui.Application.MainWindowHandle, bounds.Left, bounds.Top, defaultHeight, elevation);
            window.Show();
            return window;
        }
    }

    IntPtr RememberRevitFocus(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmMouseActivate = 0x0021;
        if (message == wmMouseActivate) previousFocus = GetFocus();
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr hwnd);
}
