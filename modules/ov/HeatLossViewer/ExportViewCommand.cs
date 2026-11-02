using System.Diagnostics;
using System.IO;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using HeatLossViewer.Export;

namespace HeatLossViewer;

[Transaction(TransactionMode.ReadOnly)]
public class ExportViewCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc is null) return Result.Cancelled;
        try
        {
            var building = new RevitRoomExtractor().Extract(doc);
            if (building.Rooms.Count == 0)
            {
                TaskDialog.Show("Выгрузка 3D", "Нет помещений с площадью и пригодным контуром.\n" + string.Join("\n", building.Warnings.Take(12)));
                return Result.Cancelled;
            }
            var paths = new BuildingExportWriter().Write(building, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitHeatLossExport"));
            try { Process.Start(new ProcessStartInfo(paths.Html) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                TaskDialog.Show("Экспорт завершён", $"Файлы сохранены в {Path.GetDirectoryName(paths.Html)}.\nБраузер не открылся: {ex.Message}");
            }
            if (building.Warnings.Count > 0)
                TaskDialog.Show("Экспорт завершён с замечаниями", $"Помещений: {building.Rooms.Count}.\n" +
                    string.Join("\n", building.Warnings.Take(10)) + "\nПолный список — в building.json и HTML.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = $"Не удалось экспортировать помещения: {ex.Message}";
            return Result.Failed;
        }
    }
}
