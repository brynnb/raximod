using System.Globalization;
using System.Text.RegularExpressions;
using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets;

/// <summary>
/// Resolves the complete authored cargo loading contract. Door articulation alone is insufficient:
/// the mount zone owns discovery, prompt/effect selection and accepted vehicle types, while the
/// mount point owns the approach and final attachment constraints.
/// </summary>
public static class VehicleCargoBindings
{
    public sealed record MountPoint(
        int index, string? rotateBone, float? startAngleDegrees, float? endAngleDegrees,
        float? openRateDegrees, float? closeRateDegrees, float? mountRejectDistance,
        string[] physicsBodyCookies, float[]? stopPointOffset, float? dismountDistance,
        float? internalId, float? mountRadius, float? mountRejectAngleDegrees, string? name,
        string[] sidewaysOccupants, bool? renderChildWhenClosed);

    public sealed record MountZone(
        int index, string[] acceptedVehicles, int[] cargoMountPointIndexes, string? effect,
        string? rejectedEffect, string? effectBone, float[]? location, string? mountString,
        float? orientationDegrees);

    public sealed record Sounds(string? open, string? close, string? eject);

    public sealed record Manifest(
        string source, string coordinateSystem, float? ejectionHeight, bool? ejectsPassengers,
        MountPoint[] mountPoints, MountZone[] mountZones, Sounds sounds);

    public static Manifest Resolve(
        IEnumerable<KeyValuePair<string, List<string>>> properties,
        string context,
        Func<string, string?> sound)
    {
        KeyValuePair<string, List<string>>[] values = properties.ToArray();
        string? Scalar(string key) => GameObjectPropertyReader.Scalar(values, context, key);
        string[] List(string key) => GameObjectPropertyReader.List(values, key).ToArray();
        float? Number(string key) => float.TryParse(Scalar(key), NumberStyles.Float,
            CultureInfo.InvariantCulture, out float value) ? value : null;
        float[]? Vector(string key)
        {
            IReadOnlyList<string>? tuple = GameObjectPropertyReader.Tuple(values, context, key, 3);
            return tuple?.Select(value => float.Parse(value, NumberStyles.Float,
                CultureInfo.InvariantCulture)).ToArray();
        }
        bool? Flag(string key) => Scalar(key) switch
        {
            null => null,
            string value when value.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            string value when value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            string value => throw new InvalidDataException($"{context} has non-boolean {key} value '{value}'"),
        };
        int[] Indices(string prefix) => values
            .Select(property => Regex.Match(property.Key, $"^{prefix}(\\d+)_", RegexOptions.IgnoreCase))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct().Order().ToArray();

        MountPoint[] points = Indices("cargomountpoint").Select(index => new MountPoint(
            index,
            Scalar($"cargomountpoint{index}_RotateBone"),
            Number($"cargomountpoint{index}_StartAngle"),
            Number($"cargomountpoint{index}_EndAngle"),
            Number($"cargomountpoint{index}_OpenRate"),
            Number($"cargomountpoint{index}_CloseRate"),
            Number($"cargomountpoint{index}_MountRejectDist"),
            List($"cargomountpoint{index}_PhysicsBodyCookies"),
            Vector($"cargomountpoint{index}_StopPointOffset"),
            Number($"cargomountpoint{index}_dismountdist"),
            Number($"cargomountpoint{index}_internalid"),
            Number($"cargomountpoint{index}_mountradius"),
            Number($"cargomountpoint{index}_mountrejectangle"),
            Scalar($"cargomountpoint{index}_name"),
            List($"cargomountpoint{index}_sidewaysoccupants"),
            Flag($"cargomountpoint{index}_renderchildwhenclosed"))).ToArray();

        MountZone[] zones = Indices("cargomountzone").Select(index => new MountZone(
            index,
            List($"cargomountzone{index}_acceptedvehicles")
                .Concat(List($"cargomountzone{index}_acceptedvehicles2"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            List($"cargomountzone{index}_cargomountpointindexes")
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray(),
            Scalar($"cargomountzone{index}_effect"),
            Scalar($"cargomountzone{index}_effect_na"),
            Scalar($"cargomountzone{index}_effectbone"),
            Vector($"cargomountzone{index}_location"),
            Scalar($"cargomountzone{index}_mountstring"),
            Number($"cargomountzone{index}_zorientation"))).ToArray();

        return new Manifest(
            "game_objects.adb",
            VehicleManifestContract.NativeDataCoordinateSystem,
            Number("cargo_ejection_height"),
            Flag("cargo_ejects_passengers"),
            points,
            zones,
            new Sounds(sound("soundkey_cargo_bay_open"), sound("soundkey_cargo_bay_close"),
                sound("soundkey_cargo_eject")));
    }
}
