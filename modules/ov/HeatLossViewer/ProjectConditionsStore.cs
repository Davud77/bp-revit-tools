using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using HeatLossViewer.Export;

namespace HeatLossViewer;

public static class ProjectConditionsStore
{
    private static readonly Guid SchemaId = new("8bebfe7b-2993-488c-b368-2117e046d828");

    public static ProjectConditions Read(Document doc)
    {
        var schema = Schema.Lookup(SchemaId);
        var info = doc.ProjectInformation;
        var data = new ProjectConditions();
        if (schema is not null)
        {
            var entity = info.GetEntity(schema);
            if (entity.IsValid())
                data = JsonSerializer.Deserialize<ProjectConditions>(entity.Get<string>(schema.GetField("Json")))
                    ?? throw new InvalidOperationException("Не удалось прочитать сведения ОВ проекта.");
        }
        // Native Revit fields remain authoritative when edited outside this window.
        data.Name = info.Name ?? "";
        data.Number = info.Number ?? "";
        data.Address = info.Address ?? "";
        return data;
    }

    /// <summary>Call in an open transaction; native fields and climate commit atomically.</summary>
    public static void Write(Document doc, ProjectConditions data)
    {
        data.Validate();
        var schema = Schema.Lookup(SchemaId);
        if (schema is null)
        {
            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName("BotPlusRTProjectConditionsV1");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("Json", typeof(string));
            schema = builder.Finish();
        }
        var info = doc.ProjectInformation;
        info.Name = data.Name;
        info.Number = data.Number;
        info.Address = data.Address;
        var entity = new Entity(schema);
        entity.Set(schema.GetField("Json"), JsonSerializer.Serialize(data));
        info.SetEntity(entity);
    }
}
