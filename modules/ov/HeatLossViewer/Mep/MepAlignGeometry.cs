namespace HeatLossViewer.Mep;

/// <summary>One parallel route for multi-routing: its free end and direction towards that end (with slope).</summary>
public sealed record RouteEnd(V3 End, V3 Direction);
/// <summary>Result for one route: the end is moved to <see cref="Corner"/>; a new segment runs Corner → End (none when straight).</summary>
public sealed record RouteLeg(V3 Corner, V3 End, double Extension);
public sealed record RouteStep(double AngleDeg, V3 Direction, IReadOnlyList<RouteLeg> Legs)
{
    public bool Straight => Math.Abs(AngleDeg) < 1e-9;
}

/// <summary>Revit-free geometry of «Выровнять» (ModPlus MEP alignment): shifts are in the same units as the input.</summary>
public static class MepAlignGeometry
{
    const double Eps = 1e-9;
    public static bool Vertical(V3 d) => Math.Abs(d.Unit().Z) > 0.999;
    static V3 Flat(V3 v) => new(v.X, v.Y, 0);
    static V3 Left(V3 u) => new(-u.Y, u.X, 0);

    /// <summary>
    /// «Выровнять»: move the element so its axis lies on the reference axis. Parallel axes become collinear; a horizontal
    /// element passes through a vertical reference (shift across it in plan); a vertical element moves in plan onto a
    /// horizontal reference. Crossing horizontal axes are aligned by height instead.
    /// </summary>
    public static V3 AxisShift(V3 movingPoint, V3 movingDir, V3 referencePoint, V3 referenceDir)
    {
        var dm = movingDir.Unit(); var dr = referenceDir.Unit();
        var delta = referencePoint - movingPoint;
        if (dm.Cross(dr).Length < 1e-6) return delta - dm * delta.Dot(dm);
        if (Vertical(dr))
        {
            var n = Left(Flat(dm).Unit());
            return n * delta.Dot(n);
        }
        if (Vertical(dm))
        {
            var h = Flat(dr).Unit();
            var flat = Flat(delta);
            return flat - h * flat.Dot(h);
        }
        throw new InvalidOperationException("Оси не параллельны. Чтобы трассы пересеклись, используйте «Выровнять по высоте».");
    }

    /// <summary>
    /// «Выровнять по высоте»: vertical shift that makes the axes intersect (or, when parallel in plan, puts the moving axis at the
    /// reference elevation at the nearest point).
    /// </summary>
    public static double HeightShift(V3 movingPoint, V3 movingDir, V3 referencePoint, V3 referenceDir)
    {
        var dm = movingDir.Unit(); var dr = referenceDir.Unit();
        if (Vertical(dm)) throw new InvalidOperationException("Вертикальный элемент по высоте не выравнивается — используйте «Выровнять».");
        if (Vertical(dr)) throw new InvalidOperationException("По вертикальной опоре высота не определена — выберите горизонтальную или наклонную опору.");
        var d = referencePoint - movingPoint;
        double det = dm.X * -dr.Y - -dr.X * dm.Y;
        double s;
        V3 onReference;
        if (Math.Abs(det) < 1e-9)
        {
            var fm = Flat(dm);
            s = Flat(d).Dot(fm) / fm.Dot(fm);
            onReference = referencePoint;
        }
        else
        {
            s = (d.X * -dr.Y - -dr.X * d.Y) / det;
            double t = (dm.X * d.Y - dm.Y * d.X) / det;
            onReference = referencePoint + dr * t;
        }
        return onReference.Z - (movingPoint + dm * s).Z;
    }

    /// <summary>«Выровнять вертикально»: keeps the lower end, puts the upper end straight above it. Returns the new upper end.</summary>
    /// <summary>Upper end placed over the lower one; any slope is accepted. Null for a horizontal element (no height to keep).</summary>
    public static V3? Plumb(V3 a, V3 b, out bool upperIsB)
    {
        upperIsB = b.Z >= a.Z;
        if (Math.Abs(b.Z - a.Z) < Eps) return null;
        upperIsB = b.Z >= a.Z;
        var low = upperIsB ? a : b; var high = upperIsB ? b : a;
        return new V3(low.X, low.Y, high.Z);
    }

    /// <summary>
    /// «Задать расстояние»: shift of the moving axis (parallel to the base) so that the horizontal or vertical distance between
    /// axes — or faces, with <paramref name="faceGap"/> = sum of half sizes and insulation — equals <paramref name="target"/>.
    /// The moving element stays on its side of the base.
    /// </summary>
    public static V3 DistanceShift(V3 movingPoint, V3 movingDir, V3 basePoint, V3 baseDir, double target, bool horizontal, double faceGap)
    {
        if (target < 0) throw new InvalidOperationException("Расстояние не может быть отрицательным.");
        var dir = DistanceDirection(movingPoint, movingDir, basePoint, baseDir, horizontal);
        var dr = baseDir.Unit();
        var off = movingPoint - basePoint; off -= dr * off.Dot(dr);
        double current = off.Dot(dir);
        double sign = current < -1e-9 ? -1 : 1;
        return dir * (sign * (target + faceGap) - current);
    }

    /// <summary>Unit direction in which «Задать расстояние» measures and moves (needed to size the faces across it).</summary>
    public static V3 DistanceDirection(V3 movingPoint, V3 movingDir, V3 basePoint, V3 baseDir, bool horizontal)
    {
        var dm = movingDir.Unit(); var dr = baseDir.Unit();
        if (dm.Cross(dr).Length > 1e-4) throw new InvalidOperationException("Элементы не параллельны — расстояние между ними не постоянно.");
        var off = movingPoint - basePoint; off -= dr * off.Dot(dr);
        V3 dir;
        if (horizontal)
        {
            if (Vertical(dr))
            {
                var flat = Flat(off);
                if (flat.Length < 1e-9) throw new InvalidOperationException("Оси совпадают: направление смещения не определено.");
                dir = flat.Unit();
            }
            else dir = Left(Flat(dr).Unit());
        }
        else
        {
            if (Vertical(dr)) throw new InvalidOperationException("Для вертикальных элементов вертикальное расстояние не определено.");
            dir = (V3.ZAxis - dr * dr.Z).Unit();
        }
        return dir;
    }

    /// <summary>Angle (deg) snapped to the nearest of <paramref name="fixedAngles"/>, keeping the turn side.</summary>
    public static double SnapAngle(double angleDeg, IReadOnlyList<double>? fixedAngles)
    {
        if (fixedAngles is not { Count: > 0 }) return angleDeg;
        double abs = Math.Abs(angleDeg);
        double best = fixedAngles.OrderBy(a => Math.Abs(a - abs)).First();
        return Math.Sign(angleDeg) * best;
    }

    public static List<double> ParseAngles(string text)
    {
        var list = new List<double>();
        foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double a) || a < 10 || a > 90)
                throw new InvalidOperationException("Фиксированные углы — числа от 10 до 90 через запятую или пробел.");
            if (!list.Contains(a)) list.Add(a);
        }
        if (list.Count == 0) throw new InvalidOperationException("Укажите хотя бы один фиксированный угол.");
        return list;
    }

    /// <summary>
    /// «Мультипостроение»: continues parallel routes from their free ends towards <paramref name="pick"/>, keeping their plan
    /// offsets from the base route. A turn of 10–90° (arbitrary or snapped) gives a corner per route and a new leg ending on the
    /// line through the pick across the new direction; a pick within 10° of straight ahead only extends or trims the routes to that line.
    /// Ends are aligned automatically; each route keeps its own slope.
    /// </summary>
    public static RouteStep MultiRoute(IReadOnlyList<RouteEnd> routes, int baseIndex, V3 pick, IReadOnlyList<double>? fixedAngles, double minLength)
    {
        if (routes.Count == 0) throw new InvalidOperationException("Нет трасс.");
        var b = routes[baseIndex];
        var u1 = Flat(b.Direction);
        if (u1.Length < 1e-9) throw new InvalidOperationException("Вертикальные трассы мультипостроением не продолжаются.");
        u1 = u1.Unit();
        foreach (var r in routes)
        {
            var f = Flat(r.Direction);
            if (f.Length < 1e-9 || f.Unit().Dot(u1) < 0.9999) throw new InvalidOperationException("Трассы должны быть параллельны на плане и продолжаться в одну сторону.");
        }
        var n1 = Left(u1);
        var toPick = Flat(pick - b.End);
        if (toPick.Length < minLength) throw new InvalidOperationException("Точка слишком близко к концу базовой трассы.");
        double angle = Math.Atan2(u1.Cross(toPick).Z, u1.Dot(toPick)) * 180 / Math.PI;
        // Under 10° the click means «how far», not «turn»: routes are only extended or trimmed.
        if (Math.Abs(angle) < 10 - 1e-9) angle = 0;
        else
        {
            if (Math.Abs(angle) > 90 + 1e-9) throw new InvalidOperationException($"Угол поворота {Math.Abs(angle):0.#}° больше 90°.");
            angle = SnapAngle(angle, fixedAngles);
        }
        var u2 = MepGeometry.Rotate(u1, V3.ZAxis, angle * Math.PI / 180);
        var n2 = Left(u2);
        // The pick defines how far the routes go: the line through it across the new direction.
        double reach = Flat(pick - b.End).Dot(u2);
        var legs = new List<RouteLeg>();
        foreach (var r in routes)
        {
            var f = Flat(r.Direction);
            double slope = r.Direction.Z / f.Length; // rise per plan unit, kept along the route
            double offset = Flat(r.End - b.End).Dot(n1);
            V3 cornerFlat;
            double s;
            if (angle == 0)
            {
                s = Flat(b.End + u1 * reach - r.End).Dot(u1);
                cornerFlat = Flat(r.End) + u1 * s;
                var end = new V3(cornerFlat.X, cornerFlat.Y, r.End.Z + slope * s);
                legs.Add(new RouteLeg(end, end, s));
                continue;
            }
            // Intersection of the route line with the offset line of the new direction.
            var a0 = Flat(r.End); var c0 = Flat(b.End) + n2 * offset;
            double det = u1.X * -u2.Y - -u2.X * u1.Y;
            var w = c0 - a0;
            s = (w.X * -u2.Y - -u2.X * w.Y) / det;
            cornerFlat = a0 + u1 * s;
            var corner = new V3(cornerFlat.X, cornerFlat.Y, r.End.Z + slope * s);
            var endFlat = Flat(b.End) + n2 * offset + u2 * reach;
            double leg = (endFlat - cornerFlat).Dot(u2);
            if (leg < minLength) throw new InvalidOperationException("Новый участок одной из трасс получается слишком коротким — укажите точку дальше.");
            legs.Add(new RouteLeg(corner, new V3(endFlat.X, endFlat.Y, corner.Z + slope * leg), s));
        }
        return new RouteStep(angle, new V3(u2.X, u2.Y, 0), legs);
    }
}
