using Autodesk.Revit.UI;
using System.Diagnostics;

namespace Ac2Revit;

/// <summary>Small closing dialog of an import: «Импорт завершён» with «ОК» and «Открыть лог».</summary>
public static class ImportDone
{
    public static void Show(string? logPath, string? fallbackReport = null)
    {
        var dialog = new TaskDialog("BotPlus RT") { MainInstruction = "Импорт завершён", CommonButtons = TaskDialogCommonButtons.Ok, DefaultButton = TaskDialogResult.Ok };
        string? target = logPath != null && File.Exists(logPath) ? logPath : fallbackReport != null && File.Exists(fallbackReport) ? fallbackReport : null;
        if (target != null) dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Открыть лог");
        if (dialog.Show() == TaskDialogResult.CommandLink1 && target != null)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception) { /* The log is next to the model; failing to open it must not fail the import. */ }
        }
    }

    /// <summary>The per-save log written beside the RVT, or null when the model has no path yet.</summary>
    public static string? LogBeside(string modelPath) => string.IsNullOrEmpty(modelPath) ? null : Path.ChangeExtension(modelPath, ".botplus-import.log");
}
