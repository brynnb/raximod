using System.Globalization;
using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets;

/// <summary>Transport-neutral native lift actions. Names are action bindings, not
/// suffixes inferred from the exported mesh (a shared mesh can contain both pad families).</summary>
public static class VehicleCreationPadBindings
{
    public sealed record Animation(string Name, float DurationMs);
    public sealed record Binding(string Record, string AttachBone, Animation Open, Animation Close,
        Animation? Active, string? ActiveEffect, bool? ChildVisibleOnAdd, float? ChildFadeInRate,
        bool? ServerTriggersClose, string? AlternateAttachBone, float[]? CreationViewpoint,
        string Source);

    public static Binding[] Resolve(IEnumerable<GameObjectDb.GameObject> records) => records
        .Where(record => GameObjectPropertyReader.Scalar(record, "type") == "vehicle_creation_pad")
        // pad_create and spawnpoint_vehicle declare a type/volume only, not
        // an animated mechanism. Partial bindings still fail Resolve below.
        .Where(record => new[] { "animattachbonename", "animname_open", "animname_close" }
            .Any(property => GameObjectPropertyReader.Scalar(record, property) is not null))
        .OrderBy(record => record.Name, StringComparer.Ordinal)
        .Select(Resolve).ToArray();

    public static Binding Resolve(GameObjectDb.GameObject record)
    {
        string? Value(string property) => GameObjectPropertyReader.Scalar(record, property);
        string Required(string property) => Value(property)
            ?? throw new InvalidDataException($"{record.Name} has no {property}");
        float? Number(string property) => Value(property) is { } value
            ? float.Parse(value, CultureInfo.InvariantCulture) : null;
        bool? Boolean(string property) => Value(property) is { } value ? bool.Parse(value) : null;
        Animation Clip(string name, string duration)
        {
            float milliseconds = (Number(duration)
                ?? throw new InvalidDataException($"{record.Name} has no {duration}")) * 1000;
            if (!float.IsFinite(milliseconds) || milliseconds <= 0)
                throw new InvalidDataException($"{record.Name} {duration} is not a positive duration");
            return new Animation(Required(name), milliseconds);
        }
        float[]? viewpoint = GameObjectPropertyReader.Tuple(record, "vehiclecreationviewpointpos", 3)?
            .Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (viewpoint?.Any(value => !float.IsFinite(value)) == true)
            throw new InvalidDataException($"{record.Name} has a non-finite creation viewpoint");
        float? fadeRate = Number("child_fadein_rate");
        if (fadeRate is { } rate && (!float.IsFinite(rate) || rate <= 0))
            throw new InvalidDataException($"{record.Name} has an invalid child fade-in rate");
        return new Binding(record.Name, Required("animattachbonename"),
            Clip("animname_open", "desired_anim_open_duration"),
            Clip("animname_close", "desired_anim_close_duration"),
            Value("activeanim_name") is null ? null : Clip("activeanim_name", "activeanim_length"),
            Value("active_effect"), Boolean("child_visible_onadd"), fadeRate,
            Boolean("server_triggers_close_anim"), Value("altanimattachbonename"), viewpoint,
            "game_objects.adb resolved " + record.Name);
    }
}
