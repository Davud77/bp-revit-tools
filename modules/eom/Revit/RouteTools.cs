using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitCanceled = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace BotPlus.Eom;

internal sealed class RouteTools : IDisposable
{
    static RouteTools? native;
    static RouteTools? active;
    // Revit исполняет PostCommand после выхода из контекста API. Идентификатор живёт до выгрузки модуля.
    static RevitCommandId? trayCommandId;
    static RevitCommandId TrayCommandId => trayCommandId ??= RevitCommandId.LookupPostableCommandId(PostableCommand.CableTray);
    readonly UIDocument ui;
    readonly ElementId viewId;
    readonly bool tray;
    readonly Window palette;
    readonly Button riser;
    IntPtr drawingFocus;
    bool requested;
    bool closed;
    bool picking;
    RiserPlacement.Size size;

    internal bool BuiltRiser { get; private set; }

    RouteTools(UIDocument ui, bool tray)
    {
        this.ui = ui;
        this.tray = tray;
        viewId = ui.ActiveView.Id;
        var doc = ui.Document;
        size = new(doc.GetDefaultElementTypeId(tray ? ElementTypeGroup.CableTrayType : ElementTypeGroup.ConduitType),
            RiserPlacement.Feet(tray ? 100 : 20), RiserPlacement.Feet(50));
        var selected = ui.Selection.GetElementIds().Select(doc.GetElement).FirstOrDefault(e => tray ? e is CableTray : e is Conduit);
        UseSegment(selected);

        palette = new Window
        {
            Title = FloatingToolbar.Title,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        BotPlusTheme.Apply(palette);
        var owner = ui.Application.MainWindowHandle;
        new WindowInteropHelper(palette).Owner = owner;
        riser = new Button
        {
            Content = new System.Windows.Shapes.Path
            {
                Width = 20, Height = 20, Stretch = Stretch.Uniform,
                Data = Geometry.Parse("M5,3 L15,3 M5,17 L15,17 M8,3 L8,17 M12,3 L12,17 M15,8 L18,5 L21,8 M18,5 L18,15"),
                StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            }.Themed(System.Windows.Shapes.Shape.StrokeProperty, "BP.Accent"),
            Width = 34, Height = 32, Padding = new Thickness(4),
            IsEnabled = ui.ActiveView is ViewPlan { GenLevel: not null },
            ToolTip = "Установить стояк"
        };
        AutomationProperties.SetName(riser, "Установить стояк");
        riser.Click += (_, _) => RequestRiser(owner);
        var mode = SystemModeToggle.Create(() => ReturnDrawingFocus(owner));
        mode.Margin = new Thickness(4, 0, 0, 0);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(riser);
        controls.Children.Add(mode);
        palette.Content = FloatingToolbar.Layout(controls, () =>
        {
            palette.Close();
            ReturnDrawingFocus(owner);
        });
        palette.Closed += (_, _) => closed = true;
        palette.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(palette).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(RememberDrawingFocus);
            AnchorPalette(palette, ui, viewId);
        };
        palette.Show();
        active = this;
    }

    internal static bool IsEmergency => SystemModeToggle.IsEmergency;
    internal static bool IsDrawing => active != null;

    internal static void AnchorPalette(Window palette, UIDocument ui, ElementId viewId)
    {
        var handle = new WindowInteropHelper(palette).Handle;
        foreach (var view in ui.GetOpenUIViews())
        {
            using (view)
            {
                if (view.ViewId != viewId) continue;
                var bounds = view.GetWindowRectangle();
                SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, 0, 0, 0x0015);
            }
        }
    }

    internal static bool TryMode(Document doc, out bool emergency)
    {
        emergency = IsEmergency;
        return active is { } session && doc.Equals(session.ui.Document) && doc.ActiveView.Id == session.viewId;
    }

    internal static RouteTools OpenConduit(UIDocument ui) => new(ui, false);

    internal static void StartTray(UIDocument ui)
    {
        ConduitSelectionToolbar.Hide();
        CloseNative();
        var command = TrayCommandId;
        if (!ui.Application.CanPostCommand(command))
            throw new InvalidOperationException("Revit не может запустить размещение лотка на этом виде.");
        native = new(ui, true);
        try { ui.Application.PostCommand(command); }
        catch { CloseNative(); throw; }
    }

    internal static void CloseNative()
    {
        native?.Dispose();
        native = null;
    }

    internal static void Shutdown()
    {
        CloseNative();
        trayCommandId?.Dispose();
        trayCommandId = null;
    }

    internal static void OnIdling(object? sender, IdlingEventArgs args)
    {
        if (native is not { } session) return;
        if (session.closed || !session.requested ||
            sender is not UIApplication app || app.ActiveUIDocument is not { } active ||
            !active.Document.Equals(session.ui.Document) || active.ActiveView.Id != session.viewId)
        {
            CloseNative();
            return;
        }

        session.PlaceRiser();
        if (session.closed) { CloseNative(); return; }
        var command = TrayCommandId;
        if (!app.CanPostCommand(command)) { CloseNative(); return; }
        try { app.PostCommand(command); }
        catch (Exception error)
        {
            CloseNative();
            TaskDialog.Show("BotPlus ЭОМ", error.Message);
        }
    }

    internal static void OnDocumentChanged(object? sender, DocumentChangedEventArgs args)
    {
        if (native is not { closed: false } session || !args.GetDocument().Equals(session.ui.Document)) return;
        var doc = args.GetDocument();
        var last = args.GetAddedElementIds().Select(doc.GetElement).OfType<CableTray>().LastOrDefault();
        if (last != null) session.UseSegment(last);
    }

    void UseSegment(Element? segment)
    {
        if (segment is CableTray t)
            size = new(t.GetTypeId(), t.Width, t.Height);
        else if (segment is Conduit c)
            UseConduit(c.GetTypeId(), c.Diameter);
    }

    internal void UseConduit(ElementId type, double? diameter) =>
        size = size with { Type = type, Width = diameter ?? size.Width };

    internal T Pick<T>(Func<T> pick)
    {
        while (true)
        {
            picking = true;
            try { return pick(); }
            catch (RevitCanceled) when (requested) { }
            finally { picking = false; }
            PlaceRiser();
        }
    }

    void PlaceRiser()
    {
        requested = false;
        palette.Hide();
        try { BuiltRiser |= RiserPlacement.Run(ui, tray, size); }
        catch (RevitCanceled) { }
        catch (Exception error) { TaskDialog.Show("BotPlus ЭОМ — Стояк", error.Message); }
        finally
        {
            if (!closed)
            {
                riser.IsEnabled = true;
                palette.Show();
            }
        }
    }

    IntPtr RememberDrawingFocus(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int mouseActivate = 0x0021;
        if (message == mouseActivate) drawingFocus = GetFocus();
        return IntPtr.Zero;
    }

    void RequestRiser(IntPtr owner)
    {
        if (requested || !tray && !picking) return;
        requested = true;
        riser.IsEnabled = false;

        // Завершаем только текущий выбор Revit. Транзакции запускаются после возврата из PickPoint или в Idling.
        ReturnDrawingFocus(owner);
        var target = GetFocus();
        if (target == IntPtr.Zero || target != owner && !IsChild(owner, target)) target = owner;
        const uint keyDown = 0x0100, keyUp = 0x0101;
        const int escape = 0x1B;
        for (int press = 0; press < (tray ? 2 : 1); press++)
        {
            PostMessage(target, keyDown, (IntPtr)escape, (IntPtr)0x00010001);
            PostMessage(target, keyUp, (IntPtr)escape, (IntPtr)unchecked((int)0xC0010001));
        }
    }

    void ReturnDrawingFocus(IntPtr owner)
    {
        SetActiveWindow(owner);
        if (drawingFocus != IntPtr.Zero && (drawingFocus == owner || IsChild(owner, drawingFocus))) SetFocus(drawingFocus);
    }

    public void Dispose()
    {
        if (ReferenceEquals(active, this)) active = null;
        if (!closed) palette.Close();
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
