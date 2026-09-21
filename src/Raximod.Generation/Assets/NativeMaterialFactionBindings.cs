using System.Runtime.CompilerServices;
using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;

namespace Raximod.Generation.Assets;

/// <summary>Package material swaps are explicit identities, not texture filename substitutions.</summary>
public static class NativeMaterialFactionBindings
{
    private static readonly ConditionalWeakTable<TextureProvider, Dictionary<string, NativeEffectPackage>> Packages = new();

    public static bool IsEmpireScope(string scope) =>
        scope.Equals("nc", StringComparison.OrdinalIgnoreCase)
        || scope.Equals("tr", StringComparison.OrdinalIgnoreCase)
        || scope.Equals("vs", StringComparison.OrdinalIgnoreCase);

    internal static NativeEffectPackage? Load(string record, TextureProvider textures)
    {
        var packages = Packages.GetValue(textures, provider =>
        {
            string path = Path.Combine(provider.AssetDirectory ?? "", "startup.pak-out", "epackage.adb");
            return File.Exists(path)
                ? NativeEffectPackageCatalog.Parse(File.ReadAllBytes(path))
                    .ToDictionary(package => package.Name, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        });
        return packages.GetValueOrDefault(record);
    }

    /// <summary>Regenerate native materials from an existing GLB's exact material/UV usage.
    /// Reads only its JSON chunk; leaves geometry, animation and embedded images untouched.</summary>
    public static void Refresh(string path, string record, TextureProvider textures, string? sharedTextures = null)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x46546c67 || reader.ReadUInt32() != 2 || reader.ReadUInt32() != stream.Length)
            throw new InvalidDataException($"{path}: invalid GLB header");
        int length = checked((int)reader.ReadUInt32());
        if (reader.ReadUInt32() != 0x4e4f534a || length > stream.Length - stream.Position)
            throw new InvalidDataException($"{path}: invalid GLB JSON chunk");
        using var json = JsonDocument.Parse(reader.ReadBytes(length));
        var names = json.RootElement.GetProperty("materials").EnumerateArray()
            .Select(material => material.GetProperty("name").GetString()
                ?? throw new InvalidDataException($"{path}: unnamed native material")).ToArray();
        var usages = new Dictionary<string, NativeMaterialManifestTool.Usage>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in json.RootElement.GetProperty("meshes").EnumerateArray())
        foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
        {
            string name = names[primitive.GetProperty("material").GetInt32()];
            var attributes = primitive.GetProperty("attributes");
            var usage = usages.GetValueOrDefault(name, new(name, 0, false, false));
            usages[name] = usage with { Sections = usage.Sections + 1,
                HasUv0 = usage.HasUv0 || attributes.TryGetProperty("TEXCOORD_0", out _),
                HasUv1 = usage.HasUv1 || attributes.TryGetProperty("TEXCOORD_1", out _) };
        }
        NativeMaterialManifestTool.Write(path, record, usages.Values.ToArray(), textures, sharedTextures);
    }
}
