using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BotPlus.Eom;

internal static class PanelDiagram
{
    const int GroupsPerSheet = 11;
    const double PaperWidth = 420, PaperHeight = 297, UnitsPerMm = 3.8;
    const double PhaseBus = 246.5, NeutralBus = 243.4, EarthBus = 240.3;
    const double FirstColumn = 100, ColumnWidth = 25, TableTop = 139, TableBottom = 78;

    internal sealed record Summary(string Name, string Location, string Source, string Inlet,
        IReadOnlyList<string> Power, IReadOnlyList<string> PhaseCurrents, int Phases, bool Neutral,
        string ProjectNumber = "", string ProjectName = "", string Organization = "");

    internal sealed record Feeder(string Number, string Load, bool Reserve, string Phase, string Poles,
        string ModelBreaker, string SelectedBreaker, string ModelCable, string SelectedCable, string Length, string Drop,
        string Power, string Demand, string Apparent, string Cos, string Current, string Voltage, string Leakage, string Rooms, string Status);

    static readonly double[] TableEdges = [TableTop, 134, 129, 124, 101, TableBottom];
    static readonly string[] TableLabels =
        ["Маркировка группы", "Установленная мощность, кВт", "Расчётный ток, А", "Наименование потребителя", "Номер помещения"];

    internal static void Show(Window owner, Summary info, IReadOnlyList<Feeder> groups)
    {
        var window = EomUi.Window("Принципиальная схема · " + info.Name, 1390, 930);
        window.Owner = owner;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        int pages = SheetCount(groups.Count), page = 0;
        var pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        var scale = new ScaleTransform(1, 1);
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        void Zoom(double factor) => scale.ScaleX = scale.ScaleY = Math.Clamp(factor, 0.1, 3);
        void Fit() => Zoom(Math.Min((scroll.ActualWidth - 22) / (PaperWidth * UnitsPerMm),
            (scroll.ActualHeight - 22) / (PaperHeight * UnitsPerMm)));
        var navigation = new List<Button>();
        void ShowPage(int index)
        {
            page = Math.Clamp(index, 0, pages - 1);
            var sheet = Draw(info, groups, page);
            sheet.LayoutTransform = scale;
            scroll.Content = sheet;
            scroll.ScrollToTop();
            scroll.ScrollToLeftEnd();
            pageLabel.Text = $"Лист {page + 1} / {pages}";
            navigation[0].IsEnabled = page > 0;
            navigation[1].IsEnabled = page + 1 < pages;
        }
        navigation.Add(EomUi.Button("Предыдущий", () => ShowPage(page - 1)));
        navigation.Add(EomUi.Button("Следующий", () => ShowPage(page + 1)));
        var buttons = EomUi.Buttons(navigation[0], navigation[1],
            EomUi.Button("−", () => Zoom(scale.ScaleX / 1.2)),
            EomUi.Button("+", () => Zoom(scale.ScaleX * 1.2)),
            EomUi.Button("Весь лист", Fit), EomUi.Button("100 %", () => Zoom(1)),
            EomUi.Button("Закрыть", window.Close));
        buttons.Children.Insert(0, pageLabel);
        EomUi.Layout(window, scroll, bottom: buttons);
        ShowPage(0);
        window.Loaded += (_, _) => Fit();
        window.ShowDialog();
    }

    internal static int SheetCount(int groupCount) => Math.Max(1, (groupCount + GroupsPerSheet - 1) / GroupsPerSheet);

    internal static Canvas Draw(Summary info, IReadOnlyList<Feeder> groups, int page = 0)
    {
        var drawing = new DrawingSheet();
        int offset = page * GroupsPerSheet;
        var columns = groups.Skip(offset).Take(GroupsPerSheet).ToList();
        double tableEnd = FirstColumn + Math.Max(1, columns.Count) * ColumnWidth;
        double busEnd = Math.Max(210, tableEnd + 3);
        double outlineEnd = busEnd + 6;

        drawing.Frame(info, page + 1, SheetCount(groups.Count));
        drawing.LeftLegend();
        drawing.Box(89, 278, outlineEnd, 193.9, dashed: true);
        drawing.Text(92, 291, outlineEnd - 94, 10, info.Source);
        drawing.Inlet(info, busEnd);
        if (columns.Count == 0) drawing.Text(102, 186, 105, 12, "Отходящих групп нет");

        for (int i = 0; i < columns.Count; i++)
            drawing.Feeder(columns[i], offset + i + 1, FirstColumn + i * ColumnWidth, info.Neutral);

        foreach (double y in TableEdges) drawing.Line(20, y, tableEnd, y);
        drawing.Line(20, TableTop, 20, TableBottom);
        for (int i = 0; i <= Math.Max(1, columns.Count); i++)
        {
            double x = FirstColumn + i * ColumnWidth;
            drawing.Line(x, TableTop, x, TableBottom);
        }
        for (int i = 0; i < TableLabels.Length; i++)
        {
            double middle = (TableEdges[i] + TableEdges[i + 1]) / 2;
            drawing.Text(20.5, middle + 1.9, FirstColumn - 21, 4, TableLabels[i], centered: true);
        }
        drawing.Text(41, 74, 181, 5, "Примечания");
        drawing.Text(41, 69, 181, 6, "* Аппарат или кабель по расчётному подбору.");
        drawing.Text(41, 64, 181, 11, "Шина PE показана условно. Состав защитных проводников уточняется по проекту.");
        return drawing.Canvas;
    }

    sealed class DrawingSheet
    {
        static readonly FontFamily Font = new("Arial Narrow, Arial");
        internal Canvas Canvas { get; } = new()
        {
            Width = PaperWidth * UnitsPerMm, Height = PaperHeight * UnitsPerMm,
            Margin = new Thickness(10), Background = Brushes.White, ClipToBounds = true
        };

        static double X(double mm) => mm * UnitsPerMm;
        static double Y(double mm) => (PaperHeight - mm) * UnitsPerMm;

        internal void Line(double x, double y, double x2, double y2, double weight = 0.23, bool dashed = false)
        {
            var line = new Line { X1 = X(x), Y1 = Y(y), X2 = X(x2), Y2 = Y(y2), StrokeThickness = X(weight), Stroke = Brushes.Black };
            if (dashed) line.StrokeDashArray = new DoubleCollection([10, 4]);
            Canvas.Children.Add(line);
        }

        internal void Box(double x, double top, double right, double bottom, double weight = 0.23, bool dashed = false)
        {
            Line(x, top, right, top, weight, dashed);
            Line(right, top, right, bottom, weight, dashed);
            Line(right, bottom, x, bottom, weight, dashed);
            Line(x, bottom, x, top, weight, dashed);
        }

        internal void Text(double x, double top, double width, double height, string value,
            bool centered = false, bool vertical = false, double size = 3.25, string? detail = null)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "—") return;
            var text = new TextBlock
            {
                Text = value, Width = X(width), MaxHeight = X(height), FontFamily = Font,
                FontSize = X(size), Foreground = Brushes.Black, LineHeight = X(size * 1.1),
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left, ToolTip = detail ?? value
            };
            FrameworkElement result = vertical
                ? new Border { Child = text, LayoutTransform = new RotateTransform(-90) }
                : text;
            System.Windows.Controls.Canvas.SetLeft(result, X(x));
            System.Windows.Controls.Canvas.SetTop(result, Y(top));
            Canvas.Children.Add(result);
        }

        void Dot(double x, double y, bool solid = true, double radius = 0.5)
        {
            var dot = new Ellipse
            {
                Width = X(radius * 2), Height = X(radius * 2), Stroke = Brushes.Black, StrokeThickness = X(0.2),
                Fill = solid ? Brushes.Black : Brushes.White
            };
            System.Windows.Controls.Canvas.SetLeft(dot, X(x - radius));
            System.Windows.Controls.Canvas.SetTop(dot, Y(y + radius));
            Canvas.Children.Add(dot);
        }

        void Breaker(double x, double top)
        {
            Line(x, top, x - 4.8, top - 11.5, 0.34);
            Line(x - 3.7, top - 6.7, x - 2.0, top - 6.0);
            Line(x - 2.0, top - 6.0, x - 2.8, top - 8.2);
            Line(x - 2.8, top - 8.2, x - 4.5, top - 8.9);
            Line(x - 1.2, top - 13.3, x + 1.2, top - 15.7);
            Line(x + 1.2, top - 13.3, x - 1.2, top - 15.7);
        }

        void Slash(double x, double y) => Line(x - 1, y - 1, x + 1, y + 1, 0.30);

        internal void Inlet(Summary info, double busEnd)
        {
            const double inletX = 120;
            string phaseLabel = info.Phases switch { 1 => "L1", 3 => "L1,L2,L3", _ => "L" };
            Text(89, PhaseBus + 2, 13, 4, phaseLabel, centered: true, size: 2.75);
            Line(103, PhaseBus, busEnd, PhaseBus, 0.42);
            Line(103, PhaseBus + 1, 103, PhaseBus - 1);
            for (int i = 0; i < (info.Phases == 3 ? 3 : 1); i++) Slash(106 + i, PhaseBus);
            if (info.Neutral)
            {
                Text(98, NeutralBus + 2, 4, 4, "N", size: 2.75);
                Line(103, NeutralBus, busEnd, NeutralBus, 0.30);
                Slash(106, NeutralBus);
                Dot(105, NeutralBus - 0.9, solid: false, radius: 0.38);
                Line(inletX, 278, inletX + 3.5, 274.5);
                Line(inletX + 3.5, 274.5, inletX + 3.5, NeutralBus);
                Dot(inletX + 3.5, NeutralBus);
            }
            Text(97, EarthBus + 2, 5, 4, "PE", size: 2.75);
            Line(103, EarthBus, busEnd, EarthBus, 0.30);
            Slash(106, EarthBus);
            Line(104.6, EarthBus - 1, 105.4, EarthBus - 1);
            double branchX = info.Neutral ? inletX + 3.5 : inletX;
            double branchY = info.Neutral ? 274.5 : 278;
            Line(branchX, branchY, inletX + 7, 271);
            Line(inletX + 7, 271, inletX + 7, EarthBus);
            Dot(inletX + 7, EarthBus);

            if (info.Inlet == "—") Line(inletX, 287, inletX, PhaseBus);
            else
            {
                Line(inletX, 287, inletX, 273);
                Breaker(inletX, 273);
                Line(inletX, 258.5, inletX, PhaseBus);
                Text(97, 274, 17, 4, "QF0");
                Text(97, 270, 17, 15, info.Inlet + "*");
            }
            Dot(inletX, PhaseBus);
            Text(140, 276, busEnd - 141, 5, info.Name);
            double y = 271;
            foreach (var line in info.Power.Concat(info.PhaseCurrents))
            {
                Text(140, y, busEnd - 141, 4.2, line);
                y -= 4.2;
            }
        }

        internal void LeftLegend()
        {
            double[] edges = [292, 269, 244, 219, 194, TableTop];
            string[] labels = [
                "Источник питания",
                "Аппарат на вводе\n(автоматический выключатель\nили выключатель нагрузки)\nНомер, тип и номинальный ток, А",
                "Аппарат на линии\n(автоматический выключатель\nили предохранитель)\nНомер, тип и номинальный ток, А",
                "Пусковой аппарат, УЗО\nили другое устройство\nНомер, тип и номинальный ток, А"
            ];
            for (int i = 0; i < labels.Length; i++)
            {
                Box(20, edges[i], 85, edges[i + 1]);
                double textHeight = (1 + labels[i].Count(c => c == '\n')) * 3.65;
                Text(21, (edges[i] + edges[i + 1] + textHeight) / 2, 63, textHeight + 1, labels[i], centered: true);
            }
            Box(20, edges[4], 85, TableTop);
            Line(52.5, 186, 52.5, 147);
            Text(28, 187, 41, 19, "Расчётная нагрузка, кВт\nКоэффициент мощности\nРасчётный ток, А\nДлина участка, м", centered: true, vertical: true);
            Text(62, 187, 41, 12, "Падение напряжения, %\nМарка и сечение кабеля", centered: true, vertical: true);
        }

        internal void Feeder(Feeder group, int position, double left, bool neutral)
        {
            double x = left + ColumnWidth / 2;
            Dot(x, PhaseBus);
            string breaker = Equipment(group.ModelBreaker, group.SelectedBreaker);
            string cable = Equipment(group.ModelCable, group.SelectedCable);
            if (breaker == "—") Line(x, PhaseBus, x, 222);
            else
            {
                Line(x, PhaseBus, x, 236.5);
                Breaker(x, 236.5);
                Text(left + 0.5, 237.5, 11, 4, $"QF{position}");
                Text(left + 0.5, 233.5, 11, 15, breaker, detail: $"Данные цепи: {group.ModelBreaker}\nПодбор: {group.SelectedBreaker}\nПолюсов: {group.Poles}");
            }
            if (!group.Reserve && group.Poles is ("1" or "2")) Text(x + 1.5, 240, 10, 4, group.Phase, size: 2.75);
            int poles = int.TryParse(group.Poles, out int count) ? Math.Clamp(count, 1, 3) : 1;
            for (int i = 0; i < poles; i++) Slash(x, 236.5 + i);
            if (group.Reserve)
            {
                Line(x, 222, x, 212, dashed: true);
                Line(x - 1, 212, x + 1, 212);
            }
            else
            {
                Line(x, 222, x, TableTop, 0.30);
                if (neutral)
                {
                    Dot(x + 3.5, NeutralBus);
                    Line(x + 3.5, NeutralBus, x + 3.5, 197.4);
                    Line(x, 193.9, x + 3.5, 197.4);
                }
                Dot(x + 7, EarthBus);
                Line(x + 7, EarthBus, x + 7, 200.9);
                Line(neutral ? x + 3.5 : x, neutral ? 197.4 : 193.9, x + 7, 200.9);
                string detail = $"Р: {group.Demand} кВт\nS: {group.Apparent} кВА\nU: {group.Voltage} В\nУтечка: {group.Leakage} мА\n{group.Status}";
                Text(x - 4.5, 192.5, 47, 4.5, $"{group.Demand} — {group.Cos} — {group.Current} — {group.Length}", vertical: true, detail: detail);
                string drop = group.ModelCable != "—" && group.ModelCable != group.SelectedCable ? "—" : group.Drop;
                Text(x + 1, 192.5, 47, 9, $"{drop} — {cable}", vertical: true,
                    detail: $"Кабель в цепи: {group.ModelCable}\nПодбор: {group.SelectedCable}\nΔU для подобранного кабеля: {group.Drop} %");
            }
            string[] values = [group.Number, group.Reserve ? "" : group.Power, group.Reserve ? "" : group.Current, group.Load, group.Rooms];
            for (int row = 0; row < values.Length; row++)
            {
                double height = TableEdges[row] - TableEdges[row + 1];
                double padding = height > 10 ? 7 : 0.4;
                Text(left + 0.5, TableEdges[row] - padding, ColumnWidth - 1, height - padding, values[row], centered: true);
            }
        }

        static string Equipment(string model, string selected) => model is "" or "—"
            ? selected is "" or "—" ? "—" : selected + "*"
            : model;

        internal void Frame(Summary info, int page, int total)
        {
            Box(20, 292, 415, 5, 0.45);
            Box(8, 90, 20, 5, 0.32);
            Line(13, 90, 13, 5);
            foreach (double y in new[] { 30.0, 65.0 }) Line(8, y, 20, y);
            Text(9, 30, 25, 4, "Инв. № подл.", centered: true, vertical: true, size: 2.7);
            Text(9, 65, 35, 4, "Подп. и дата", centered: true, vertical: true, size: 2.7);
            Text(9, 90, 25, 4, "Взам. инв. №", centered: true, vertical: true, size: 2.7);
            for (int i = 0; i < 2; i++)
            {
                double bottom = 90 + i * 65;
                Box(0.2, bottom + 65, 20, bottom, 0.28);
                foreach (double x in new[] { 5.0, 10.0, 15.0 }) Line(x, bottom + 65, x, bottom);
                foreach (double dy in new[] { 20.0, 40.0, 55.0 }) Line(5, bottom + dy, 20, bottom + dy);
                Text(0.7, bottom + 30, 30, 4, "Согласовано", centered: true, vertical: true, size: 2.7);
            }

            Box(230, 60, 415, 5, 0.42);
            Line(295, 60, 295, 5, 0.42);
            foreach (double y in new[] { 50.0, 35.0, 20.0 }) Line(295, y, 415, y, 0.38);
            Line(365, 35, 365, 5, 0.38);
            Line(365, 30, 415, 30);
            Line(380, 35, 380, 20);
            Line(395, 35, 395, 20);
            foreach (double y in new[] { 55.0, 50.0, 45.0, 40.0, 35.0, 30.0, 25.0, 20.0, 15.0, 10.0 })
                Line(230, y, 295, y, y == 35 ? 0.38 : 0.23);
            double[] edges = [230, 240, 250, 260, 270, 285, 295];
            string[] headings = ["Изм.", "Кол.уч.", "Лист", "№ док.", "Подп.", "Дата"];
            for (int i = 0; i < headings.Length; i++)
            {
                Line(edges[i], 60, edges[i], 35);
                Text(edges[i], 39.7, edges[i + 1] - edges[i], 4.5, headings[i], centered: true, size: 2.6);
            }
            foreach (double x in new[] { 255.0, 270.0, 285.0 }) Line(x, 35, x, 5);
            Text(231, 29, 22, 4, "Разраб.", size: 2.8);
            Text(231, 24, 22, 4, "Проверил", size: 2.8);
            Text(231, 14, 22, 4, "Н. контр.", size: 2.8);
            Text(296, 58, 118, 7, info.ProjectNumber, centered: true);
            Text(296, 48, 118, 12, info.ProjectName, centered: true);
            Text(296, 32, 68, 10, info.Location, centered: true);
            Text(366, 34.7, 14, 4, "Стадия", centered: true, size: 2.7);
            Text(380, 34.7, 15, 4, "Лист", centered: true, size: 2.7);
            Text(395, 34.7, 20, 4, "Листов", centered: true, size: 2.7);
            Text(380, 27, 15, 6, page.ToString(), centered: true);
            Text(395, 27, 20, 6, total.ToString(), centered: true);
            Text(296, 17, 68, 11, "Принципиальная схема группового щита\n" + info.Name, centered: true);
            Text(366, 17, 48, 10, info.Organization, centered: true);
            Text(385, 4.8, 30, 4.8, "Формат А3", centered: true, size: 2.7);
        }
    }
}
