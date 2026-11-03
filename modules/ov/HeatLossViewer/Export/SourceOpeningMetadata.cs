using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using System.Text.Json;

namespace HeatLossViewer.Export;

/// <summary>Public Ac2Revit storage contract; no dependency on the importer assembly.</summary>
public sealed record SourceOpeningMetadata(string Project,string Key,string Guid,string Kind,string HostGuid,bool Empty,JsonElement Raw)
{
    public static readonly Guid SchemaId=new("01cc4a13-4361-4c9c-88a4-a54f807274ce");
    public static SourceOpeningMetadata? Read(Element element)
    {
        var schema=Schema.Lookup(SchemaId);if(schema==null)return null;
        var entity=element.GetEntity(schema);if(!entity.IsValid())return null;
        string key=entity.Get<string>(schema.GetField("SourceKey")),project=entity.Get<string>(schema.GetField("ProjectId"));
        string text=entity.Get<string>(schema.GetField("RawJson"));JsonElement raw=default;
        if(!string.IsNullOrWhiteSpace(text))try{using var json=JsonDocument.Parse(text);raw=json.RootElement.Clone();}catch(JsonException){}
        string S(string name)=>raw.ValueKind==JsonValueKind.Object && raw.TryGetProperty(name,out var value) && value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
        return new(project,key,S("sourceGuid"),S("kind"),S("hostGuid"),raw.ValueKind==JsonValueKind.Object && raw.TryGetProperty("emptyOpening",out var empty) && empty.ValueKind==JsonValueKind.True,raw);
    }
    public BoundingBoxXYZ? VoidBounds()
    {
        if(Raw.ValueKind!=JsonValueKind.Object || !Raw.TryGetProperty("voidGeometry",out var body) || !body.TryGetProperty("vertices",out var array))return null;
        var points=array.EnumerateArray().Select(p=>new XYZ(UnitUtils.ConvertToInternalUnits(p.GetProperty("x").GetDouble(),UnitTypeId.Meters),UnitUtils.ConvertToInternalUnits(p.GetProperty("y").GetDouble(),UnitTypeId.Meters),UnitUtils.ConvertToInternalUnits(p.GetProperty("z").GetDouble(),UnitTypeId.Meters))).ToList();
        return points.Count==0?null:new BoundingBoxXYZ{Min=new XYZ(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)),Max=new XYZ(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z))};
    }
}
