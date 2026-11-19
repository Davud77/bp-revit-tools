using Autodesk.Revit.DB;
using System.Text;

namespace Ac2Revit;

/// <summary>Diagnostic: how Revit places a wall by profile (heights against the level), on a throw-away transaction.</summary>
public static class ProfileWallProbe
{
    public static string Run(Document doc)
    {
        var text = new StringBuilder();
        var type = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t => t.Kind == WallKind.Basic);
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        string F(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters)).ToString();
        XYZ P(double s, double z) => new XYZ(Geometry.Feet(100 + s), Geometry.Feet(100), Geometry.Feet(z));
        var shapes = new Dictionary<string, double[][]>
        {
            ["rect -0.9..4.9"] = [[0, -0.9], [7, -0.9], [7, 4.9], [0, 4.9]],
            ["rect 0..3"] = [[0, 0], [7, 0], [7, 3], [0, 3]],
            ["trapezoid 0..(2,5)"] = [[0, 0], [7, 0], [7, 2], [0, 5]],
            ["stair -0.9..(0.62,2.79,4.9)"] = [[0, -0.9], [7.24, -0.9], [7.24, 0.62], [4.22, 2.79], [3.12, 2.79], [0.2, 4.9], [0, 4.9]],
        };
        foreach (var level in new[] { levels.First(l => Math.Abs(l.Elevation) < 1e-6), levels.Last() })
            foreach (var (name, pts) in shapes)
            {
                using var t = new Transaction(doc, "probe");
                t.Start();
                try
                {
                    var curves = new List<Curve>();
                    for (int i = 0; i < pts.Length; ++i)
                        curves.Add(Line.CreateBound(P(pts[i][0], pts[i][1]), P(pts[(i + 1) % pts.Length][0], pts[(i + 1) % pts.Length][1])));
                    var wall = Wall.Create(doc, curves, type.Id, level.Id, false);
                    doc.Regenerate();
                    if (Environment.GetEnvironmentVariable("BP_PROBE_FIX") != "0")
                    {
                        double lowest = pts.Min(p => p[1]), highest = pts.Max(p => p[1]);
                        wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(ElementId.InvalidElementId);
                        wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(Geometry.Feet(highest - lowest));
                        wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.Set(Geometry.Feet(lowest) - level.Elevation);
                        doc.Regenerate();
                    }
                    XYZ? min = null, max = null;
                    foreach (var solid in NativeGeometry.Solids(wall))
                        foreach (Autodesk.Revit.DB.Edge edge in solid.Edges)
                            foreach (var p in edge.Tessellate())
                            {
                                min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                                max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
                            }
                    text.AppendLine($"level {F(level.Elevation)} | {name}: solid z {(min == null ? "none" : F(min.Z) + ".." + F(max!.Z))}, x {(min == null ? "" : F(min.X) + ".." + F(max!.X))}, height param {F(wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0)}, base offset {F(wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0)}");
                }
                catch (Exception ex) { text.AppendLine($"level {F(level.Elevation)} | {name}: {ex.Message}"); }
                finally { if (t.GetStatus() == TransactionStatus.Started) t.RollBack(); }
            }
        return text.ToString();
    }
}
