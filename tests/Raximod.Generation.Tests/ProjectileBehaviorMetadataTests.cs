using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class ProjectileBehaviorMetadataTests
{
    [Fact]
    public void ProjectileAudioRetainsLoopAttenuationAndChargeFamilyInsteadOfGuessingNearMisses()
    {
        var ammo = Record("ammo", ("projectile", "orb"));
        var orb = Record("orb", ("sound", "spiker_projectile.wav"), ("sound_loop", "true"),
            ("sound_volume", ".85"), ("sound_min", "1.25"), ("sound_range", "30"), ("charge_effect_count", "4"));
        var objects = new Dictionary<string, GameObjectDb.GameObject> { ["ammo"] = ammo, ["orb"] = orb };
        var exported = ProjectileBehaviorMetadata.ExportAmmunition("ammo", ammo,
            AmmunitionProjectileResolver.Resolve(objects, ammo, 0), objects);
        var audio = Assert.IsType<Dictionary<string, object?>>(exported["flightAudio"]);
        Assert.Equal(new[] { "spiker_projectile.wav" }, Assert.IsType<string[]>(audio["files"]));
        Assert.Equal(true, audio["loop"]);
        Assert.Equal(.85f, audio["volume"]);
        Assert.Equal(1.25f, audio["minimumDistance"]);
        Assert.Equal(30f, audio["maximumDistance"]);
        Assert.Equal(4f, exported["chargeEffectCount"]);
        Assert.False(exported.ContainsKey("nearMissSounds"));
        orb.Properties["sound_range"] = ["invalid"];
        Assert.Throws<InvalidDataException>(() => ProjectileBehaviorMetadata.ExportAmmunition("ammo", ammo,
            AmmunitionProjectileResolver.Resolve(objects, ammo, 0), objects));
        orb.Properties["sound_range"] = ["10", "30"];
        Assert.Throws<InvalidDataException>(() => ProjectileBehaviorMetadata.ExportAmmunition("ammo", ammo,
            AmmunitionProjectileResolver.Resolve(objects, ammo, 0), objects));
    }

    [Fact]
    public void FlightClassificationUsesAuthoredLifecycleInsteadOfVelocityThreshold()
    {
        GameObjectDb.GameObject bullet = Record("bullet",
            ("initial_velocity", "500"),
            ("on_life_expired", "nothing"));
        GameObjectDb.GameObject rocket = Record("rocket",
            ("initial_velocity", "500"),
            ("on_life_expired", "Detonate"));

        Assert.Empty(ProjectileBehaviorMetadata.FlightReasons(bullet));
        Assert.Equal(["lifespanAction"], ProjectileBehaviorMetadata.FlightReasons(rocket));
    }

    [Fact]
    public void ExportPreservesProxyLinkedAndSecondaryProjectileRecords()
    {
        GameObjectDb.GameObject ammunition = Record("special_ammo", ("projectile", "root"));
        GameObjectDb.GameObject root = Record("root",
            ("damage_proxy", "cloud"),
            ("multi_stage", "true"),
            ("secondary_projectile_name", "buddy"),
            ("num_second_stage_projectiles", "5"),
            ("slave_count", "1"),
            ("slave_type", "slave"),
            ("slave_x", ".4"),
            ("damage_type", "aggravated"),
            ("aggravated_damage_type", "plasma"));
        GameObjectDb.GameObject cloud = Record("cloud",
            ("type", "radiation_cloud"),
            ("lifespan", "10000"),
            ("pulse_interval", "225"),
            ("damage_radius", "5"),
            ("damage_type", "radiation"));
        GameObjectDb.GameObject buddy = Record("buddy", ("initial_velocity", "30"), ("lifespan", "2"));
        GameObjectDb.GameObject slave = Record("slave", ("initial_velocity", "60"), ("lifespan", "3"));
        var objects = new Dictionary<string, GameObjectDb.GameObject>(StringComparer.OrdinalIgnoreCase)
        {
            [ammunition.Name] = ammunition,
            [root.Name] = root,
            [cloud.Name] = cloud,
            [buddy.Name] = buddy,
            [slave.Name] = slave,
        };
        AmmunitionProjectileResolver.Result resolution = AmmunitionProjectileResolver.Resolve(objects, ammunition, 0);

        Dictionary<string, object?> exported = ProjectileBehaviorMetadata.ExportAmmunition(
            ammunition.Name, ammunition, resolution, objects);

        Assert.True(Assert.IsType<bool>(exported["requiresProjectileFlight"]));
        Assert.Equal("aggravated", exported["damageType"]);
        Assert.Equal("plasma", exported["aggravatedDamageType"]);
        Assert.Equal(5f, exported["secondaryProjectileCount"]);
        Assert.Equal(0.4f, exported["slaveOffsetX"]);

        var proxy = Assert.IsType<Dictionary<string, object?>>(exported["damageProxy"]);
        Assert.Equal("radiation_cloud", proxy["type"]);
        Assert.Equal(10000f, proxy["lifespanMs"]);
        Assert.Equal(225f, proxy["pulseIntervalMs"]);

        var secondary = Assert.IsType<Dictionary<string, object?>>(exported["secondaryProjectile"]);
        Assert.Equal("buddy", secondary["definition"]);
        Assert.Equal(30f, secondary["initialVelocity"]);

        var linked = Assert.IsType<Dictionary<string, object?>>(exported["slaveProjectile"]);
        Assert.Equal("slave", linked["definition"]);
        Assert.Equal(60f, linked["initialVelocity"]);
    }

    [Fact]
    public void SelectedSiblingVariantDoesNotInheritBaseProjectileFields()
    {
        GameObjectDb.GameObject ammunition = Record("shared_ammo",
            ("projectile", "scythe"),
            ("projectile1", "flail"));
        GameObjectDb.GameObject scythe = Record("scythe", ("slave_count", "1"), ("slave_type", "scythe_slave"));
        GameObjectDb.GameObject flail = Record("flail", ("long_range_projectile", "true"));
        GameObjectDb.GameObject slave = Record("scythe_slave", ("lifespan", "3"));
        var objects = new Dictionary<string, GameObjectDb.GameObject>(StringComparer.OrdinalIgnoreCase)
        {
            [ammunition.Name] = ammunition,
            [scythe.Name] = scythe,
            [flail.Name] = flail,
            [slave.Name] = slave,
        };

        var resolution = AmmunitionProjectileResolver.Resolve(objects, ammunition, 1);
        Dictionary<string, object?> exported = ProjectileBehaviorMetadata.ExportAmmunition(
            ammunition.Name, ammunition, resolution, objects);

        Assert.Equal("flail", exported["projectileDefinition"]);
        Assert.Null(exported["slaveProjectileDefinition"]);
        Assert.Null(exported["slaveProjectile"]);
        Assert.Contains("longRangeProjectile", Assert.IsType<string[]>(exported["projectileFlightReasons"]));
    }

    private static GameObjectDb.GameObject Record(
        string name,
        params (string Name, string Value)[] properties) => new()
        {
            Name = name,
            Type = properties.FirstOrDefault(property => property.Name == "type").Value ?? "projectile",
            IsResolved = true,
            InheritanceChain = [name],
            Properties = properties.ToDictionary(
                property => property.Name,
                property => new List<string> { property.Value },
                StringComparer.OrdinalIgnoreCase),
        };
}
