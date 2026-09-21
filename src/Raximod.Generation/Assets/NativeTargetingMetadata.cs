using System.Globalization;
using System.Text.Json.Serialization;
using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets;

/// <summary>Original object targeting properties, independent of rendering and collision bounds.</summary>
public static class NativeTargetingMetadata
{
    private static readonly string[] Fields = [
        "radius", "sphere_offset", "autolock_steer_toward_centroid", "targetpos_bone", "targetpos_offset",
    ];

    public sealed record PropertySource(
        [property: JsonPropertyName("definedBy")] string DefinedBy,
        [property: JsonPropertyName("commandIndex")] int CommandIndex,
        [property: JsonPropertyName("streamOffset")] int StreamOffset);

    public sealed record Targeting(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("definition")] string Definition,
        [property: JsonPropertyName("coordinateSystem")] string CoordinateSystem,
        [property: JsonPropertyName("radius")] float Radius,
        [property: JsonPropertyName("sphereOffset")] float[] SphereOffset,
        [property: JsonPropertyName("steerTowardCentroid")] bool? SteerTowardCentroid,
        [property: JsonPropertyName("targetBone")] string? TargetBone,
        [property: JsonPropertyName("targetOffset")] float[]? TargetOffset,
        [property: JsonPropertyName("propertySources")] IReadOnlyDictionary<string, PropertySource> PropertySources);

    public static Targeting Resolve(GameObjectDb.GameObject definition, IEnumerable<string>? modelNodes = null)
    {
        string? Scalar(string key) => GameObjectPropertyReader.Scalar(definition, key);
        float Number(string key, string value)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
                || !float.IsFinite(result))
                throw new InvalidDataException($"Target '{definition.Name}' has invalid {key}: '{value}'");
            return result;
        }
        float[]? Vector(string key) => GameObjectPropertyReader.Tuple(definition, key, 3)?
            .Select(value => Number(key, value)).ToArray();
        float radius = Number("radius", Scalar("radius") ?? "missing");
        if (radius <= 0) throw new InvalidDataException($"Target '{definition.Name}' requires a positive radius");
        float[] sphere = Vector("sphere_offset")
            ?? throw new InvalidDataException($"Target '{definition.Name}' is missing sphere_offset");
        bool? centroid = Scalar("autolock_steer_toward_centroid") switch
        {
            null => null,
            string value when value.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            string value when value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            string value => throw new InvalidDataException($"Target '{definition.Name}' has invalid centroid flag '{value}'"),
        };
        string? bone = Scalar("targetpos_bone");
        // Never invent an alias from a similarly named body. The current exported
        // corpus contains every authored target bone; missing ones are export defects.
        if (bone != null && modelNodes != null && !modelNodes.Contains(bone, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Target '{definition.Name}' model is missing targetpos_bone '{bone}'");
        var sources = new SortedDictionary<string, PropertySource>(StringComparer.Ordinal);
        foreach (string field in Fields.Where(definition.Properties.ContainsKey))
        {
            if (!definition.PropertySources.TryGetValue(field, out var source))
                throw new InvalidDataException($"Target '{definition.Name}' has no provenance for {field}");
            sources.Add(field, new(source.DefinedBy, source.CommandIndex, source.StreamOffset));
        }
        return new("game_objects.adb", definition.Name, VehicleManifestContract.NativeDataCoordinateSystem,
            radius, sphere, centroid, bone, Vector("targetpos_offset"), sources);
    }
}
