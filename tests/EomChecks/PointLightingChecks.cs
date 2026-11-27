using BotPlus.Eom.Core;

internal static class PointLightingChecks
{
    internal static void Run()
    {
        static void Check(bool value, string name) { if (!value) throw new Exception("Point lighting: " + name); }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-8;
        static void Reject(Action action, string name)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new Exception("Point lighting accepted: " + name);
        }
        const string constant = "IESNA:LM-63-2002\n[TEST] synthetic\nTILT=NONE\n1 -1 1 3 1 1 2 0 0 0\n1 1 10\n0 90 180\n0\n100 100 100";
        var ies = IesPhotometry.Parse(constant);
        var source = new PointLightSource(new(0, 0, 2), new(1, 0, 0), new(0, 1, 0), new(0, 0, -1), ies);
        Check(Near(source.Lux(new(0, 0, 0)), 25), "100 cd at 2 m = 25 lx");
        Check(Near(source.Lux(new(2, 0, 0)), 100 / (8 * Math.Sqrt(2))), "inverse square and receiving cosine");
        Check(Near(source.Lux(new(0, 0, 3)), 0), "no light onto the back of a horizontal receiving plane");
        Reject(() => source.Lux(new(0, 0, 1.99)), "singularity");
        Reject(() => source.Lux(source.Position), "source on calculation plane");
        var scaled = IesPhotometry.Parse(constant.Replace("1 -1 1 3", "1 -1 2 3").Replace("1 1 10", "0.5 1 10"));
        Check(Near(scaled.Intensity(0, 0), 100), "candela multiplier and ballast applied once");
        var quarter = IesPhotometry.Parse("TILT=NONE\n1 1000 1 3 2 1 2 0 0 0 1 1 10\n0 45 90\n0 90\n100 50 0\n200 100 0");
        Check(Near(quarter.Intensity(22.5, 45), 112.5), "bilinear C/gamma interpolation");
        Check(Near(quarter.Intensity(45, 315), quarter.Intensity(45, 45)), "quarter symmetry across C0");
        Check(Near(quarter.Intensity(45, 135), quarter.Intensity(45, 45)), "quarter symmetry across C90");
        Check(Near(quarter.Intensity(120, 0), 0), "no extrapolation outside measured vertical hemisphere");
        var asymmetric = IesPhotometry.Parse("TILT=NONE\n1 -1 1 2 5 1 2 0 0 0 1 1 1\n0 180\n0 90 180 270 360\n100 100 200 200 300 300 400 400 100 100");
        Check(Near(asymmetric.Intensity(45, 315), 250) && Near(asymmetric.Intensity(45, -45), 250), "full C wrap");
        var rotated = source with { Photometry = asymmetric, X = new(0, 1, 0), Y = new(-1, 0, 0) };
        var plain = source with { Photometry = asymmetric };
        Check(Near(rotated.Lux(new(0, 2, 0)), plain.Lux(new(2, 0, 0))), "rotation of family photometric axes");
        Reject(() => IesPhotometry.Parse(constant.Replace("TILT=NONE", "TILT=INCLUDE")), "tilt table");
        Reject(() => IesPhotometry.Parse(constant.Replace("3 1 1 2", "3 1 2 2")), "Type B");
        Reject(() => IesPhotometry.Parse(constant.Replace("100 100 100", "100 -1 100")), "negative candela");
        Reject(() => IesPhotometry.Parse(constant.Replace("100 100 100", "100 NaN 100")), "NaN");
        Reject(() => IesPhotometry.Parse(constant.Replace("100 100 100", "100 100")), "truncated table");
        Reject(() => IesPhotometry.Parse(constant.Replace("0 90 180", "0 90 90")), "repeated angle");
        Reject(() => IesPhotometry.Parse(constant + " 7"), "trailing numeric data");
        Reject(() => IesPhotometry.Parse(constant.Replace("1 1 10", "1 0.8 10")), "unsupported legacy factor");
        var map = PointLightMap.Calculate(-1, -1, 1, 1, 0, 0.25, 2, [source, source], (_, _) => true);
        Check(map.Points.Count == 81 && map.Triangles.Count == 128, "grid nodes and cells");
        Check(Near(map.Max, 25) && map.Min > 0 && map.Average > map.Min && map.Average < map.Max, "summation, maintenance and statistics");
        Check(map.Contours([20]).Count > 0 && map.Contours([100]).Count == 0, "isoline crossing / absent level");
        var masked = PointLightMap.Calculate(-2, -2, 2, 2, 0, 0.25, 1, [source], (x, y) => Math.Abs(x) > 0.4 || Math.Abs(y) > 0.4);
        Check(masked.Points.All(p => Math.Abs(p.X) > 0.4 || Math.Abs(p.Y) > 0.4), "hole excluded");
        Check(masked.Triangles.All(t => Math.Abs((t.A.X + t.B.X + t.C.X) / 3) > 0.4 || Math.Abs((t.A.Y + t.B.Y + t.C.Y) / 3) > 0.4), "triangles do not bridge detected hole");
        Reject(() => PointLightMap.Calculate(0, 0, 1000, 1000, 0, 0.05, 1, [source], (_, _) => true), "oversized grid");
        Reject(() => PointLightMap.Calculate(0, 0, 1, 1, 0, 0.25, 1, [], (_, _) => true), "no sources");
        Reject(() => PointLightMap.Calculate(0, 0, 1, 1, 0, 0.25, 1, [source], (_, _) => false), "empty domain");
        var linear = new PointLightMap();
        linear.Triangles.Add(new(new(0, 0, 0), new(2, 0, 2), new(0, 2, 0)));
        Check(Near(linear.Average, 2.0 / 3), "area-weighted linear triangle average");
        var line = linear.Contours([1]).Single();
        Check(Near(line.A.X, 1) && Near(line.B.X, 1) && Near(Math.Abs(line.A.Y - line.B.Y), 1), "analytic contour x=1");
        var grid = Lighting.SpacedGrid(1, 2, 8, 7, 2, 1.5);
        Check(grid.Count == 9 && grid[0] == (2.5, 3) && grid[^1] == (6.5, 6), "exact independent steps and equal margins on both axes");
        Check(grid[1] == (4.5, 3) && grid[3] == (2.5, 4.5), "separate X and Y steps");
        Check(Lighting.SpacedGrid(1, 2, 3, 4, 10, 10).Single() == (2, 3), "oversized steps yield a centered point");
        Reject(() => Lighting.SpacedGrid(0, 0, 1, 1, double.NaN, 1), "invalid placement step");
        Reject(() => Lighting.SpacedGrid(0, 0, 100, 100, 0.01, 0.01), "placement grid limit");
        Console.WriteLine("PASS point lighting: IES, interpolation, symmetry, rotation, inverse square, maintenance, masking and isolines");
    }
}
