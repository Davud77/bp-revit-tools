using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BotPlus.Shared;

namespace BotPlus.RT;

/// <summary>Настройки плагина: сведения о версии, тема окон модулей и состав панелей ленты.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        BotPlusSettings.Load();
        var window = new SettingsWindow();
        new WindowInteropHelper(window) { Owner = data.Application.MainWindowHandle };
        if (window.ShowDialog() != true) return Result.Cancelled;
        try
        {
            BotPlusSettings.Save(window.ThemeMode, window.HiddenPanels);
            RibbonStyle.ApplyVisibility(data.Application.GetRibbonPanels("BotPlus RT"));
        }
        catch (Exception error) { message = "Не удалось сохранить настройки: " + error.Message; return Result.Failed; }
        return Result.Succeeded;
    }
}
