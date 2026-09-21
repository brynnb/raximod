using System.Globalization;
using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets;

/// <summary>
/// Builds the transport-neutral projectile contract shared by the infantry and vehicle exporters.
/// All values come from resolved <c>game_objects.adb</c> records; the only derived value is the
/// explicit, auditable <c>requiresProjectileFlight</c> classification.
/// </summary>
public static class ProjectileBehaviorMetadata
{
    public static Dictionary<string, object?> ExportAmmunition(
        string ammunitionDefinition,
        GameObjectDb.GameObject? ammunition,
        AmmunitionProjectileResolver.Result resolution,
        IReadOnlyDictionary<string, GameObjectDb.GameObject> objects)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["definition"] = ammunitionDefinition,
            ["provenance"] = ammunition?.Provenance,
            ["projectileDefinition"] = resolution.ProjectileName,
            ["projectileProvenance"] = resolution.Projectile?.Provenance,
        };
        AddProjectileFields(
            result,
            resolution.Projectile,
            // projectileN records are sibling selections, not an inheritance chain. Sparse
            // variants must remain sparse unless game_objects.adb gives them a real parent;
            // otherwise ancient_ammo_vehicle, for example, leaks the Scythe slave onto Flail.
            null,
            objects,
            includeReferences: true);
        return result;
    }

    public static IReadOnlyList<string> FlightReasons(
        GameObjectDb.GameObject? projectile,
        GameObjectDb.GameObject? fallback = null)
    {
        var reasons = new List<string>();
        string? Value(string name) => Scalar(projectile, fallback, name);
        float? Number(string name) => Float(projectile, fallback, name);
        bool Flag(string name) => Boolean(projectile, fallback, name);
        void Add(bool condition, string reason)
        {
            if (condition) reasons.Add(reason);
        }

        Add(Flag("exists_on_remote_clients"), "existsOnRemoteClients");
        Add(Flag("grenade_projectile"), "grenadeProjectile");
        Add(Flag("hasgravity"), "hasGravity");
        Add((Number("bounce_count") ?? 0f) > 0f, "bounce");
        Add(Flag("player_guided"), "playerGuided");
        Add(Flag("autolock"), "autoLock");
        Add(Flag("ballistic_bomb"), "ballisticBomb");
        Add(Flag("multi_stage"), "multiStage");
        Add(Flag("flak_projectile"), "flakProjectile");
        Add(Flag("long_range_projectile"), "longRangeProjectile");
        Add(Flag("network_behavior_on_detonate_only"), "networkOnDetonateOnly");
        Add(!string.IsNullOrWhiteSpace(Value("damage_proxy")), "damageProxy");
        Add((Number("slave_count") ?? 0f) > 0f, "linkedProjectile");
        Add((Number("lash_radius") ?? 0f) > 0f, "lash");
        Add((Number("acceleration") ?? 0f) != 0f, "acceleration");
        Add((Number("minimum_detonation_distance") ?? 0f) > 0f, "minimumDetonationDistance");
        Add(IsAction(Value("on_life_expired")), "lifespanAction");
        Add(IsAction(Value("on_user_input")), "userInputAction");
        return reasons;
    }

    private static void AddProjectileFields(
        Dictionary<string, object?> target,
        GameObjectDb.GameObject? projectile,
        GameObjectDb.GameObject? fallback,
        IReadOnlyDictionary<string, GameObjectDb.GameObject> objects,
        bool includeReferences)
    {
        string? Value(string name) => Scalar(projectile, fallback, name);
        float? Number(string name) => Float(projectile, fallback, name);
        bool Flag(string name) => Boolean(projectile, fallback, name);
        float[]? Vector(string name) => Vector3(projectile, fallback, name);

        float? initialVelocity = Number("initial_velocity");
        float? acceleration = Number("acceleration");
        float? accelerationUntil = Number("acceleration_until");
        float? lifespan = Number("lifespan");
        float? maximumRange = MaximumRange(initialVelocity, acceleration, accelerationUntil, lifespan);
        IReadOnlyList<string> flightReasons = FlightReasons(projectile, fallback);
        string? damageProxyDefinition = Value("damage_proxy");
        string? secondaryDefinition = Value("secondary_projectile_name");
        string? slaveDefinition = Value("slave_type");

        object[] secondaryLayout = Enumerable.Range(0, 16)
            .Select(index => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = index,
                ["distance"] = Number($"secondary_projectile_info{index}_distance"),
                ["orientationDegrees"] = Number($"secondary_projectile_info{index}_orientation"),
            })
            .Where(item => item["distance"] is not null || item["orientationDegrees"] is not null)
            .Cast<object>()
            .ToArray();

        Set(target, "projectileEffect", Value("projectile_effect"));
        Set(target, "hitEffect", Value("hiteffect"));
        Set(target, "alternateHitEffect", Value("alternate_hit_effect"));
        Set(target, "avatarHitEffect", Value("avatar_hit_effect"));
        Set(target, "heavyHitEffect", Value("heavy_hit_effect"));
        Set(target, "vehicleHitEffect", Value("vehicle_hit_effect"));
        Set(target, "splashEffect", Value("splash_effect"));
        Set(target, "dudEffect", Value("dud_effect"));
        // `sound` is projectile-owned audio, not necessarily a near-miss one-shot.
        // Spiker/Maelstrom explicitly set sound_loop; preserve their attenuation
        // and charge-family metadata rather than flattening this to filenames.
        Set(target, "flightAudio", FlightAudio(projectile, fallback));
        Set(target, "chargeEffectCount", Number("charge_effect_count"));
        Set(target, "initialVelocity", initialVelocity);
        Set(target, "acceleration", acceleration);
        Set(target, "accelerationUntilSeconds", accelerationUntil);
        Set(target, "lifespanSeconds", lifespan);
        Set(target, "existsOnRemoteClients", Flag("exists_on_remote_clients"));
        Set(target, "serverSideSplash", Flag("hasserversidesplash"));
        Set(target, "hasGravity", Flag("hasgravity"));
        Set(target, "gravity", Number("gravity"));
        Set(target, "grenadeProjectile", Flag("grenade_projectile"));
        Set(target, "playerGuided", Flag("player_guided"));
        Set(target, "playerGuidedTurnRate", Number("player_guided_turn_rate"));
        Set(target, "playerGuidedTurnRatePitch", Number("player_guided_turn_rate_pitch"));
        Set(target, "playerGuidedTurnRateYaw", Number("player_guided_turn_rate_yaw"));
        Set(target, "bounceCount", Number("bounce_count"));
        Set(target, "bounceRestitution", Number("bounce_restitution"));
        Set(target, "bounceFriction", Number("bounce_friction"));
        Set(target, "bounceEffect", Value("bounce_effect"));
        Set(target, "collisionRadius", Number("radius"));
        Set(target, "collisionSphereOffset", Vector("sphere_offset"));
        Set(target, "addParentVelocity", Flag("add_parent_velocity"));
        Set(target, "spawnProjectileAt", Value("spawnprojectileat"));
        Set(target, "minimumDetonationDistance", Number("minimum_detonation_distance"));
        Set(target, "ballisticBomb", Flag("ballistic_bomb"));
        Set(target, "detonationAltitude", Number("detonation_altitude"));
        Set(target, "detonationBackoff", Number("detonation_backoff"));
        Set(target, "multiStage", Flag("multi_stage"));
        Set(target, "multiStageSpawnServerSide", Flag("multi_stage_spawn_server_side"));
        Set(target, "secondaryProjectileDefinition", secondaryDefinition);
        Set(target, "secondaryProjectileCount", Number("num_second_stage_projectiles"));
        Set(target, "secondaryProjectileLayout", secondaryLayout);
        Set(target, "networkOnDetonateOnly", Flag("network_behavior_on_detonate_only"));
        Set(target, "convergenceThreshold", Number("convergence_threshold"));
        Set(target, "flakProjectile", Flag("flak_projectile"));
        Set(target, "longRangeProjectile", Flag("long_range_projectile"));
        Set(target, "damageRadius", Number("damage_radius"));
        Set(target, "damageAtEdge", Number("damage_at_edge"));
        Set(target, "damageType", Value("damage_type"));
        Set(target, "damageTypeSecondary", Value("damage_type_secondary"));
        Set(target, "damageProxyDefinition", damageProxyDefinition);
        Set(target, "lashDamage", Number("lash_damage"));
        Set(target, "lashRadius", Number("lash_radius"));
        Set(target, "lashDelaySeconds", Number("lash_delay"));
        Set(target, "lashIntervalSeconds", Number("lash_interval"));
        Set(target, "lashEffect", Value("lash_effect"));
        Set(target, "minimumTurnRate", Number("min_turn_rate"));
        Set(target, "maximumTurnRate", Number("max_turn_rate"));
        Set(target, "userInputAction", Value("on_user_input"));
        Set(target, "autoLock", Flag("autolock"));
        Set(target, "autoLockRange", Number("autolock_range"));
        Set(target, "autoLockAngle", Number("autolock_angle"));
        Set(target, "autoLockTurnRate", Number("autolock_turnrate"));
        Set(target, "autoLockActivationSeconds", Number("autolock_activate_duration"));
        Set(target, "autoLockDeactivationSeconds", Number("autolock_deactivate_duration"));
        Set(target, "autoLockVehicles", Flag("autolock_vehicle"));
        Set(target, "autoLockFlightVehicles", Flag("autolock_flightvehicle"));
        Set(target, "autoLockHeavyArmor", Flag("autolock_heavy"));
        Set(target, "autoLockIff", Value("autolock_iff"));
        Set(target, "autoLockFireAfterLock", Flag("autolock_fire_after_lock"));
        Set(target, "autoLockDumbfireWithoutLock", Flag("autolock_dumbfire_without_lock"));
        Set(target, "autoLockFireAndForget", Flag("autolock_fire_and_forget_projectile"));
        Set(target, "impactObjectAction", Value("on_impact_object"));
        Set(target, "impactAvatarAction", Value("on_impact_avatar"));
        Set(target, "impactVehicleAction", Value("on_impact_vehicle"));
        Set(target, "impactFlightVehicleAction", Value("on_impact_flight_vehicle"));
        Set(target, "impactTerrainAction", Value("on_impact_terrain"));
        Set(target, "impactWaterAction", Value("on_impact_water"));
        Set(target, "lifespanAction", Value("on_life_expired"));
        Set(target, "aggravatedDamageType", Value("aggravated_damage_type"));
        Set(target, "aggravatedDamageDurationMs", Number("aggravated_damage_duration"));
        Set(target, "aggravatedDamageMaxFactor", Number("aggravated_damage_max_factor"));
        Set(target, "cumulativeAggravatedDamageDegrade", Flag("cumulative_aggravated_damage_degrade"));
        Set(target, "directAggravatedDamage", Flag("direct_aggravated_damage"));
        Set(target, "directAggravatedDamageDegradationPercentage", Number("direct_aggravated_damage_degradation_percentage"));
        Set(target, "directAggravatedDamageInflictionRateMs", Number("direct_aggravated_damage_infliction_rate"));
        Set(target, "splashAggravatedDamage", Flag("splash_aggravated_damage"));
        Set(target, "splashAggravatedDamageDegradationPercentage", Number("splash_aggravated_damage_degradation_percentage"));
        Set(target, "splashAggravatedDamageInflictionRateMs", Number("splash_aggravated_damage_infliction_rate"));
        Set(target, "vanuAggravated", Flag("vanu_aggravated"));
        Set(target, "additionalEffect", Flag("additional_effect"));
        Set(target, "jammerProjectile", Flag("jammer_projectile"));
        Set(target, "mineSweeper", Flag("mine_sweeper"));
        Set(target, "avatarJammedEffectDurationMs", Number("avatar_jammed_effect_duration"));
        Set(target, "vehicleJammedEffectDurationMs", Number("vehicle_jammed_effect_duration"));
        Set(target, "amsJammedEffectDurationMs", Number("ams_jammed_effect_duration"));
        Set(target, "spitfireJammedEffectDurationMs", Number("spitfire_jammed_effect_duration"));
        Set(target, "turretJammedEffectDurationMs", Number("turret_jammed_effect_duration"));
        Set(target, "motionSensorJammedEffectDurationMs", Number("motion_sensor_jammed_effect_duration"));
        Set(target, "slaveCount", Number("slave_count"));
        Set(target, "slaveProjectileDefinition", slaveDefinition);
        Set(target, "slaveOffsetX", Number("slave_x"));
        Set(target, "slaveOffsetY", Number("slave_y"));
        Set(target, "requiresProjectileFlight", flightReasons.Count > 0);
        Set(target, "projectileFlightReasons", flightReasons.ToArray());
        Set(target, "maximumRange", maximumRange);

        if (!includeReferences) return;
        Set(target, "damageProxy", ReferenceObject(objects, damageProxyDefinition, proxy: true));
        Set(target, "secondaryProjectile", ReferenceObject(objects, secondaryDefinition, proxy: false));
        Set(target, "slaveProjectile", ReferenceObject(objects, slaveDefinition, proxy: false));
    }

    private static Dictionary<string, object?>? ReferenceObject(
        IReadOnlyDictionary<string, GameObjectDb.GameObject> objects,
        string? definition,
        bool proxy)
    {
        if (string.IsNullOrWhiteSpace(definition)) return null;
        if (!objects.TryGetValue(definition, out GameObjectDb.GameObject? record))
            throw new InvalidDataException($"game_objects.adb referenced projectile record '{definition}' is missing");

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["definition"] = definition,
            ["provenance"] = record.Provenance,
            ["type"] = Scalar(record, null, "type"),
        };
        if (proxy)
        {
            Set(result, "lifespanMs", Float(record, null, "lifespan"));
            Set(result, "pulseIntervalMs", Float(record, null, "pulse_interval"));
            Set(result, "griefIntervalMs", Float(record, null, "grief_interval"));
            Set(result, "damageRadius", Float(record, null, "damage_radius"));
            Set(result, "damageAtEdge", Float(record, null, "damage_at_edge"));
            Set(result, "damageType", Scalar(record, null, "damage_type"));
            Set(result, "cloudEffect", Scalar(record, null, "cloud_effect"));
            Set(result, "aggravatedDamageType", Scalar(record, null, "aggravated_damage_type"));
            Set(result, "aggravatedDamageDurationMs", Float(record, null, "aggravated_damage_duration"));
            Set(result, "aggravatedDamageMaxFactor", Float(record, null, "aggravated_damage_max_factor"));
        }
        else
        {
            AddProjectileFields(result, record, null, objects, includeReferences: false);
        }
        return result;
    }

    private static object? FlightAudio(GameObjectDb.GameObject? projectile, GameObjectDb.GameObject? fallback)
    {
        IReadOnlyList<string> files = GameObjectPropertyReader.List(projectile, "sound");
        if (files.Count == 0) files = GameObjectPropertyReader.List(fallback, "sound");
        if (files.Count == 0) return null;
        if (files.Any(file => !file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Projectile '{projectile?.Name}' sound must contain WAV filenames.");
        float? Number(string name)
        {
            string? raw = Scalar(projectile, fallback, name);
            if (raw is null) return null;
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                || !float.IsFinite(value) || value < 0)
                throw new InvalidDataException($"Projectile '{projectile?.Name}' has invalid {name}: {raw}");
            return value;
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["files"] = files.ToArray(),
            ["loop"] = Boolean(projectile, fallback, "sound_loop"),
            ["volume"] = Number("sound_volume"),
            ["minimumDistance"] = Number("sound_min"),
            ["maximumDistance"] = Number("sound_range"),
        };
    }

    private static string? Scalar(
        GameObjectDb.GameObject? projectile,
        GameObjectDb.GameObject? fallback,
        string name) => GameObjectPropertyReader.Scalar(projectile, name)
            ?? GameObjectPropertyReader.Scalar(fallback, name);

    private static float? Float(
        GameObjectDb.GameObject? projectile,
        GameObjectDb.GameObject? fallback,
        string name)
    {
        string? text = Scalar(projectile, fallback, name);
        if (text is null) return null;
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : null;
    }

    private static bool Boolean(
        GameObjectDb.GameObject? projectile,
        GameObjectDb.GameObject? fallback,
        string name)
    {
        string? text = Scalar(projectile, fallback, name);
        if (bool.TryParse(text, out bool value)) return value;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            && number != 0;
    }

    private static float[]? Vector3(
        GameObjectDb.GameObject? projectile,
        GameObjectDb.GameObject? fallback,
        string name)
    {
        IReadOnlyList<string>? values = GameObjectPropertyReader.Tuple(projectile, name, 3)
            ?? GameObjectPropertyReader.Tuple(fallback, name, 3);
        if (values is null) return null;
        var result = new float[3];
        for (int index = 0; index < result.Length; index++)
            if (!float.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out result[index]))
                return null;
        return result;
    }

    private static float? MaximumRange(
        float? initialVelocity,
        float? acceleration,
        float? accelerationUntil,
        float? lifespan)
    {
        if (!initialVelocity.HasValue || !lifespan.HasValue) return null;
        float acceleratingSeconds = Math.Clamp(accelerationUntil ?? 0f, 0f, lifespan.Value);
        float accelerationValue = acceleration ?? 0f;
        return initialVelocity.Value * lifespan.Value
            + 0.5f * accelerationValue * acceleratingSeconds * acceleratingSeconds
            + accelerationValue * acceleratingSeconds * (lifespan.Value - acceleratingSeconds);
    }

    private static bool IsAction(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Equals("nothing", StringComparison.OrdinalIgnoreCase)
        && !value.Equals("dud", StringComparison.OrdinalIgnoreCase);

    private static void Set(Dictionary<string, object?> target, string name, object? value) =>
        target[name] = value;
}
