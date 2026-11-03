using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using HeatLossViewer.Export;
using System.Text.Json;
using System.Windows.Interop;

namespace HeatLossViewer;

[Transaction(TransactionMode.Manual)]
public sealed class OvLibraryCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) => Open(data, ref message, false);
    internal static Result Open(ExternalCommandData data, ref string message, bool radiators)
    {
        var doc = data.Application.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument) return Result.Cancelled;
        try
        {
            var original = RevitOvLibrary.Read(doc);
            var draft = JsonSerializer.Deserialize<OvLibraryData>(JsonSerializer.Serialize(original))!;
            var building = new RevitRoomExtractor().Extract(doc); building.OvLibrary = draft;
            var window = new OvLibraryWindow(draft, building, radiators);
            new WindowInteropHelper(window).Owner = data.Application.MainWindowHandle;
            if (window.ShowDialog() != true) return Result.Cancelled;
            using var t = new Transaction(doc, "ОВ — библиотека и параметры радиаторов");
            t.Start(); RevitOvLibrary.Write(doc, draft, original);
            if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit не сохранил библиотеку.");
            return Result.Succeeded;
        }
        catch (Exception ex) { message = "Библиотека ОВ: " + ex.Message; return Result.Failed; }
    }
}
[Transaction(TransactionMode.Manual)]
public sealed class OvRadiatorsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) => OvLibraryCommand.Open(data, ref message, true);
}
