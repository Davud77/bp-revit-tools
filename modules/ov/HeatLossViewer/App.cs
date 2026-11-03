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
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            if (!panel.GetItems().Any(i => i.Name == "OVProjectInformation"))
                panel.AddItem(new PushButtonData("OVProjectInformation", "Сведения\nо проекте", assemblyPath,
                    typeof(ProjectInformationCommand).FullName!)
                {
                    ToolTip = "Название, адрес, климатический район, температуры и ветер. Сохраняет параметры в модели для выгрузки ОВ."
                });
            if (!panel.GetItems().Any(i => i.Name == "ExportHeatLoss3D"))
            {
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
            }
            if (!panel.GetItems().Any(i => i.Name == "OVLibrary"))
                panel.AddItem(new PushButtonData("OVLibrary", "Библиотека", assemblyPath, typeof(OvLibraryCommand).FullName!) { ToolTip = "Материалы, типы конструкций и радиаторы из текущего проекта." });
            if (!panel.GetItems().Any(i => i.Name == "OVRadiators"))
                panel.AddItem(new PushButtonData("OVRadiators", "Радиаторы", assemblyPath, typeof(OvRadiatorsCommand).FullName!) { ToolTip = "Открыть вкладку подбора радиаторов в библиотеке проекта." });

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
