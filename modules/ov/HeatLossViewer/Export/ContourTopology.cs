namespace HeatLossViewer.Export;

public static class ContourTopology
{
    public static double SignedArea(List<double[]> ring)
    {
        double area = 0, ox = ring[0][0], oy = ring[0][1];
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            area += (a[0] - ox) * (b[1] - oy) - (b[0] - ox) * (a[1] - oy);
        }
        return area / 2;
    }

    private static bool Contains(List<double[]> ring, double[] p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i]; var b = ring[j];
            if ((a[1] > p[1]) != (b[1] > p[1]) &&
                p[0] < (b[0] - a[0]) * (p[1] - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
        }
        return inside;
    }

    // Nesting is independent of Revit loop order and mirrored link orientation.
    public static List<RoomPolygon> Build(IEnumerable<List<double[]>> loops)
    {
        var rings = loops.Where(r => r.Count >= 3 && Math.Abs(SignedArea(r)) > 1e-8)
            .OrderByDescending(r => Math.Abs(SignedArea(r))).ToList();
        var depths = new int[rings.Count];
        var polygons = new Dictionary<int, RoomPolygon>();
        for (int i = 0; i < rings.Count; i++)
        {
            int parent = -1;
            for (int j = i - 1; j >= 0; j--)
                if (Contains(rings[j], rings[i][0])) { parent = j; break; }
            depths[i] = parent < 0 ? 0 : depths[parent] + 1;
            if (depths[i] % 2 == 0) polygons[i] = new RoomPolygon { Contour = rings[i] };
            else polygons[parent].Holes.Add(rings[i]);
        }
        return polygons.Values.ToList();
    }
}
