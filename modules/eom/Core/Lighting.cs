namespace BotPlus.Eom.Core;

/// <summary>
/// Светотехнические свойства помещения — аналог вкладки «Светотехнические свойства» контура помещения nanoCAD BIM Электро.
/// Хранятся в модели по UniqueId помещения (в том числе помещений из связей АР).
/// </summary>
public sealed record RoomLight
{
    /// <summary>Прототип, из которого заполнены свойства (пусто — задано вручную).</summary>
    public string Prototype { get; init; } = "";
    /// <summary>Разряд и подразряд зрительных работ («-» — не задан).</summary>
    public string VisualCategory { get; init; } = "-";
    /// <summary>Нормируемая освещённость, лк.</summary>
    public double Lux { get; init; } = 200;
    /// <summary>Высота рабочей поверхности над полом, м.</summary>
    public double WorkPlaneM { get; init; } = 0.8;
    /// <summary>Коэффициенты отражения потолка, стен и пола, о.е.</summary>
    public double Ceiling { get; init; } = 0.7;
    public double Walls { get; init; } = 0.5;
    public double Floor { get; init; } = 0.3;
    /// <summary>Коэффициент запаса Kз.</summary>
    public double Maintenance { get; init; } = 1.4;
    /// <summary>Высота установки светильников от пола, м (0 — по высоте помещения).</summary>
    public double MountM { get; init; }
    /// <summary>Светильник из БД (наименование типа).</summary>
    public string Luminaire { get; init; } = "";
    /// <summary>Заданное количество светильников (0 — расчёт по требуемой освещённости).</summary>
    public int FixedCount { get; init; }
    /// <summary>Расставлять рекомендуемое (сеткой), а не минимально необходимое количество.</summary>
    public bool UseRecommended { get; init; } = true;
    /// <summary>Характеристики среды по прототипу (для справки и спецификаций).</summary>
    public string Environment { get; init; } = "Нормальная";
    public double Temperature { get; init; } = 20;
    public string RoomCategory { get; init; } = "Не категорируется";
    public string ExplosionZone { get; init; } = "Невзрывоопасная";
    public string FireZone { get; init; } = "Непожароопасная";
    /// <summary>UniqueId светильников, расставленных BotPlus в помещении.</summary>
    public List<string> Placed { get; init; } = [];

    public void Validate()
    {
        if (Lux <= 0) throw new InvalidOperationException("Нормируемая освещённость должна быть больше нуля.");
        if (WorkPlaneM < 0) throw new InvalidOperationException("Высота рабочей поверхности не может быть отрицательной.");
        foreach (var (v, n) in new[] { (Ceiling, "потолка"), (Walls, "стен"), (Floor, "пола") })
            if (v is < 0 or > 1) throw new InvalidOperationException($"Коэффициент отражения {n}: от 0 до 1.");
        if (Maintenance < 1) throw new InvalidOperationException("Коэффициент запаса Kз не меньше 1.");
        if (FixedCount < 0) throw new InvalidOperationException("Количество светильников не может быть отрицательным.");
    }

    /// <summary>Свойства из прототипа: светотехника заменяется, выбор светильника и высота установки сохраняются.</summary>
    public RoomLight From(RoomPrototype p) => this with
    {
        Prototype = p.Label, VisualCategory = p.VisualCategory, Lux = p.Lux, WorkPlaneM = p.WorkPlaneM,
        Ceiling = p.Ceiling, Walls = p.Walls, Floor = p.Floor, Maintenance = p.Maintenance, Environment = p.Environment,
        Temperature = p.Temperature, RoomCategory = p.RoomCategory, ExplosionZone = p.ExplosionZone, FireZone = p.FireZone
    };
}

/// <summary>
/// Таблица коэффициентов использования: строки — индекс помещения i, столбцы — сочетания отражений ρп/ρс/ρр.
/// Между индексами — линейная интерполяция, по отражениям — ближайшее сочетание.
/// </summary>
public sealed class UtilizationTable
{
    public List<double> Indexes { get; set; } = [];
    public List<double[]> Reflections { get; set; } = [];
    /// <summary>Values[k][j] — коэффициент для Indexes[k] и Reflections[j], о.е.</summary>
    public List<double[]> Values { get; set; } = [];

    public bool IsValid => Indexes.Count > 0 && Reflections.Count > 0 && Values.Count == Indexes.Count && Values.All(v => v.Length == Reflections.Count)
        && Reflections.All(r => r.Length == 3) && Indexes.Zip(Indexes.Skip(1)).All(p => p.First < p.Second);

    /// <summary>Номер ближайшего сочетания отражений (потолок и стены весомее пола).</summary>
    public int Nearest(double ceiling, double walls, double floor)
    {
        int best = 0; double bestD = double.MaxValue;
        for (int j = 0; j < Reflections.Count; j++)
        {
            var r = Reflections[j];
            double d = Math.Pow(r[0] - ceiling, 2) + Math.Pow(r[1] - walls, 2) + 0.5 * Math.Pow(r[2] - floor, 2);
            if (d < bestD - 1e-12) { bestD = d; best = j; }
        }
        return best;
    }

    public double Get(double index, double ceiling, double walls, double floor)
    {
        if (!IsValid) return 0;
        int j = Nearest(ceiling, walls, floor);
        if (index <= Indexes[0]) return Values[0][j];
        for (int k = 1; k < Indexes.Count; k++)
            if (index <= Indexes[k])
            {
                double t = (index - Indexes[k - 1]) / (Indexes[k] - Indexes[k - 1]);
                return Values[k - 1][j] + (Values[k][j] - Values[k - 1][j]) * t;
            }
        return Values[^1][j];
    }

    public string ReflectionText(int j) => string.Join("/", Reflections[j].Select(v => (v * 100).ToString("0")));

    /// <summary>
    /// Типовые коэффициенты использования помещения ηп для светильника прямого света с косинусной КСС (тип Д).
    /// Ки = ηп·ηсв (КПД светильника). Справочная таблица — уточняйте по паспорту светильника.
    /// </summary>
    public static UtilizationTable Standard() => new()
    {
        Indexes = [0.6, 0.8, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0],
        Reflections = [[0.7, 0.5, 0.3], [0.7, 0.5, 0.1], [0.5, 0.5, 0.1], [0.5, 0.3, 0.1], [0.3, 0.1, 0.1], [0, 0, 0]],
        Values =
        [
            [0.47, 0.44, 0.40, 0.34, 0.29, 0.26],
            [0.57, 0.53, 0.49, 0.43, 0.38, 0.35],
            [0.64, 0.59, 0.55, 0.50, 0.44, 0.41],
            [0.71, 0.65, 0.61, 0.56, 0.50, 0.47],
            [0.76, 0.69, 0.65, 0.61, 0.55, 0.52],
            [0.83, 0.75, 0.71, 0.68, 0.62, 0.59],
            [0.88, 0.79, 0.75, 0.72, 0.66, 0.63],
            [0.92, 0.82, 0.78, 0.76, 0.69, 0.66],
            [0.97, 0.86, 0.82, 0.80, 0.73, 0.70],
            [1.00, 0.88, 0.84, 0.83, 0.75, 0.72],
        ]
    };
}

/// <summary>Геометрия помещения для расчёта: площадь, периметр, габарит по X и Y и высота.</summary>
public sealed record RoomGeometry(double AreaM2, double PerimeterM, double SizeXM, double SizeYM, double HeightM);

/// <summary>Результат расчёта методом Ки для одного светильника — строка окна «Расчёт освещённости методом Ки».</summary>
public sealed record KiResult(LuminaireItem Luminaire, double Index, double Ki, double HeightM, int Required, double ERequired,
    int Recommended, int Rows, int Cols, double ERecommended, string Problem)
{
    public bool Ok => Problem.Length == 0;
    /// <summary>Количество к расстановке с учётом выбора «рекомендуемое / необходимое».</summary>
    public int Count(bool recommended) => recommended ? Recommended : Required;
    public double Illuminance(bool recommended) => recommended ? ERecommended : ERequired;
    public (int Rows, int Cols) Grid(bool recommended, RoomGeometry room) => recommended ? (Rows, Cols) : Lighting.Layout(Required, room.SizeXM, room.SizeYM, exact: true);
}

public static class Lighting
{
    /// <summary>Индекс помещения i = 2S/(h·P) (для прямоугольника — S/(h·(A+B))), h — высота светильника над рабочей поверхностью.</summary>
    public static double RoomIndex(double area, double perimeter, double height) =>
        area <= 0 || perimeter <= 0 || height <= 0 ? 0 : 2 * area / (height * perimeter);

    /// <summary>Коэффициент использования светильника: собственная таблица Ки или типовая ηп × КПД.</summary>
    public static double Utilization(LuminaireItem lum, double index, double ceiling, double walls, double floor) =>
        lum.Utilization is { IsValid: true } own ? own.Get(index, ceiling, walls, floor)
        : UtilizationTable.Standard().Get(index, ceiling, walls, floor) * Math.Clamp(lum.Efficiency, 0.01, 1);

    /// <summary>Метод коэффициента использования: N = E·S·Kз·z/(Φ·Ки), Φ — поток светильника (все лампы).</summary>
    public static int Count(double lux, double area, double flux, double ki, double maintenance = 1.4, double z = 1) =>
        lux <= 0 || area <= 0 || flux <= 0 || ki <= 0 ? 0 : (int)Math.Ceiling(lux * area * maintenance * z / (flux * ki) - 1e-9);

    /// <summary>Средняя освещённость при N светильниках: E = N·Φ·Ки/(S·Kз·z).</summary>
    public static double Illuminance(int count, double area, double flux, double ki, double maintenance = 1.4, double z = 1) =>
        area <= 0 || maintenance <= 0 ? 0 : count * flux * ki / (area * maintenance * z);

    /// <summary>
    /// Сетка рядов × колонок для n светильников в габарите X×Y. Шаги по осям должны отличаться не более чем вдвое.
    /// exact: только ровно n (лучшее соотношение шагов); иначе — наименьшее n' ≥ n, которое ложится ровной сеткой.
    /// </summary>
    public static (int Rows, int Cols) Layout(int n, double sizeX, double sizeY, bool exact = false)
    {
        if (n <= 0 || sizeX <= 0 || sizeY <= 0) return (0, 0);
        (int, int, double) Best(int total)
        {
            (int r, int c, double ratio) best = (0, 0, double.MaxValue);
            for (int c = 1; c <= total; c++)
            {
                if (total % c != 0) continue;
                int r = total / c;
                double sx = sizeX / c, sy = sizeY / r, ratio = Math.Max(sx, sy) / Math.Min(sx, sy);
                if (ratio < best.ratio - 1e-9) best = (r, c, ratio);
            }
            return best;
        }
        if (!exact)
            for (int total = n; total <= 2 * n + 2; total++)
            {
                var (r, c, ratio) = Best(total);
                if (ratio <= 2 + 1e-9) return (r, c);
            }
        var (rows, cols, _) = Best(n);
        return (rows, cols);
    }

    /// <summary>Полный расчёт методом Ки. fixedCount &gt; 0 — расчёт освещённости при заданном количестве.</summary>
    public static KiResult Calculate(RoomGeometry room, RoomLight light, LuminaireItem lum)
    {
        double mount = light.MountM > 0 ? light.MountM : room.HeightM;
        double h = mount - light.WorkPlaneM;
        if (h <= 0.05) return new(lum, 0, 0, h, 0, 0, 0, 0, 0, 0, "Светильник ниже рабочей поверхности");
        if (lum.LuminaireFlux <= 0) return new(lum, 0, 0, h, 0, 0, 0, 0, 0, 0, "У светильника не задан световой поток");
        double index = RoomIndex(room.AreaM2, room.PerimeterM, h);
        double ki = Utilization(lum, index, light.Ceiling, light.Walls, light.Floor);
        int required = light.FixedCount > 0 ? light.FixedCount : Count(light.Lux, room.AreaM2, lum.LuminaireFlux, ki, light.Maintenance);
        double eRequired = Illuminance(required, room.AreaM2, lum.LuminaireFlux, ki, light.Maintenance);
        var (rows, cols) = Layout(required, room.SizeXM, room.SizeYM, exact: light.FixedCount > 0);
        int recommended = rows * cols;
        double eRecommended = Illuminance(recommended, room.AreaM2, lum.LuminaireFlux, ki, light.Maintenance);
        string problem = ki <= 0 ? "Нет коэффициента использования" : light.FixedCount > 0 && eRequired < light.Lux ? $"Освещённость {eRequired:0} лк ниже нормы {light.Lux:0} лк" : "";
        return new(lum, index, ki, h, required, eRequired, recommended, rows, cols, eRecommended, problem);
    }

    /// <summary>Равномерная сетка rows × cols в прямоугольнике, отступ от стен — половина шага.</summary>
    public static List<(double X, double Y)> Grid(double minX, double minY, double maxX, double maxY, int rows, int cols)
    {
        var points = new List<(double, double)>();
        double w = maxX - minX, l = maxY - minY;
        if (rows <= 0 || cols <= 0 || w <= 0 || l <= 0) return points;
        double dx = w / cols, dy = l / rows;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++) points.Add((minX + dx * (c + 0.5), minY + dy * (r + 0.5)));
        return points;
    }

    /// <summary>Сетка на count точек по соотношению сторон (для сложных контуров, где лишние точки отбрасываются).</summary>
    public static List<(double X, double Y)> Grid(double minX, double minY, double maxX, double maxY, int count)
    {
        double w = maxX - minX, l = maxY - minY;
        if (count <= 0 || w <= 0 || l <= 0) return [];
        int cols = Math.Max(1, (int)Math.Round(Math.Sqrt(count * w / l)));
        int rows = (int)Math.Ceiling(count / (double)cols);
        return Grid(minX, minY, maxX, maxY, rows, cols);
    }

    /// <summary>Центрированная сетка с заданными шагами; шаг сохраняется при отбрасывании точек вне контура.</summary>
    public static List<(double X, double Y)> SpacedGrid(double minX, double minY, double maxX, double maxY, double stepX, double stepY)
    {
        if (new[] { minX, minY, maxX, maxY, stepX, stepY }.Any(v => !double.IsFinite(v)) || stepX <= 0 || stepY <= 0 || maxX <= minX || maxY <= minY)
            throw new InvalidOperationException("Проверьте габариты помещения и положительные шаги по X и Y.");
        double cols = Math.Max(1, Math.Floor((maxX - minX) / stepX + 1e-9));
        double rows = Math.Max(1, Math.Floor((maxY - minY) / stepY + 1e-9));
        if (rows * cols > 5000) throw new InvalidOperationException("Сетка содержит больше 5000 точек. Увеличьте шаг.");
        var points = new List<(double X, double Y)>((int)(rows * cols));
        double startX = minX + ((maxX - minX) - (cols - 1) * stepX) / 2;
        double startY = minY + ((maxY - minY) - (rows - 1) * stepY) / 2;
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++) points.Add((startX + x * stepX, startY + y * stepY));
        return points;
    }

    /// <summary>Стандартная шкала освещённости, лк.</summary>
    public static readonly double[] LuxScale = [5, 7, 10, 20, 30, 50, 75, 100, 150, 200, 300, 400, 500, 600, 750, 1000, 1250, 1500, 2000, 2500, 3000, 3500, 4000, 4500, 5000];
}
