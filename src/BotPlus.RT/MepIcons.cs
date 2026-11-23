using System.Windows;
using System.Windows.Media;

namespace BotPlus.RT;

/// <summary>
/// Иконки инструментов ИОС в стиле ленты Revit: объёмные светло-серые трубы и фланцы с бликом,
/// синие коннекторы и стрелки действия, красный крест для разъединения. Холст 32×32, без зависимостей от Revit.
/// </summary>
internal static class MepIcons
{
    static readonly Color Edge = Color.FromRgb(70, 76, 84);
    static readonly Brush Blue = Frozen(new SolidColorBrush(Color.FromRgb(38, 140, 228)));
    static readonly Brush BlueLight = Frozen(new SolidColorBrush(Color.FromRgb(120, 190, 245)));
    static readonly Brush Red = Frozen(new SolidColorBrush(Color.FromRgb(229, 57, 53)));
    static readonly Pen Outline = Frozen(new Pen(new SolidColorBrush(Edge), 1) { LineJoin = PenLineJoin.Round });

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>Металл: тень по краям, блик ближе к свету (сверху/слева), как у трёхмерных иконок Revit.</summary>
    static Brush Metal(bool vertical, double light = 1)
    {
        byte L(double v) => (byte)Math.Clamp(v * light, 0, 255);
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = vertical ? new Point(1, 0) : new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromRgb(L(150), L(156), L(164)), 0));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(L(250), L(251), L(252)), 0.3));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(L(214), L(218), L(223)), 0.62));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(L(128), L(134), L(142)), 1));
        return Frozen(b);
    }

    internal static bool Draw(DrawingContext d, string name)
    {
        void Rect(double x, double y, double w, double h, Brush fill, double r = 0.8) => d.DrawRoundedRectangle(fill, Outline, new System.Windows.Rect(x, y, w, h), r, r);
        // Труба с фланцами на концах: тело + чуть более широкие тёмные кольца.
        void VPipe(double x, double y, double w, double h, bool top = true, bool bottom = true)
        {
            Rect(x, y, w, h, Metal(true));
            if (top) Rect(x - 1.5, y, w + 3, 3, Metal(true, 0.88), 0.6);
            if (bottom) Rect(x - 1.5, y + h - 3, w + 3, 3, Metal(true, 0.88), 0.6);
        }
        void HPipe(double x, double y, double w, double h, bool left = true, bool right = true)
        {
            Rect(x, y, w, h, Metal(false));
            if (left) Rect(x, y - 1.5, 3, h + 3, Metal(false, 0.88), 0.6);
            if (right) Rect(x + w - 3, y - 1.5, 3, h + 3, Metal(false, 0.88), 0.6);
        }
        void Link(double x1, double y1, double x2, double y2)
        {
            var dash = new Pen(Blue, 1.6) { DashStyle = new DashStyle([1.2, 1.2], 0) };
            d.DrawLine(dash, new Point(x1, y1), new Point(x2, y2));
        }
        void Node(double x, double y, double r = 2.1) => d.DrawRectangle(Blue, new Pen(Brushes.White, 0.8), new System.Windows.Rect(x - r, y - r, 2 * r, 2 * r));
        void Shape(string path, Brush? fill, Pen? pen) => d.DrawGeometry(fill, pen, Geometry.Parse(path));
        void Cross(double cx, double cy)
        {
            var white = new Pen(Brushes.White, 5.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var red = new Pen(Red, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            foreach (var p in new[] { white, red })
            {
                d.DrawLine(p, new Point(cx - 4, cy - 4), new Point(cx + 4, cy + 4));
                d.DrawLine(p, new Point(cx + 4, cy - 4), new Point(cx - 4, cy + 4));
            }
        }
        // Синяя круговая стрелка вокруг вертикальной оси (эллипс в перспективе).
        void Spin(double cx, double cy, double rx, double ry)
        {
            var pen = new Pen(Blue, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var arc = new PathGeometry([new PathFigure(new Point(cx + rx * 0.55, cy + ry * 0.83),
                [new ArcSegment(new Point(cx - rx, cy), new Size(rx, ry), 0, false, SweepDirection.Clockwise, true),
                 new ArcSegment(new Point(cx + rx * 0.7, cy - ry * 0.72), new Size(rx, ry), 0, false, SweepDirection.Clockwise, true)], false)]);
            d.DrawGeometry(null, pen, arc);
            Shape(FormattableString.Invariant($"M{cx + rx * 0.55 - 0.5},{cy + ry * 0.83 - 3.4} L{cx + rx * 0.55 + 3.4},{cy + ry * 0.83 + 0.4} L{cx + rx * 0.55 - 1.2},{cy + ry * 0.83 + 2.8} Z"), Blue, null);
        }

        switch (name)
        {
            case "OVMepConnect":
            case "OVMepConnectConnectors":
                VPipe(3, 3, 6, 26); VPipe(23, 3, 6, 26);
                Link(11, 16, 21, 16); Node(11.5, 16); Node(20.5, 16);
                return true;
            case "OVMepConnectLine":
                HPipe(1, 6, 12, 6); HPipe(19, 6, 12, 6); Link(13, 9, 19, 9);
                HPipe(1, 20, 12, 6); HPipe(19, 20, 12, 6); Link(13, 23, 19, 23);
                return true;
            case "OVMepConnectDiffuser":
                VPipe(2, 3, 7, 26);
                foreach (double y in new[] { 4.0, 18.0 })
                {
                    Rect(19, y, 11, 11, Metal(false), 0.6);
                    d.DrawRectangle(null, Outline, new System.Windows.Rect(21.5, y + 2.5, 6, 6));
                    d.DrawRectangle(BlueLight, null, new System.Windows.Rect(23.5, y + 4.5, 2, 2));
                    Link(10, y + 5.5, 18.5, y + 5.5);
                }
                Node(11.5, 9.5, 1.8); Node(11.5, 23.5, 1.8);
                return true;
            case "OVMepDisconnect":
                VPipe(3, 3, 6, 26); VPipe(23, 3, 6, 26);
                Link(11, 12, 21, 12); Node(11.5, 12); Node(20.5, 12);
                Cross(22, 24);
                return true;
            case "OVMepDetachBranch":
                // Схема системы: оборудование → узлы, нижняя ветка отсоединяется.
                Rect(2, 4, 28, 13, Metal(false, 1.02), 1);
                d.DrawEllipse(Blue, null, new Point(8, 10.5), 2.6, 2.6);
                Shape("M14.5,13.5 L17.5,7.5 L20.5,13.5 Z", Blue, null);
                d.DrawRectangle(Blue, null, new System.Windows.Rect(23, 8, 5, 5));
                Shape("M8,17 L8,24 L13,24", null, new Pen(Blue, 1.6) { DashStyle = new DashStyle([1.2, 1.2], 0) });
                Node(14.5, 24, 1.8);
                Cross(23.5, 24.5);
                return true;
            case "OVMepRotate":
            case "OVMepRotateConnector":
                // Тройник: горизонтальная магистраль и вертикальный отвод, вокруг отвода — стрелка вращения.
                HPipe(2, 21, 28, 8);
                Rect(10.5, 20, 11, 10, Metal(true, 0.95), 0.6);
                VPipe(12, 4, 8, 17, true, false);
                Spin(16, 10, 13, 4.5);
                return true;
            case "OVMepRotateOrient":
                // Вертикальная опорная труба и наклонный элемент со стрелкой направления.
                VPipe(21, 2, 7, 28);
                d.PushTransform(new RotateTransform(-45, 9, 22));
                Rect(3, 18.5, 13, 7, Metal(false), 0.6); Rect(3, 17, 3, 10, Metal(false, 0.88), 0.6);
                d.Pop();
                d.DrawLine(new Pen(Blue, 2) { EndLineCap = PenLineCap.Round }, new Point(9, 9), new Point(16, 2.5));
                Shape("M17.5,1 L16.6,6.2 L12.3,1.9 Z", Blue, null);
                return true;
            case "OVMepRotateTray":
                // Лоток: U-профиль в перспективе (задняя стенка, дно, передний торец), сверху — стрелка поворота на 90°.
                Shape("M11,11 L14,11 L14,22 L28,22 L28,11 L31,11 L31,25 L11,25 Z", Metal(true, 0.72), Outline);
                Shape("M4,18 L11,11 L14,11 L7,18 Z M21,18 L28,11 L31,11 L24,18 Z", Metal(false, 0.95), Outline);
                Shape("M7,29 L14,22 L28,22 L21,29 Z", Metal(false, 0.85), Outline);
                Shape("M4,18 L7,18 L7,29 L21,29 L21,18 L24,18 L24,32 L4,32 Z", Metal(true), Outline);
                Shape("M24,32 L31,25 L31,11 L24,18 Z", Metal(true, 0.8), Outline);
                Shape("M3,13 C3,7 6,3.5 12,3", null, new Pen(Blue, 2) { StartLineCap = PenLineCap.Round });
                Shape("M16,3 L11.5,0 L11.5,6 Z", Blue, null);
                return true;
            case "OVMepAlign":
            case "OVMepAlignAxis":
                // Опорная труба снизу, выравниваемая — со смещением; синяя ось и стрелка к ней.
                HPipe(2, 21, 28, 7);
                HPipe(8, 4, 22, 7);
                d.DrawLine(new Pen(Blue, 1.4) { DashStyle = new DashStyle([2, 1.5], 0) }, new Point(1, 24.5), new Point(31, 24.5));
                Shape("M4,9 L4,17 M1.5,14.5 L4,18 L6.5,14.5", null, new Pen(Blue, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
                return true;
            case "OVMepAlignHeight":
                HPipe(2, 22, 28, 7);
                HPipe(2, 4, 16, 7);
                Shape("M25,17 L25,5 M21.5,8.5 L25,4.5 L28.5,8.5", null, new Pen(Blue, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
                return true;
            case "OVMepAlignVertical":
                d.DrawLine(new Pen(Blue, 1.4) { DashStyle = new DashStyle([2, 1.5], 0) }, new Point(22, 1), new Point(22, 31));
                d.PushTransform(new RotateTransform(-14, 9, 30));
                Rect(6, 3, 7, 27, Metal(true), 0.8);
                d.Pop();
                VPipe(19, 3, 6, 27);
                Shape("M10,9 C14,6 16,6 18,7", null, new Pen(Blue, 1.8) { EndLineCap = PenLineCap.Round });
                return true;
            case "OVMepAlignMulti":
                // Две параллельные трассы с поворотом на 90° и отводами.
                // Две вложенные Г-образные трассы: горизонтальный участок, отвод, вертикальный участок вверх.
                foreach (var (y, x) in new[] { (22.0, 22.0), (13.0, 13.0) })
                {
                    Rect(x, 2, 6, y - 2 + 6, Metal(true), 0.6);
                    Rect(1, y, x - 1 + 6, 6, Metal(false), 0.6);
                    d.DrawRectangle(Metal(true, 0.85), Outline, new System.Windows.Rect(x - 0.5, y - 0.5, 7, 7));
                }
                d.DrawLine(new Pen(Blue, 1.4) { DashStyle = new DashStyle([2, 1.5], 0) }, new Point(31, 31), new Point(31, 2));
                return true;
            case "OVMepAlignDistance":
                HPipe(6, 3, 24, 7);
                HPipe(6, 22, 24, 7);
                Shape("M3,10 L3,22 M0.8,13 L3,9.8 L5.2,13 M0.8,19 L3,22.2 L5.2,19", null, new Pen(Blue, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
                return true;
            case "BotPlusOpenView":
                // Лист с видовым экраном и стрелкой «открыть».
                Shape("M3,3 L29,3 L29,29 L3,29 Z", Metal(false, 1.06), Outline);
                d.DrawRectangle(null, new Pen(Blue, 1.6), new System.Windows.Rect(6, 6, 14, 12));
                Shape("M8,15 L11,10 L14,13 L17,9", null, new Pen(Blue, 1.2));
                d.DrawLine(Outline, new Point(6, 22), new Point(16, 22)); d.DrawLine(Outline, new Point(6, 25), new Point(13, 25));
                Shape("M17,27 L27,17 M21,17 L27,17 L27,23", null, new Pen(Blue, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
                return true;
            case "BotPlusViewRange":
                // Разрез этажа: плоскости диапазона и ползунок.
                Rect(2, 4, 28, 3, Metal(false, 0.9), 0.4); Rect(2, 26, 28, 3, Metal(false, 0.9), 0.4);
                foreach (double y in new[] { 9.5, 15.5, 21.5 }) d.DrawLine(new Pen(Blue, 1.4) { DashStyle = new DashStyle([2, 1.5], 0) }, new Point(3, y), new Point(29, y));
                d.DrawLine(new Pen(Blue, 2), new Point(6, 15.5), new Point(18, 15.5));
                d.DrawEllipse(Brushes.White, new Pen(Blue, 1.8), new Point(18, 15.5), 3.2, 3.2);
                return true;
            case "OVMepInsulation":
            {
                // Труба в изоляции: жёлтый слой поверх трубы, срез показывает трубу внутри и толщину слоя.
                var wool = new LinearGradientBrush(Color.FromRgb(255, 226, 120), Color.FromRgb(214, 160, 30), 90); wool.Freeze();
                var woolPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(150, 104, 12)), 1));
                d.DrawRoundedRectangle(wool, woolPen, new System.Windows.Rect(2, 9, 21, 14), 2, 2);
                foreach (double x in new[] { 7.0, 12.0, 17.0 }) d.DrawLine(woolPen, new Point(x, 9.5), new Point(x, 22.5));
                HPipe(19, 12.5, 11, 7, false, true);
                d.DrawEllipse(wool, woolPen, new Point(22, 16), 3.2, 7);
                d.DrawEllipse(Metal(true), Outline, new Point(22, 16), 1.6, 3.5);
                return true;
            }
            case "OVMepRiserNumbering":
            {
                // Стояк из двух сегментов и синяя бирка с номером.
                VPipe(5, 2, 7, 13, true, true);
                VPipe(5, 17, 7, 13, true, true);
                d.DrawRoundedRectangle(Blue, Frozen(new Pen(Brushes.White, 1)), new System.Windows.Rect(15, 9, 15, 14), 2.5, 2.5);
                var digit = new FormattedText("1", System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 11, Brushes.White, 1.0);
                d.DrawText(digit, new Point(22.5 - digit.Width / 2, 16 - digit.Height / 2));
                d.DrawLine(Frozen(new Pen(Blue, 1.6)), new Point(12, 16), new Point(15, 16));
                return true;
            }
            case "OVMepUpdater":
            case "OVMepUpdaterSettings":
            case "OVMepUpdaterRefresh":
                // Лист параметров с фрагментом трубы; у обновления — синяя круговая стрелка.
                Shape("M4,2 L20,2 L26,8 L26,30 L4,30 Z", Metal(false, 1.05), Outline);
                Shape("M20,2 L20,8 L26,8", null, Outline);
                foreach (double y in new[] { 12.0, 17.0, 22.0 })
                {
                    d.DrawRectangle(Blue, null, new System.Windows.Rect(7, y - 1, 2, 2));
                    d.DrawLine(Outline, new Point(11, y), new Point(name.EndsWith("Refresh") ? 17 : 22, y));
                }
                if (name.EndsWith("Refresh"))
                {
                    var pen = new Pen(Blue, 2) { StartLineCap = PenLineCap.Round };
                    d.DrawEllipse(Brushes.White, Outline, new Point(24, 24), 7.5, 7.5);
                    d.DrawGeometry(null, pen, Geometry.Parse("M20,22.5 A4.3,4.3 0 0 1 28,22.5 M28,25.5 A4.3,4.3 0 0 1 20,25.5"));
                    Shape("M29.6,20 L28.4,24.3 L25.3,21.6 Z M18.4,28 L19.6,23.7 L22.7,26.4 Z", Blue, null);
                }
                else HPipe(6, 25, 17, 4, true, true);
                return true;
        }
        return false;
    }
}
