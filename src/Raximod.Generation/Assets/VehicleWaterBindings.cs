using System.Globalization;
using System.Text.Json.Serialization;
using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets;

/// <summary>Water movement metadata from the resolved gameplay definition, never the visual model alias.</summary>
public static class VehicleWaterBindings
{
    private static readonly string[] Fields = [
        "water_maxdragdepth", "water_maxspeedpercentage", "water_floatsatmaxdragdepth",
        "water_maxspeedpercentage_hover_secondstil", "water_disableatmaxdepth",
        "water_underwaterlifespan", "water_underwaterlifespanrecovery", "water_check_necessary",
        "wantswater", "ishovervehicle",
    ];

    public sealed record Source(
        [property: JsonPropertyName("definedBy")] string DefinedBy,
        [property: JsonPropertyName("commandIndex")] int CommandIndex,
        [property: JsonPropertyName("streamOffset")] int StreamOffset);

    public sealed record Water(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("definition")] string Definition,
        [property: JsonPropertyName("maxDragDepth")] float? MaxDragDepth,
        [property: JsonPropertyName("maxSpeedFraction")] float? MaxSpeedFraction,
        [property: JsonPropertyName("floatsAtMaxDragDepth")] bool? FloatsAtMaxDragDepth,
        [property: JsonPropertyName("hoverSecondsToMaxDrag")] float? HoverSecondsToMaxDrag,
        [property: JsonPropertyName("disableAtMaxDepth")] bool? DisableAtMaxDepth,
        [property: JsonPropertyName("underwaterLifespanSeconds")] float? UnderwaterLifespanSeconds,
        [property: JsonPropertyName("underwaterRecoverySeconds")] float? UnderwaterRecoverySeconds,
        [property: JsonPropertyName("checkNecessary")] bool? CheckNecessary,
        [property: JsonPropertyName("wantsWater")] bool? WantsWater,
        [property: JsonPropertyName("isHoverVehicle")] bool? IsHoverVehicle,
        [property: JsonPropertyName("propertySources")] IReadOnlyDictionary<string, Source> PropertySources);

    public static Water? Resolve(GameObjectDb.GameObject definition)
    {
        if (!Fields.Any(definition.Properties.ContainsKey)) return null;
        string? Value(string field) => GameObjectPropertyReader.Scalar(definition, field);
        float? Number(string field)
        {
            string? value = Value(field);
            if (value is null) return null;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                || !float.IsFinite(number))
                throw new InvalidDataException($"Vehicle '{definition.Name}' has invalid {field}: '{value}'");
            return number;
        }
        bool? Flag(string field) => Value(field) switch
        {
            null => null,
            string value when value.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            string value when value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            string value => throw new InvalidDataException($"Vehicle '{definition.Name}' has invalid {field}: '{value}'"),
        };
        var sources = new SortedDictionary<string, Source>(StringComparer.Ordinal);
        foreach (string field in Fields.Where(definition.Properties.ContainsKey))
        {
            if (!definition.PropertySources.TryGetValue(field, out var source))
                throw new InvalidDataException($"Vehicle '{definition.Name}' has no source provenance for {field}");
            sources.Add(field, new(source.DefinedBy, source.CommandIndex, source.StreamOffset));
        }
        // Null means unauthored, not an inferred default. In particular, -1
        // is the native indefinite underwater lifespan and must survive export.
        // Source model aliases lose the variants' 0.64 speed overrides.
        return new("game_objects.adb", definition.Name,
            Number("water_maxdragdepth"), Number("water_maxspeedpercentage"),
            Flag("water_floatsatmaxdragdepth"), Number("water_maxspeedpercentage_hover_secondstil"),
            Flag("water_disableatmaxdepth"), Number("water_underwaterlifespan"),
            Number("water_underwaterlifespanrecovery"), Flag("water_check_necessary"),
            Flag("wantswater"), Flag("ishovervehicle"), sources);
    }
}
