using System.Globalization;
using System.IO;
using System.Text.Json;

namespace HeatLossViewer.Mep;

/// <summary>Revit-free vector so the connection and rotation math is checked without Revit.</summary>
public readonly record struct V3(double X, double Y, double Z)
{
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);
    public static V3 operator *(V3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public V3 Cross(V3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Length => Math.Sqrt(Dot(this));
    public V3 Unit() { double l = Length; if (l < 1e-12) throw new InvalidOperationException("Нулевое направление."); return this * (1 / l); }
    public static readonly V3 ZAxis = new(0, 0, 1);
}

public sealed record Rotation(V3 Axis, double Angle);
public sealed record ChainLink(int Index, bool Reversed);
public sealed record ChainGap(int From, int To, double Gap);
public sealed record Chain(IReadOnlyList<ChainLink> Order, IReadOnlyList<ChainGap> Gaps);
public sealed record DiffuserRoute(V3 RiserBottom, V3 RiserTop, V3 TapPoint, bool Horizontal, double FlexLength);

public static class MepGeometry
{
    public const double AngleTolerance = 1e-6;
    /// <summary>Same order as Revit's ShortCurveTolerance (internal feet); callers pass the model tolerance.</summary>
    public const double DefaultTolerance = 1e-4;

    public static V3 Rotate(V3 v, V3 axis, double angle)
    {
        var k = axis.Unit();
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return v * c + k.Cross(v) * s + k * (k.Dot(v) * (1 - c));
    }

    public static double SignedAngle(V3 from, V3 to, V3 axis)
    {
        var k = axis.Unit();
        return Math.Atan2(k.Dot(from.Cross(to)), from.Dot(to));
    }

    /// <summary>Rotation that turns <paramref name="d"/> into <paramref name="target"/>. For opposite vectors the axis is
    /// <paramref name="preferred"/> when it is perpendicular, otherwise any perpendicular direction.</summary>
    public static Rotation Align(V3 d, V3 target, V3? preferred = null)
    {
        d = d.Unit(); target = target.Unit();
        var axis = d.Cross(target);
        double dot = Math.Clamp(d.Dot(target), -1, 1);
        if (axis.Length > AngleTolerance) return new Rotation(axis.Unit(), Math.Atan2(axis.Length, dot));
        if (dot > 0) return new Rotation(Perpendicular(d), 0);
        var p = preferred is { } q && Math.Abs(q.Unit().Dot(d)) < AngleTolerance ? q.Unit() : Perpendicular(d);
        return new Rotation(p, Math.PI);
    }

    public static V3 Perpendicular(V3 d)
    {
        var helper = Math.Abs(d.Z) < 0.9 ? V3.ZAxis : new V3(1, 0, 0);
        return d.Cross(helper).Unit();
    }

    /// <summary>
    /// Angle θ such that rotating <paramref name="d"/> about <paramref name="axis"/> by θ gives <paramref name="target"/>.
    /// Null when unreachable: a rotation about a fixed axis keeps the angle between the vector and the axis.
    /// </summary>
    public static double? AngleAbout(V3 axis, V3 d, V3 target)
    {
        var k = axis.Unit(); d = d.Unit(); target = target.Unit();
        if (Math.Abs(k.Dot(d) - k.Dot(target)) > 1e-4) return null;
        var dp = d - k * k.Dot(d); var tp = target - k * k.Dot(target);
        if (dp.Length < 1e-6 || tp.Length < 1e-6) return 0;
        return SignedAngle(dp, tp, k);
    }

    /// <summary>Both orientations along a reference line (it has no preferred sense): 0, 1 or 2 distinct angles.</summary>
    public static IReadOnlyList<(double Angle, bool Along)> OrientAbout(V3 axis, V3 d, V3 line)
    {
        var result = new List<(double, bool)>();
        if (AngleAbout(axis, d, line) is { } a) result.Add((Normalize(a), true));
        if (AngleAbout(axis, d, -line) is { } b && !result.Any(r => Math.Abs(Normalize(r.Item1 - b)) < 1e-6)) result.Add((Normalize(b), false));
        return result;
    }

    /// <summary>Angle in (-π, π].</summary>
    public static double Normalize(double angle)
    {
        double a = Math.IEEERemainder(angle, 2 * Math.PI);
        return a <= -Math.PI ? a + 2 * Math.PI : a;
    }

    /// <summary>
    /// Axis whose positive rotation is counter-clockwise for a viewer standing at the reference connector and looking at the
    /// rotated element. A connector's BasisZ points out of its owner: for the element's own connector the viewer looks along
    /// −BasisZ, for a connector of an unrelated element along +BasisZ.
    /// </summary>
    public static V3 ViewerAxis(V3 basisZ, bool ownConnector) => ownConnector ? basisZ.Unit() : -basisZ.Unit();

    /// <summary>Index of the point nearest to the click, measured across the view (depth along the view direction ignored).</summary>
    public static int Nearest(IReadOnlyList<V3> points, V3 pick, V3? viewDirection)
    {
        if (points.Count == 0) return -1;
        V3? w = viewDirection is { } v ? v.Unit() : null;
        double Distance(V3 p)
        {
            var d = p - pick;
            if (w is { } u) d -= u * u.Dot(d);
            return d.Length;
        }
        int best = 0;
        for (int i = 1; i < points.Count; i++) if (Distance(points[i]) < Distance(points[best]) - 1e-12) best = i;
        return best;
    }

    public static double DistanceToLine(V3 p, V3 origin, V3 direction)
    {
        var k = direction.Unit(); var d = p - origin;
        return (d - k * k.Dot(d)).Length;
    }

    /// <summary>
    /// Orders straight segments lying on one line. Each link tells whether the segment is reversed so that the chain runs
    /// start → end. Gaps are between consecutive segments; overlap or a segment off the line is an error.
    /// </summary>
    public static Chain OrderCollinear(IReadOnlyList<(V3 Start, V3 End)> segments, double tolerance = DefaultTolerance)
    {
        if (segments.Count < 2) throw new InvalidOperationException("Выберите не менее двух прямолинейных участков.");
        var origin = segments[0].Start;
        var direction = (segments[0].End - segments[0].Start).Unit();
        var items = new List<(int Index, double A, double B, bool Reversed)>();
        for (int i = 0; i < segments.Count; i++)
        {
            var (s, e) = segments[i];
            if (DistanceToLine(s, origin, direction) > tolerance || DistanceToLine(e, origin, direction) > tolerance)
                throw new InvalidOperationException("Участки не лежат на одной прямой.");
            double a = (s - origin).Dot(direction), b = (e - origin).Dot(direction);
            items.Add(a <= b ? (i, a, b, false) : (i, b, a, true));
        }
        items.Sort((x, y) => x.A.CompareTo(y.A));
        var gaps = new List<ChainGap>();
        for (int i = 1; i < items.Count; i++)
        {
            double gap = items[i].A - items[i - 1].B;
            if (gap < -tolerance) throw new InvalidOperationException("Участки перекрываются: соединение неоднозначно.");
            gaps.Add(new ChainGap(items[i - 1].Index, items[i].Index, Math.Max(gap, 0)));
        }
        return new Chain(items.Select(x => new ChainLink(x.Index, x.Reversed)).ToList(), gaps);
    }

    /// <summary>
    /// Route from a diffuser's vertical connector to a horizontal duct: optional flexible part along the connector
    /// direction, then a vertical riser to the duct axis elevation, and a horizontal leg when the riser misses the duct
    /// (or when a tee needs the branch exactly on the axis).
    /// </summary>
    public static DiffuserRoute RouteDiffuser(V3 terminal, V3 terminalDirection, V3 mainStart, V3 mainEnd, double mainHalfSize,
        double flexLength, bool exactAxis, double tolerance = DefaultTolerance)
    {
        var t = terminalDirection.Unit();
        if (Math.Abs(t.Z) < 0.999) throw new InvalidOperationException("Свободный коннектор диффузора не вертикален.");
        var axis = mainEnd - mainStart;
        if (Math.Abs(axis.Unit().Z) > 0.01) throw new InvalidOperationException("Поддерживается подключение к горизонтальному воздуховоду.");
        double s = (terminal - mainStart).Dot(axis) / axis.Dot(axis);
        var onAxis = mainStart + axis * s;
        double rise = onAxis.Z - terminal.Z;
        if (rise * t.Z <= 0) throw new InvalidOperationException("Коннектор диффузора направлен от воздуховода.");
        var flat = new V3(terminal.X - onAxis.X, terminal.Y - onAxis.Y, 0);
        double offset = flat.Length;
        bool horizontal = offset > (exactAxis ? tolerance : mainHalfSize);
        double along = s * axis.Length;
        double reach = horizontal ? 0 : mainHalfSize;
        if (along < reach - tolerance || along > axis.Length - reach + tolerance)
            throw new InvalidOperationException("Диффузор находится за пределами воздуховода.");
        double vertical = Math.Abs(rise);
        if (flexLength < 0) throw new InvalidOperationException("Длина гибкого участка не может быть отрицательной.");
        // A riser shorter than the duct half-size would end inside the duct; keep a usable rigid part.
        if (flexLength > 0 && vertical - flexLength < mainHalfSize + tolerance)
            throw new InvalidOperationException("Гибкий участок длиннее расстояния до воздуховода.");
        var bottom = terminal + t * flexLength;
        var top = new V3(terminal.X, terminal.Y, onAxis.Z);
        return new DiffuserRoute(bottom, top, horizontal ? onAxis : top, horizontal, flexLength);
    }

    /// <summary>Breadth-first reach over a connector graph. Edges may be blocked (reference connector) or axial
    /// (connector on the rotation axis and parallel to it: the joint stays valid without moving the neighbour).</summary>
    public static HashSet<T> Reachable<T>(T start, Func<T, IEnumerable<(T Neighbor, bool Axial, bool Blocked)>> edges, bool includeAxial) where T : notnull
    {
        var seen = new HashSet<T> { start };
        var queue = new Queue<T>(); queue.Enqueue(start);
        while (queue.Count > 0)
            foreach (var (n, axial, blocked) in edges(queue.Dequeue()))
            {
                if (blocked || (axial && !includeAxial)) continue;
                if (seen.Add(n)) queue.Enqueue(n);
            }
        return seen;
    }

    /// <summary>Degrees typed by the user, comma or dot; result in radians.</summary>
    public static double ParseAngle(string text)
    {
        if (!double.TryParse(text.Trim().Replace(',', '.').TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out double deg)
            || !double.IsFinite(deg) || Math.Abs(deg) > 360 || Math.Abs(deg) < 1e-6)
            throw new InvalidOperationException("Введите угол в градусах: от 0 до 360.");
        return deg * Math.PI / 180;
    }
}

public sealed class FlexOptions
{
    public bool Use { get; set; }
    public string Type { get; set; } = "";
    public double LengthMm { get; set; } = 500;
}

/// <summary>Per-user tool settings (not project data): last angle, options and diffuser setup.</summary>
public sealed class MepToolSettings
{
    public string Angle { get; set; } = "90";
    public bool RotateConnected { get; set; } = true;
    public bool RotateAxial { get; set; }
    public bool Tee { get; set; }
    public FlexOptions Round { get; set; } = new();
    public FlexOptions Rectangular { get; set; } = new();
    public FlexOptions Oval { get; set; } = new();
    public List<string> AngleHistory { get; set; } = [];

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BotPlus RT", "mep-tools.json");

    public static MepToolSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return File.Exists(path) ? JsonSerializer.Deserialize<MepToolSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception e) when (e is System.IO.IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Remember(string angle)
    {
        AngleHistory.Remove(angle); AngleHistory.Insert(0, angle);
        if (AngleHistory.Count > 8) AngleHistory.RemoveRange(8, AngleHistory.Count - 8);
        Angle = angle;
    }
}
