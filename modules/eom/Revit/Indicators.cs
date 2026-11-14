using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;

namespace BotPlus.Eom;

/// <summary>
/// Индикаторы статуса: две цветные точки в воздухе над каждым прибором, без создания элементов модели (DirectContext3D).
/// Точка 1 — статус (красная — не подключён, зелёная — подключён), точка 2 — цвет группы щита.
/// </summary>
internal sealed class Indicators : IDirectContext3DServer
{
    static Indicators? active;
    readonly Guid id = new("9C1B2E7D-4A5F-4C3E-8B6A-1D2E3F4A5B6C");
    readonly Document document;
    readonly List<(XYZ Point, ColorWithTransparency Color)> markers = [];
    Outline? bounds;
    VertexBuffer? vertices; IndexBuffer? indices; int vertexCount, triangleCount;
    VertexFormat? format; EffectInstance? effect;

    internal static bool Enabled => active != null;

    // Размеры в футах: радиус точки ≈ 60 мм, шаг пары ≈ 150 мм, зазор от габарита прибора ≈ 75 мм.
    const double Radius = 0.2, Step = 0.5, Margin = 0.25, Cell = 3;

    Indicators(Document doc, Network net)
    {
        document = doc;
        var red = new ColorWithTransparency(EomColors.Unconnected.R, EomColors.Unconnected.G, EomColors.Unconnected.B, 0); var green = new ColorWithTransparency(EomColors.Connected.R, EomColors.Connected.G, EomColors.Connected.B, 0);
        var items = new List<(DeviceInfo Device, ColorWithTransparency Status, ColorWithTransparency Group)>();
        int index = 0;
        foreach (var circuit in net.AllCircuits)
        {
            var color = GroupColor(index++);
            foreach (var d in circuit.Devices.Where(d => !d.IsPanel)) items.Add((d, green, color));
        }
        foreach (var d in net.Unconnected) items.Add((d, red, new ColorWithTransparency(EomColors.NoGroup.R, EomColors.NoGroup.G, EomColors.NoGroup.B, 0)));

        // Препятствия в плане: габариты всех приборов. Ищем место для пары точек вокруг прибора, кольцами наружу,
        // так чтобы пара не легла ни на свой прибор, ни на соседние, ни на уже поставленные пары.
        var obstacles = new Dictionary<(int, int), List<(double X0, double Y0, double X1, double Y1)>>();
        void Add((double X0, double Y0, double X1, double Y1) r)
        {
            for (int i = (int)Math.Floor(r.X0 / Cell); i <= (int)Math.Floor(r.X1 / Cell); i++)
                for (int j = (int)Math.Floor(r.Y0 / Cell); j <= (int)Math.Floor(r.Y1 / Cell); j++)
                    (obstacles.TryGetValue((i, j), out var list) ? list : obstacles[(i, j)] = []).Add(r);
        }
        bool Free((double X0, double Y0, double X1, double Y1) r)
        {
            for (int i = (int)Math.Floor(r.X0 / Cell); i <= (int)Math.Floor(r.X1 / Cell); i++)
                for (int j = (int)Math.Floor(r.Y0 / Cell); j <= (int)Math.Floor(r.Y1 / Cell); j++)
                    if (obstacles.TryGetValue((i, j), out var list) && list.Any(o => o.X0 < r.X1 && r.X0 < o.X1 && o.Y0 < r.Y1 && r.Y0 < o.Y1)) return false;
            return true;
        }
        (double, double, double, double) Footprint(DeviceInfo d)
        {
            if (d.Min != null && d.Max != null) return (d.Min.X, d.Min.Y, d.Max.X, d.Max.Y);
            var p = d.Point!; return (p.X - 0.3, p.Y - 0.3, p.X + 0.3, p.Y + 0.3);
        }
        foreach (var (d, _, _) in items) if (d.Point != null || d.Min != null) Add(Footprint(d));

        foreach (var (d, status, group) in items)
        {
            if (d.Point == null && d.Min == null) continue;
            var (x0, y0, x1, y1) = Footprint(d);
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, z = (d.Max?.Z ?? d.Point!.Z) + 0.3;
            (double X, double Y)? chosen = null, fallback = null;
            for (int ring = 0; ring < 6 && chosen == null; ring++)
            {
                double m = Margin + ring * 0.6, left = x0 - m - Radius - Step, right = x1 + m + Radius, below = y0 - m - Radius, above = y1 + m + Radius;
                // Справа, сверху, слева, снизу, затем углы. Координата — центр первой (левой) точки пары.
                foreach (var (px, py) in new[] { (right, cy), (cx - Step / 2, above), (left, cy), (cx - Step / 2, below), (right, above), (left, above), (right, below), (left, below) })
                {
                    var rect = (px - Radius, py - Radius, px + Step + Radius, py + Radius);
                    fallback ??= (px, py);
                    if (Free(rect)) { chosen = (px, py); Add(rect); break; }
                }
            }
            var (ax, ay) = chosen ?? fallback!.Value;
            markers.Add((new XYZ(ax, ay, z), status));
            markers.Add((new XYZ(ax + Step, ay, z), group));
        }
        if (markers.Count > 0)
        {
            var min = new XYZ(markers.Min(m => m.Point.X) - 1, markers.Min(m => m.Point.Y) - 1, markers.Min(m => m.Point.Z) - 1);
            var max = new XYZ(markers.Max(m => m.Point.X) + 1, markers.Max(m => m.Point.Y) + 1, markers.Max(m => m.Point.Z) + 1);
            bounds = new Outline(min, max);
        }
    }

    /// <summary>Гр.1 — зелёный, Гр.2 — красный, Гр.3 — синий, далее — по кругу цветов.</summary>
    internal static ColorWithTransparency GroupColor(int index)
    {
        var (r, g, b) = EomColors.Group(index);
        return new ColorWithTransparency(r, g, b, 0);
    }

    static MultiServerService Service => (MultiServerService)ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService);

    internal static int Enable(Document doc, Network net)
    {
        Disable();
        var server = new Indicators(doc, net);
        var service = Service;
        service.AddServer(server);
        var ids = service.GetActiveServerIds(); ids.Add(server.id); service.SetActiveServers(ids);
        active = server;
        return server.markers.Count / 2;
    }

    internal static void Disable()
    {
        if (active == null) return;
        try
        {
            var service = Service;
            var ids = service.GetActiveServerIds(); ids.Remove(active.id); service.SetActiveServers(ids);
            service.RemoveServer(active.id);
        }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        active.vertices?.Dispose(); active.indices?.Dispose(); active.format?.Dispose(); active.effect?.Dispose();
        active = null;
    }

    public Guid GetServerId() => id;
    public string GetVendorId() => "BTPL";
    public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
    public string GetName() => "BotPlus ЭОМ — индикаторы";
    public string GetDescription() => "Точки статуса подключения и цвета группы у приборов.";
    public string GetApplicationId() => "";
    public string GetSourceId() => "";
    public bool UsesHandles() => false;
    public bool CanExecute(View view) => view.Document.Equals(document) && view is View3D or ViewPlan;
    public Outline? GetBoundingBox(View view) => bounds;
    public bool UseInTransparentPass(View view) => false;

    // Октаэдр: виден с любого направления и на плане, и в 3D.
    static readonly XYZ[] Corners = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
    static readonly int[] Faces = [0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4, 2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5];

    void Build()
    {
        vertexCount = markers.Count * Corners.Length; triangleCount = markers.Count * Faces.Length / 3;
        format = new VertexFormat(VertexFormatBits.PositionColored);
        effect = new EffectInstance(VertexFormatBits.PositionColored);
        vertices = new VertexBuffer(vertexCount * VertexPositionColored.GetSizeInFloats());
        vertices.Map(vertexCount * VertexPositionColored.GetSizeInFloats());
        var vs = vertices.GetVertexStreamPositionColored();
        foreach (var (p, c) in markers) foreach (var corner in Corners) vs.AddVertex(new VertexPositionColored(p + corner * Radius, c));
        vertices.Unmap();
        indices = new IndexBuffer(triangleCount * IndexTriangle.GetSizeInShortInts());
        indices.Map(triangleCount * IndexTriangle.GetSizeInShortInts());
        var ist = indices.GetIndexStreamTriangle();
        for (int m = 0; m < markers.Count; m++)
            for (int f = 0; f < Faces.Length; f += 3)
                ist.AddTriangle(new IndexTriangle(m * 6 + Faces[f], m * 6 + Faces[f + 1], m * 6 + Faces[f + 2]));
        indices.Unmap();
    }

    public void RenderScene(View view, DisplayStyle style)
    {
        if (markers.Count == 0) return;
        // Индексы short: одним буфером не больше ~5400 маркеров; для тестового модуля этого достаточно.
        if (markers.Count * Corners.Length > short.MaxValue) return;
        if (vertices == null || !vertices.IsValid()) Build();
        DrawContext.FlushBuffer(vertices, vertexCount, indices, triangleCount * 3, format, effect, PrimitiveType.TriangleList, 0, triangleCount);
    }
}
