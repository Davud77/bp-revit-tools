using System.Reflection;
using Autodesk.Revit.UI;

namespace HeatLossViewer
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            // Сведения, 3D, библиотека и радиаторы — на отдельной вкладке «BP ОВ»; общие инструменты ИОС остаются на «BotPlus RT».
            string tabName = BotPlus.Shared.BotPlusSettings.OvTab;
            foreach (string tab in new[] { "BotPlus RT", tabName })
                try { application.CreateRibbonTab(tab); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { /* Вкладка уже существует */ }

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
            var ios = application.GetRibbonPanels("BotPlus RT").FirstOrDefault(p => p.Name == "ИОС") ?? application.CreateRibbonPanel("BotPlus RT", "ИОС");
            AddMepTools(ios, assemblyPath);
            Mep.EngineeringUpdater.Register(application.ActiveAddInId);
            return Result.Succeeded;
        }

        static void AddMepTools(RibbonPanel panel, string path)
        {
            if (panel.GetItems().Any(i => i.Name == "OVMepUpdater")) return;
            PushButtonData Item(string name, string text, Type command, string tip) => new(name, text, path, command.FullName!) { ToolTip = tip };
            var updater = (PulldownButton)panel.AddItem(new PulldownButtonData("OVMepUpdater", "Апдейтер") { ToolTip = "Наименование и количество инженерных элементов по правилам проекта." });
            updater.AddPushButton(Item("OVMepUpdaterSettings", "Настройки правил", typeof(Mep.EngineeringRulesCommand),
                "Правила по категории и типу: конструктор наименования и расчёт количества с запасом, единицами и округлением."));
            updater.AddPushButton(Item("OVMepUpdaterRefresh", "Обновить параметры", typeof(Mep.EngineeringRefreshCommand),
                "Применить сохранённые правила к выделенным элементам или, без выделения, ко всему проекту."));
            var connect = (PulldownButton)panel.AddItem(new PulldownButtonData("OVMepConnect", "Соединить") { ToolTip = "Соединение и разъединение MEP-элементов." });
            connect.AddPushButton(Item("OVMepConnectConnectors", "Соединить коннекторы", typeof(Mep.MepConnectCommand),
                "Присоединяемый элемент, затем опорный: ближайшие свободные коннекторы. Первый элемент перемещается и поворачивается. Esc — выход."));
            connect.AddPushButton(Item("OVMepConnectLine", "Соединить участки на прямой", typeof(Mep.MepConnectLineCommand),
                "Пакетное соединение прямолинейных участков одной прямой: удлиняются свободные концы, добавляются муфты и переходы."));
            connect.AddPushButton(Item("OVMepConnectDiffuser", "Подключить диффузоры", typeof(Mep.MepDiffuserCommand),
                "Диффузор, затем воздуховод: врезка или тройник, при необходимости гибкий участок. Esc — выход."));
            connect.AddSeparator();
            connect.AddPushButton(Item("OVMepDisconnect", "Разъединить", typeof(Mep.MepDisconnectCommand),
                "Разъединить ближайший к щелчку подключенный коннектор. Esc — выход."));
            connect.AddPushButton(Item("OVMepDetachBranch", "Отключить ветку", typeof(Mep.MepDetachBranchCommand),
                "Элемент ветки, затем элемент системы: соединения между ними разрываются, ветка выделяется."));
            var rotate = (PulldownButton)panel.AddItem(new PulldownButtonData("OVMepRotate", "Вращать") { ToolTip = "Пробные повороты MEP-элементов с принятием или отменой." });
            rotate.AddPushButton(Item("OVMepRotateConnector", "Вокруг коннектора", typeof(Mep.MepRotateCommand),
                "Элемент (можно выбрать заранее), затем коннектор оси. Угол, направление, подключенные элементы; «Принять» или «Отмена»."));
            rotate.AddPushButton(Item("OVMepRotateOrient", "По направлению кривой", typeof(Mep.MepOrientCommand),
                "Направить коннектор фитинга вдоль опорной MEP-кривой; при двух решениях — выбор направления."));
            rotate.AddPushButton(Item("OVMepRotateTray", "Лоток на ребро", typeof(Mep.TrayRotateCommand),
                "Повернуть выбранные кабельные лотки на 90° вокруг собственной оси."));
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            Mep.EngineeringUpdater.Unregister(application.ActiveAddInId);
            return Result.Succeeded;
        }
    }
}
