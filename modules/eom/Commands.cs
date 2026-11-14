using System.Text;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ComboBox = System.Windows.Controls.ComboBox;

namespace BotPlus.Eom;

[Transaction(TransactionMode.Manual)]
public sealed class ProjectSettingsCommand : EomCommand
{
    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document;
        if (ProjectSettingsWindow.Edit(EomStore.Settings(doc)) is not { } edit) return Result.Cancelled;
        using var t = new Transaction(doc, "ЭОМ: настройки проекта"); t.Start();
        string report = "Настройки сохранены в модели.";
        var settings = edit.Settings;
        if (edit.CreateLines)
        {
            int removed = WiringSymbols.RemoveOldGraphics(doc);
            var (ready, message) = WiringSymbols.Ensure(doc, ui.Application.Application);
            report += "\n" + (message == "" ? $"Обозначения проводок ГОСТ 21.210: семейство «{WiringSymbols.Family}» есть в проекте ({WiringSymbols.Types(doc).Count} типов)." : message);
            if (removed > 0) report += $"\nУдалены прежние образцы линий и фильтры трасс ЭОМ: {removed}.";
            settings = settings with { LinesCreated = ready };
        }
        EomStore.SaveSettings(doc, settings);
        t.Commit();
        TaskDialog.Show("BotPlus ЭОМ", report);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class CatalogCommand : EomCommand
{
    protected override Result Run(UIDocument ui) { CatalogWindow.Show(); return Result.Succeeded; }
}

[Transaction(TransactionMode.Manual)]
public sealed class EquipmentCommand : EomCommand
{
    protected override Result Run(UIDocument ui)
    {
        App.Equipment?.Refresh(ui.Document);
        ui.Application.GetDockablePane(App.EquipmentPaneId).Show();
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class LightingCommand : EomCommand
{
    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document;
        var rooms = Rooms.Collect(doc);
        if (rooms.Count == 0) { TaskDialog.Show("BotPlus ЭОМ", "В проекте и связях нет размещённых помещений."); return Result.Cancelled; }
        var symbols = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => s.Family.FamilyPlacementType is FamilyPlacementType.OneLevelBased or FamilyPlacementType.OneLevelBasedHosted)
            .Select(s => (s.Id, $"{s.FamilyName}: {s.Name}")).OrderBy(s => s.Item2).ToList();
        if (LightingWindow.Show(rooms, symbols) is not { } placement) return Result.Cancelled;
        var symbol = (FamilySymbol)doc.GetElement(placement.Symbol);
        using var t = new Transaction(doc, "ЭОМ: авторасстановка светильников"); t.Start();
        int total = 0; var problems = new List<string>();
        foreach (var row in placement.Rows)
        {
            int placed = Rooms.Place(doc, row.Room, symbol, row.Count, placement.MountM);
            total += placed;
            if (placed < row.Count) problems.Add($"{row.Number} {row.Name}: размещено {placed} из {row.Count} — контур слишком сложный для сетки.");
        }
        t.Commit();
        TaskDialog.Show("BotPlus ЭОМ", $"Размещено светильников: {total} в {placement.Rows.Count} помещениях." + (problems.Count > 0 ? "\n\n" + string.Join("\n", problems) : ""));
        return Result.Succeeded;
    }
}

/// <summary>Штатное построение лотка/короба Revit. Обозначение проводки ГОСТ 21.210 ставит IUpdater (см. RouteService).</summary>
public abstract class RouteCommand : EomCommand
{
    static readonly HashSet<string> warned = [];
    protected abstract PostableCommand Command { get; }
    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document;
        if (RouteService.Current != WiringSymbols.None)
        {
            // Семейство обозначений есть в шаблоне АДСК; в другом файле копируем его из открытого файла АДСК.
            using var t = new Transaction(doc, "ЭОМ: обозначения проводок ГОСТ"); t.Start();
            WiringSymbols.RemoveOldGraphics(doc);
            var (ready, message) = WiringSymbols.Ensure(doc, ui.Application.Application);
            t.Commit();
            if (!ready && warned.Add(doc.PathName + doc.Title))
                new TaskDialog("BotPlus ЭОМ") { MainInstruction = "Трасса будет построена без обозначения проводки", MainContent = message }.Show();
        }
        ui.Application.PostCommand(RevitCommandId.LookupPostableCommandId(Command));
        return Result.Succeeded;
    }
}
[Transaction(TransactionMode.Manual)] public sealed class TrayCommand : RouteCommand { protected override PostableCommand Command => PostableCommand.CableTray; }
[Transaction(TransactionMode.Manual)] public sealed class ConduitCommand : RouteCommand { protected override PostableCommand Command => PostableCommand.Conduit; }

/// <summary>Обозначает способ прокладки (ГОСТ 21.210) у всех лотков и коробов активного плана, перестраивает существующие обозначения.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class WiringCommand : EomCommand
{
    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document; var view = ui.ActiveView;
        if (view is not ViewPlan || view.IsTemplate) { TaskDialog.Show("BotPlus ЭОМ", "Откройте план этажа: обозначения проводок ставятся на плане."); return Result.Cancelled; }
        string choice = RouteService.Current == WiringSymbols.None ? WiringSymbols.Auto : RouteService.Current;
        using var t = new Transaction(doc, "ЭОМ: обозначения проводок ГОСТ"); t.Start();
        int removed = WiringSymbols.RemoveOldGraphics(doc);
        var (ready, message) = WiringSymbols.Ensure(doc, ui.Application.Application);
        if (!ready) { t.RollBack(); TaskDialog.Show("BotPlus ЭОМ", message); return Result.Cancelled; }
        var report = WiringSymbols.Mark(doc, view, choice);
        t.Commit();
        var text = new StringBuilder();
        if (message != "") text.AppendLine(message);
        text.AppendLine($"Способ прокладки: {choice}.");
        text.AppendLine($"Новых обозначений: {report.Added}, перестроено: {report.Updated}, пропущено: {report.Skipped} (вертикальные участки и короткие отрезки не обозначаются).");
        if (report.MissingTypes.Count > 0) text.AppendLine($"В семействе нет типов: {string.Join(", ", report.MissingTypes)}. Доступны: {string.Join(", ", WiringSymbols.Types(doc).Keys.Order())}.");
        if (report.Errors.Count > 0) text.AppendLine("Ошибки размещения:\n" + string.Join("\n", report.Errors));
        if (report.Added == 0 && WiringSymbols.LastError is { } last && !report.Errors.Contains(last)) text.AppendLine("Последняя ошибка при построении трассы: " + last);
        if (WiringSymbols.Types(doc).Values.FirstOrDefault() is { } sample) text.AppendLine($"Семейство «{WiringSymbols.Family}»: {WiringSymbols.Describe(sample)}.");
        if (new FilteredElementCollector(doc, view.Id).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().FirstOrDefault(i => i.Symbol?.FamilyName == WiringSymbols.Family) is { } placedOne)
            text.AppendLine(WiringSymbols.LengthParameter(placedOne) is { } length ? $"Длина обозначения задаётся параметром «{length.Definition.Name}»." : "Параметра длины в семействе нет: обозначение повторяется вдоль трассы.");
        if (removed > 0) text.AppendLine($"Удалены прежние образцы линий и фильтры трасс ЭОМ: {removed}.");
        TaskDialog.Show("BotPlus ЭОМ", text.ToString().TrimEnd());
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class ConnectCommand : EomCommand
{
    sealed class DevicesOnly : ISelectionFilter
    {
        public bool AllowElement(Element e) => e is FamilyInstance && e.Category is { } c && c.BuiltInCategory != BuiltInCategory.OST_ElectricalEquipment && Network.DeviceCategories.Contains(c.BuiltInCategory);
        public bool AllowReference(Reference r, XYZ p) => false;
    }
    sealed class PanelsOnly : ISelectionFilter
    {
        public bool AllowElement(Element e) => e is FamilyInstance && e.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalEquipment;
        public bool AllowReference(Reference r, XYZ p) => false;
    }
    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document;
        var filter = new DevicesOnly();
        var ids = ui.Selection.GetElementIds().Where(id => filter.AllowElement(doc.GetElement(id))).ToList();
        if (ids.Count == 0)
            ids = ui.Selection.PickObjects(ObjectType.Element, filter, "Выберите розетки, светильники и другие приборы группы и нажмите «Готово»").Select(r => r.ElementId).ToList();
        // Revit создаёт группу только из приборов со свободным силовым соединителем; остальные отсеиваем заранее и называем.
        var noConnector = new List<string>(); var busy = new List<string>(); var ready = new List<ElementId>();
        foreach (var id in ids)
        {
            var fi = (FamilyInstance)doc.GetElement(id);
            string name = $"{fi.Symbol.FamilyName}: {fi.Name} (ID {id.Value})";
            if (!Network.HasPowerConnector(fi)) noConnector.Add(name);
            else if (Network.InPowerCircuit(fi)) busy.Add(name);
            else ready.Add(id);
        }
        string Skipped()
        {
            var text = new StringBuilder();
            if (noConnector.Count > 0) text.Append($"\n\nБез силового электрического соединителя в семействе ({noConnector.Count}):\n" + string.Join("\n", noConnector.Take(8)) + (noConnector.Count > 8 ? "\n…" : ""));
            if (busy.Count > 0) text.Append($"\n\nУже подключены к другой группе ({busy.Count}):\n" + string.Join("\n", busy.Take(8)) + (busy.Count > 8 ? "\n…" : ""));
            return text.ToString();
        }
        if (ready.Count == 0)
        {
            TaskDialog.Show("BotPlus ЭОМ", "Подключать нечего: ни у одного выбранного прибора нет свободного силового соединителя." + Skipped() +
                "\n\nСемейство можно подключить к щиту, только если в нём есть электрический соединитель типа «Силовой». Обычные модели и аннотации без соединителя Revit в цепь не включает.");
            return Result.Cancelled;
        }
        var panel = (FamilyInstance)doc.GetElement(ui.Selection.PickObject(ObjectType.Element, new PanelsOnly(), "Выберите щит"));
        using var t = new Transaction(doc, "ЭОМ: подключение к щиту"); t.Start();
        ElectricalSystem system;
        try { system = ElectricalSystem.Create(doc, ready, ElectricalSystemType.PowerCircuit); }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            t.RollBack();
            TaskDialog.Show("BotPlus ЭОМ", "Revit не создал группу из выбранных приборов. Обычно причина — разное напряжение или число полюсов у соединителей. Выберите приборы с одинаковыми параметрами питания.");
            return Result.Cancelled;
        }
        try { system.SelectPanel(panel); }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            t.RollBack();
            TaskDialog.Show("BotPlus ЭОМ", "Щит не принял группу. Проверьте, что у щита задана распределительная система, есть силовой соединитель, а напряжение совпадает с напряжением приборов.");
            return Result.Cancelled;
        }
        var settings = EomStore.Settings(doc);
        t.Commit();
        App.Etm?.Refresh(doc);
        TaskDialog.Show("BotPlus ЭОМ", $"Группа {EomSettings.Mark(settings.GroupMask, system.PanelName, system.CircuitNumber)}: подключено приборов {ready.Count}." +
            (noConnector.Count + busy.Count > 0 ? "\n\nПропущено:" + Skipped() : ""));
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class EtmCommand : EomCommand
{
    protected override Result Run(UIDocument ui)
    {
        App.Etm?.Refresh(ui.Document);
        App.Etm?.ShowWindow(ui.Application.MainWindowHandle);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class LoadsCommand : EomCommand
{
    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document; var settings = EomStore.Settings(doc); var catalog = EomCatalog.Load();
        var net = Network.Read(doc, settings);
        var rows = new List<TableRow>();
        foreach (var p in net.Panels)
        {
            var load = EomCalc.PanelLoad(p.Circuits.Select(c => c.Input));
            double cos = load.S > 0 ? load.P / load.S : 1;
            rows.Add(new(p.Id, p.Name, "Итого по щиту", $"{load.P / 1000:0.###}", $"{load.Q / 1000:0.###}", $"{load.S / 1000:0.###}", $"{cos:0.00}", $"{EomCalc.Current(load.P, cos, 3, settings):0.#}", "", "—", ""));
            foreach (var c in p.Circuits)
            {
                var r = EomCalc.Select(c.Input, settings, catalog);
                rows.Add(new(c.Id, "", EomSettings.Mark(settings.GroupMask, p.Name, c.Input.Number) + " " + c.Input.Name, $"{c.Input.ActivePowerW / 1000:0.###}", "", "", $"{c.Input.PowerFactor:0.00}",
                    $"{r.CurrentA:0.#}", $"{r.VoltageDrop:0.##}", "—", r.Problem));
            }
        }
        TableWindow.Show("Нагрузки и КЗ",
            $"Расчёт по {settings.LoadNorm}, Кс = 1 (коэффициенты спроса по нормам — в разработке). ΔU — по подобранному кабелю с запасом длины {settings.CableReserve:0.#}%. Токи КЗ (ГОСТ 28249-93, петля «фаза-ноль») — в разработке.",
            ["Щит", "Группа", "Pр, кВт", "Qр, квар", "Sр, кВА", "cos φ", "Iр, А", "ΔU, %", "Iкз, кА", "Замечание"], rows, ui);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class SelectionCommand : EomCommand
{
    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document; var settings = EomStore.Settings(doc); var catalog = EomCatalog.Load();
        var net = Network.Read(doc, settings);
        var results = net.AllCircuits.Select(c => (Circuit: c, Result: EomCalc.Select(c.Input, settings, catalog))).ToList();
        var rows = results.Select(x => new TableRow(x.Circuit.Id, x.Circuit.Input.Panel, EomSettings.Mark(settings.GroupMask, x.Circuit.Input.Panel, x.Circuit.Input.Number),
            $"{x.Result.CurrentA:0.#}", x.Result.Breaker?.Name ?? "—", x.Result.Cable?.Name ?? "—", $"{x.Result.Cable?.AllowedCurrent:0}", $"{x.Result.CableLengthM:0.#}",
            $"{x.Result.VoltageDrop:0.##}", x.Result.Ok ? "" : x.Result.Problem));
        bool write = false;
        TableWindow.Show("Автоподбор защит и сечений",
            $"Автомат: ближайший In ≥ Iр. Кабель: минимальное сечение с Iдоп ≥ In и ΔU ≤ {settings.VoltageDropLimit:0.##}%. Заполнение лотков — в разработке. «Записать» сохранит номинал автомата в группу Revit и кабель в данные ЭОМ.",
            ["Щит", "Группа", "Iр, А", "Автомат", "Кабель", "Iдоп, А", "L, м", "ΔU, %", "Замечание"], rows, ui,
            ("Записать в модель", w => { write = true; w.Close(); }));
        if (!write) return Result.Cancelled;
        using var t = new Transaction(doc, "ЭОМ: автоподбор"); t.Start();
        int count = 0;
        foreach (var (c, r) in results)
        {
            if (r.Breaker == null || r.Cable == null) continue;
            var system = doc.GetElement(c.Id);
            var rating = system.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM);
            if (rating is { IsReadOnly: false }) rating.Set(UnitUtils.ConvertToInternalUnits(r.Breaker.Rating, UnitTypeId.Amperes));
            EomStore.SaveChoice(system, new(r.Cable.Name, r.Breaker.Name, r.CableLengthM));
            count++;
        }
        t.Commit();
        App.Etm?.Refresh(doc);
        TaskDialog.Show("BotPlus ЭОМ", $"Записано групп: {count} из {results.Count}.");
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class IndicatorsCommand : EomCommand
{
    protected override Result Run(UIDocument ui)
    {
        if (Indicators.Enabled) Indicators.Disable();
        else
        {
            int count = Indicators.Enable(ui.Document, Network.Read(ui.Document, EomStore.Settings(ui.Document)));
            if (count == 0) { Indicators.Disable(); TaskDialog.Show("BotPlus ЭОМ", "В проекте нет приборов с силовыми соединителями."); return Result.Cancelled; }
        }
        ui.RefreshActiveView();
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class ChecksCommand : EomCommand
{
    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document; var settings = EomStore.Settings(doc); var catalog = EomCatalog.Load();
        var net = Network.Read(doc, settings);
        var rows = new List<TableRow>();
        foreach (var d in net.Unconnected) rows.Add(new(d.Id, "Ошибка", "Прибор не подключён к сети", $"{d.Category}: {d.Name}"));
        foreach (var c in net.Orphans) rows.Add(new(c.Id, "Ошибка", "Группа без щита", $"Группа {c.Input.Number} {c.Input.Name}"));
        foreach (var c in net.AllCircuits)
        {
            var r = EomCalc.Select(c.Input, settings, catalog);
            string name = EomSettings.Mark(settings.GroupMask, c.Input.Panel, c.Input.Number);
            if (!r.Ok) rows.Add(new(c.Id, "Ошибка", "Подбор невозможен", $"{name}: {r.Problem}"));
            if (c.RatingA > 0 && c.RatingA < r.CurrentA) rows.Add(new(c.Id, "Ошибка", "Автомат меньше расчётного тока", $"{name}: In {c.RatingA:0} А < Iр {r.CurrentA:0.#} А"));
            var chosen = catalog.Cables.FirstOrDefault(x => x.Name == c.Cable);
            if (chosen != null && c.RatingA > 0 && chosen.AllowedCurrent < c.RatingA) rows.Add(new(c.Id, "Ошибка", "Кабель не защищён автоматом", $"{name}: Iдоп {chosen.AllowedCurrent:0} А < In {c.RatingA:0} А"));
            if (chosen != null)
            {
                double drop = EomCalc.VoltageDrop(r.CurrentA, r.CableLengthM, chosen, c.Input.PowerFactor, c.Input.Poles, settings);
                if (drop > settings.VoltageDropLimit) rows.Add(new(c.Id, "Ошибка", "Превышение ΔU", $"{name}: {drop:0.##}% > {settings.VoltageDropLimit:0.##}%"));
            }
            if (c.Input.LengthM <= 0) rows.Add(new(c.Id, "Предупреждение", "Нет длины группы", $"{name}: Revit не рассчитал длину — проверьте путь цепи"));
        }
        if (rows.Count == 0) rows.Add(new(null, "OK", "Ошибок не найдено", ""));
        TableWindow.Show("Мастер проверок",
            "Проверяются подключение приборов, группы без щита, автоматы и сечения, потеря напряжения. Несрабатывание автомата при КЗ, переполнение лотков и разрывы трасс — в разработке. Двойной щелчок — показать в модели.",
            ["Уровень", "Проверка", "Описание"], rows, ui);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class SingleLineCommand : EomCommand
{
    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document; var settings = EomStore.Settings(doc);
        var panels = Network.Read(doc, settings).Panels.Where(p => p.Circuits.Count > 0).ToList();
        if (panels.Count == 0) { TaskDialog.Show("BotPlus ЭОМ", "В проекте нет щитов с подключёнными группами. Подключите приборы командой «Подключить к щиту»."); return Result.Cancelled; }
        var window = EomUi.Window("Однолинейная схема", 480, 260);
        var list = new ComboBox { Width = 300, ItemsSource = panels.Select(p => $"{p.Name} — групп: {p.Circuits.Count}").ToList(), SelectedIndex = 0 };
        var sheet = new CheckBox { Content = "Разместить на новом листе", IsChecked = true, Margin = new System.Windows.Thickness(0, 8, 0, 0) };
        var body = new StackPanel(); body.Children.Add(EomUi.Row("Щит", list)); body.Children.Add(sheet);
        bool ok = false;
        EomUi.Layout(window, body, EomUi.Hint("Создаётся чертёжный вид: шина, отходящие линии с аппаратами и таблица групп по данным ЭТМ."),
            EomUi.Buttons(EomUi.Button("Создать", () => { ok = true; window.Close(); }, true), EomUi.Button("Отмена", window.Close)));
        window.ShowDialog();
        if (!ok) return Result.Cancelled;
        using var t = new Transaction(doc, "ЭОМ: однолинейная схема"); t.Start();
        var view = SingleLine.Draw(doc, panels[list.SelectedIndex], settings, EomCatalog.Load());
        View target = sheet.IsChecked == true ? SingleLine.PlaceOnSheet(doc, view) : view;
        t.Commit();
        ui.ActiveView = target;
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class JournalCommand : EomCommand
{
    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document; var settings = EomStore.Settings(doc); var catalog = EomCatalog.Load();
        var net = Network.Read(doc, settings);
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV для Excel (*.csv)|*.csv", FileName = $"{doc.Title} — кабельный журнал.csv" };
        if (dialog.ShowDialog() != true) return Result.Cancelled;
        static string Q(string v) => v.Contains(';') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var csv = new StringBuilder();
        csv.AppendLine("Кабельный журнал");
        csv.AppendLine("Марка кабеля;Откуда;Куда;Кабель;Длина с запасом, м;Автомат;Pу, кВт;Iр, А;ΔU, %");
        foreach (var c in net.AllCircuits)
        {
            var r = EomCalc.Select(c.Input, settings, catalog);
            string cable = c.Cable.Length > 0 ? c.Cable : r.Cable?.Name ?? "";
            string to = string.Join(", ", c.Devices.Select(d => d.Name).Distinct().Take(3)) + (c.Devices.Count > 3 ? " …" : "");
            csv.AppendLine(string.Join(";", new[] { EomSettings.Mark(settings.CableMask, c.Input.Panel, c.Input.Number), c.Input.Panel, to, cable, r.CableLengthM.ToString("0.#"),
                c.RatingA > 0 ? $"{c.RatingA:0} А" : r.Breaker?.Name ?? "", (c.Input.ActivePowerW / 1000).ToString("0.###"), r.CurrentA.ToString("0.#"), r.VoltageDrop.ToString("0.##") }.Select(Q)));
        }
        csv.AppendLine();
        csv.AppendLine("Таблица щитов");
        csv.AppendLine("Щит;Групп;Pр, кВт;Qр, квар;Sр, кВА");
        foreach (var p in net.Panels)
        {
            var load = EomCalc.PanelLoad(p.Circuits.Select(c => c.Input));
            csv.AppendLine(string.Join(";", Q(p.Name), p.Circuits.Count, (load.P / 1000).ToString("0.###"), (load.Q / 1000).ToString("0.###"), (load.S / 1000).ToString("0.###")));
        }
        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
        TaskDialog.Show("BotPlus ЭОМ", $"Кабельный журнал сохранён: {dialog.FileName}\nГрупп: {net.AllCircuits.Count()}, щитов: {net.Panels.Count}.");
        return Result.Succeeded;
    }
}
