using System.Reflection;
using Autodesk.Revit.UI;

namespace HeatLossViewer
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            string tabName = "BotPlus RT";
            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Вкладка уже существует
            }

            RibbonPanel panel = application.GetRibbonPanels(tabName).FirstOrDefault(p => p.Name == "ОВ")
                ?? application.CreateRibbonPanel(tabName, "ОВ");
            if (panel.GetItems().Any(i => i.Name == "ExportHeatLoss3D")) return Result.Succeeded;

            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            PushButtonData buttonData = new PushButtonData(
                name: "ExportHeatLoss3D",
                text: "Выгрузить\n3D-модель",
                assemblyName: assemblyPath,
                className: "HeatLossViewer.ExportViewCommand"
            )
            {
                ToolTip = "Выгружает геометрию помещений в building.json и открывает интерактивный 3D-вид в браузере."
            };

            panel.AddItem(buttonData);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
