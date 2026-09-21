using System.Text.Json;
using Raximod.EngineAssets.Textures;
using Raximod.Generation.Assets;
using Raximod.Generation.Effects;

namespace Raximod.Generation.Continents;

/// <summary>A dependency-scoped sky bundle using the ordinary effect and material exporters.</summary>
public static class SkyEffectAssets
{
    public static EffectGraphExportTool.Effect[] Select(
        IEnumerable<EffectGraphExportTool.Effect> effects, IEnumerable<string> roots)
    {
        var byName = effects.ToDictionary(effect => effect.Name, StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(roots);
        while (pending.TryDequeue(out string? name))
        {
            if (!selected.Add(name)) continue;
            if (!byName.TryGetValue(name, out var effect))
                throw new InvalidDataException($"Sky references missing effect '{name}'");
            foreach (var link in effect.Links) pending.Enqueue(link.Target);
        }
        return selected.Order(StringComparer.OrdinalIgnoreCase).Select(name => byName[name]).ToArray();
    }

    public static void Export(string planetside, string directory, IEnumerable<string> roots,
        TextureProvider textures)
    {
        var effects = Select(EffectGraphExportTool.ReadEffects(planetside), roots);
        // The installed sky corpus is entirely sprite layers. Report a new mesh
        // dependency rather than publish a manifest whose referenced mesh is absent.
        var meshes = effects.SelectMany(effect => effect.Meshes)
            .Where(name => !name.Equals("null", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        if (meshes.Length > 0)
            throw new InvalidDataException($"Sky effect mesh export is not implemented: {string.Join(", ", meshes)}");
        var materials = effects.SelectMany(effect => effect.Materials)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string name in materials)
            if (textures.MaterialCommands?.Lookup(name) == null)
                throw new InvalidDataException($"Sky effect references missing material '{name}'");
        NativeMaterialManifestTool.Write(Path.Combine(directory, "sky-effects.glb"), "sky-effects",
            materials.Select(name => new NativeMaterialManifestTool.Usage(name, 1, true, false)).ToArray(), textures);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "sky-effects.materials.json")));
        var missing = manifest.RootElement.GetProperty("missingTextures").EnumerateArray().ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"Sky effect textures are missing: {string.Join(", ", missing.Select(item => item.GetString()))}");
        File.WriteAllText(Path.Combine(directory, "sky-effects.json"), JsonSerializer.Serialize(new
        {
            format = "raxicore-effect-graph", version = 2, effects
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
