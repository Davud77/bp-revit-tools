using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BotPlus.Shared;

namespace BotPlus.RT;

[Transaction(TransactionMode.ReadOnly)]
public sealed class CopyFromLinkCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        CopyWindow.Show(data.Application, "Копировать из связи");
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class CopyFromProjectCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        CopyWindow.Show(data.Application, "Копировать из проекта");
}

internal static class CopyWindow
{
    internal static Result Show(UIApplication app, string title)
    {
        var window = new Window
        {
            Title = title,
            Width = 480,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        new WindowInteropHelper(window) { Owner = app.MainWindowHandle };
        BotPlusTheme.Apply(window);
        window.ShowDialog();
        return Result.Succeeded;
    }
}
