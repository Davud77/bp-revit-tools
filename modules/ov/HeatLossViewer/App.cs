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
            Mep.InsulationUpdater.Register(application.ActiveAddInId);
            return Result.Succeeded;
        }

        /// <summary>
        /// Инструменты ИОС — split-кнопки, как у ModPlus: после запуска Revit главной стоит первая команда,
        /// выбранная из списка команда выполняется и становится главной до следующего выбора или перезапуска.
        /// </summary>
        static void AddMepTools(RibbonPanel panel, string path)
        {
            if (panel.GetItems().Any(i => i.Name == "OVMepUpdater")) return;
            PushButtonData Item(string name, string text, Type command, string tip) => new(name, text, path, command.FullName!) { ToolTip = tip };
            SplitButton Split(string name, string text)
            {
                var split = (SplitButton)panel.AddItem(new SplitButtonData(name, text));
                split.IsSynchronizedWithCurrentItem = true;
                return split;
            }

            var connect = Split("OVMepConnect", "Соединить");
            connect.AddPushButton(Item("OVMepConnectConnectors", "Соединить\nконнекторы", typeof(Mep.MepConnectCommand),
                "Присоединяемый элемент, затем опорный: ближайшие свободные коннекторы. Первый элемент перемещается и поворачивается. Esc — выход."));
            connect.AddPushButton(Item("OVMepConnectLine", "Соединить\nпрямолинейные", typeof(Mep.MepConnectLineCommand),
                "Пакетное соединение прямолинейных участков одной прямой: удлиняются свободные концы, добавляются муфты и переходы."));
            connect.AddPushButton(Item("OVMepConnectDiffuser", "Подключить\nдиффузоры", typeof(Mep.MepDiffuserCommand),
                "Диффузор, затем воздуховод: врезка или тройник, при необходимости гибкий участок. Esc — выход."));
            connect.AddPushButton(Item("OVMepDisconnect", "Разъединить\nконнекторы", typeof(Mep.MepDisconnectCommand),
                "Разъединить ближайший к щелчку подключенный коннектор. Esc — выход."));
            connect.AddPushButton(Item("OVMepDetachBranch", "Отсоединить\nот системы", typeof(Mep.MepDetachBranchCommand),
                "Элемент ветки, затем элемент системы: соединения между ними разрываются, ветка выделяется."));

            var rotate = Split("OVMepRotate", "Вращение MEP");
            rotate.AddPushButton(Item("OVMepRotateConnector", "Вращение\nMEP элементов", typeof(Mep.MepRotateCommand),
                "Элемент (можно выбрать заранее), затем коннектор оси. Угол, направление, подключенные элементы; «Принять» или «Отмена»."));
            rotate.AddPushButton(Item("OVMepRotateOrient", "MEP вращение\nв направлении", typeof(Mep.MepOrientCommand),
                "Направить коннектор фитинга вдоль опорной MEP-кривой; при двух решениях — выбор направления."));
            rotate.AddPushButton(Item("OVMepRotateTray", "Лоток\nна ребро", typeof(Mep.TrayRotateCommand),
                "Повернуть выбранные кабельные лотки на 90° вокруг собственной оси."));

            var align = Split("OVMepAlign", "Выровнять");
            align.AddPushButton(Item("OVMepAlignAxis", "Выровнять", typeof(Mep.MepAlignAxisCommand),
                "Опорный элемент, затем выравниваемый: оси становятся соосными; горизонтальный элемент проходит через вертикальную опору и наоборот. У фитинга ось — по ближайшему коннектору. Esc — выход."));
            align.AddPushButton(Item("OVMepAlignHeight", "Выровнять\nпо высоте", typeof(Mep.MepAlignHeightCommand),
                "Опорный элемент, затем выравниваемый: перемещение по высоте до пересечения осей (параллельные — на одну отметку). Esc — выход."));
            align.AddPushButton(Item("OVMepAlignVertical", "Выровнять\nвертикально", typeof(Mep.MepAlignVerticalCommand),
                "Наклонные стояки (можно выделить заранее): нижний конец остаётся, верхний ставится строго над ним."));
            align.AddPushButton(Item("OVMepAlignMulti", "Мульти-\nпостроение", typeof(Mep.MepMultiRouteCommand),
                "Параллельные трассы и базовая, затем точки на плане: продолжение трасс с поворотом 10–90° и отводами, удлинение, сохранение уклона. Esc — завершить."));
            align.AddPushButton(Item("OVMepAlignDistance", "Задать\nрасстояние", typeof(Mep.MepAlignDistanceCommand),
                "Расстояние между осями или гранями (с изоляцией) по горизонтали или вертикали: базовый элемент, затем выравниваемые. История значений."));

            var updater = Split("OVMepUpdater", "Апдейтер");
            updater.AddPushButton(Item("OVMepUpdaterSettings", "Апдейтер\nпараметров", typeof(Mep.EngineeringRulesCommand),
                "Правила по категории и типу: конструктор наименования и расчёт количества с запасом, единицами и округлением."));
            updater.AddPushButton(Item("OVMepUpdaterRefresh", "Обновить\nпараметры", typeof(Mep.EngineeringRefreshCommand),
                "Применить сохранённые правила к выделенным элементам или, без выделения, ко всему проекту."));

            panel.AddItem(Item("OVMepInsulation", "Изоляция", typeof(Mep.InsulationCommand),
                "Тип и толщина изоляции для каждой трубы системы по ADSK_Наименование. «OK» строит изоляцию сразу, новые трубы получают её автоматически."));
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            Mep.InsulationUpdater.Unregister(application.ActiveAddInId);
            Mep.EngineeringUpdater.Unregister(application.ActiveAddInId);
            return Result.Succeeded;
        }
    }
}
