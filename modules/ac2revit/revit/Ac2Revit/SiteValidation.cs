using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>Isolated site fixtures in new projects from the given template; the user's documents are not touched.</summary>
public static class SiteValidation
{
    const string Units = """{"length":{"unit":"Millimeter","accuracy":"Off","decimals":0},"area":{"unit":"SquareMeter","accuracy":"Off","decimals":2},"volume":{"unit":"CubicMeter","accuracy":"Off","decimals":3},"angle":{"unit":"DecimalDegree","decimals":2,"accuracy":0}}""";

    public static object Run(UIApplication ui, string folder, string template)
    {
        Directory.CreateDirectory(folder);
        double g = 10 * Math.PI / 180;
        string F(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var cases = new (string Name, string Geo)[]
        {
            ("kaspiysk-default", """{"projectLocation":{"longitude":37.583333333,"latitude":55.75,"altitude":0,"north":1.570796327},"surveyPoint":{"position":{"eastings":0,"northings":0,"elevation":0},"positionInProject":{"x":0,"y":0,"z":0},"geoReferencingParameters":{"crsName":"","xAxisAbscissa":1,"xAxisOrdinate":0,"scale":1}}}"""),
            ("north-60deg", """{"projectLocation":{"latitude":43.0,"longitude":47.5,"altitude":-25,"north":1.0471975511965976},"surveyPoint":{"position":{"eastings":0,"northings":0,"elevation":0},"positionInProject":{"x":0,"y":0,"z":0},"geoReferencingParameters":{"xAxisAbscissa":1,"xAxisOrdinate":0,"scale":1}}}"""),
            ("survey-grid-10deg", "{\"projectLocation\":{\"latitude\":44.04,\"longitude\":43.06,\"altitude\":0,\"north\":" + F(Math.PI / 2 - g) + "},\"surveyPoint\":{\"position\":{\"eastings\":512345.25,\"northings\":4876543.5,\"elevation\":318.2},\"positionInProject\":{\"x\":12,\"y\":-4,\"z\":1.5},\"geoReferencingParameters\":{\"crsName\":\"МСК-26\",\"xAxisAbscissa\":" + F(Math.Cos(g)) + ",\"xAxisOrdinate\":" + F(Math.Sin(g)) + ",\"scale\":1}}}"),
        };
        var results = new List<object>(); bool all = true;
        foreach (var (name, geo) in cases)
        {
            string project = Guid.NewGuid().ToString("D"), path = Path.Combine(folder, name + ".rvt");
            if (File.Exists(path)) File.Delete(path);
            var manifest = new Manifest { SchemaVersion = "1.0.0", Source = new() { ProjectId = project }, Units = new() { Length = "m", Angle = "rad" } };
            manifest.Coordinates.Geolocation = JsonDocument.Parse(geo).RootElement.Clone();
            manifest.ProjectUnits = JsonDocument.Parse(Units).RootElement.Clone();
            var plan = SiteGeometry.Plan(manifest.Coordinates.Geolocation)!;
            var doc = ui.Application.NewProjectDocument(template);
            var checks = new Dictionary<string, object>();
            try
            {
                string Status(Report r) => r.Items.First(i => i.SourceKey == "site:location").Status;
                string UnitStatus(Report r) => r.Items.First(i => i.SourceKey == "site:units").Status;
                var first = SiteStage.Run(doc, manifest);
                checks["first"] = Status(first); checks["firstUnits"] = UnitStatus(first);
                checks["pointsAfterFirst"] = Points(doc, plan);
                checks["revitAngleDegrees"] = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).Angle * 180 / Math.PI;
                checks["latitude"] = doc.SiteLocation.Latitude * 180 / Math.PI; checks["longitude"] = doc.SiteLocation.Longitude * 180 / Math.PI;
                checks["lengthUnit"] = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId;
                checks["lengthAccuracy"] = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).Accuracy;
                checks["repeat"] = Status(SiteStage.Run(doc, manifest));
                doc.SaveAs(path, new SaveAsOptions { MaximumBackups = 1 }); doc.Close(false);
                doc = ui.Application.OpenDocumentFile(path);
                checks["pointsAfterReopen"] = Points(doc, plan);
                checks["afterReopen"] = Status(SiteStage.Run(doc, manifest));
                // A user change after import is kept and reported.
                using (var t = new Transaction(doc, "Пользователь меняет общие координаты"))
                {
                    t.Start(); doc.ActiveProjectLocation.SetProjectPosition(XYZ.Zero, new ProjectPosition(Geometry.Feet(1), Geometry.Feet(2), 0, 0)); t.Commit();
                }
                checks["userChange"] = Status(SiteStage.Run(doc, manifest));
                checks["userChangeKept"] = Math.Abs(UnitUtils.ConvertFromInternalUnits(doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero).EastWest, UnitTypeId.Meters) - 1) < 1e-6;
                doc.Close(false);
                bool ok = checks["first"] as string == "created" && checks["firstUnits"] is "updated" or "unchanged" && ((PointCheck)checks["pointsAfterFirst"]).Ok &&
                    checks["repeat"] as string == "unchanged" && ((PointCheck)checks["pointsAfterReopen"]).Ok && checks["afterReopen"] as string == "unchanged" &&
                    checks["userChange"] as string == "conflict" && (bool)checks["userChangeKept"] &&
                    Math.Abs((double)checks["latitude"] - plan.LatitudeDegrees!.Value) < 1e-6 && Math.Abs((double)checks["longitude"] - plan.LongitudeDegrees!.Value) < 1e-6 &&
                    (string)checks["lengthUnit"] == UnitTypeId.Millimeters.TypeId;
                all &= ok;
                results.Add(new { name, ok, plan, checks });
            }
            catch (Exception error)
            {
                all = false; try { doc.Close(false); } catch { }
                results.Add(new { name, ok = false, plan, checks, error = error.ToString() });
            }
        }
        var result = new { status = all ? "complete" : "failed", template, cases = results };
        File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
        return result;
    }

    /// <summary>Archicad points and the true north vector read back from Revit.</summary>
    sealed record PointCheck(bool Ok, List<object> Rows);
    static PointCheck Points(Document doc, SitePlan plan)
    {
        var rows = new List<object>(); bool ok = true;
        foreach (var (x, y, z) in new[] { (0.0, 0.0, 0.0), (12.0, -4.0, 1.5), (25.0, 7.0, 0.0) })
        {
            var p = doc.ActiveProjectLocation.GetProjectPosition(new XYZ(Geometry.Feet(x), Geometry.Feet(y), Geometry.Feet(z)));
            double M(double v) => UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Meters);
            var expected = plan.ToShared(x, y, z);
            double dx = M(p.EastWest) - expected.E, dy = M(p.NorthSouth) - expected.N;
            ok &= Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6;
            rows.Add(new { internalPoint = new[] { x, y, z }, revit = new[] { M(p.EastWest), M(p.NorthSouth), M(p.Elevation) }, expected = new[] { expected.E, expected.N, expected.Z }, dx, dy });
        }
        return new PointCheck(ok, rows);
    }
}
