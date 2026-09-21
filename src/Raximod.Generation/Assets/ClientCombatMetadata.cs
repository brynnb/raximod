using System.Globalization;
using System.Text.RegularExpressions;
using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets;

/// <summary>Transport-neutral combat/input values from the owning resolved ADB record.</summary>
public static class ClientCombatMetadata
{
    public static float? Number(GameObjectDb.GameObject source, string key)
    {
        string? raw = GameObjectPropertyReader.Scalar(source, key);
        if (raw is null) return null;
        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            && float.IsFinite(value) && value >= 0) return value;
        throw new InvalidDataException($"{source.Name}: invalid {key}: '{raw}'");
    }

    public static float? ModeNumber(GameObjectDb.GameObject source, int mode, string suffix) =>
        Number(source, $"firemode{mode}_{suffix}");

    public static object? Stamina(GameObjectDb.GameObject source, int mode)
    {
        float? required = ModeNumber(source, mode, "stamina_required");
        float? drain = ModeNumber(source, mode, "stamina_drain");
        if (required is null && drain is null) return null;
        if (required is null || drain is null || required % 1 != 0 || drain % 1 != 0)
            throw new InvalidDataException($"{source.Name}: mode {mode} requires a complete integer stamina pair");
        return new { required, drain };
    }

    public static object Optics(GameObjectDb.GameObject source)
    {
        var levels = source.Properties.Keys.Select(key => (key, match: Regex.Match(key, @"^zoomlevel(\d+)$")))
            .Where(entry => entry.match.Success)
            .Select(entry => (index: int.Parse(entry.match.Groups[1].Value, CultureInfo.InvariantCulture),
                value: Number(source, entry.key)!.Value)).OrderBy(entry => entry.index).ToArray();
        for (int index = 0; index < levels.Length; index++)
            if (levels[index].index != index + 1 || levels[index].value <= 1
                || (index > 0 && levels[index].value <= levels[index - 1].value))
                throw new InvalidDataException($"{source.Name}: zoom levels must be contiguous, increasing magnifications");
        string? ticks = GameObjectPropertyReader.Scalar(source, "showscopeticks");
        if (ticks is not null && !bool.TryParse(ticks, out _))
            throw new InvalidDataException($"{source.Name}: invalid showscopeticks '{ticks}'");
        string? rangefinder = GameObjectPropertyReader.Scalar(source, "is_oicw");
        if (rangefinder is not null && !bool.TryParse(rangefinder, out _))
            throw new InvalidDataException($"{source.Name}: invalid is_oicw '{rangefinder}'");
        return new { magnifications = levels.Select(entry => entry.value).ToArray(),
            showScopeTicks = ticks is not null && bool.Parse(ticks),
            rangefinder = rangefinder is not null && bool.Parse(rangefinder) };
    }

    public static object InfantryCatalog(IEnumerable<GameObjectDb.GameObject> objects) => new
    {
        format = "planetside-infantry-gameplay", version = 1, source = "startup.pak/game_objects.adb",
        armor = objects.Where(source => source.Type.Equals("armor", StringComparison.OrdinalIgnoreCase))
            .OrderBy(source => source.Name, StringComparer.Ordinal)
            .ToDictionary(source => source.Name, Armor, StringComparer.Ordinal),
    };

    private static object Armor(GameObjectDb.GameObject source)
    {
        float? Value(string key) => Number(source, key);
        float Required(string key) => Value(key)
            ?? throw new InvalidDataException($"{source.Name}: missing MAX input field {key}");
        return new
        {
            definition = source.Name, provenance = source.Provenance,
            accuracy = new
            {
                // Native armor offsets +cc/e0/e4: subtracted from mode COFrecovery.
                recoveryBonusMs = Value("recoilaimrecoverybonus"),
                stationaryCrouchRecoveryBonusMs = Value("stationarycrouchrecoilaimrecoverybonus"),
                stationaryRecoveryBonusMs = Value("stationaryrecoilaimrecoverybonus"),
                damagePenaltyPerPoint = Value("damagecofpenalty"),
                maximumDamagePenalty = Value("damagecofpenalty_max"),
                movementPenalties = new
                {
                    jump = Value("jumpcofpenalty"), crouchwalk = Value("crouchwalkcofpenalty"),
                    run = Value("runcofpenalty"), walk = Value("walkcofpenalty"),
                },
            },
            look = Value("heavy_armor_turn_rate") is null ? null : new
            {
                keyboardTurnRate = Value("heavy_armor_turn_rate"),
                yawCapAngleUnitsPerSecond = Required("heavy_armor_turn_rate_cap"),
                // Original 3.15.84.0 armor constructor 0x94d9b0, +b4/+b8.
                // ADB omits both pitch values; these are recovered engine defaults.
                keyboardPitchRate = Value("heavy_armor_pitch_rate"),
                keyboardPitchDefaultAngleUnitsPerSecond = 0x1555,
                pitchCapAngleUnitsPerSecond = Value("heavy_armor_pitch_rate_cap") ?? 5000,
                pitchCapProvenance = Value("heavy_armor_pitch_rate_cap") is null
                    ? "planetside.exe 3.15.84.0 0x94da9d armor+b8 default" : "ADB heavy_armor_pitch_rate_cap",
                minimumYawFactor = Required("min_turn_rate"), maximumYawFactor = Required("max_turn_rate"),
                minimumPitchFactor = Required("min_pitch_rate"), maximumPitchFactor = Required("max_pitch_rate"),
                walkForwardSpeed = Required("walk_forward_speed"), runForwardSpeed = Required("run_forward_speed"),
                overdriveYawMultiplier = Value("overdrive_turn_rate_multiplier"),
                overdrivePitchMultiplier = Value("overdrive_pitch_rate_multiplier"),
            },
        };
    }
}
