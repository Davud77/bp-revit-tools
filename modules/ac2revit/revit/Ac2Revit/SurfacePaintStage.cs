using Autodesk.Revit.DB;
using System.Text.Json;

namespace Ac2Revit;

/// <summary>
/// Archicad lets every wall, slab and roof override the surface of each side (reference side, opposite side, edges /
/// top, bottom, edges). A Revit layer material has only one colour for all walls of a type, so those overrides were
/// lost: the facade took the colour of the composite's building material. This stage paints the overridden sides with
/// Revit's face paint, using one material per Archicad surface. Sides that are not overridden keep the colour of the
/// layer material, as in Archicad.
/// </summary>
public static class SurfacePaintStage
{
    sealed record Job(string Key, Element Element, string Kind, (string Role, string Surface)[] Overrides);

    public static Report Run(Document doc, Manifest manifest, string project, Action<string, int, int>? progress = null)
    {
        var report = new Report { ProjectId = project, Mode = "surfacePaint" };
        var surfaces = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in manifest.Surfaces)
            if (s.TryGetProperty("attributeId", out var id) && s.TryGetProperty("surfaceColor", out _)) surfaces[id.S("guid")] = s;
        var index = Storage.Index(doc, project);
        var jobs = new List<Job>();
        foreach (var e in manifest.Elements)
        {
            if (e.Kind is not ("Wall" or "Slab" or "Roof")) continue;
            if (e.Raw.ValueKind != JsonValueKind.Object || !e.Raw.TryGetProperty("details", out var details)) continue;
            if (!index.TryGetValue(e.SourceGuid, out var element)) continue;
            string[] fields = e.Kind == "Wall" ? ["referenceMaterial", "oppositeMaterial", "sideMaterial"] : ["topMaterial", "bottomMaterial", "sideMaterial"];
            string[] roles = e.Kind == "Wall" ? ["exterior", "interior", "edges"] : ["top", "bottom", "edges"];
            bool native = element is Wall || element is Floor || element is RoofBase;
            if (!native) continue;
            var found = new List<(string, string)>();
            for (int i = 0; i < fields.Length; ++i)
                if (details.TryGetProperty(fields[i], out var m) && m.TryGetProperty("overridden", out var o) && o.ValueKind == JsonValueKind.True
                    && m.TryGetProperty("attributeId", out var attribute) && surfaces.ContainsKey(attribute.S("guid")))
                    found.Add((roles[i], attribute.S("guid")));
            if (found.Count > 0) jobs.Add(new Job(e.SourceGuid, element, e.Kind, found.ToArray()));
        }
        if (jobs.Count == 0) { report.Add("surface-paint", "SurfacePaint", "notice", "No wall, slab or roof overrides a surface."); return report; }

        var materials = EnsureMaterials(doc, surfaces, jobs.SelectMany(j => j.Overrides).Select(o => o.Surface).Distinct(StringComparer.OrdinalIgnoreCase));
        int painted = 0, failed = 0, done = 0;
        var readClock = new System.Diagnostics.Stopwatch(); var paintClock = new System.Diagnostics.Stopwatch(); var commitClock = new System.Diagnostics.Stopwatch();
        foreach (var batch in jobs.Chunk(400))
        {
            using var transaction = new Transaction(doc, "Archicad surface colours");
            transaction.Start();
            foreach (var job in batch)
            {
                if (done++ % 100 == 0) progress?.Invoke("surfacePaint", done - 1, jobs.Count);
                try
                {
                    readClock.Start(); var faces = Faces(doc, job.Element); readClock.Stop();
                    foreach (var (role, surface) in job.Overrides)
                    {
                        if (!faces.TryGetValue(role, out var list) || list.Count == 0) { failed++; report.Add(job.Key, "SurfacePaint", "notice", "Side " + role + " has no face to paint"); continue; }
                        foreach (var face in list)
                        {
                            try { paintClock.Start(); doc.Paint(job.Element.Id, face, materials[surface]); paintClock.Stop(); painted++; }
                            catch (Exception ex) { failed++; if (failed <= 50) report.Add(job.Key, "SurfacePaint", "notice", "Face of side " + role + " cannot be painted: " + ex.Message); }
                        }
                    }
                }
                catch (Exception ex) { failed++; report.Add(job.Key, "SurfacePaint", "notice", ex.Message); }
            }
            commitClock.Start(); var committed = transaction.Commit(); commitClock.Stop();
            if (committed != TransactionStatus.Committed) throw new InvalidDataException("Surface paint transaction rolled back");
        }
        progress?.Invoke("surfacePaint", jobs.Count, jobs.Count);
        report.Add("surface-paint", "SurfacePaint", "updated",
            $"{jobs.Count} walls, slabs and roofs with overridden Archicad surfaces: {painted} faces painted with {materials.Count} surface materials, {failed} sides not painted. Time: reading faces {readClock.Elapsed.TotalSeconds:0.#} s, painting {paintClock.Elapsed.TotalSeconds:0.#} s, commits {commitClock.Elapsed.TotalSeconds:0.#} s.");
        return report;
    }

    static Dictionary<string, ElementId> EnsureMaterials(Document doc, IReadOnlyDictionary<string, JsonElement> surfaces, IEnumerable<string> wanted)
    {
        var existing = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First().Id);
        var result = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
        using var transaction = new Transaction(doc, "Archicad surface materials");
        transaction.Start();
        foreach (var guid in wanted)
        {
            var s = surfaces[guid]; var c = s.GetProperty("surfaceColor");
            byte Channel(string name) => (byte)Math.Clamp((int)Math.Round(c.D(name) * 255, MidpointRounding.AwayFromZero), 0, 255);
            var color = new Color(Channel("red"), Channel("green"), Channel("blue"));
            int transparency = (int)Math.Clamp(Math.Round(s.D("transparency")), 0, 100);
            string name = SourceLabel.Format($"Поверхность {s.S("name")} #{color.Red:X2}{color.Green:X2}{color.Blue:X2}" + (transparency > 0 ? $" прозр. {transparency}%" : ""));
            if (!existing.TryGetValue(name, out var id))
            {
                id = Material.Create(doc, name);
                var material = (Material)doc.GetElement(id);
                material.Color = color; material.Transparency = transparency;
                existing[name] = id;
            }
            result[guid] = id;
        }
        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Surface materials were not committed");
        return result;
    }

    /// <summary>The faces of an element by Archicad side: exterior/interior for walls, top/bottom for slabs and roofs, and all others as edges.</summary>
    static Dictionary<string, List<Face>> Faces(Document doc, Element element)
    {
        var result = new Dictionary<string, List<Face>> { ["exterior"] = new(), ["interior"] = new(), ["top"] = new(), ["bottom"] = new(), ["edges"] = new() };
        var named = new HashSet<string>();
        void Add(string role, IEnumerable<Reference> references)
        {
            foreach (var reference in references)
                if (element.GetGeometryObjectFromReference(reference) is Face face) { result[role].Add(face); named.Add(reference.ConvertToStableRepresentation(doc)); }
        }
        if (element is HostObject host)
        {
            if (element is Wall)
            {
                Add("exterior", HostObjectUtils.GetSideFaces(host, ShellLayerType.Exterior));
                Add("interior", HostObjectUtils.GetSideFaces(host, ShellLayerType.Interior));
            }
            else
            {
                Add("top", HostObjectUtils.GetTopFaces(host));
                Add("bottom", HostObjectUtils.GetBottomFaces(host));
            }
        }
        using var geometry = element.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine });
        void Collect(GeometryElement g)
        {
            foreach (var o in g)
                if (o is GeometryInstance instance) { using var inner = instance.GetInstanceGeometry(); Collect(inner); }
                else if (o is Solid solid)
                    foreach (Face face in solid.Faces)
                        if (face.Reference != null && !named.Contains(face.Reference.ConvertToStableRepresentation(doc))) result["edges"].Add(face);
        }
        if (geometry != null) Collect(geometry);
        return result;
    }
}
