using System.Text.Json;
using System.Text.Json.Nodes;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;

namespace Raximod.Generation.Assets;

/// <summary>Upgrade resolved metadata without rebuilding geometry, textures or material identities.</summary>
public static class NativeMaterialMetadataRefresh
{
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
