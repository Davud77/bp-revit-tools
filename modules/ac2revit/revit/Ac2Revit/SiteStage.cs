using Autodesk.Revit.DB;
using System.Reflection;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>
/// Project location, true north / shared coordinates and display units from Archicad. Applied once per source
/// project; a repeat keeps them, and coordinates the user changed after import are reported, not overwritten.
/// </summary>
public static class SiteStage
{
    const string Key = "site:location";
    const double Metre = 1e-6; // comparison tolerance, metres

    public static Report Run(Document doc, Manifest manifest)
    {
        var report = new Report { ProjectId = manifest.Source.ProjectId, Mode = "site" };
        var site = SiteGeometry.Plan(manifest.Coordinates.Geolocation);
        var units = SiteGeometry.Units(manifest.ProjectUnits);
        if (site == null) report.Add(Key, "Site", "unsupported", "Экспорт не содержит геолокации Archicad; расположение и север Revit не изменены.");
        if (units == null) report.Add("site:units", "Units", "unsupported", "Экспорт не содержит единиц Archicad (Tapir GetCalculationUnits); единицы шаблона Revit сохранены.");
        if (site == null && units == null) return report;

        string hash = Json.Hash(new { site, units });
        var info = doc.ProjectInformation;
        var tag = Storage.Read(info);
        bool owned = tag is { } t && string.Equals(t.Project, manifest.Source.ProjectId, StringComparison.OrdinalIgnoreCase) && t.Key == Key;
        var location = doc.ActiveProjectLocation;
        var current = location.GetProjectPosition(XYZ.Zero);
        bool untouched = Math.Abs(current.EastWest) < 1e-9 && Math.Abs(current.NorthSouth) < 1e-9 && Math.Abs(current.Elevation) < 1e-9 && Math.Abs(current.Angle) < 1e-12;

        // The source changed (or first import): apply. Same source again: keep whatever the user did since.
        bool sameSource = owned && tag!.Value.Hash == hash;
        bool foreign = !owned && !untouched;
        using var transaction = new Transaction(doc, "Расположение, север и единицы Archicad");
        transaction.Start();
        if (site != null)
        {
            if (owned && Matches(location, site)) report.Add(Key, "Site", "unchanged", Describe(site));
            else if (foreign)
                report.Add(Key, "Site", "conflict", "Общие координаты документа уже заданы (не BotPlus); расположение Archicad не применено. " + Describe(site));
            else if (sameSource)
                report.Add(Key, "Site", "conflict", "Общие координаты изменены после импорта; изменения пользователя сохранены. " + Describe(site));
            else
            {
                Apply(doc, location, site);
                report.Add(Key, "Site", owned ? "updated" : "created", Describe(site));
            }
            foreach (string note in site.Notes) report.Add(Key, "Site", "warning", note);
        }
        if (units != null)
        {
            string summary = $"Длина {units.Length} ({units.LengthDecimals} зн.), площадь {units.Area} ({units.AreaDecimals}), объём {units.Volume} ({units.VolumeDecimals}), угол {units.Angle} ({units.AngleDecimals})";
            if (sameSource) report.Add("site:units", "Units", "unchanged", summary + "; единицы документа после импорта не изменяются.");
            else
            {
                string changed = ApplyUnits(doc, units);
                report.Add("site:units", "Units", changed.Length == 0 ? "unchanged" : "updated", summary + (changed.Length == 0 ? "" : "; изменено: " + changed));
            }
            foreach (string note in units.Notes) report.Add("site:units", "Units", "warning", note);
        }
        if (!foreign) Storage.Tag(info, manifest.Source.ProjectId, Key, hash, JsonSerializer.Serialize(new { site, units }, Json.Options));
        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Site transaction rolled back");
        return report;
    }

    static void Apply(Document doc, ProjectLocation location, SitePlan site)
    {
        if (site.LatitudeDegrees is double lat && site.LongitudeDegrees is double lon)
        {
            doc.SiteLocation.Latitude = lat * Math.PI / 180;
            doc.SiteLocation.Longitude = lon * Math.PI / 180;
        }
        // Revit documents the angle as rotation to true north; its sign is confirmed by reading the result back.
        foreach (double sign in new[] { 1.0, -1.0 })
        {
            location.SetProjectPosition(XYZ.Zero, new ProjectPosition(Geometry.Feet(site.EastWest), Geometry.Feet(site.NorthSouth), Geometry.Feet(site.Elevation), sign * site.AngleRadians));
            if (Matches(location, site)) return;
            if (Math.Abs(site.AngleRadians) < 1e-12) break;
        }
        throw new InvalidDataException("Revit не подтвердил общие координаты: положение контрольных точек не совпало с расчётом Archicad.");
    }

    /// <summary>Two internal points, 10 m apart, must land where the Archicad plan puts them.</summary>
    static bool Matches(ProjectLocation location, SitePlan site)
    {
        foreach (var (x, y, z) in new[] { (0.0, 0.0, 0.0), (10.0, 0.0, 3.0), (0.0, 10.0, 0.0) })
        {
            var actual = location.GetProjectPosition(new XYZ(Geometry.Feet(x), Geometry.Feet(y), Geometry.Feet(z)));
            var (e, n, h) = site.ToShared(x, y, z);
            double M(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
            // ProjectPosition elevation does not include the point's own Z; compare it at the origin only.
            if (Math.Abs(M(actual.EastWest) - e) > Metre || Math.Abs(M(actual.NorthSouth) - n) > Metre) return false;
            if (x == 0 && y == 0 && z == 0 && Math.Abs(M(actual.Elevation) - h) > Metre) return false;
        }
        return true;
    }

    static string ApplyUnits(Document doc, UnitPlan plan)
    {
        var units = doc.GetUnits(); var changed = new List<string>();
        void Set(ForgeTypeId spec, string unitName, int decimals, string label)
        {
            var unit = typeof(UnitTypeId).GetProperty(unitName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as ForgeTypeId;
            if (unit == null || !UnitUtils.IsValidUnit(spec, unit)) { changed.Add(label + ": единица " + unitName + " недоступна"); return; }
            var options = units.GetFormatOptions(spec);
            double accuracy = Math.Pow(10, -decimals);
            bool same = !options.UseDefault && options.GetUnitTypeId() == unit && Math.Abs(options.Accuracy - accuracy) < 1e-12;
            if (same) return;
            var next = new FormatOptions(unit);
            if (next.IsValidAccuracy(accuracy)) next.Accuracy = accuracy;
            units.SetFormatOptions(spec, next); changed.Add(label);
        }
        Set(SpecTypeId.Length, plan.Length, plan.LengthDecimals, "длина");
        Set(SpecTypeId.Area, plan.Area, plan.AreaDecimals, "площадь");
        Set(SpecTypeId.Volume, plan.Volume, plan.VolumeDecimals, "объём");
        Set(SpecTypeId.Angle, plan.Angle, plan.AngleDecimals, "угол");
        if (changed.Count > 0) doc.SetUnits(units);
        return string.Join(", ", changed);
    }

    static string Describe(SitePlan s) =>
        (s.Mode == "survey" ? "Геопривязка Archicad: " : "Без геопривязки, север Archicad: ") +
        $"поворот к общим координатам {s.AngleRadians * 180 / Math.PI:0.######}°, начало проекта в общих координатах E={s.EastWest:0.###} N={s.NorthSouth:0.###} H={s.Elevation:0.###} м" +
        (s.LatitudeDegrees is double la && s.LongitudeDegrees is double lo ? $", широта {la:0.######}°, долгота {lo:0.######}°" : "");
}
