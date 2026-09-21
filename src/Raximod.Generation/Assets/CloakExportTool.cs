using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;

namespace Raximod.Generation.Assets;

/// <summary>Native CloakHandler inputs, including complete faction tuples. No Babylon policy.</summary>
public static class CloakExportTool
{
    public static object Profile(GameObjectDb.GameObject record)
    {
        float[] Numbers(string property, int arity)
        {
            if (!record.Properties.TryGetValue(property, out var values) || values.Count != arity)
                throw new InvalidDataException($"{record.Name}.{property}: expected {arity} authored values");
            return values.Select(value => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                && float.IsFinite(number) && number >= 0 ? number
                : throw new InvalidDataException($"{record.Name}.{property}: invalid value {value}")).ToArray();
        }
        bool[] Flags(string property, int arity) => record.Properties.TryGetValue(property, out var values) && values.Count == arity
            ? values.Select(value => bool.TryParse(value, out bool flag) ? flag
                : throw new InvalidDataException($"{record.Name}.{property}: invalid boolean {value}")).ToArray()
            : throw new InvalidDataException($"{record.Name}.{property}: expected {arity} authored values");
        object? Channel(string name) => !record.Properties.ContainsKey($"cloak_{name}_penaltypct") ? null : new
        {
            penalty = Numbers($"cloak_{name}_penaltypct", 3),
            hold = Numbers($"cloak_{name}_postpenaltyduration", 3),
            recovery = Numbers($"cloak_{name}_recoverytime", 3),
        };
        return new
        {
            record.Name,
            factionOrder = new[] { "nc", "tr", "vs" },
            // Original virtual empire getter -> 0(TR):1, 1(NC):0, 2(VS):2 (0x6ab550/0x6aba00).
            additive = Flags("cloak_penalitesareadditive", 3),
            fadeInRate = Numbers("cloak_transition_fadeinrate", 3),
            fadeOutRate = Numbers("cloak_transition_fadeoutrate", 3),
            minimumRange = Numbers("cloak_alphafade_minrange", 1)[0],
            maximumRange = Numbers("cloak_alphafade_maxrange", 1)[0],
            zoom = Numbers("cloak_alphafade_zoom", 1)[0],
            maximumSpeed = Numbers("cloak_movement_speedformaxpenalty", 3),
            movement = Channel("movement"), jumping = Channel("jumping"),
            damage = Channel("damage"), useObject = Channel("useobject"),
            useAvatarCloaking = record.Properties.ContainsKey("cloak_useavatarcloaking") && Flags("cloak_useavatarcloaking", 1)[0],
            startsActive = record.Properties.ContainsKey("cloak_isactive") && Flags("cloak_isactive", 1)[0],
            propertySources = record.PropertySources.Where(pair => pair.Key.StartsWith("cloak_", StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value),
        };
    }

    public static void Run(string planetside, string output)
    {
        string source = Path.Combine(planetside, "startup.pak-out");
        byte[] raw = File.ReadAllBytes(Path.Combine(source, "game_objects.adb"));
        var objects = GameObjectDb.Parse(raw);
        if (!raw.AsSpan().SequenceEqual(objects.Encode())) throw new InvalidDataException("Cloak source round-trip failed");
        var profiles = objects.ResolvedObjects.Where(record => record.Properties.ContainsKey("cloak_movement_penaltypct"))
            .OrderBy(record => record.Name, StringComparer.Ordinal).Select(Profile).ToArray();
        var darklight = objects.ResolvedObjects.Single(record => record.Name == "darklight_vision");
        float Effect(string property)
        {
            var values = darklight.Properties[property];
            if (values.Count != 1 || !float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                || !float.IsFinite(value) || value <= 0) throw new InvalidDataException($"darklight_vision.{property} must be a positive scalar");
            return value;
        }
        var light = NativeLightCatalog.Parse(File.ReadAllBytes(Path.Combine(source, "light3d.adb")))
            .Single(value => value.Name == "darklight_vision");
        Directory.CreateDirectory(output);
        NativeMaterialManifestTool.Write(Path.Combine(output, "cloak.glb"), "cloak",
            [new("cool_stealth", 1, true, false)], new TextureProvider(planetside));
        var document = new
        {
            format = "planetside-native-cloak", version = 1,
            source = new[] { "game_objects.adb", "light3d.adb", "materials.adb", "stages.adb", "renderstate.adb" },
            gameObjectsSha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
            material = "cool_stealth", profiles,
            // Preserve effect_data without assigning an unverified semantic meaning.
            darklight = new { effectData = Effect("effect_data"), range = Effect("effect_data1"), light },
        };
        string path = Path.Combine(output, "cloak.json");
        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (!File.Exists(path) || File.ReadAllText(path) != json) File.WriteAllText(path, json);
        Console.WriteLine($"Cloak: {profiles.Length} native profiles, material stages/textures, Darklight preset -> {output}");
    }
}
