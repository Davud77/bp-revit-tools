using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using HeatLossViewer.Export;

namespace HeatLossViewer;

public static class OvLibraryStore
{
    private static readonly Guid Id = new("9aa1418e-cdd8-4823-99fc-2ea38f99c4c7");
    public static OvLibraryData Read(Document doc)
    {
        var schema = Schema.Lookup(Id);
        if (schema is null) return new();
        var entity = doc.ProjectInformation.GetEntity(schema);
        return entity.IsValid() ? JsonSerializer.Deserialize<OvLibraryData>(entity.Get<string>(schema.GetField("Json"))) ?? new() : new();
    }
    public static void Write(Document doc, OvLibraryData data)
    {
        data.Validate();
        var schema = Schema.Lookup(Id);
        if (schema is null)
        {
            var b = new SchemaBuilder(Id); b.SetSchemaName("BotPlusRTOvLibraryV1");
            b.SetReadAccessLevel(AccessLevel.Public); b.SetWriteAccessLevel(AccessLevel.Public); b.AddSimpleField("Json", typeof(string)); schema = b.Finish();
        }
        var e = new Entity(schema); e.Set(schema.GetField("Json"), JsonSerializer.Serialize(data)); doc.ProjectInformation.SetEntity(e);
    }
}
