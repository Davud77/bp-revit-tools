using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BotPlus.Eom;

/// <summary>
/// Модуль ЭОМ: своя вкладка «BotPlus ЭОМ» с шестью панелями по этапам работы — проект, оборудование, трассы, ЭТМ, проверки, документация.
/// В настройках плагина вся вкладка включается и выключается одной галочкой «ЭОМ».
/// </summary>
public sealed class App : IExternalApplication
{
    internal const string Tab = BotPlusSettings.EomTab;
    internal static readonly DockablePaneId EquipmentPaneId = new(new Guid("8D2A6F14-3B7E-4C59-A1D0-6E9F2C4B7A35"));
    internal static EtmPane? Etm;
    internal static EquipmentPane? Equipment;
    internal static RevitQueue? Queue;

    public Result OnStartup(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(Tab); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        string path = typeof(App).Assembly.Location;
        PushButtonData B(string id, string text, Type command, string tip) =>
            new("BotPlusEom" + id, text, path, command.FullName!) { ToolTip = tip, AvailabilityClassName = typeof(EomAvailable).FullName! };

        var project = app.CreateRibbonPanel(Tab, "Проект и БД");
        project.AddItem(B("Settings", "Настройки\nпроекта", typeof(ProjectSettingsCommand),
            "Нормы расчёта, запас длины кабеля, допустимая ΔU и маски маркировки. Проверяет семейство условных обозначений проводок ГОСТ 21.210."));
        project.AddStackedItems(
            B("Catalog", "Менеджер БД", typeof(CatalogCommand), "Каталоги кабелей, автоматов и УЗО, лотков и светильников."),
            B("Import", "Импорт ТЗ", typeof(ImportTaskCommand), Planned.Tip(Planned.ImportTask)));

        var equipment = app.CreateRibbonPanel(Tab, "Оборудование и светотехника");
        equipment.AddItem(B("Equipment", "База\nУГО", typeof(EquipmentCommand),
            "Панель с загруженными семействами ЭОМ (розетки, выключатели, щиты, светильники) и их эскизами. Перетащите тип на план — начнётся размещение."));
        equipment.AddItem(B("Lighting", "Расчёт\nосвещённости", typeof(LightingCommand),
            "Расчёт числа светильников методом коэффициента использования по помещениям проекта и связей АР."));
        equipment.AddStackedItems(
            B("Layout", "Авторасстановка", typeof(LightingCommand), "Расстановка светильников сеткой внутри помещения. Открывает расчёт освещённости: выберите помещения и нажмите «Расставить»."),
            B("Isolux", "Изолюксы", typeof(IsoluxCommand), Planned.Tip(Planned.Isolux)));

        var routes = app.CreateRibbonPanel(Tab, "Трассы и подключение");
        routes.AddStackedItems(
            B("RouteTray", "Лоток", typeof(TrayCommand), "Построение кабельного лотка штатной командой Revit. Вдоль нового лотка на плане ставится обозначение проводки ГОСТ 21.210 по способу прокладки, выбранному в строке параметров во время построения."),
            B("RouteConduit", "Короб, труба", typeof(ConduitCommand), "Построение короба или трубы штатной командой Revit. Вдоль нового короба на плане ставится обозначение проводки ГОСТ 21.210 по способу прокладки, выбранному в строке параметров во время построения."),
            B("Wiring", "Обозначения проводок", typeof(WiringCommand), "Условные обозначения проводок ГОСТ 21.210 у всех лотков и коробов активного плана по способу прокладки, выбранному последним в строке параметров (по умолчанию «Авто»). Существующие обозначения перестраиваются по трассам."));
        routes.AddItem(B("Connect", "Подключить\nк щиту", typeof(ConnectCommand),
            "Выберите приборы, затем щит: создаётся группа (цепь) Revit, приборы подключаются к щиту."));
        routes.AddStackedItems(
            B("Riser", "Стояки", typeof(RiserCommand), Planned.Tip(Planned.Riser)),
            B("Cable", "Автотрассировка", typeof(AutoRouteCommand), Planned.Tip(Planned.AutoRoute)));

        var etm = app.CreateRibbonPanel(Tab, "ЭТМ и расчёты");
        etm.AddItem(B("Etm", "Менеджер\nЭТМ", typeof(EtmCommand), "Схема сети в отдельном окне: источник, щиты, автоматы групп, нижестоящие щиты и неподключённое оборудование."));
        etm.AddStackedItems(
            B("Loads", "Нагрузки и КЗ", typeof(LoadsCommand), "Расчётные мощности щитов, токи групп и потери напряжения."),
            B("Protection", "Автоподбор", typeof(SelectionCommand), "Подбор автоматов и сечений кабелей из каталога по току и потере напряжения."));

        var checks = app.CreateRibbonPanel(Tab, "Индикация и проверки");
        checks.AddItem(B("Indicators", "Индикаторы\nстатуса", typeof(IndicatorsCommand),
            "Включает или выключает цветные точки у приборов: статус подключения и цвет группы. Модель не меняется."));
        checks.AddItem(B("Checks", "Мастер\nпроверок", typeof(ChecksCommand), "Неподключённые приборы, группы без щита, превышение ΔU и недостаточные сечения."));

        var docs = app.CreateRibbonPanel(Tab, "Документация");
        docs.AddItem(B("SingleLine", "Однолинейная\nсхема", typeof(SingleLineCommand), "Чертёжный вид с однолинейной схемой выбранного щита по данным ЭТМ."));
        docs.AddStackedItems(
            B("Journal", "Кабельный журнал", typeof(JournalCommand), "Выгрузка кабельного журнала и таблиц щитов в CSV для Excel."),
            B("Spec", "Спецификация", typeof(SpecCommand), Planned.Tip(Planned.Spec)));
        docs.AddStackedItems(
            B("Report", "Отчёты", typeof(ReportCommand), Planned.Tip(Planned.Report)),
            B("Comb", "Гребёнка", typeof(CombCommand), Planned.Tip(Planned.Comb)));

        Etm = new EtmPane();
        Equipment = new EquipmentPane();
        app.RegisterDockablePane(EquipmentPaneId, "BotPlus База УГО", Equipment);
        app.ViewActivated += OnViewActivated;
        Queue = new RevitQueue();
        RouteService.Register(app);
        app.Idling += RouteService.OnIdling;
        app.SelectionChanged += RouteService.OnSelectionChanged;
        RouteOptionsBar.Start(app.MainWindowHandle);
        return Result.Succeeded;
    }

    /// <summary>База УГО следует за активным проектом.</summary>
    static void OnViewActivated(object? sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
    {
        try { Equipment?.Follow(e.Document); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
    }

    public Result OnShutdown(UIControlledApplication app)
    {
        app.Idling -= RouteService.OnIdling;
        app.ViewActivated -= OnViewActivated;
        app.SelectionChanged -= RouteService.OnSelectionChanged;
        RouteOptionsBar.Stop();
        RouteService.Unregister();
        Indicators.Disable();
        Queue?.Dispose(); Queue = null;
        return Result.Succeeded;
    }
}

/// <summary>Кнопки ЭОМ доступны только в проекте (не в семействе).</summary>
public sealed class EomAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication app, CategorySet categories) =>
        app.ActiveUIDocument?.Document is { IsFamilyDocument: false };
}

/// <summary>Общая обёртка команд: проверка документа и единый вывод ошибок.</summary>
public abstract class EomCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        if (ui == null || ui.Document.IsFamilyDocument) { message = "Откройте проект Revit."; return Result.Cancelled; }
        try { return Run(ui); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        catch (Exception error) { TaskDialog.Show("BotPlus ЭОМ", error.Message); return Result.Failed; }
    }
    protected abstract Result Run(UIDocument ui);
}

/// <summary>Кнопка будущей функции: показывает, что она будет делать и на чём будет построена.</summary>
public abstract class PlannedCommand : EomCommand
{
    protected abstract (string Title, string What, string How) Info { get; }
    protected override Result Run(UIDocument ui)
    {
        var (title, what, how) = Info;
        new TaskDialog("BotPlus ЭОМ") { MainInstruction = title, MainContent = what, ExpandedContent = "Реализация: " + how, FooterText = "Функция запланирована и пока не работает." }.Show();
        return Result.Cancelled;
    }
}

internal static class Planned
{
    internal static readonly (string, string, string) ImportTask = ("Импорт ТЗ (XML / Excel)",
        "Загрузка технологического задания смежников (ОВ, ВК, ТХ): мощность, cos φ, категория надёжности. Параметры электроприёмников создаются или обновляются.",
        "чтение XML/XLSX, сопоставление по марке/ID, Parameter.Set в транзакции, отчёт об изменениях.");
    internal static readonly (string, string, string) Isolux = ("Изолюксы",
        "Градиент освещённости на полу помещения без создания элементов модели.",
        "Analysis Visualization Framework: SpatialFieldManager, AnalysisDisplayColoredSurfaceSettings, точечный метод по IES.");
    internal static readonly (string, string, string) Riser = ("Межэтажные переходы (стояки)",
        "Связь вертикальных участков трасс между уровнями для сквозной трассировки.",
        "граф соединителей CableTray/Conduit, общие идентификаторы стояка в Extensible Storage.");
    internal static readonly (string, string, string) AutoRoute = ("Автотрассировка кабеля",
        "Поиск кратчайшего пути по графу лотков и коробов, а где их нет — ортогонально по стенам и перекрытиям. Длина: Lгор + ΔZ + запас.",
        "граф из ConnectorManager трасс, алгоритм Дейкстры, ReferenceIntersector для обхода конструкций.");
    internal static readonly (string, string, string) Spec = ("Спецификация ГОСТ 21.110",
        "Оборудование, кабели и доукомплектование трасс консолями, крышками, соединителями и метизами из БД.",
        "ViewSchedule + расчётные параметры, правила комплектации в каталоге.");
    internal static readonly (string, string, string) Report = ("Отчёты для экспертизы",
        "Расчёты нагрузок, токов КЗ и освещённости со всеми формулами в Word/PDF.",
        "шаблон DOCX (OpenXML), формулы из ядра расчёта, экспорт PDF.");
    internal static readonly (string, string, string) Comb = ("Специальная выноска (гребёнка)",
        "Многополочные выноски к пучкам кабелей и трассам по настраиваемой маске.",
        "семейство марки с полками, IndependentTag + 2D-аннотация, маски из настроек проекта.");
    internal static string Tip((string Title, string What, string How) info) => info.What + " (в разработке)";
}

[Transaction(TransactionMode.Manual)] public sealed class ImportTaskCommand : PlannedCommand { protected override (string, string, string) Info => Planned.ImportTask; }
[Transaction(TransactionMode.Manual)] public sealed class IsoluxCommand : PlannedCommand { protected override (string, string, string) Info => Planned.Isolux; }
[Transaction(TransactionMode.Manual)] public sealed class RiserCommand : PlannedCommand { protected override (string, string, string) Info => Planned.Riser; }
[Transaction(TransactionMode.Manual)] public sealed class AutoRouteCommand : PlannedCommand { protected override (string, string, string) Info => Planned.AutoRoute; }
[Transaction(TransactionMode.Manual)] public sealed class SpecCommand : PlannedCommand { protected override (string, string, string) Info => Planned.Spec; }
[Transaction(TransactionMode.Manual)] public sealed class ReportCommand : PlannedCommand { protected override (string, string, string) Info => Planned.Report; }
[Transaction(TransactionMode.Manual)] public sealed class CombCommand : PlannedCommand { protected override (string, string, string) Info => Planned.Comb; }
