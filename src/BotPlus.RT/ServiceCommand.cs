using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BotPlus.RT;

[Transaction(TransactionMode.ReadOnly)]
public sealed class ServiceCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var version = typeof(Application).Assembly.GetName().Version;
        TaskDialog.Show("BotPlus RT — Сервис", $"BotPlus RT {version?.ToString(3)}\nAutodesk Revit 2026\n\nМодули: MCP, Ac2Revit, ОВ, Отверстия\nhttps://github.com/Davud77/bp-revit-tools");
        return Result.Succeeded;
    }
}
