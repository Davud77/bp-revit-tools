namespace Ac2Revit;

/// <summary>
/// Reads a pitched roof's footprint rule from the exact IFC mesh of the roof: for every edge of the roof outline,
/// the plane of the upper surface that meets it gives that edge's slope. Revit's "roof by footprint" builds
/// the ridges, hips and valleys itself from those edge slopes. No Revit API here, so the rule is unit-testable.
/// The caller still has to verify the built roof against the mesh; this class only proposes.
/// </summary>
public static class RoofPlanner
{
    public readonly record struct P(double X, double Y);
    /// <summary>One outline edge (counter-clockwise outer loop). Slope is the rise over run of the roof plane that
    /// starts at the edge and climbs inwards; null when the edge does not define a slope (flat or gable edge).</summary>
    public sealed record Edge(P A, P B, double? Slope, double TopZ);
    public sealed record Plan(IReadOnlyList<Edge> Outer, IReadOnlyList<IReadOnlyList<P>> Holes, double EaveZ, double MinZ, double MaxZ, double MeshVolume, double TopArea);

    public static Plan Build(IReadOnlyList<(double X, double Y, double Z)> vertices, IReadOnlyList<int[]> triangles,
        IReadOnlyList<P> outline, IReadOnlyList<IReadOnlyList<P>> holes)
    {
        if (triangles.Count == 0 || vertices.Count < 3) throw new InvalidDataException("Roof mesh is empty");
        var loop = Clean(outline);
        if (loop.Count < 3) throw new InvalidDataException("Roof outline has fewer than three corners");
        if (SignedArea(loop) < 0) loop.Reverse();

        var top = new List<Plane>();
        double volume = 0, topArea = 0, minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var v in vertices) { minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z); }
        foreach (var t in triangles)
        {
            var a = vertices[t[0]]; var b = vertices[t[1]]; var c = vertices[t[2]];
            volume += (a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X)) / 6;
            var plane = Plane.Of(a, b, c);
            if (plane != null && plane.NormalZ > 0.02) { top.Add(plane); if (plane.FacesUp) topArea += Math.Abs(plane.SignedFlat(a, b, c)) / plane.NormalZ; }
        }
        if (top.Count == 0) throw new InvalidDataException("Roof mesh has no upward surface");

        var edges = new List<Edge>();
        for (int i = 0; i < loop.Count; ++i)
        {
            var a = loop[i]; var b = loop[(i + 1) % loop.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6) continue;
            double ux = dx / length, uy = dy / length, nx = -uy, ny = ux;   // inward for a counter-clockwise loop
            var mid = new P((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            Plane? hit = null;
            foreach (double inset in new[] { 0.05, 0.15, 0.4, 1.0 })
            {
                var sample = new P(mid.X + nx * inset, mid.Y + ny * inset);
                hit = top.Where(p => p.Contains(sample)).OrderByDescending(p => p.ZAt(sample.X, sample.Y)).FirstOrDefault();
                if (hit != null) break;
            }
            if (hit == null) throw new InvalidDataException($"No roof surface meets outline edge {i}");
            double gradient = Math.Sqrt(hit.A * hit.A + hit.B * hit.B), along = hit.A * ux + hit.B * uy, across = hit.A * nx + hit.B * ny;
            double? slope;
            if (gradient < Math.Tan(0.5 * Math.PI / 180)) slope = null;                                   // flat roof plane
            else if (Math.Abs(across) < 0.05 * gradient) slope = null;                                    // plane runs parallel to the edge: a gable end
            else if (Math.Abs(along) > 0.03 * gradient) slope = null;                                    // oblique plane: Revit cannot slope an edge sideways
            else if (across < 0) slope = null;                                                           // the high edge of a shed roof
            else slope = across;
            edges.Add(new Edge(a, b, slope, hit.ZAt(mid.X, mid.Y)));
        }
        if (edges.Count < 3) throw new InvalidDataException("Roof outline is degenerate");
        // Eaves may lie at different heights: Revit keeps a vertical offset for every slope-defining edge.
        var sloped = edges.Where(e => e.Slope.HasValue).Select(e => e.TopZ).OrderBy(z => z).ToList();
        var basis = sloped.Count > 0 ? sloped : edges.Select(e => e.TopZ).OrderBy(z => z).ToList();
        double eave = basis[basis.Count / 2];
        var holeLoops = new List<IReadOnlyList<P>>();
        foreach (var hole in holes) { var h = Clean(hole); if (h.Count >= 3) holeLoops.Add(h); }
        return new Plan(edges, holeLoops, eave, minZ, maxZ, Math.Abs(volume), topArea);
    }


    /// <summary>
    /// Plan outline of the upper surface of a roof mesh: the boundary of its upward triangles, with collinear points
    /// merged. Used when Archicad's polygon is not what the exact IFC mesh shows (the roof was trimmed by other
    /// elements); the result is still verified against the mesh before a native roof is accepted.
    /// </summary>
    public static (IReadOnlyList<P> Outer, IReadOnlyList<IReadOnlyList<P>> Holes) Silhouette(IReadOnlyList<(double X, double Y, double Z)> vertices, IReadOnlyList<int[]> triangles)
    {
        const double grid = 1000; // 1 mm
        (long, long) K(P p) => ((long)Math.Round(p.X * grid), (long)Math.Round(p.Y * grid));
        var edges = new Dictionary<((long, long), (long, long)), int>();
        var points = new Dictionary<(long, long), P>();
        foreach (var t in triangles)
        {
            var a = vertices[t[0]]; var b = vertices[t[1]]; var c = vertices[t[2]];
            double nz = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            double nx = (b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y), ny = (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z);
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-12 || nz / length <= 0.02) continue;                 // upward faces only: the underside would cancel them
            var p = new[] { new P(a.X, a.Y), new P(b.X, b.Y), new P(c.X, c.Y) };
            for (int i = 0; i < 3; ++i)
            {
                var from = K(p[i]); var to = K(p[(i + 1) % 3]);
                if (from == to) continue;
                points[from] = p[i]; points[to] = p[(i + 1) % 3];
                edges[(from, to)] = edges.GetValueOrDefault((from, to)) + 1;
            }
        }
        var boundary = new Dictionary<(long, long), List<(long, long)>>();
        foreach (var (edge, count) in edges)
        {
            if (edges.GetValueOrDefault((edge.Item2, edge.Item1)) >= count) continue;       // shared with a neighbour
            if (!boundary.TryGetValue(edge.Item1, out var list)) boundary[edge.Item1] = list = new();
            list.Add(edge.Item2);
        }
        if (boundary.Count == 0) throw new InvalidDataException("Roof mesh has no upper surface boundary");
        var loops = new List<List<P>>();
        var used = new HashSet<((long, long), (long, long))>();
        foreach (var start in boundary.Keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2))
        {
            foreach (var first in boundary[start].ToList())
            {
                if (used.Contains((start, first))) continue;
                var loop = new List<P> { points[start] };
                var current = start; var next = first;
                int guard = 0;
                while (guard++ < 100000)
                {
                    used.Add((current, next));
                    if (next == start) break;
                    loop.Add(points[next]); current = next;
                    if (!boundary.TryGetValue(current, out var outs)) throw new InvalidDataException("Roof silhouette is open");
                    var step = outs.Where(o => !used.Contains((current, o))).Select(o => ((long, long)?)o).FirstOrDefault();
                    if (step == null) throw new InvalidDataException("Roof silhouette is open");
                    next = step.Value;
                }
                loops.Add(Simplify(loop));
            }
        }
        loops = loops.Where(l => l.Count >= 3).ToList();
        var outers = loops.Where(l => SignedArea(l) > 0).ToList();
        if (outers.Count != 1) throw new InvalidDataException(outers.Count == 0 ? "Roof silhouette has no outer loop" : "The roof surface consists of several separate parts");
        return (outers[0], loops.Where(l => SignedArea(l) < 0).Select(l => (IReadOnlyList<P>)l).ToList());
    }

    static List<P> Simplify(List<P> loop)
    {
        var list = Clean(loop);
        // Triangulation leaves 1 cm slivers where two planes meet; Revit cannot sketch such short edges.
        for (int i = list.Count - 1; i >= 0 && list.Count > 3; --i)
        {
            var p = list[i]; var q = list[(i + list.Count - 1) % list.Count];
            if (Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y)) < 0.02) list.RemoveAt(i);
        }
        bool changed = true;
        while (changed && list.Count > 3)
        {
            changed = false;
            for (int i = 0; i < list.Count && list.Count > 3; ++i)
            {
                var a = list[(i + list.Count - 1) % list.Count]; var b = list[i]; var c = list[(i + 1) % list.Count];
                double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
                double span = Math.Max(Math.Sqrt((c.X - a.X) * (c.X - a.X) + (c.Y - a.Y) * (c.Y - a.Y)), 1e-9);
                if (Math.Abs(cross) / span < 0.002) { list.RemoveAt(i); changed = true; break; }   // within 2 mm of the straight line
            }
        }
        return list;
    }

    static List<P> Clean(IEnumerable<P> points)
    {
        var list = new List<P>();
        foreach (var p in points) if (list.Count == 0 || Math.Abs(p.X - list[^1].X) > 1e-6 || Math.Abs(p.Y - list[^1].Y) > 1e-6) list.Add(p);
        if (list.Count > 1 && Math.Abs(list[0].X - list[^1].X) <= 1e-6 && Math.Abs(list[0].Y - list[^1].Y) <= 1e-6) list.RemoveAt(list.Count - 1);
        return list;
    }

    public static double SignedArea(IReadOnlyList<P> loop)
    {
        double sum = 0;
        for (int i = 0; i < loop.Count; ++i) { var a = loop[i]; var b = loop[(i + 1) % loop.Count]; sum += a.X * b.Y - b.X * a.Y; }
        return sum / 2;
    }

    sealed class Plane
    {
        public double A, B, C, NormalZ;
        public bool FacesUp;
        P p0, p1, p2;
        public static Plane? Of((double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c)
        {
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-12 || Math.Abs(nz) < 1e-12) return null;
            bool up = nz > 0;
            if (nz < 0) { nx = -nx; ny = -ny; nz = -nz; }
            return new Plane { FacesUp = up,  A = -nx / nz, B = -ny / nz, C = a.Z + (nx * a.X + ny * a.Y) / nz, NormalZ = nz / length, p0 = new(a.X, a.Y), p1 = new(b.X, b.Y), p2 = new(c.X, c.Y) };
        }
        public double SignedFlat((double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c) => ((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / 2;
        public double ZAt(double x, double y) => A * x + B * y + C;
        public bool Contains(P s)
        {
            double d(P a, P b, P p) => (a.X - p.X) * (b.Y - p.Y) - (b.X - p.X) * (a.Y - p.Y);
            double d1 = d(p0, p1, s), d2 = d(p1, p2, s), d3 = d(p2, p0, s);
            const double e = 1e-9;
            return !((d1 < -e || d2 < -e || d3 < -e) && (d1 > e || d2 > e || d3 > e));
        }
    }
}
