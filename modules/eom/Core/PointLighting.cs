using System.Globalization;

namespace BotPlus.Eom.Core;

/// <summary>IES LM-63 Type C, TILT=NONE. Силы света в канделах без перенормировки по потоку БД.</summary>
public sealed class IesPhotometry
{
    readonly double[] vertical, horizontal;
    readonly double[][] candela;
    IesPhotometry(double[] v, double[] h, double[][] cd) { vertical = v; horizontal = h; candela = cd; }

    public static IesPhotometry Parse(string text)
    {
        if (text.Length > 16_000_000) throw new InvalidOperationException("Файл IES слишком большой.");
        var lines = text.Replace("\r", "").Split('\n');
        int tilt = Array.FindIndex(lines, l => l.Trim().StartsWith("TILT=", StringComparison.OrdinalIgnoreCase));
        if (tilt < 0 || !lines[tilt].Trim().Equals("TILT=NONE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Поддерживается IES Type C с TILT=NONE. TILT=INCLUDE и внешние таблицы наклона не поддерживаются.");
        var tokens = string.Join(" ", lines.Skip(tilt + 1)).Split([' ', '\t', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        int p = 0;
        double Read()
        {
            if (p >= tokens.Length || !double.TryParse(tokens[p++], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
                throw new InvalidOperationException("Повреждённый IES: ожидалось конечное число.");
            return v;
        }
        int Count()
        {
            double n = Read();
            if (n < 1 || n > 4096 || n != Math.Truncate(n)) throw new InvalidOperationException("Некорректное количество углов IES (1–4096).");
            return (int)n;
        }
        double lamps = Read(), lumens = Read(), multiplier = Read();
        int nv = Count(), nh = Count();
        if (Read() != 1) throw new InvalidOperationException("Поддерживается только фотометрия IES Type C.");
        double units = Read();
        if (units != 1 && units != 2) throw new InvalidOperationException("Неизвестные единицы IES.");
        Read(); Read(); Read(); // Размеры светящего тела не используются в модели точечного источника.
        double ballast = Read(), reserved = Read(); Read();
        if (lamps < 1 || (lumens <= 0 && lumens != -1) || multiplier <= 0 || ballast <= 0 || reserved != 1)
            throw new InvalidOperationException("Некорректные коэффициенты IES; старый ballast-lamp factor, отличный от 1, не поддерживается.");
        if ((long)nv * nh > 1_000_000) throw new InvalidOperationException("Слишком большая таблица IES.");
        double[] Angles(int n, double max)
        {
            var a = Enumerable.Range(0, n).Select(_ => Read()).ToArray();
            if (a[0] < 0 || a[^1] > max || a.Zip(a.Skip(1)).Any(x => x.First >= x.Second))
                throw new InvalidOperationException("Углы IES должны возрастать и находиться в допустимом диапазоне.");
            return a;
        }
        var v = Angles(nv, 180); var h = Angles(nh, 360);
        if (nv < 2 || (v[0] != 0 && v[0] != 90) || (v[^1] != 90 && v[^1] != 180) || v[0] == v[^1]
            || h[0] != 0 || (h[^1] != 0 && h[^1] != 90 && h[^1] != 180 && h[^1] != 360))
            throw new InvalidOperationException("Неподдерживаемые диапазоны углов IES: γ 0–90/180 или 90–180; C 0, 0–90/180/360.");
        var cd = new double[nh][];
        for (int j = 0; j < nh; j++)
        {
            cd[j] = new double[nv];
            for (int i = 0; i < nv; i++)
            {
                double value = Read() * multiplier * ballast;
                if (!double.IsFinite(value) || value < 0) throw new InvalidOperationException("Отрицательная или неконечная сила света IES.");
                cd[j][i] = value;
            }
        }
        if (p != tokens.Length) throw new InvalidOperationException("Лишние данные после таблицы IES.");
        if (h[^1] == 360 && Enumerable.Range(0, nv).Any(i => Math.Abs(cd[0][i] - cd[^1][i]) > 1e-5 * Math.Max(1, cd[0][i])))
            throw new InvalidOperationException("Плоскости C0 и C360 в IES не совпадают.");
        return new(v, h, cd);
    }

    public double Intensity(double gamma, double c)
    {
        if (!double.IsFinite(gamma) || !double.IsFinite(c)) throw new ArgumentOutOfRangeException(nameof(gamma));
        if (gamma < vertical[0] || gamma > vertical[^1]) return 0;
        c = (c % 360 + 360) % 360;
        if (horizontal[^1] == 90) { if (c > 180) c = 360 - c; if (c > 90) c = 180 - c; }
        else if (horizontal[^1] == 180 && c > 180) c = 360 - c;
        static (int A, int B, double T) Bracket(double[] a, double x)
        {
            if (a.Length == 1) return (0, 0, 0);
            int b = Array.BinarySearch(a, x);
            if (b >= 0) return (b, b, 0);
            b = Math.Clamp(~b, 1, a.Length - 1);
            return (b - 1, b, (x - a[b - 1]) / (a[b] - a[b - 1]));
        }
        var (va, vb, vt) = Bracket(vertical, gamma);
        var (ha, hb, ht) = Bracket(horizontal, c);
        double a = candela[ha][va] * (1 - vt) + candela[ha][vb] * vt;
        double b = candela[hb][va] * (1 - vt) + candela[hb][vb] * vt;
        return a * (1 - ht) + b * ht;
    }
}

public readonly record struct LightVector(double X, double Y, double Z)
{
    public static LightVector operator -(LightVector a, LightVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public double Dot(LightVector b) => X * b.X + Y * b.Y + Z * b.Z;
}

/// <summary>Метровые координаты; X/Y задают C0/C90, Down — γ0. Оси ортонормированы.</summary>
public sealed record PointLightSource(LightVector Position, LightVector X, LightVector Y, LightVector Down, IesPhotometry Photometry)
{
    public double Lux(LightVector point)
    {
        var d = point - Position;
        double r2 = d.Dot(d);
        if (r2 < 0.0025) throw new InvalidOperationException("Расчётная точка ближе 50 мм к источнику света.");
        if (d.Z >= 0) return 0; // Горизонтальная рабочая поверхность, нормаль вверх.
        double r = Math.Sqrt(r2);
        double gamma = Math.Acos(Math.Clamp(d.Dot(Down) / r, -1, 1)) * 180 / Math.PI;
        double c = Math.Atan2(d.Dot(Y), d.Dot(X)) * 180 / Math.PI;
        return Photometry.Intensity(gamma, c) * (-d.Z / r) / r2;
    }
}

public readonly record struct LuxPoint(double X, double Y, double Lux);
public readonly record struct LuxTriangle(LuxPoint A, LuxPoint B, LuxPoint C);
public readonly record struct LuxContour(double Level, LuxPoint A, LuxPoint B);

/// <summary>Узловая сетка и треугольники внутри контура; среднее взвешено площадями треугольников.</summary>
public sealed class PointLightMap
{
    public List<LuxPoint> Points { get; } = [];
    public List<LuxTriangle> Triangles { get; } = [];
    public double Min => Triangles.SelectMany(t => new[] { t.A.Lux, t.B.Lux, t.C.Lux }).Min();
    public double Max => Triangles.SelectMany(t => new[] { t.A.Lux, t.B.Lux, t.C.Lux }).Max();
    static double Area(LuxTriangle t) => Math.Abs((t.B.X - t.A.X) * (t.C.Y - t.A.Y) - (t.C.X - t.A.X) * (t.B.Y - t.A.Y)) / 2;
    public double Average => Triangles.Sum(t => Area(t) * (t.A.Lux + t.B.Lux + t.C.Lux) / 3) / Triangles.Sum(Area);
    public double Uniformity => Average > 0 ? Min / Average : 0;

    public static PointLightMap Calculate(double minX, double minY, double maxX, double maxY, double z, double step,
        double maintenance, IReadOnlyList<PointLightSource> sources, Func<double, double, bool> inside)
    {
        if (new[] { minX, minY, maxX, maxY, z, step, maintenance }.Any(v => !double.IsFinite(v))
            || step < 0.05 || maintenance < 1 || maxX <= minX || maxY <= minY || sources.Count == 0)
            throw new InvalidOperationException("Проверьте область расчёта, шаг (от 0,05 м), Kз (от 1) и источники.");
        double cols = Math.Ceiling((maxX - minX) / step), rows = Math.Ceiling((maxY - minY) / step);
        if ((cols + 1) * (rows + 1) > 40000 || (cols + 1) * (rows + 1) * sources.Count > 10_000_000)
            throw new InvalidOperationException("Слишком большая сетка: увеличьте шаг (до 40 000 узлов и 10 млн пар точка–светильник).");
        int nx = (int)cols, ny = (int)rows;
        var map = new PointLightMap(); var grid = new LuxPoint?[nx + 1, ny + 1];
        for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
            {
                double x = minX + (maxX - minX) * i / nx, y = minY + (maxY - minY) * j / ny;
                if (!inside(x, y)) continue;
                double lux = sources.Sum(s => s.Lux(new(x, y, z))) / maintenance;
                if (!double.IsFinite(lux)) throw new InvalidOperationException("Неконечная освещённость: проверьте фотометрию.");
                var point = new LuxPoint(x, y, lux); grid[i, j] = point; map.Points.Add(point);
            }
        void Add(LuxPoint? a, LuxPoint? b, LuxPoint? c)
        {
            if (a is not { } aa || b is not { } bb || c is not { } cc) return;
            // Не соединяем точки через отверстия и вогнутые границы, обнаруживаемые на шаге сетки.
            if (!inside((aa.X + bb.X) / 2, (aa.Y + bb.Y) / 2) || !inside((bb.X + cc.X) / 2, (bb.Y + cc.Y) / 2)
                || !inside((cc.X + aa.X) / 2, (cc.Y + aa.Y) / 2) || !inside((aa.X + bb.X + cc.X) / 3, (aa.Y + bb.Y + cc.Y) / 3)) return;
            map.Triangles.Add(new(aa, bb, cc));
        }
        for (int j = 0; j < ny; j++) for (int i = 0; i < nx; i++)
        {
            Add(grid[i, j], grid[i + 1, j], grid[i + 1, j + 1]);
            Add(grid[i, j], grid[i + 1, j + 1], grid[i, j + 1]);
        }
        if (map.Triangles.Count == 0) throw new InvalidOperationException("Нет расчётной поверхности внутри помещения: уменьшите шаг или проверьте высоту.");
        return map;
    }

    public List<LuxContour> Contours(IEnumerable<double> levels)
    {
        var result = new List<LuxContour>();
        foreach (double level in levels.Distinct().Where(double.IsFinite).Order())
            foreach (var t in Triangles)
            {
                var cuts = new List<LuxPoint>();
                void Edge(LuxPoint a, LuxPoint b)
                {
                    if ((a.Lux < level) == (b.Lux < level)) return;
                    double f = (level - a.Lux) / (b.Lux - a.Lux);
                    cuts.Add(new(a.X + f * (b.X - a.X), a.Y + f * (b.Y - a.Y), level));
                }
                Edge(t.A, t.B); Edge(t.B, t.C); Edge(t.C, t.A);
                if (cuts.Count == 2 && Math.Abs(cuts[0].X - cuts[1].X) + Math.Abs(cuts[0].Y - cuts[1].Y) > 1e-9)
                    result.Add(new(level, cuts[0], cuts[1]));
            }
        return result;
    }
}
