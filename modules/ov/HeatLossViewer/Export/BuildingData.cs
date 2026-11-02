using System.Globalization;

namespace HeatLossViewer.Export;

// Transport contract only: no Autodesk types or calculation logic.
public sealed class BuildingData
{
    public string SchemaVersion { get; init; } = "1.1";
    public string ProjectName { get; init; } = "";
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.Now;
    public string CoordinateSystem { get; init; } = "host-internal-XY-Z-up";
    public object Units { get; init; } = new { length = "m", area = "m2", volume = "m3" };
    public int TotalRooms => Rooms.Count;
    public int TotalBoundaries => Rooms.Sum(r => r.BoundariesCount);
    public List<RoomData> Rooms { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<OpeningData> Openings { get; init; } = [];
}

public sealed class RoomData
{
    public long Id { get; init; }
    // Preserve exact 64-bit IDs in JavaScript, whose Number is limited to 53 bits.
    public string IdText => Id.ToString(CultureInfo.InvariantCulture);
    public string Key { get; init; } = "";
    public string SourceDocument { get; init; } = "";
    public string SourceInstance { get; init; } = "";
    public string Number { get; init; } = "";
    public string Name { get; init; } = "";
    public string LevelName { get; init; } = "";
    public string LevelKey { get; init; } = "";
    public double Elevation { get; init; }
    public double BaseElevation { get; init; }
    public double Area { get; init; }
    public double Volume { get; init; }
    public double Height { get; init; }
    public string HeightSource { get; init; } = "";
    public List<double[]> Contour { get; init; } = [];
    public List<RoomPolygon> Polygons { get; init; } = [];
    public int BoundariesCount { get; init; }
    public List<SurfaceData> Surfaces { get; init; } = [];
    public string EnvelopeStatus { get; set; } = "demo";
}

public sealed class SurfaceData
{
    public string Key { get; init; } = "";
    public string HostKey { get; init; } = "";
    public string ElementId { get; init; } = "";
    public string TypeName { get; init; } = "";
    public string Kind { get; init; } = "wall";
    public string Exposure { get; set; } = "unknown";
    public string ClassificationSource { get; set; } = "demo";
    public double GrossArea { get; init; }
    public double[] Normal { get; init; } = [];
    public double[] Point { get; init; } = [];
    public double[] BoundsMin { get; init; } = [];
    public double[] BoundsMax { get; init; } = [];
    public double? NetArea { get; set; }
    public string AreaSource { get; set; } = "room-face-beta";
    public string? AreaNote { get; set; }
    public List<double[]> Triangles { get; set; } = [];
    public List<SurfaceCutData> Cuts { get; set; } = [];
}

public sealed class SurfaceCutData
{
    public string Key { get; init; } = "";
    public string Kind { get; set; } = "cut";
    public string? OpeningKey { get; set; }
    public double Area { get; init; }
    public double[] Center { get; init; } = [];
    public double[] BoundsMin { get; init; } = [];
    public double[] BoundsMax { get; init; } = [];
    public List<double[]> Triangles { get; init; } = [];
}

public sealed class OpeningData
{
    public string Key { get; init; } = "";
    public string IdText { get; init; } = "";
    public string HostKey { get; init; } = "";
    public string Kind { get; init; } = "window";
    public string Name { get; init; } = "";
    public List<string> RoomKeys { get; init; } = [];
    public double[] Position { get; init; } = [];
    public double[] Tangent { get; init; } = [];
    public double Width { get; init; }
    public double Height { get; init; }
    public double Area => Width * Height;
    public string GeometrySource { get; init; } = "family-parameters";
    public bool IsExterior { get; init; }
    public Dictionary<string, string> SurfaceKeys { get; init; } = [];
}

public sealed class RoomPolygon
{
    public List<double[]> Contour { get; init; } = [];
    public List<List<double[]>> Holes { get; init; } = [];
}
