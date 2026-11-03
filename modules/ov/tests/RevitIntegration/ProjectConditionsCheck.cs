using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using HeatLossViewer;
using HeatLossViewer.Export;

namespace RevitIntegration;

public static class ProjectConditionsCheck
{
    public static object Run(UIApplication ui, string output)
    {
        string path = Path.Combine(output, "project-conditions-fixture.rvt");
        if (File.Exists(path)) throw new IOException("Fixture already exists");
        var checks = new List<string>();
        void Check(bool success, string label) { if (!success) throw new InvalidOperationException(label); checks.Add(label); }
        var doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            Check(ProjectConditionsStore.Read(doc).WindSpeed is null, "unspecified climate remains null");
            var data = new ProjectConditions { Name = "ОВ — проверка проекта", Number = "ОВ-01", Address = "Тестовый адрес", ClimateRegion = "II", ClimateSubregion = "IIБ", Notes = "Тест, не нормативные значения", OutdoorTemperature = -23.5, IndoorTemperature = 21, WindSpeed = 0, HeatingPeriodDays = 180, HeatingPeriodTemperature = -2.5, BuildingHeight = 12, FirstFloorHeight = 0.6 };
            data.Calculation.Building["wall"] = .34;
            data.Calculation.FloorOverrides["host/level/check"] = new() { ["indoor"] = 22 };
            data.Calculation.RoomOverrides["host/room/check"] = new() { ["heatGain"] = 75 };
            using (var t = new Transaction(doc, "Save climate fixture"))
            { t.Start(); ProjectConditionsStore.Write(doc, data); Check(t.Commit() == TransactionStatus.Committed, "transaction committed"); }
            Check(doc.ProjectInformation.Name == data.Name && doc.ProjectInformation.Number == data.Number && doc.ProjectInformation.Address == data.Address, "native Revit information synchronized");
            using (var t = new Transaction(doc, "Rollback climate fixture"))
            { t.Start(); ProjectConditionsStore.Write(doc, new ProjectConditions { Name = "cancelled", WindSpeed = 10 }); t.RollBack(); }
            Check(ProjectConditionsStore.Read(doc).Name == data.Name && ProjectConditionsStore.Read(doc).WindSpeed == 0, "rollback keeps native fields and climate");
            doc.SaveAs(path, new SaveAsOptions());
        }
        finally { doc.Close(false); }
        doc = ui.Application.OpenDocumentFile(path);
        try
        {
            var data = ProjectConditionsStore.Read(doc);
            Check(data.OutdoorTemperature == -23.5 && data.WindSpeed == 0 && data.IndoorTemperature == 21 && data.ClimateSubregion == "IIБ" && data.HeatingPeriodDays == 180 && data.FirstFloorHeight == 0.6, "climate survives RVT save, close and reopen");
            Check(data.Calculation.Building["wall"] == .34 && data.Calculation.FloorOverrides["host/level/check"]["indoor"] == 22 && data.Calculation.RoomOverrides["host/room/check"]["heatGain"] == 75, "building, floor and room inputs survive RVT reopen");
            using (var t = new Transaction(doc, "Native rename"))
            { t.Start(); doc.ProjectInformation.Name = "ОВ — новое имя"; t.Commit(); }
            Check(ProjectConditionsStore.Read(doc).Name == "ОВ — новое имя", "native rename remains authoritative");
            var export = new RevitRoomExtractor().Extract(doc);
            Check(export.ProjectName == "ОВ — новое имя" && export.ProjectConditions?.OutdoorTemperature == -23.5 && export.ProjectConditions.WindSpeed == 0, "room export includes project name and climate");
            new BuildingExportWriter().Write(export, Path.Combine(output, "export"));
        }
        finally { doc.Close(false); }
        ui.OpenAndActivateDocument(path);
        return new { status = "passed", checks, fixture = path };
    }
}
