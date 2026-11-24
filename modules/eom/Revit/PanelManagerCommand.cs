using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using System.ComponentModel;
using Binding = System.Windows.Data.Binding;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

namespace BotPlus.Eom;

[Transaction(TransactionMode.Manual)]
public sealed class PanelManagerCommand : EomCommand
{
    sealed record PanelRow(PanelInfo Panel, string Label, string Floor, double Elevation, string Room);
    sealed record GroupRow(CircuitInfo Circuit, string Number, string Load, string Power, string Demand, string Apparent, string Current, string Voltage, string Leakage, string Phase,
        string Poles, string Length, string Breaker, string Cable, string Drop, string Status,
        bool Reserve, string Cos, string ModelBreaker, string SelectedBreaker, string ModelCable, string SelectedCable);

    protected override Result Run(UIDocument ui)
    {
        var doc = ui.Document;
        var settings = EomStore.Settings(doc);
        var catalog = EomCatalog.Load();
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .Select(link => (Document: link.GetLinkDocument(), Inverse: link.GetTotalTransform().Inverse)).Where(x => x.Document != null).ToList();
        (Autodesk.Revit.DB.Architecture.Room? Room, Transform? Transform) LocateRoom(FamilyInstance instance, XYZ? point)
        {
            var room = instance.Room;
            Transform? roomTransform = null;
            if (room == null && point != null)
            {
                room = doc.GetRoomAtPoint(point);
                foreach (var link in links)
                {
                    if (room != null) break;
                    room = link.Document!.GetRoomAtPoint(link.Inverse.OfPoint(point));
                    if (room != null) roomTransform = link.Inverse.Inverse;
                }
            }
            return (room, roomTransform);
        }
        PanelRow Locate(PanelInfo panel)
        {
            var instance = (FamilyInstance)doc.GetElement(panel.Id);
            var level = doc.GetElement(instance.LevelId) as Level;
            if (level == null && instance.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM) is { StorageType: StorageType.ElementId } p)
                level = doc.GetElement(p.AsElementId()) as Level;
            if (level == null && instance.Host is { } host && host.Document.Equals(doc))
                level = host as Level ?? doc.GetElement(host.LevelId) as Level;
            var (room, roomTransform) = LocateRoom(instance, Network.Point(instance));
            if (level == null && room?.Document.Equals(doc) == true) level = room.Level;
            var roomLabel = room == null ? "Без помещения" : $"{room.Number} {room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name}".Trim();
            string? floorName = level?.Name;
            double? elevation = level?.ProjectElevation;
            if (elevation == null && roomTransform != null && room?.Level is { } linkedLevel)
            {
                floorName = $"{linkedLevel.Name} · {room.Document.Title}";
                elevation = roomTransform.OfPoint(new XYZ(0, 0, linkedLevel.ProjectElevation)).Z;
            }
            string floor = elevation is { } z ? $"{floorName} ({UnitUtils.ConvertFromInternalUnits(z, UnitTypeId.Meters):+0.###;-0.###;0} м)" : "Без уровня";
            return new(panel, $"{panel.Name} · {panel.Circuits.Count} гр.\n{panel.Type} · ID {panel.Id.Value}", floor, elevation ?? double.PositiveInfinity, roomLabel);
        }
        List<PanelRow> ReadPanels() => Network.Read(doc, settings).Panels.Where(p => p.Circuits.Count > 0 || doc.GetElement(p.Id) is FamilyInstance instance &&
            (instance.MEPModel is ElectricalEquipment { IsSwitchboard: true }
             || instance.get_Parameter(BuiltInParameter.RBS_ELEC_MAX_POLE_BREAKERS) is { StorageType: StorageType.Integer } slots && slots.AsInteger() > 0
             || !string.IsNullOrWhiteSpace(instance.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString())))
            .Select(Locate).OrderBy(p => p.Elevation).ThenBy(p => p.Floor, NaturalComparer.Instance)
            .ThenBy(p => p.Room == "Без помещения").ThenBy(p => p.Room, NaturalComparer.Instance).ThenBy(p => p.Panel.Name, NaturalComparer.Instance).ToList();
        var panels = ReadPanels();

        string Feed(PanelInfo panel)
        {
            if (panel.FeedCircuit is not { } id || doc.GetElement(id) is not ElectricalSystem system) return "Вводная цепь не назначена";
            var choice = EomStore.CircuitChoice(system);
            string source = panel.FeedPanel == null ? "Группа без питающего щита" : system.PanelName;
            double? rating = Value(system, BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM, UnitTypeId.Amperes);
            string? protection = choice?.Breaker ?? (rating > 0 ? $"{rating:0.##} А" : null);
            return string.Join(" · ", new[] { source, system.CircuitNumber, protection, choice?.Cable }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        GroupRow Row(CircuitInfo c, PanelCalculation.Feeder calculation, string phase)
        {
            var input = c.Input;
            var element = doc.GetElement(c.Id);
            double? volts = Value(element, BuiltInParameter.RBS_ELEC_VOLTAGE, UnitTypeId.Volts);
            bool reserve = element is ElectricalSystem { CircuitType: CircuitType.Spare };
            var saved = EomStore.CircuitChoice(element);
            string modelBreaker = saved?.Breaker ?? (c.RatingA > 0 ? $"{c.RatingA:0.##} А" : "—");
            string modelCable = saved?.Cable ?? "—";
            string selectedBreaker = reserve ? "—" : calculation.Breaker?.Name ?? "—";
            string selectedCable = !reserve && calculation.Cable is { } recommendation
                ? $"{(calculation.CableCount > 1 ? $"{calculation.CableCount} × " : "")}{recommendation.Name}" : "—";
            string breaker = selectedBreaker == "—" ? modelBreaker : $"{modelBreaker} → {selectedBreaker}";
            string cable = selectedCable == "—" ? modelCable : $"{modelCable} → {selectedCable}";
            return new(c, EomSettings.Mark(settings.GroupMask, input.Panel, input.Number), reserve ? "Резерв" : input.Name,
                Format(calculation.Installed), Format(calculation.Demand), Format(calculation.Apparent), Format(calculation.Current), Format(volts),
                calculation.Length > 0 && !reserve ? Format(calculation.Leakage) : "—", phase, input.Poles.ToString(),
                calculation.Length > 0 ? Format(calculation.Length) : "—", breaker, cable, Format(calculation.Drop), reserve ? "Резерв Revit" : calculation.Note,
                reserve, Format(input.PowerFactor is > 0 and <= 1 ? input.PowerFactor : null), modelBreaker, selectedBreaker, modelCable, selectedCable);
        }

        var window = EomUi.Window("Менеджер щитов", 1450, 790);
        window.MinWidth = 860;
        var search = new TextBox { Margin = new Thickness(0, 4, 0, 8), ToolTip = "Этаж, помещение, имя, семейство или ID щита" };
        var list = new ListBox { DisplayMemberPath = nameof(PanelRow.Label) };
        var groupHeader = new FrameworkElementFactory(typeof(TextBlock));
        groupHeader.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        groupHeader.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        groupHeader.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 7, 0, 3));
        list.GroupStyle.Add(new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = groupHeader } });
        var panelName = EomUi.Heading("");
        var metrics = EomUi.Hint("");
        var demandBox = EomUi.Number(1);
        var simulatePhases = new CheckBox { Content = "Рассчитать баланс фаз", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var demandByPanel = new Dictionary<ElementId, double>();
        var demandByCircuit = new Dictionary<ElementId, double>();
        ElementId? activePanel = null;
        PanelDiagram.Summary? diagramSummary = null;
        var groups = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
        void Column(string label, string property, int width) => groups.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(property), Width = width });
        Column("Группа", nameof(GroupRow.Number), 100);
        Column("Нагрузка", nameof(GroupRow.Load), 165);
        Column("P, кВт", nameof(GroupRow.Power), 68);
        Column("Pр, кВт", nameof(GroupRow.Demand), 68);
        Column("Sр, кВА", nameof(GroupRow.Apparent), 68);
        Column("I, А", nameof(GroupRow.Current), 65);
        Column("U, В", nameof(GroupRow.Voltage), 65);
        Column("Утечка, мА", nameof(GroupRow.Leakage), 83);
        Column("Фаза", nameof(GroupRow.Phase), 64);
        Column("Полюсы", nameof(GroupRow.Poles), 62);
        Column("Длина, м", nameof(GroupRow.Length), 74);
        Column("Защита", nameof(GroupRow.Breaker), 125);
        Column("Кабель", nameof(GroupRow.Cable), 132);
        Column("ΔU, %", nameof(GroupRow.Drop), 68);
        Column("Примечание", nameof(GroupRow.Status), 300);
        var devices = new ListBox { Height = 115, DisplayMemberPath = nameof(DeviceInfo.Name) };
        var deviceLabel = EomUi.Heading("Потребители группы");
        var deleteReserve = EomUi.Button("Удалить резерв…", RemoveReserve);
        deleteReserve.IsEnabled = false;

        PanelInfo SelectedPanel() => (list.SelectedItem as PanelRow)?.Panel ?? throw new InvalidOperationException("Выберите щит.");
        void RefreshPanel()
        {
            if (list.SelectedItem is not PanelRow row)
            {
                panelName.Text = panels.Count == 0 ? "Щиты в проекте не найдены" : "Нет щитов по запросу";
                metrics.Text = ""; groups.ItemsSource = null; diagramSummary = null; return;
            }
            var p = row.Panel;
            var element = doc.GetElement(p.Id);
            string Kva(BuiltInParameter parameter) => Format(Value(element, parameter, UnitTypeId.VoltAmperes) / 1000);
            string Amps(BuiltInParameter parameter) => Format(Value(element, parameter, UnitTypeId.Amperes));
            if (!demandByPanel.TryGetValue(p.Id, out double demandFactor))
            {
                var factor = element.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_TOTAL_DEMAND_FACTOR_PARAM)?.AsDouble();
                demandFactor = factor is > 0 and <= 1 ? factor.Value : 1;
            }
            if (activePanel != p.Id) { demandBox.Text = demandFactor.ToString("0.###"); activePanel = p.Id; }
            int? phaseCount = element.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NUMPHASES_PARAM) is { StorageType: StorageType.Integer } phaseParameter
                ? phaseParameter.AsInteger() : null;
            var feeders = p.Circuits.Where(c => doc.GetElement(c.Id) is not ElectricalSystem { CircuitType: CircuitType.Space }).Select(c =>
            {
                var system = (ElectricalSystem)doc.GetElement(c.Id);
                double voltage = Value(system, BuiltInParameter.RBS_ELEC_VOLTAGE, UnitTypeId.Volts) ?? 0;
                bool reserve = system.CircuitType == CircuitType.Spare;
                return (Circuit: c, System: system, Reserve: reserve,
                    Calculation: PanelCalculation.Calculate(reserve ? c.Input with { ActivePowerW = 0, LengthM = 0 } : c.Input,
                        voltage, demandByCircuit.GetValueOrDefault(c.Id, demandFactor), settings, catalog, feedsPanel: c.Kind == CircuitKind.Feeder));
            }).ToList();
            var working = feeders.Where(f => !f.Reserve).ToList();
            double installed = working.Sum(f => f.Calculation.Installed);
            double demand = working.Sum(f => f.Calculation.Demand);
            double apparent = working.Sum(f => f.Calculation.Apparent);
            double largest = working.Select(f => f.Circuit.Kind == CircuitKind.Feeder ? f.Calculation.Demand : f.Calculation.Installed).DefaultIfEmpty().Max();
            if (largest > demand)
            {
                demand = largest;
                apparent = working.Max(f => f.Circuit.Kind == CircuitKind.Feeder ? f.Calculation.Apparent
                    : f.Circuit.Input.PowerFactor is > 0 and <= 1 ? f.Calculation.Installed / f.Circuit.Input.PowerFactor : 0);
            }
            var phase = new double[3];
            var assigned = new Dictionary<ElementId, string>();
            var single = new List<(ElementId Id, double Current)>();
            int withoutPhase = 0;
            double effectiveFactor = installed > 0 ? Math.Round(demand / installed, 2) : 0;
            foreach (var f in working.Where(f => f.Calculation.Current > 0))
            {
                double current = f.Calculation.Current * (f.Circuit.Kind == CircuitKind.Feeder ? 1 : effectiveFactor);
                if (f.Circuit.Input.Poles == 3)
                { for (int i = 0; i < 3; i++) phase[i] += current; assigned[f.Circuit.Id] = "L1–L3"; continue; }
                if (phaseCount == 1) { phase[0] += current; assigned[f.Circuit.Id] = "L1"; continue; }
                if (simulatePhases.IsChecked == true && phaseCount == 3) { single.Add((f.Circuit.Id, current)); continue; }
                double[] sourcePhases = [Value(f.System, BuiltInParameter.RBS_ELEC_APPARENT_CURRENT_PHASEA_PARAM, UnitTypeId.Amperes) ?? 0,
                    Value(f.System, BuiltInParameter.RBS_ELEC_APPARENT_CURRENT_PHASEB_PARAM, UnitTypeId.Amperes) ?? 0,
                    Value(f.System, BuiltInParameter.RBS_ELEC_APPARENT_CURRENT_PHASEC_PARAM, UnitTypeId.Amperes) ?? 0];
                int index = Array.IndexOf(sourcePhases, sourcePhases.Max());
                if (sourcePhases[index] <= 0) { withoutPhase++; continue; }
                phase[index] += current;
                assigned[f.Circuit.Id] = $"L{index + 1}";
            }
            if (single.Count > 0)
            {
                var (assignment, totals) = PanelCalculation.Balance(single.Select(f => f.Current).ToList(), phase[0]);
                phase = totals;
                for (int i = 0; i < assignment.Length; i++) assigned[single[i].Id] = $"L{assignment[i] + 1}";
            }
            double leakage = working.Sum(f => f.Calculation.Leakage * (f.Circuit.Input.Poles == 3 ? 1 : 1.0 / 3));
            double largestBreaker = feeders.Select(f => Math.Max(f.Circuit.RatingA, f.Calculation.Breaker?.Rating ?? 0)).DefaultIfEmpty().Max();
            double inlet = Math.Max(phase.Max(), (largestBreaker + 0.5) / 1.25);
            int? inletPoles = p.FeedCircuit is { } feed && doc.GetElement(feed) is ElectricalSystem input ? input.PolesNumber : phaseCount;
            int invalidInputs = working.Count(f => f.Calculation.Installed > 0 && f.Calculation.Current <= 0);
            var inputBreaker = inletPoles is 1 or 3 && withoutPhase == 0 && invalidInputs == 0 && installed > 0
                && inletPoles is { } poles
                ? PanelCalculation.SelectBreaker(inlet, poles, catalog) : null;
            var distribution = (element as FamilyInstance)?.MEPModel is ElectricalEquipment equipment
                ? equipment.DistributionSystem : null;
            var project = doc.ProjectInformation;
            diagramSummary = new(p.Name, $"{row.Floor} · {row.Room}", Feed(p), inputBreaker?.Name ?? "—",
                [$"Pу = {Format(installed)} кВт", $"Pр = {Format(demand)} кВт", $"Sр = {Format(apparent)} кВА"],
                Enumerable.Range(0, phaseCount == 1 ? 1 : 3).Select(i => $"Iр L{i + 1} = {Format(phase[i])} А").ToArray(),
                phaseCount ?? 0, distribution?.NumWires > phaseCount, project?.Number ?? "", project?.Name ?? "", project?.OrganizationName ?? "");
            panelName.Text = $"{p.Name}  ·  {p.Circuits.Count} отходящих групп";
            metrics.Text = $"Ввод: {Feed(p)}\n" +
                $"Расчёт по формулам «элпро»   ·   Pу {installed:0.###} кВт   ·   Pр {demand:0.###} кВт   ·   Sр {apparent:0.###} кВА   ·   cos φ {(apparent > 0 ? demand / apparent : 0):0.##}\n" +
                $"Токи групп по фазам, А   ·   L1 {phase[0]:0.###}   L2 {phase[1]:0.###}   L3 {phase[2]:0.###}   ·   Iут ввода {leakage:0.###} мА   ·   Перекос {phase.Max() - phase.Min():0.###} А   ·   Ввод {inputBreaker?.Name ?? "—"}" +
                (withoutPhase > 0 ? $"   ·   Групп без фазовых данных: {withoutPhase}" : "") +
                (invalidInputs > 0 ? $"   ·   Групп без исходных данных: {invalidInputs}" : "") +
                $"\nДанные Revit   ·   Sр {Kva(BuiltInParameter.RBS_ELEC_PANEL_TOTALESTLOAD_PARAM)} кВА   ·   L1 {Amps(BuiltInParameter.RBS_ELEC_DEMAND_CURRENT_PHASEA)}   L2 {Amps(BuiltInParameter.RBS_ELEC_DEMAND_CURRENT_PHASEB)}   L3 {Amps(BuiltInParameter.RBS_ELEC_DEMAND_CURRENT_PHASEC)} А";
            var selected = (groups.SelectedItem as GroupRow)?.Circuit.Id;
            var rows = feeders.Select(f => Row(f.Circuit, f.Calculation, assigned.GetValueOrDefault(f.Circuit.Id, "—"))).ToList();
            groups.ItemsSource = rows;
            groups.SelectedItem = rows.FirstOrDefault(g => g.Circuit.Id == selected) ?? rows.FirstOrDefault();
        }
        void ShowDiagram()
        {
            if (diagramSummary is not { } info) throw new InvalidOperationException("Выберите щит.");
            var roomByDevice = new Dictionary<ElementId, string?>();
            string Rooms(CircuitInfo circuit)
            {
                foreach (var device in circuit.Devices)
                {
                    if (roomByDevice.ContainsKey(device.Id)) continue;
                    roomByDevice[device.Id] = doc.GetElement(device.Id) is FamilyInstance instance
                        ? LocateRoom(instance, device.Point).Room?.Number : null;
                }
                return string.Join(", ", circuit.Devices.Select(d => roomByDevice[d.Id])
                    .OfType<string>().Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().OrderBy(n => n, NaturalComparer.Instance));
            }
            var feeders = groups.Items.Cast<GroupRow>().Select(g => new PanelDiagram.Feeder(g.Number, g.Load, g.Reserve,
                g.Phase, g.Poles, g.ModelBreaker, g.SelectedBreaker, g.ModelCable, g.SelectedCable, g.Length, g.Drop,
                g.Power, g.Demand, g.Apparent, g.Cos, g.Current, g.Voltage, g.Leakage, g.Reserve ? "" : Rooms(g.Circuit), g.Status)).ToList();
            PanelDiagram.Show(window, info, feeders);
        }
        void Filter(ElementId? preferred)
        {
            var filtered = panels.Where(p => $"{p.Floor} {p.Room} {p.Label}".Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
            var view = new ListCollectionView(filtered);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PanelRow.Floor)));
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PanelRow.Room)));
            list.ItemsSource = view;
            list.SelectedItem = filtered.FirstOrDefault(p => p.Panel.Id == preferred) ?? filtered.FirstOrDefault();
            RefreshPanel();
        }
        void ShowDevices()
        {
            if (groups.SelectedItem is not GroupRow group || group.Circuit.Devices.Count == 0)
                throw new InvalidOperationException("Выберите группу с потребителями.");
            var ids = group.Circuit.Devices.Select(d => d.Id).ToList();
            ui.Selection.SetElementIds(ids);
            try { ui.ShowElements(ids); } catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
        }
        search.TextChanged += (_, _) => Filter((list.SelectedItem as PanelRow)?.Panel.Id);
        list.SelectionChanged += (_, _) => RefreshPanel();
        groups.SelectionChanged += (_, _) =>
        {
            var selected = groups.SelectedItem as GroupRow;
            devices.ItemsSource = selected?.Circuit.Devices;
            deviceLabel.Text = selected == null ? "Потребители группы" : $"Потребители группы {selected.Number} · {selected.Circuit.Devices.Count}";
            deleteReserve.IsEnabled = selected is { Reserve: true };
        };
        devices.MouseDoubleClick += (_, _) => { if (devices.SelectedItem is DeviceInfo d) EomUi.Show(ui, d.Id); };
        groups.MouseDoubleClick += (_, _) => { if (groups.SelectedItem is GroupRow { Circuit.Devices.Count: > 0 }) ShowDevices(); };

        var left = new DockPanel { Margin = new Thickness(0, 0, 14, 0) };
        var searchBox = new StackPanel(); searchBox.Children.Add(EomUi.Heading($"Щиты проекта · {panels.Count}")); searchBox.Children.Add(search);
        DockPanel.SetDock(searchBox, Dock.Top); left.Children.Add(searchBox); left.Children.Add(list);
        var right = new DockPanel();
        var summary = new StackPanel(); summary.Children.Add(panelName); summary.Children.Add(metrics);
        DockPanel.SetDock(summary, Dock.Top); right.Children.Add(summary);
        var detail = new StackPanel(); detail.Children.Add(deviceLabel); detail.Children.Add(devices);
        DockPanel.SetDock(detail, Dock.Bottom); right.Children.Add(detail); right.Children.Add(groups);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(275) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(left); Grid.SetColumn(right, 1); body.Children.Add(right);
        var demandControls = new WrapPanel();
        demandControls.Children.Add(EomUi.Row("Коэффициент спроса для расчёта", demandBox));
        demandControls.Children.Add(EomUi.Button("Пересчитать", () =>
        {
            double demand = EomUi.Parse(demandBox, "Коэффициент спроса");
            if (!double.IsFinite(demand) || demand <= 0 || demand > 1) throw new InvalidOperationException("Коэффициент спроса должен быть больше нуля и не выше 1.");
            demandByPanel[SelectedPanel().Id] = demand;
            RefreshPanel();
        }));
        simulatePhases.Click += (_, _) => RefreshPanel();
        demandControls.Children.Add(simulatePhases);
        demandControls.Children.Add(EomUi.Button("Кс группы…", () =>
        {
            if (groups.SelectedItem is not GroupRow group || doc.GetElement(group.Circuit.Id) is ElectricalSystem { CircuitType: CircuitType.Spare })
                throw new InvalidOperationException("Выберите рабочую группу.");
            var dialog = EomUi.Window("Коэффициент спроса · " + group.Number, 460, 200);
            dialog.Owner = window;
            var input = EomUi.Number(demandByCircuit.GetValueOrDefault(group.Circuit.Id, EomUi.Parse(demandBox, "Коэффициент спроса")));
            EomUi.Layout(dialog, EomUi.Row("Коэффициент спроса группы", input), bottom: EomUi.Buttons(EomUi.Button("Применить", () =>
            {
                double factor = EomUi.Parse(input, "Коэффициент спроса");
                if (!double.IsFinite(factor) || factor <= 0 || factor > 1) throw new InvalidOperationException("Введите число больше нуля и не выше 1.");
                demandByCircuit[group.Circuit.Id] = factor;
                dialog.DialogResult = true;
            }, true), EomUi.Button("Отмена", dialog.Close)));
            if (dialog.ShowDialog() == true) RefreshPanel();
        }));
        summary.Children.Add(demandControls);
        void AddReserve()
        {
            var panel = SelectedPanel();
            var dialog = EomUi.Window("Новый резерв · " + panel.Name, 460, 200);
            dialog.Owner = window;
            var ratingBox = EomUi.Number(16);
            double rating = 0;
            EomUi.Layout(dialog, EomUi.Row("Номинал автомата резерва", ratingBox, "А"), EomUi.Hint("Revit добавит резерв в первую свободную позицию спецификации щита."),
                EomUi.Buttons(EomUi.Button("Добавить", () =>
                {
                    rating = EomUi.Parse(ratingBox, "Номинал");
                    if (!double.IsFinite(rating) || rating <= 0) throw new InvalidOperationException("Номинал должен быть больше нуля.");
                    dialog.DialogResult = true;
                }, true), EomUi.Button("Отмена", dialog.Close)));
            if (dialog.ShowDialog() != true) return;
            PanelReserves.Add(doc, panel.Id, rating);
            panels = ReadPanels();
            Filter(panel.Id);
        }
        void RemoveReserve()
        {
            var panel = SelectedPanel();
            if (groups.SelectedItem is not GroupRow { Reserve: true } group) return;
            var confirmation = System.Windows.MessageBox.Show(window,
                $"Удалить резервную линию «{group.Number}» из щита «{panel.Name}»?",
                "Удаление резерва", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;
            PanelReserves.Remove(doc, panel.Id, group.Circuit.Id);
            panels = ReadPanels();
            Filter(panel.Id);
        }
        EomUi.Layout(window, body, bottom: EomUi.Buttons(
            EomUi.Button("Добавить резерв…", AddReserve),
            deleteReserve,
            EomUi.Button("Схема…", ShowDiagram),
            EomUi.Button("Показать щит", () => EomUi.Show(ui, SelectedPanel().Id)),
            EomUi.Button("Показать потребителей", ShowDevices), EomUi.Button("Закрыть", window.Close)));
        Filter(ui.Selection.GetElementIds().FirstOrDefault(id => panels.Any(p => p.Panel.Id == id)));
        window.ShowDialog();
        return Result.Succeeded;
    }

    static string Format(double? value) => value.HasValue ? $"{value.Value:0.###}" : "—";

    static double? Value(Element element, BuiltInParameter id, ForgeTypeId unit)
    {
        var p = element.get_Parameter(id);
        return p is { StorageType: StorageType.Double, HasValue: true } ? UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit) : null;
    }
}
