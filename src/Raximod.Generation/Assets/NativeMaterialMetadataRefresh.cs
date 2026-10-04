using System.Text.Json;
using System.Text.Json.Nodes;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Assets;

/// <summary>Upgrade resolved metadata without rebuilding geometry, textures or material identities.</summary>
public static class NativeMaterialMetadataRefresh
{
    public sealed record SubmissionSection(uint MeshId, uint SectionId, string Material);

    // Retail 0x9d2195 stores sections by ID, not their archive read order.
    // 0x9d5d20 visits those slots forwards; its alpha material batches are
    // flushed FIFO at 0xa195f0. mat_sortkey belongs to a different draw queue.
    internal static SubmissionSection[] OrderedSections(IEnumerable<SubmissionSection> sections) =>
        sections.GroupBy(section => section.MeshId)
            .SelectMany(mesh => mesh.OrderBy(section => section.SectionId)).ToArray();

    public static bool SubmissionOrder(string path, UberModel.MeshSystem system)
    {
        var document = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException($"{path}: missing material document");
        if (document["format"]?.GetValue<string>() != "raxicore-native-materials"
            || document["materials"] is not JsonArray materials)
            throw new InvalidDataException($"{path}: unsupported native material document");
        var used = materials.Where(material => material!["sections"]!.GetValue<int>() > 0)
            .ToDictionary(material => material!["name"]!.GetValue<string>(),
                material => material!["sections"]!.GetValue<int>(), StringComparer.OrdinalIgnoreCase);
        // Existing geometry, rather than today's selection policy, determines
        // which mesh IDs belong to a metadata-only refresh. Otherwise an omitted
        // distance LOD could take a retained material's first-submission slot.
        string selectionPath = path.Replace(".materials.json", ".mesh-selection.json", StringComparison.Ordinal);
        HashSet<int> kept;
        if (File.Exists(selectionPath))
        {
            var selection = JsonNode.Parse(File.ReadAllText(selectionPath))!;
            if (selection["format"]?.GetValue<string>() != "raxicore-highest-quality-mesh-selection"
                || selection["candidates"] is not JsonArray candidates
                || candidates.Count != system.Meshes.Count
                || !candidates.Select(candidate => candidate!["meshIndex"]!.GetValue<int>())
                    .Order().SequenceEqual(Enumerable.Range(0, system.Meshes.Count)))
                throw new InvalidDataException($"{path}: mesh-selection companion does not match the native record");
            kept = candidates.Where(candidate => candidate!["keep"]!.GetValue<bool>())
                .Select(candidate => candidate!["meshIndex"]!.GetValue<int>()).ToHashSet();
        }
        else
        {
            // A single native mesh is unambiguous. Multiple legacy meshes may
            // have been selectively exported: a count match cannot identify
            // them, and today's LOD policy is not historical export provenance.
            if (system.Meshes.Count != 1)
                throw new InvalidDataException($"{path}: multiple source meshes without a selection receipt; regenerate the model");
            kept = new HashSet<int> { 0 };
        }
        var sections = system.Meshes.Where((_, index) => kept.Contains(index))
            .SelectMany(mesh => mesh.Sections
            .Where(section => used.ContainsKey(section.MaterialName) && section.VertexCount > 0 && section.IndexCount >= 3)
            .Select(section => new SubmissionSection(mesh.Id, section.Id, section.MaterialName))).ToArray();
        // Material-only refresh counts merged GLB primitives, whereas a full
        // export counts native sections. Use that count only to identify used
        // materials; the selection receipt provides actual geometry ownership.
        var missing = used.Keys.Except(sections.Select(section => section.Material), StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length != 0)
            throw new InvalidDataException($"{path}: exported materials have no native section identity; regenerate the model: {string.Join(", ", missing)}");
        var order = JsonSerializer.SerializeToNode(OrderedSections(sections),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (JsonNode.DeepEquals(document["submissionOrder"], order)) return false;
        document["submissionOrder"] = order;
        File.WriteAllText(path, document.ToJsonString());
        return true;
    }

    /// <summary>Upgrade existing geometry companions through the source decoder,
    /// without re-encoding textures or replacing GLB geometry/animations.</summary>
    public static int SubmissionOrders(string source, string output, string? record = null, string? library = null,
        bool selectionReceiptsOnly = false)
    {
        var paths = Directory.Exists(output)
            ? Directory.GetFiles(output, "*.materials.json").Order(StringComparer.Ordinal).ToArray()
            : new[] { Path.ChangeExtension(output, ".materials.json") };
        if (selectionReceiptsOnly)
        {
            if (!Directory.Exists(output) || record is not null)
                throw new ArgumentException("--selection-receipts-only requires a directory and no --record.");
            var verified = paths.Where(path => File.Exists(path.Replace(".materials.json", ".mesh-selection.json", StringComparison.Ordinal))).ToArray();
            Console.WriteLine($"native submission refresh: {verified.Length} geometry receipts; {paths.Length - verified.Length} legacy companions excluded (regenerate before upgrading)");
            paths = verified;
        }
        string? loadedLibrary = null;
        UberModel? model = null;
        int changed = 0;
        foreach (string path in paths)
        {
            string identity = record ?? JsonNode.Parse(File.ReadAllText(path))!["record"]!.GetValue<string>();
            string coveragePath = path.Replace(".materials.json", ".source-coverage.json", StringComparison.Ordinal);
            string? sourceLibrary = library ?? (File.Exists(coveragePath)
                ? JsonNode.Parse(File.ReadAllText(coveragePath))?["library"]?.GetValue<string>() : null);
            string selected = GlbExportTool.FindLibrary(source, sourceLibrary, identity);
            if (selected != loadedLibrary)
            {
                model = UberModel.Load(File.ReadAllBytes(selected));
                loadedLibrary = selected;
            }
            var system = model!.FetchMeshSystem(identity)
                ?? throw new InvalidDataException($"{path}: native record {identity} could not be decoded");
            if (SubmissionOrder(path, system)) changed++;
        }
        return changed;
    }

    public static bool RenderStates(string path, TextureProvider textures)
    {
        var document = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException($"{path}: missing material document");
        if (document["format"]?.GetValue<string>() != "raxicore-native-materials"
            || document["materials"] is not JsonArray materials)
            throw new InvalidDataException($"{path}: unsupported native material document");
        bool changed = false;
        foreach (var material in materials)
        foreach (string prefix in new[] { "section", "base" })
        {
            if (material is not JsonObject definition || definition[prefix + "Commands"] is not JsonArray commands)
                throw new InvalidDataException($"{path}: missing native {prefix} commands");
            var source = commands.Select(command => new AsciiCommandDatabase.Command(
                command!["name"]!.GetValue<string>(),
                command["arguments"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray())).ToArray();
            // Reuse the full exporter's native preset lookup and ordering. A stale
            // sidecar is not permission to guess blending or drop unknown commands.
            var resolved = JsonSerializer.SerializeToNode(NativeMaterialManifestTool.RenderStates(source, textures))!.AsArray();
            foreach (var preset in resolved)
                if (preset!["resolved"]!.GetValue<bool>() != true)
                    throw new InvalidDataException($"{path}: unresolved renderstate.adb preset {preset["name"]}");
            if (JsonNode.DeepEquals(definition[prefix + "RenderStates"], resolved)) continue;
            definition[prefix + "RenderStates"] = resolved;
            changed = true;
        }
        // Validate every reference before replacing the original sidecar. All
        // textures, stage programs, faction swaps and unknown metadata survive.
        if (changed) File.WriteAllText(path, document.ToJsonString());
        return changed;
    }
}
