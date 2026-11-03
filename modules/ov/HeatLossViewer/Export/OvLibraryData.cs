using System.Text.Json;

namespace HeatLossViewer.Export;

public sealed class OvLibraryData
{
    public List<OvMaterial> Materials { get; set; } = [];
    public List<OvConstruction> Constructions { get; set; } = [];
    public OvRadiatorState Radiators { get; set; } = new();
    public void Validate()
    {
        foreach (var m in Materials)
            foreach (var n in new[] { m.Lambda, m.Density, m.SpecificHeat })
                if (n.HasValue && (!double.IsFinite(n.Value) || n <= 0)) throw new ArgumentException("Материал «" + m.Name + "»: параметры должны быть положительными числами или пустыми.");
        foreach (var c in Constructions)
        {
            if (!double.IsFinite(c.N) || c.N <= 0 || !double.IsFinite(c.Homogeneity) || c.Homogeneity <= 0 || c.Homogeneity > 1)
                throw new ArgumentException("Тип «" + c.Name + "»: n > 0, однородность от 0 до 1.");
            if (c.R0.HasValue && (!double.IsFinite(c.R0.Value) || c.R0 <= 0)) throw new ArgumentException("Тип «" + c.Name + "»: R₀ должно быть больше нуля.");
            if (!double.IsFinite(c.Gn) || c.Gn < 0 || !double.IsFinite(c.F) || c.F <= 0 || !double.IsFinite(c.Ai) || c.Ai < 0)
                throw new ArgumentException("Тип «" + c.Name + "»: проверьте Gn, f и ai.");
        }
        Radiators.Validate();
    }
    public double? Resistance(OvConstruction c)
    {
        if (!c.UseLayers) return c.R0;
        if (!c.Layers.Any(l => l.Thickness > 0)) return null;
        double result = .115 + .043;
        foreach (var layer in c.Layers)
        {
            if (layer.Thickness <= 0) continue;
            var lambda = Materials.FirstOrDefault(m => m.Key == layer.MaterialKey)?.Lambda;
            if (!(lambda > 0)) return null;
            result += layer.Thickness / lambda.Value;
        }
        return c.Homogeneity * result;
    }
}

public sealed class OvMaterial
{
    public string Key { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string Name { get; set; } = "";
    public double? Lambda { get; set; }
    public double? Density { get; set; }
    public double? SpecificHeat { get; set; }
    public int WallTypes { get; set; }
    public bool ReadOnly { get; set; }
}
public sealed class OvConstruction
{
    public string Key { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "wall";
    public int Instances { get; set; }
    public bool ReadOnly { get; set; }
    public bool UseInCalculation { get; set; }
    public bool UseLayers { get; set; }
    public bool GroundFloor { get; set; }
    public double? R0 { get; set; }
    public double N { get; set; } = 1;
    public double Homogeneity { get; set; } = 1;
    public double Gn { get; set; } = 5;
    public double F { get; set; } = .67;
    public double Ai { get; set; } = 1;
    public List<OvLayer> Layers { get; set; } = [];
}
public sealed class OvLayer
{
    public string MaterialKey { get; set; } = "";
    public string MaterialName { get; set; } = "";
    public double Thickness { get; set; }
    public string Function { get; set; } = "";
}
public sealed class OvRadiatorState
{
    public Dictionary<string, JsonElement> Settings { get; set; } = [];
    public List<JsonElement> Custom { get; set; } = [];
    public void Validate()
    {
        if (Custom.Count > 10000 || JsonSerializer.Serialize(this).Length > 12_000_000) throw new ArgumentException("Слишком большой каталог радиаторов.");
        void Check(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var n) || !double.IsFinite(n))) throw new ArgumentException("Некорректное число в настройках радиаторов.");
            if (value.ValueKind == JsonValueKind.Object) foreach (var p in value.EnumerateObject()) Check(p.Value);
            if (value.ValueKind == JsonValueKind.Array) foreach (var v in value.EnumerateArray()) Check(v);
        }
        foreach (var v in Settings.Values) Check(v);
        var ids = new HashSet<string>();
        foreach (var product in Custom)
        {
            Check(product);
            if (!product.TryGetProperty("id", out var id) || !ids.Add(id.GetString() ?? "")) throw new ArgumentException("Неуникальный id радиатора.");
            foreach (var key in new[] { "lengthMm", "heightMm", "depthMm", "qNominal", "n" })
                if (!product.TryGetProperty(key, out var v) || !v.TryGetDouble(out var number) || number <= 0) throw new ArgumentException("Проверьте габариты и мощность собственного радиатора.");
            if (!product.TryGetProperty("mounts", out var mounts) || mounts.ValueKind != JsonValueKind.Array || mounts.GetArrayLength() == 0)
                throw new ArgumentException("Укажите тип установки радиатора.");
        }
    }
}
