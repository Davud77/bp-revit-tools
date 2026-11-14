using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace BotPlus.Eom;

/// <summary>Данные ЭОМ внутри RVT: настройки проекта на «Сведениях о проекте» и подобранные кабели/автоматы на группах.</summary>
internal static class EomStore
{
    static readonly Guid SettingsId = new("E3D4A9B2-61C8-4F0A-8E3B-7B5C2D1F9A01");
    static readonly Guid CircuitId = new("E3D4A9B2-61C8-4F0A-8E3B-7B5C2D1F9A02");
    static readonly Guid WiringId = new("E3D4A9B2-61C8-4F0A-8E3B-7B5C2D1F9A03");

    static Schema Get(Guid id, string name)
    {
        var schema = Schema.Lookup(id); if (schema != null) return schema;
        var builder = new SchemaBuilder(id); builder.SetSchemaName(name);
        builder.SetReadAccessLevel(AccessLevel.Public); builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("Json", typeof(string));
        return builder.Finish();
    }
    static string? Read(Element element, Guid id)
    {
        var schema = Schema.Lookup(id); if (schema == null) return null;
        var entity = element.GetEntity(schema);
        return entity.IsValid() ? entity.Get<string>("Json") : null;
    }
    static void Write(Element element, Guid id, string name, string json)
    {
        var schema = Get(id, name); var entity = new Entity(schema);
        entity.Set("Json", json); element.SetEntity(entity);
    }

    internal static EomSettings Settings(Document doc) => EomSettings.FromJson(Read(doc.ProjectInformation, SettingsId));
    /// <summary>Требует открытой транзакции.</summary>
    internal static void SaveSettings(Document doc, EomSettings settings) => Write(doc.ProjectInformation, SettingsId, "BotPlusEomSettingsV1", settings.ToJson());

    internal sealed record Choice(string Cable, string Breaker, double CableLengthM);
    internal static Choice? CircuitChoice(Element circuit)
    {
        var json = Read(circuit, CircuitId);
        try { return json == null ? null : System.Text.Json.JsonSerializer.Deserialize<Choice>(json); } catch { return null; }
    }
    internal static void SaveChoice(Element circuit, Choice choice) =>
        Write(circuit, CircuitId, "BotPlusEomCircuitV1", System.Text.Json.JsonSerializer.Serialize(choice));

    /// <summary>Трасса (лоток или короб), к которой привязано обозначение проводки.</summary>
    internal static long? WiringHost(Element symbol) => long.TryParse(Read(symbol, WiringId), out var id) ? id : null;
    internal static void SaveWiringHost(Element symbol, ElementId host) => Write(symbol, WiringId, "BotPlusEomWiringV1", host.Value.ToString());
}
