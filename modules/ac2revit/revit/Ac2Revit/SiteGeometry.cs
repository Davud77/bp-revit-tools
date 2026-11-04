using System.Text.Json;

namespace Ac2Revit;

/// <summary>
/// Archicad site data mapped to Revit shared coordinates. Revit keeps one rotation between project and shared
/// coordinates, and its shared +Y is the north shown by Revit. The Archicad project origin stays the Revit internal
/// origin (identity import), so only the project location changes.
/// shared(p) = Origin + Rot(Angle)·p, metres, counter-clockwise angle.
/// </summary>
public sealed record SitePlan(string Mode, double AngleRadians, double EastWest, double NorthSouth, double Elevation,
    double? LatitudeDegrees, double? LongitudeDegrees, IReadOnlyList<string> Notes)
{
    public (double E, double N, double Z) ToShared(double x, double y, double z)
    {
        double c = Math.Cos(AngleRadians), s = Math.Sin(AngleRadians);
        return (EastWest + c * x - s * y, NorthSouth + s * x + c * y, Elevation + z);
    }
}

public sealed record UnitPlan(string Length, int LengthDecimals, string Area, int AreaDecimals, string Volume, int VolumeDecimals,
    string Angle, int AngleDecimals, IReadOnlyList<string> Notes);

public static class SiteGeometry
{
    const double DegreeTolerance = 0.01;
    /// <summary>Archicad's location for a new project (Moscow); reported, because it is rarely the real site.</summary>
    public const double DefaultLatitude = 55.75, DefaultLongitude = 37.583333333;

    public static SitePlan? Plan(JsonElement? geolocation)
    {
        if (geolocation is not { ValueKind: JsonValueKind.Object } root) return null;
        // Raw Tapir response or its addOnCommandResponse.
        if (root.TryGetProperty("response", out var response) && response.TryGetProperty("result", out var result) &&
            result.TryGetProperty("addOnCommandResponse", out var inner)) root = inner;
        bool hasLocation = root.TryGetProperty("projectLocation", out var location) && location.ValueKind == JsonValueKind.Object;
        bool hasSurvey = root.TryGetProperty("surveyPoint", out var survey) && survey.ValueKind == JsonValueKind.Object;
        if (!hasLocation && !hasSurvey) return null;
        var notes = new List<string>();
        double? lat = hasLocation && location.TryGetProperty("latitude", out var la) ? la.GetDouble() : null;
        double? lon = hasLocation && location.TryGetProperty("longitude", out var lo) ? lo.GetDouble() : null;
        double altitude = hasLocation && location.TryGetProperty("altitude", out var al) ? al.GetDouble() : 0;
        double? north = hasLocation && location.TryGetProperty("north", out var no) ? no.GetDouble() : null;
        if (lat is double latitude && (latitude < -90 || latitude > 90)) throw new InvalidDataException("Latitude outside [-90, 90]");
        if (lon is double longitude && (longitude < -180 || longitude > 180)) throw new InvalidDataException("Longitude outside [-180, 180]");
        if (lat is double a && lon is double b && Math.Abs(a - DefaultLatitude) < 1e-6 && Math.Abs(b - DefaultLongitude) < 1e-6)
            notes.Add("Широта и долгота совпадают со значением Archicad по умолчанию (Москва); вероятно, расположение проекта не задано.");

        double E = 0, N = 0, Z = 0, px = 0, py = 0, pz = 0, ax = 1, ay = 0, scale = 1;
        if (hasSurvey)
        {
            if (survey.TryGetProperty("position", out var pos)) { E = Num(pos, "eastings"); N = Num(pos, "northings"); Z = Num(pos, "elevation"); }
            if (survey.TryGetProperty("positionInProject", out var pip)) { px = Num(pip, "x"); py = Num(pip, "y"); pz = Num(pip, "z"); }
            if (survey.TryGetProperty("geoReferencingParameters", out var geo))
            {
                ax = geo.TryGetProperty("xAxisAbscissa", out var x) ? x.GetDouble() : 1;
                ay = geo.TryGetProperty("xAxisOrdinate", out var y) ? y.GetDouble() : 0;
                scale = geo.TryGetProperty("scale", out var sc) ? sc.GetDouble() : 1;
                if (geo.TryGetProperty("crsName", out var crs) && crs.GetString() is { Length: > 0 } name) notes.Add("Система координат Archicad: " + name + ".");
            }
        }
        if (Math.Abs(scale - 1) > 1e-9)
            notes.Add($"Масштаб геопривязки Archicad {scale:G9} не переносится: в Revit общие координаты без масштаба.");
        double gridAngle = Math.Abs(ax) + Math.Abs(ay) < 1e-12 ? 0 : Math.Atan2(ay, ax);
        bool georeferenced = Math.Abs(E) > 1e-9 || Math.Abs(N) > 1e-9 || Math.Abs(Z) > 1e-9 || Math.Abs(gridAngle) > 1e-12 ||
            Math.Abs(px) > 1e-9 || Math.Abs(py) > 1e-9 || Math.Abs(pz) > 1e-9;

        string mode; double angle, ox, oy, oz;
        if (georeferenced)
        {
            // Shared coordinates are the survey grid: the survey point keeps its eastings/northings/elevation.
            mode = "survey"; angle = gridAngle;
            ox = E - (Math.Cos(angle) * px - Math.Sin(angle) * py); oy = N - (Math.Sin(angle) * px + Math.Cos(angle) * py); oz = Z - pz;
            if (north is double n)
            {
                double trueNorth = Normalize(Math.PI / 2 - n), difference = Normalize(trueNorth - angle) * 180 / Math.PI;
                if (Math.Abs(difference) > DegreeTolerance)
                    notes.Add($"Истинный север Archicad отличается от севера сетки геопривязки на {difference:0.###}°; в Revit общие координаты следуют геопривязке, отклонение записано.");
            }
        }
        else
        {
            // No georeferencing: Revit true north follows the Archicad north direction, origin altitude is kept.
            mode = "trueNorth"; angle = north is double n ? Normalize(Math.PI / 2 - n) : 0; ox = 0; oy = 0; oz = altitude;
        }
        return new SitePlan(mode, angle, ox, oy, oz, lat, lon, notes);
    }

    public static UnitPlan? Units(JsonElement? calculationUnits)
    {
        if (calculationUnits is not { ValueKind: JsonValueKind.Object } root) return null;
        if (root.TryGetProperty("response", out var response) && response.TryGetProperty("result", out var result) &&
            result.TryGetProperty("addOnCommandResponse", out var inner)) root = inner;
        if (!root.TryGetProperty("length", out var length)) return null;
        var notes = new List<string>();
        string Unit(JsonElement e) => e.TryGetProperty("unit", out var u) ? u.GetString() ?? "" : "";
        int Decimals(JsonElement e) => e.TryGetProperty("decimals", out var d) ? Math.Clamp(d.GetInt32(), 0, 9) : 0;
        string Map(string kind, string unit, string fallback, params (string Source, string Revit)[] table)
        {
            foreach (var (source, revit) in table) if (source == unit) return revit;
            notes.Add($"Единица {kind} Archicad «{unit}» не поддержана в Revit; оставлена {fallback}.");
            return fallback;
        }
        root.TryGetProperty("area", out var area); root.TryGetProperty("volume", out var volume); root.TryGetProperty("angle", out var angle);
        return new UnitPlan(
            Map("длины", Unit(length), "Millimeters", ("Meter", "Meters"), ("Decimeter", "Decimeters"), ("Centimeter", "Centimeters"), ("Millimeter", "Millimeters"),
                ("FootFracInch", "FeetFractionalInches"), ("FootDecInch", "FeetFractionalInches"), ("DecFoot", "Feet"), ("FracInch", "FractionalInches"), ("DecInch", "Inches")), Decimals(length),
            area.ValueKind == JsonValueKind.Object ? Map("площади", Unit(area), "SquareMeters", ("SquareMeter", "SquareMeters"), ("SquareCentimeter", "SquareCentimeters"),
                ("SquareMillimeter", "SquareMillimeters"), ("SquareFoot", "SquareFeet"), ("SquareInch", "SquareInches")) : "SquareMeters", area.ValueKind == JsonValueKind.Object ? Decimals(area) : 2,
            volume.ValueKind == JsonValueKind.Object ? Map("объёма", Unit(volume), "CubicMeters", ("CubicMeter", "CubicMeters"), ("Liter", "Liters"), ("CubicCentimeter", "CubicCentimeters"),
                ("CubicMillimeter", "CubicMillimeters"), ("CubicFoot", "CubicFeet"), ("CubicInch", "CubicInches"), ("CubicYard", "CubicYards"), ("Gallon", "UsGallons")) : "CubicMeters", volume.ValueKind == JsonValueKind.Object ? Decimals(volume) : 2,
            angle.ValueKind == JsonValueKind.Object ? Map("угла", Unit(angle), "Degrees", ("DecimalDegree", "Degrees"), ("DegreeMinSec", "DegreesMinutes"), ("Grad", "Gradians"), ("Radian", "Radians"), ("Surveyors", "Degrees")) : "Degrees",
            angle.ValueKind == JsonValueKind.Object ? Decimals(angle) : 2, notes);
    }

    static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    static double Normalize(double a) { a %= 2 * Math.PI; if (a > Math.PI) a -= 2 * Math.PI; if (a <= -Math.PI) a += 2 * Math.PI; return a; }
}
