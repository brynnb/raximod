using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Raximod.Generation;
using Raximod.Generation.Assets;

namespace Raximod.Modules;

public static class ExportWeaponsCommand
{
    public static int Run(string[] args)
    {
        if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--reuse-assets"))
        {
            Console.Error.WriteLine("usage: ExportWeapons <PlanetSide-directory> <output-directory> [--reuse-assets]");
            return 1;
        }

        string planetside = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        bool reuseAssets = args.Length == 3;
        Directory.CreateDirectory(output);
        var progress = new SynchronousProgress<string>(Console.WriteLine);

        string[] weaponNames =
        [
            "ilc9", "repeater", "isp", "beamer", "suppressor", "anniversary_guna",
            "anniversary_gun", "anniversary_gunb", "cycler", "gauss", "pulsar", "punisher",
            "flechette", "spiker", "frag_grenade", "jammer_grenade", "plasma_grenade", "katana",
            "chainblade", "magcutter", "forceblade", "mini_chaingun", "r_shotgun", "lasher",
            "maelstrom", "striker", "hunterseeker", "lancer", "phoenix", "rocklet", "thumper",
            "radiator", "heavy_sniper", "bolt_driver", "oicw", "flamethrower", "winchester",
            "pellet_gun", "six_shooter", "dynomite",
            "medicalapplicator", "nano_dispenser", "bank", "trek",
            "remote_electronics_kit", "flail_targeting_laser",
            "boomer_trigger", "command_detonater", "ace", "advanced_ace", "router_telepad",
            "medkit", "super_medkit", "super_armorkit", "super_staminakit",
            "trhev_dualcycler", "trhev_pounder", "trhev_burster",
            "nchev_scattercannon", "nchev_falcon", "nchev_sparrow",
            "vshev_quasar", "vshev_comet", "vshev_starfire",
        ];

        string gameObjectsPath = Path.Combine(planetside, "startup.pak-out", "game_objects.adb");
        GameObjectDb gameObjectDatabase = GameObjectDb.Parse(File.ReadAllBytes(gameObjectsPath));
        var addendum = AddendumListDatabase.Parse(
            File.ReadAllText(Path.Combine(planetside, "startup.pak-out", "addendum.lst")),
            "startup.pak-out/addendum.lst");
        var gameObjects = gameObjectDatabase.ResolvedObjects
            .ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var available = SharedLibraries(planetside)
            .SelectMany(path => GlbExportTool.ListRecords(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var firstPersonAnimationProfiles = new Dictionary<string, FirstPersonAnimationProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["fp_ff_talon"] = new(
                ["fp_ff_sword"],
                [
                    "fp_ff_sword_equip",
                    "fp_ff_sword_fire1",
                    "fp_ff_sword_holster",
                    "fp_ff_sword_idle",
                    "fp_ff_sword_run",
                ],
                States(
                    ("idle", ["fp_ff_sword_idle"]),
                    ("run", ["fp_ff_sword_run"]),
                    ("fire", ["fp_ff_sword_fire1"]),
                    ("equip", ["fp_ff_sword_equip"]),
                    ("holster", ["fp_ff_sword_holster"]))),
            ["fp_t_remote_electronics_kit"] = new(
                ["fp_remote_electronics_kit", "fp_t_remote_electronics_kit"],
                [
                    "fp_remote_electronics_kit_equip",
                    "fp_remote_electronics_kit_fire1_idle",
                    "fp_remote_electronics_kit_holster",
                    "fp_remote_electronics_kit_idle",
                    "fp_remote_electronics_kit_run",
                    "fp_t_remote_electronics_kit_fire1",
                ],
                States(
                    ("idle", ["fp_remote_electronics_kit_idle"]),
                    ("run", ["fp_remote_electronics_kit_run"]),
                    ("fire", [
                        "fp_t_remote_electronics_kit_fire1",
                        "fp_remote_electronics_kit_fire1_idle",
                    ]),
                    ("equip", ["fp_remote_electronics_kit_equip"]),
                    ("holster", ["fp_remote_electronics_kit_holster"])),
                FireModes(
                    (0, ["fp_t_remote_electronics_kit_fire1"]),
                    (1, ["fp_remote_electronics_kit_fire1_idle"])),
                BoneAliases(("t_remote_electronics_kit", "remote_electronics_kit"))),
            ["fp_remote_electronics_kit"] = new(
                ["fp_remote_electronics_kit"],
                [
                    "fp_remote_electronics_kit_equip",
                    "fp_remote_electronics_kit_fire1_idle",
                    "fp_remote_electronics_kit_holster",
                    "fp_remote_electronics_kit_idle",
                    "fp_remote_electronics_kit_run",
                ],
                States(
                    ("idle", ["fp_remote_electronics_kit_idle"]),
                    ("run", ["fp_remote_electronics_kit_run"]),
                    ("fire", ["fp_remote_electronics_kit_fire1_idle"]),
                    ("equip", ["fp_remote_electronics_kit_equip"]),
                    ("holster", ["fp_remote_electronics_kit_holster"])),
                FireModes((0, ["fp_remote_electronics_kit_fire1_idle"]))),
            ["fp_punisher"] = new(
                ["fp_punisher"],
                [
                    "fp_punisher_equip",
                    "fp_punisher_fire1",
                    "fp_punisher_fire2",
                    "fp_punisher_holster",
                    "fp_punisher_idle",
                    "fp_punisher_reload",
                    "fp_punisher_run",
                ],
                States(
                    ("idle", ["fp_punisher_idle"]),
                    ("run", ["fp_punisher_run"]),
                    ("fire", ["fp_punisher_fire1", "fp_punisher_fire2"]),
                    ("reload", ["fp_punisher_reload"]),
                    ("equip", ["fp_punisher_equip"]),
                    ("holster", ["fp_punisher_holster"])),
                FireModes(
                    (0, ["fp_punisher_fire1"]),
                    (1, ["fp_punisher_fire2"]))),
            ["fp_ace"] = new(
                ["fp_ace"],
                ["fp_ace_equip", "fp_ace_holster", "fp_ace_idle", "fp_ace_run", "fp_ace_use"],
                States(
                    ("idle", ["fp_ace_idle"]),
                    ("run", ["fp_ace_run"]),
                    ("fire", ["fp_ace_use"]),
                    ("equip", ["fp_ace_equip"]),
                    ("holster", ["fp_ace_holster"])),
                FireModes(
                    (0, ["fp_ace_use"]),
                    (1, ["fp_ace_use"]),
                    (2, ["fp_ace_use"]),
                    (3, ["fp_ace_use"]))),
            ["fp_advanced_ace"] = new(
                ["fp_advanced_ace"],
                [
                    "fp_advanced_ace_deploy",
                    "fp_advanced_ace_equip",
                    "fp_advanced_ace_holster",
                    "fp_advanced_ace_idle",
                    "fp_advanced_ace_run",
                ],
                States(
                    ("idle", ["fp_advanced_ace_idle"]),
                    ("run", ["fp_advanced_ace_run"]),
                    ("fire", ["fp_advanced_ace_deploy"]),
                    ("equip", ["fp_advanced_ace_equip"]),
                    ("holster", ["fp_advanced_ace_holster"])),
                FireModes(
                    (0, ["fp_advanced_ace_deploy"]),
                    (1, ["fp_advanced_ace_deploy"]),
                    (2, ["fp_advanced_ace_deploy"]))),
        };

        object? FirstPersonAnimationOverrides(string? record)
        {
            if (record is null || !firstPersonAnimationProfiles.TryGetValue(record, out var profile)) return null;
            return new
            {
                sourcePrefixes = profile.SourcePrefixes,
                boneAliases = profile.BoneAliases,
                states = profile.States,
                fireModes = profile.FireModes,
            };
        }

        string? Property(GameObjectDb.GameObject gameObject, string name) =>
            GameObjectPropertyReader.Scalar(gameObject, name);

        string[] Values(GameObjectDb.GameObject gameObject, string name) =>
            GameObjectPropertyReader.List(gameObject, name).ToArray();

        string[] ResolveThirdPersons(string definition, GameObjectDb.GameObject gameObject)
        {
            string[] authored = Values(gameObject, "meshsequence");
            string[] requested = authored
                .Where(available.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (requested.Length > 0) return requested;

            string requestedFallback = authored.FirstOrDefault() ?? definition;
            string[] candidates = definition switch
            {
                "anniversary_guna" => [requestedFallback, "anniv_gun_tr"],
                "anniversary_gun" => [requestedFallback, "anniv_gun_nc"],
                "anniversary_gunb" => [requestedFallback, "anniv_gun_vs"],
                "flamethrower" => [requestedFallback, "flamethrower2_sm"],
                _ => [requestedFallback, definition],
            };
            return [candidates.FirstOrDefault(available.Contains) ?? requestedFallback];
        }

        string? ResolveFirstPerson(string definition, string thirdPerson)
        {
            string[] candidates = [$"fp_{definition}", $"fp_{thirdPerson}"];
            return candidates.FirstOrDefault(available.Contains);
        }

        float? Float(GameObjectDb.GameObject gameObject, string name)
        {
            string? value = Property(gameObject, name);
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                ? number
                : null;
        }

        bool? Boolean(GameObjectDb.GameObject gameObject, string name)
        {
            string? value = Property(gameObject, name);
            if (bool.TryParse(value, out bool result)) return result;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                return number != 0;
            return null;
        }

        float[]? Vector(GameObjectDb.GameObject gameObject, string name)
        {
            IReadOnlyList<string>? values = GameObjectPropertyReader.Tuple(gameObject, name, 3);
            if (values is null) return null;
            var result = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i])) return null;
            return result;
        }

        object[] Sounds(GameObjectDb.GameObject gameObject, GameObjectDb.GameObject? fallback = null) => gameObject.Properties
            .Concat(fallback?.Properties ?? [])
            .Where(property => property.Key.Contains("sound", StringComparison.OrdinalIgnoreCase))
            .SelectMany(property => property.Value
                .Where(value => value.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                .Select(value => new { cue = property.Key, file = value }))
            .DistinctBy(value => (value.cue.ToLowerInvariant(), value.file.ToLowerInvariant()))
            .Cast<object>()
            .ToArray();

        object Ammunition(string name, int projectileIndex = 0)
        {
            gameObjects.TryGetValue(name, out GameObjectDb.GameObject? ammunition);
            AmmunitionProjectileResolver.Result resolution = AmmunitionProjectileResolver.Resolve(
                gameObjects, ammunition, projectileIndex);
            if (resolution.Warning is not null) Console.Error.WriteLine($"warning: {resolution.Warning}");
            return ProjectileBehaviorMetadata.ExportAmmunition(name, ammunition, resolution, gameObjects);
        }

        bool IsGrenade(GameObjectDb.GameObject gameObject) => string.Equals(
            Property(gameObject, "type"), "hand_grenade", StringComparison.OrdinalIgnoreCase);
        // GameObjectDb has already applied the database's explicit set_resource_parent
        // graph. Keep one canonical source here: treating the classification `type` as
        // another prototype, or re-adding generic_grenade, would guess at and duplicate
        // inheritance outside the lossless decoder.
        GameObjectDb.GameObject?[] WeaponSources(GameObjectDb.GameObject gameObject) =>
            [gameObject];
        string? WeaponProperty(GameObjectDb.GameObject gameObject, string name) =>
            WeaponSources(gameObject).Reverse().Select(source => source is null ? null : Property(source, name))
                .FirstOrDefault(value => value != null);
        float? WeaponFloat(GameObjectDb.GameObject gameObject, string name)
        {
            string? value = WeaponProperty(gameObject, name);
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                ? number : null;
        }
        string[] WeaponValues(GameObjectDb.GameObject gameObject, string name)
        {
            return WeaponSources(gameObject).Reverse()
                .Select(source => source is null ? Array.Empty<string>() : Values(source, name))
                .FirstOrDefault(values => values.Length > 0) ?? [];
        }
        string? WeaponSound(GameObjectDb.GameObject gameObject, string name)
        {
            foreach (GameObjectDb.GameObject source in WeaponSources(gameObject).Reverse().OfType<GameObjectDb.GameObject>())
            {
                IReadOnlyList<string>? tuple = GameObjectPropertyReader.Tuple(source, name, 1, 2, 3);
                if (tuple is not null) return tuple[0];
            }
            return null;
        }
        string[] WeaponMuzzles(GameObjectDb.GameObject gameObject, int fireMode)
        {
            return ClientWeaponMetadataResolver.Muzzles(fireMode, WeaponSources(gameObject));
        }

        string AnimationType(GameObjectDb.GameObject gameObject) =>
            WeaponProperty(gameObject, "animationtype") ?? "low";

        object? Deployment(GameObjectDb.GameObject weapon, GameObjectDb.GameObject placementSource, int fireMode)
        {
            string ModeProperty(string suffix) => $"firemode{fireMode}_{suffix}";
            string? deployedDefinition = Property(placementSource, ModeProperty("deployed_weapon"))
                ?? Property(weapon, ModeProperty("deployed_weapon"));
            if (string.IsNullOrWhiteSpace(deployedDefinition)) return null;
            gameObjects.TryGetValue(deployedDefinition, out GameObjectDb.GameObject? deployed);

            bool? ModeBoolean(string suffix) => Boolean(placementSource, ModeProperty(suffix))
                ?? Boolean(weapon, ModeProperty(suffix));
            bool? DeployedBoolean(string suffix) => deployed is null ? null : Boolean(deployed, suffix);
            bool? Surface(string modeSuffix, string objectSuffix) =>
                ModeBoolean(modeSuffix) ?? DeployedBoolean(objectSuffix);

            return new
            {
                definition = deployedDefinition,
                provenance = deployed?.Provenance,
                placementSourceProvenance = placementSource.Provenance,
                maximumDistance = (deployed is null ? null : Float(deployed, "deployrange"))
                    ?? Float(placementSource, "deployrange")
                    ?? Float(weapon, "deployrange"),
                terrain = Surface("deploy_on_terrain", "odf_terrain"),
                building = Surface("deploy_on_building", "odf_buildings"),
                ceiling = Surface("deploy_on_ceiling", "odf_ceilings"),
                wall = Surface("deploy_on_wall", "odf_walls"),
                inside = DeployedBoolean("odf_inside"),
                outside = ModeBoolean("deploy_outside") ?? DeployedBoolean("odf_outside"),
                friendlySoi = DeployedBoolean("odf_soi_friendly"),
                enemySoi = DeployedBoolean("odf_soi_enemy"),
                neutralSoi = DeployedBoolean("odf_soi_none"),
            };
        }

        var entries = weaponNames.Select(name =>
        {
            if (!gameObjects.TryGetValue(name, out GameObjectDb.GameObject? gameObject))
                throw new InvalidOperationException($"Weapon '{name}' is missing from game_objects.adb");
            string[] thirds = ResolveThirdPersons(name, gameObject);
            string third = thirds[0];
            string? first = ResolveFirstPerson(name, third);
            float? firstPersonRotationRaw = Float(gameObject, "fp_rot");
            // The retail renderer indexes its 0x4000-entry trigonometric table with
            // fp_rot & 0x3fff. It is therefore a signed 14-bit turn: 16384 units is
            // 360 degrees. Keep the raw value too so this executable-proven contract
            // cannot silently regress to the unrelated 16-bit angle convention used
            // by other PlanetSide fields.
            double? firstPersonRotationDegrees = firstPersonRotationRaw is float rawRotation
                ? rawRotation * (360d / 16384d)
                : null;
            GameObjectDb.GameObject?[] propertySources = WeaponSources(gameObject);
            var fireModes = ClientWeaponMetadataResolver.FireModeIndices(gameObject)
                .Select(index => {
                    int projectileIndex = (int)(WeaponFloat(gameObject, $"firemode{index}_projectile_index") ?? 0);
                    string[] ammunition = WeaponValues(gameObject, $"firemode{index}_ammo_types");
                    string[] fireSound = WeaponValues(gameObject, $"clientfiremode{index}_fire_sound");
                    float? SoundNumber(int valueIndex) => valueIndex < fireSound.Length
                        && float.TryParse(fireSound[valueIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                            ? value : null;
                    if (ammunition.Length == 0 && string.Equals(
                        WeaponProperty(gameObject, $"firemode{index}_use_common_ammo"), "true", StringComparison.OrdinalIgnoreCase))
                        ammunition = WeaponValues(gameObject, "firemode0_ammo_types");
                    return new
                {
                    index,
                    ammunition = ammunition.Select(name => Ammunition(name, projectileIndex)).ToArray(),
                    projectileIndex,
                    muzzle = WeaponMuzzles(gameObject, index).FirstOrDefault(),
                    muzzles = WeaponMuzzles(gameObject, index),
                    presentation = ClientWeaponMetadataResolver.FirePresentation(index, propertySources),
                    burst = ClientWeaponMetadataResolver.FireBurst(index, propertySources),
                    recoil = ClientCombatMetadata.ModeNumber(gameObject, index, "recoil"),
                    burstRefireSlopMs = ClientCombatMetadata.ModeNumber(gameObject, index, "burst_refire_slop"),
                    stamina = ClientCombatMetadata.Stamina(gameObject, index),
                    refireTimeMs = WeaponFloat(gameObject, $"firemode{index}_refiretime"),
                    reloadTimeSeconds = WeaponFloat(gameObject, $"firemode{index}_reloadtime"),
                    fireSound = fireSound.FirstOrDefault(),
                    fireVolume = SoundNumber(1),
                    fireMaxDistance = SoundNumber(2),
                    fireSpinUpSound = WeaponSound(gameObject, $"clientfiremode{index}_fire_spinup_sound"),
                    fireSpinDownSound = WeaponSound(gameObject, $"clientfiremode{index}_fire_spindown_sound"),
                    alternateFireSound = WeaponSound(gameObject, $"clientfiremode{index}_altfire_sound"),
                    alternateReloadSound = WeaponSound(gameObject, $"clientfiremode{index}_altreload_sound"),
                    loopingFireSound = string.Equals(
                        WeaponProperty(gameObject, $"clientfiremode{index}_looping_fire_sound"),
                        "true", StringComparison.OrdinalIgnoreCase),
                    loopingFireAnimation = string.Equals(
                        WeaponProperty(gameObject, $"clientfiremode{index}_looping_fire_anim"),
                        "true", StringComparison.OrdinalIgnoreCase),
                    chargeUpFireEffect = string.Equals(
                        WeaponProperty(gameObject, $"clientfiremode{index}_chargeup_fire_effect"),
                        "true", StringComparison.OrdinalIgnoreCase),
                    crosshair = WeaponProperty(gameObject, $"clientfiremode{index}_crosshair"),
                    defaultConeOfFire = WeaponFloat(gameObject, $"firemode{index}_defaultCOF"),
                    crouchConeOfFire = WeaponFloat(gameObject, $"firemode{index}_crouchCOF"),
                    maximumConeOfFire = WeaponFloat(gameObject, $"firemode{index}_maxCOF"),
                    coneOfFireRecoveryMs = WeaponFloat(gameObject, $"firemode{index}_COFrecovery"),
                    shotsBeforeConeOfFirePenalty = WeaponFloat(
                        gameObject,
                        $"firemode{index}_max_shots_per_burst_before_cof_penalty"),
                    shotsPerRound = WeaponFloat(gameObject, $"firemode{index}_shotsperround") ?? 1,
                    ammunitionPerShot = WeaponFloat(gameObject, $"firemode{index}_ammopershot") ?? 1,
                    shotSpread = WeaponFloat(gameObject, $"firemode{index}_shotspread") ?? 0,
                    fireDelayMs = WeaponFloat(gameObject, $"firemode{index}_fire_delay"),
                    chargeTimeMs = WeaponFloat(gameObject, $"firemode{index}_chargetime"),
                    chargeDrainIntervalMs = WeaponFloat(gameObject, $"firemode{index}_charge_drain_interval"),
                }; }).ToArray();
            return new
            {
                definition = name,
                sourceObject = gameObject.Name,
                optics = ClientCombatMetadata.Optics(gameObject),
                provenance = gameObject.Provenance,
                // meshsequence is ordered and may deliberately contain a left/right
                // pair (TR MAX arms). Preserve every authored member; the former
                // FirstOrDefault silently discarded the right-hand weapon.
                thirdPersonRecords = thirds.Where(available.Contains).ToArray(),
                // Retail amends UBR rigs after loading. Keep authored parent-local sockets
                // and provenance separate from original geometry, just as vehicle exports do.
                thirdPersonBoneAdditions = thirds.Where(available.Contains).Select(addendum.Bones).ToArray(),
                thirdPersonUris = thirds.Where(available.Contains)
                    .Select(value => $"models/third-person/{value}.glb").ToArray(),
                thirdPersonRecord = available.Contains(third) ? third : null,
                thirdPersonUri = available.Contains(third) ? $"models/third-person/{third}.glb" : null,
                firstPersonRecord = first,
                firstPersonBoneAdditions = first is null ? [] : addendum.Bones(first),
                firstPersonUri = first is null ? null : $"models/first-person/{first}.glb",
                firstPersonAnimationOverrides = FirstPersonAnimationOverrides(first),
                animationType = AnimationType(gameObject),
                melee = string.Equals(Property(gameObject, "melee"), "true", StringComparison.OrdinalIgnoreCase),
                knife = string.Equals(Property(gameObject, "is_a_knife"), "true", StringComparison.OrdinalIgnoreCase),
                grenade = IsGrenade(gameObject),
                deleteAfterUse = string.Equals(Property(gameObject, "delete_after_use"), "true", StringComparison.OrdinalIgnoreCase),
                grenadeFullThrowDurationMs = WeaponFloat(gameObject, "grenade_full_throw_duration"),
                grenadeMinimumVelocity = WeaponFloat(gameObject, "grenade_minimum_velocity"),
                grenadeMaximumVelocity = WeaponFloat(gameObject, "grenade_maximum_velocity"),
                firstPersonOffset = Vector(gameObject, "fp_offset"),
                firstPersonRotationRaw,
                firstPersonRotationDegrees,
                equipTimeMs = WeaponFloat(gameObject, "equiptime"),
                holsterTimeMs = WeaponFloat(gameObject, "holstertime"),
                reloadTimeSeconds = WeaponFloat(gameObject, "firemode0_reloadtime"),
                refireTimeMs = WeaponFloat(gameObject, "firemode0_refiretime"),
                muzzle = WeaponMuzzles(gameObject, 0).FirstOrDefault(),
                fireModes,
                turningAccuracy = ClientWeaponMetadataResolver.TurningAccuracy(gameObject),
                loopingSound = ClientWeaponMetadataResolver.LoopingSound(gameObject),
                sounds = propertySources.OfType<GameObjectDb.GameObject>()
                    .SelectMany(source => Sounds(source)).DistinctBy(value => JsonSerializer.Serialize(value)).ToArray(),
            };
        }).ToArray();

        var deployments = weaponNames.SelectMany(name =>
        {
            GameObjectDb.GameObject weapon = gameObjects[name];
            GameObjectDb.GameObject placementSource = gameObjects.TryGetValue($"{name}_deployable", out var deployable)
                ? deployable : weapon;
            return Enumerable.Range(0, 8)
                .Select(index => new { weapon = name, fireMode = index, placement = Deployment(weapon, placementSource, index) })
                .Where(entry => entry.placement is not null);
        }).ToArray();

        string[] thirdRecords = entries.SelectMany(item => item.thirdPersonRecords)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] firstRecords = entries.Where(item => item.firstPersonRecord is not null)
            .Select(item => item.firstPersonRecord!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Dropped ammunition is rendered from the ammunition object's meshsequence,
        // not from the logical definition name (for example, 9mmbullet uses
        // std_bullet_ammo_box). Keep this compact mapping in the weapon manifest so
        // the browser never has to download the full native game-object database or
        // guess a GLB URI from an inventory definition.
        string[] equipmentDefinitions = weaponNames
            .SelectMany(name => Enumerable.Range(0, 8)
                .SelectMany(index => WeaponValues(gameObjects[name], $"firemode{index}_ammo_types")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var equipment = equipmentDefinitions
            .Where(gameObjects.ContainsKey)
            .Select(definition =>
            {
                GameObjectDb.GameObject gameObject = gameObjects[definition];
                string[] records = Values(gameObject, "meshsequence")
                    .Where(value => !value.Equals("none", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string[] exported = records.Where(available.Contains).ToArray();
                return new
                {
                    definition,
                    provenance = gameObject.Provenance,
                    worldModelRecords = exported,
                    worldModelBoneAdditions = exported.Select(addendum.Bones).ToArray(),
                    worldModelUris = exported.Select(value => $"models/equipment/{value}.glb").ToArray(),
                    worldModelRecord = exported.FirstOrDefault(),
                    worldModelUri = exported.Length == 0 ? null : $"models/equipment/{exported[0]}.glb",
                };
            })
            .ToArray();
        string[] equipmentRecords = equipment.SelectMany(item => item.worldModelRecords)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // The native first-person MAX meshes use textureless soldier-material aliases.
        // PlanetSide's player renderer supplies the matching faction armor atlas at
        // runtime; a standalone glTF exporter must make that authored substitution
        // explicitly. These aliases use the same UV layouts as the corresponding MAX
        // armor materials and are shared by every weapon variant for that faction.
        var firstPersonMaxMaterialReplacements = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["fp_tr1hev"] = "tr1hev1",
            ["fp_nc1hevarmsa"] = "nc1hev1",
            ["fp_nc1hevarmsb"] = "nc1hev2",
            ["fp_vs1hevaemsa"] = "vs1heva",
        };

        GlbBatchExportTool.Result thirdResult = GlbBatchExportTool.Run(new GlbBatchExportTool.Options(
            planetside, Path.Combine(output, "models", "third-person"), thirdRecords, Overwrite: !reuseAssets,
            PreserveRigidBoneAttachments: true), progress);
        GlbBatchExportTool.Result firstResult = GlbBatchExportTool.Run(new GlbBatchExportTool.Options(
            planetside, Path.Combine(output, "models", "first-person"), firstRecords,
            Overwrite: !reuseAssets, IncludeAnimations: true,
            MaterialReplacements: firstPersonMaxMaterialReplacements), progress);
        GlbBatchExportTool.Result equipmentResult = GlbBatchExportTool.Run(new GlbBatchExportTool.Options(
            planetside, Path.Combine(output, "models", "equipment"), equipmentRecords,
            Overwrite: !reuseAssets), progress);

        string firstPersonOutput = Path.Combine(output, "models", "first-person");
        string[] aliasRecords = ["fp_ff_talon", "fp_t_remote_electronics_kit"];
        var aliasResults = new List<object>();
        var aliasFailures = new List<GlbBatchExportTool.Failure>();
        foreach (string record in aliasRecords)
        {
            FirstPersonAnimationProfile profile = firstPersonAnimationProfiles[record];
            try
            {
                string aliasPath = Path.Combine(firstPersonOutput, record + ".glb");
                // Metadata-only refreshes must preserve previously validated shared-pool
                // URIs, including aliases that bypass the ordinary batch exporter.
                if (reuseAssets && File.Exists(aliasPath)
                    && GlbBatchExportTool.ExistingAssetPreservesAnimations(aliasPath, profile.Clips))
                {
                    aliasResults.Add(new
                    {
                        record,
                        sourcePrefixes = profile.SourcePrefixes,
                        boneAliases = profile.BoneAliases,
                        clips = profile.Clips,
                        animations = profile.Clips.Length,
                    });
                    continue;
                }
                GlbExportTool.Result result = GlbExportTool.Run(new GlbExportTool.Options(
                    planetside,
                    record,
                    aliasPath,
                    IncludeAnimations: true,
                    MaxAnimations: int.MaxValue,
                    AnimationPrefixes: profile.SourcePrefixes,
                    AnimationNames: profile.Clips,
                    AnimationTrackAliases: profile.BoneAliases), progress);
                if (result.Animations != profile.Clips.Length)
                {
                    throw new InvalidOperationException(
                        $"{record} exported {result.Animations} of {profile.Clips.Length} requested alias clips.");
                }
                aliasResults.Add(new
                {
                    record,
                    sourcePrefixes = profile.SourcePrefixes,
                    boneAliases = profile.BoneAliases,
                    clips = profile.Clips,
                    animations = result.Animations,
                });
            }
            catch (Exception exception)
            {
                aliasFailures.Add(new GlbBatchExportTool.Failure(record, exception.Message));
                Console.Error.WriteLine($"failed first-person animation alias {record}: {exception.Message}");
            }
        }

        var manifest = new
        {
            schemaVersion = 5,
            source = "startup.pak-out/game_objects.adb",
            gameObjects = gameObjectDatabase.Diagnostics,
            coordinateSystem = "gltf-y-up",
            weapons = entries,
            equipment,
            deployments,
            extraction = new
            {
                thirdPerson = new
                {
                    thirdResult.Requested,
                    thirdResult.Exported,
                    thirdResult.Existing,
                    thirdResult.Missing,
                    thirdResult.Failed
                },
                firstPerson = new
                {
                    firstResult.Requested,
                    firstResult.Exported,
                    firstResult.Existing,
                    firstResult.Missing,
                    firstResult.Failed
                },
                equipment = new
                {
                    equipmentResult.Requested,
                    equipmentResult.Exported,
                    equipmentResult.Existing,
                    equipmentResult.Missing,
                    equipmentResult.Failed
                },
                firstPersonAnimationAliases = new
                {
                    requested = aliasRecords.Length,
                    exported = aliasResults,
                    failed = aliasFailures,
                },
            },
        };
        // Publish browser models only after every image has a verified shared companion.
        SharedGlbImageExport.Run(output, recursive: true);
        string manifestPath = Path.Combine(output, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"wrote {entries.Length} weapon definitions -> {manifestPath}");
        return thirdResult.Failed.Count + firstResult.Failed.Count + equipmentResult.Failed.Count
            + aliasFailures.Count == 0 ? 0 : 2;

        static IEnumerable<string> SharedLibraries(string root)
        {
            string[] names =
            [
                "uber.ubr", "patch1/patch1.ubr", "patch2/patch2.ubr", "patch3/patch3.ubr",
                "patch4/patch4.ubr", "patch5/patch5.ubr", "expansion1/expansion1.ubr",
            ];
            return names.Select(name => Path.Combine(root, name)).Where(File.Exists);
        }

        static Dictionary<string, string[]> States(params (string State, string[] Clips)[] entries) =>
            entries.ToDictionary(entry => entry.State, entry => entry.Clips, StringComparer.OrdinalIgnoreCase);

        static Dictionary<string, string[]> FireModes(params (int Mode, string[] Clips)[] entries) =>
            entries.ToDictionary(
                entry => entry.Mode.ToString(CultureInfo.InvariantCulture),
                entry => entry.Clips,
                StringComparer.OrdinalIgnoreCase);

        static Dictionary<string, string> BoneAliases(params (string Source, string Target)[] entries) =>
            entries.ToDictionary(
                entry => entry.Source,
                entry => entry.Target,
                StringComparer.OrdinalIgnoreCase);
    }

    sealed record FirstPersonAnimationProfile(
        string[] SourcePrefixes,
        string[] Clips,
        Dictionary<string, string[]> States,
        Dictionary<string, string[]>? FireModes = null,
        Dictionary<string, string>? BoneAliases = null);
}
