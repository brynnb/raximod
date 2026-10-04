using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation;
using Raximod.Generation.Assets;

namespace Raximod.Modules;

public static class ExportVehiclesCommand
{
    public static int Run(string[] args)
    {
        if (args.Length is < 3 or > 4 || (args.Length == 4 && args[3] != "--reuse-assets"))
        {
            Console.Error.WriteLine("usage: <PlanetSideDir> <PSForeverDir> <output-directory> [--reuse-assets]");
            return 1;
        }

        string planetside = Path.GetFullPath(args[0]);
        string psForever = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[2]);
        bool reuseAssets = args.Length == 4;
        Directory.CreateDirectory(output);
        var progress = new SynchronousProgress<string>(Console.WriteLine);

        var vehicleModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fury"] = "quadassault",
            ["quadassault"] = "quadassault",
            ["quadstealth"] = "quadstealth",
            ["two_man_assault_buggy"] = "two_man_assault_buggy",
            ["skyguard"] = "skyguard",
            ["threemanheavybuggy"] = "threemanheavybuggy",
            ["twomanheavybuggy"] = "twomanheavybuggy",
            ["twomanhoverbuggy"] = "twomanhoverbuggy",
            ["mediumtransport"] = "mediumtransport",
            ["battlewagon"] = "battlewagontr",
            ["thunderer"] = "mediumtransport",
            ["aurora"] = "delivererv",
            ["apc_tr"] = "apc",
            ["apc_nc"] = "apc",
            ["apc_vs"] = "apc",
            ["lightning"] = "lightning",
            ["prowler"] = "prowler",
            ["vanguard"] = "vanguard",
            ["magrider"] = "magrider",
            ["ant"] = "ant",
            ["ams"] = "ams",
            ["router"] = "router",
            ["switchblade"] = "switchblade",
            ["flail"] = "flail",
            ["mosquito"] = "mosquito",
            ["lightgunship"] = "lightgunship",
            ["wasp"] = "mosquito",
            ["liberator"] = "liberator",
            ["vulture"] = "liberator",
            ["dropship"] = "dropship",
            ["galaxy_gunship"] = "galaxy_gunship",
            ["lodestar"] = "lodestar",
            ["phantasm"] = "phantasm",
            ["droppod"] = "droppod",
            ["orbital_shuttle"] = "orbital_shuttle",
        };
        var turretModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["manned_turret"] = "manned_turret",
            ["vanu_sentry_turret"] = "vanu_sentry_turret",
            ["portable_manned_turret"] = "portable_manned_turret",
            ["portable_manned_turret_nc"] = "portable_manned_turret",
            ["portable_manned_turret_tr"] = "portable_manned_turret",
            ["portable_manned_turret_vs"] = "portable_manned_turret",
            ["spitfire_turret"] = "spitfire_turret",
            ["spitfire_cloaked"] = "spitfire_turret",
            ["spitfire_aa"] = "spitfire_turret",
        };
        string[] excludedBfrs =
        [
            "aphelion_gunner", "colossus_gunner", "peregrine_gunner",
            "aphelion_flight", "colossus_flight", "peregrine_flight",
        ];
        var records = VehicleExportSupport.RecordNames(planetside);
        var addendum = AddendumListDatabase.Parse(File.ReadAllText(Path.Combine(planetside, "startup.pak-out", "addendum.lst")));
        string gameObjectsPath = Path.Combine(planetside, "startup.pak-out", "game_objects.adb");
        if (!File.Exists(gameObjectsPath)) throw new FileNotFoundException("Extracted game_objects.adb not found", gameObjectsPath);
        GameObjectDb gameObjectDatabase = GameObjectDb.Parse(File.ReadAllBytes(gameObjectsPath));
        var gameObjects = gameObjectDatabase.ResolvedObjects
            .ToDictionary(gameObject => gameObject.Name, StringComparer.OrdinalIgnoreCase);
        var vehicleRenderRecords = vehicleModels.ToDictionary(pair => pair.Key,
            pair => VehiclePresentationBindings.BodyRecord(pair.Value,
                gameObjects.GetValueOrDefault(pair.Value), gameObjects.GetValueOrDefault(pair.Key)),
            StringComparer.OrdinalIgnoreCase);
        var vehicleBodyComponents = vehicleModels.ToDictionary(pair => pair.Key,
            pair => VehiclePresentationBindings.BodyComponents(Object(pair.Value), Object(pair.Key)), StringComparer.OrdinalIgnoreCase);
        var bodyComponentResults = new Dictionary<string, ExportedAsset>(StringComparer.OrdinalIgnoreCase);

        var effectPackages = NativeEffectPackageCatalog.Parse(File.ReadAllBytes(
            Path.Combine(planetside, "startup.pak-out", "epackage.adb")))
            .ToDictionary(package => package.Name, StringComparer.OrdinalIgnoreCase);
        var serverObjectClasses = ClientWeaponMetadataResolver.ServerObjectClasses(File.ReadAllText(
            Path.Combine(psForever, "src/main/scala/net/psforever/packet/game/objectcreate/ObjectClass.scala")));
        var animationCatalog = GlbExportTool.LoadAnimationCatalog(planetside);
        string[] interactionAnimationNames = animationCatalog.Select(c => c.Name).ToArray();
        var entryCompiler = new VehicleEntryBindings(NativeAnimationPackageCatalog.Parse(
            File.ReadAllBytes(Path.Combine(planetside, "startup.pak-out", "apackage.adb"))), animationCatalog,
            VehicleExitBindings.Load(planetside, animationCatalog));
        var nativeEntries = vehicleModels.ToDictionary(pair => pair.Key,
            pair => entryCompiler.Resolve(gameObjects[pair.Key], gameObjects.GetValueOrDefault(pair.Value)),
            StringComparer.OrdinalIgnoreCase);
        AsciiCommandDatabase effectDatabase = AsciiCommandDatabase.TryLoad(planetside, "effects.adb")
            ?? throw new FileNotFoundException("Required startup database effects.adb was not found");
        var effectNames = effectDatabase.Records.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vehicleExplosionAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["quadassault"] = "quad_explosion",
            ["fury"] = "quad_explosion",
            ["quadstealth"] = "quad_stealth_explosion",
            ["two_man_assault_buggy"] = "twoman_assualt_explosion",
            ["threemanheavybuggy"] = "threeman_explosion",
            ["twomanheavybuggy"] = "twomanheavy_explosion",
            ["twomanhoverbuggy"] = "thresher_explode",
            ["aurora"] = "hover_explosion",
            ["battlewagon"] = "mediumtransport_explosion",
            ["thunderer"] = "mediumtransport_explosion",
            ["router"] = "explosion_router",
            ["switchblade"] = "explosion_switchblade",
            ["flail"] = "explosion_flail",
            ["wasp"] = "mosquito_explosion",
            ["lodestar"] = "dropship_explosion",
        };

        GameObjectDb.GameObject? Object(string name) => gameObjects.TryGetValue(name, out GameObjectDb.GameObject? value)
            ? value : null;
        GameObjectDb.GameObject? NativeWeapon(string name) =>
            ClientWeaponMetadataResolver.NativeWeapon(name, gameObjects, serverObjectClasses);
        string? ObjectValue(GameObjectDb.GameObject? gameObject, string key) =>
            GameObjectPropertyReader.Scalar(gameObject, key);
        string[] ObjectValues(GameObjectDb.GameObject? gameObject, string key) =>
            GameObjectPropertyReader.List(gameObject, key).ToArray();
        string? PhysicsName(string definition, string sourceRecord) =>
            ObjectValue(Object(definition), "physics") ?? ObjectValue(Object(sourceRecord), "physics");
        string? DestroyedPhysics(string definition, string sourceRecord) =>
            ObjectValue(Object(definition), "destroyedphysics")
            ?? ObjectValue(Object(sourceRecord), "destroyedphysics");
        string? DeployedPhysics(string definition, string sourceRecord) =>
            ObjectValue(Object(definition), "physics_deployed")
            ?? ObjectValue(Object(sourceRecord), "physics_deployed");
        bool? SourceFlag(string definition, string sourceRecord, string key)
        {
            string? value = ObjectValue(Object(definition), key) ?? ObjectValue(Object(sourceRecord), key);
            if (value is null) return null;
            if (bool.TryParse(value, out bool parsed)) return parsed;
            throw new InvalidDataException(
                $"Vehicle '{definition}' has non-boolean {key} value '{value}'");
        }
        GameObjectDb.GameObject? WeaponSource(string record)
        {
            GameObjectDb.GameObject? weapon = NativeWeapon(record);
            string? source = ObjectValues(weapon, "meshsequence").FirstOrDefault();
            return Object(source ?? "") ?? weapon;
        }
        ClientWeaponMetadataResolver.WeaponComponent[] WeaponComponents(string record)
        {
            GameObjectDb.GameObject? weapon = NativeWeapon(record);
            ClientWeaponMetadataResolver.WeaponComponent[] components =
                ClientWeaponMetadataResolver.Components(weapon);
            if (components.Length > 0) return components;
            string? attachBone = ObjectValues(weapon, "weaponattachbonenames").FirstOrDefault();
            return [new ClientWeaponMetadataResolver.WeaponComponent(0, weapon?.Name ?? record, attachBone)];
        }
        object[] WeaponSounds(string record)
        {
            GameObjectDb.GameObject? weapon = NativeWeapon(record);
            GameObjectDb.GameObject? source = WeaponSource(record);
            return ((source?.Properties ?? []).Concat(weapon?.Properties ?? []))
                .Where(property => property.Key.Contains("sound", StringComparison.OrdinalIgnoreCase))
                .SelectMany(property => property.Value
                    .Where(value => value.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    .Select(value => new { cue = property.Key, file = value }))
                .DistinctBy(value => (value.cue.ToLowerInvariant(), value.file.ToLowerInvariant()))
                .Cast<object>().ToArray();
        }
        object AmmunitionData(string name, int projectileIndex)
        {
            GameObjectDb.GameObject? ammunition = Object(name);
            AmmunitionProjectileResolver.Result resolution = AmmunitionProjectileResolver.Resolve(
                gameObjects, ammunition, projectileIndex);
            if (resolution.Warning is not null) Console.Error.WriteLine($"warning: {resolution.Warning}");
            return ProjectileBehaviorMetadata.ExportAmmunition(name, ammunition, resolution, gameObjects);
        }
        var attachmentResults = new Dictionary<string, ExportedAsset?>(StringComparer.OrdinalIgnoreCase);
        object[] WeaponFireModes(string record)
        {
            GameObjectDb.GameObject? weapon = NativeWeapon(record);
            GameObjectDb.GameObject? source = WeaponSource(record);
            if (source is null) return [];
            // A mesh component supplies defaults, but the owning weapon system is
            // authoritative. Wasp deliberately overrides its Mosquito component's
            // ammunition and cadence; Lightning and Vulture override theirs too.
            string? Value(string key) => ObjectValue(weapon, key) ?? ObjectValue(source, key);
            float? Number(string key) => float.TryParse(
                Value(key),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float value) ? value : null;
            string[] Values(string key) => ObjectValues(weapon, key) is { Length: > 0 } weaponValues
                ? weaponValues : ObjectValues(source, key);
            string? Sound(string key)
            {
                IReadOnlyList<string>? tuple = GameObjectPropertyReader.Tuple(weapon, key, 1, 2, 3)
                    ?? GameObjectPropertyReader.Tuple(source, key, 1, 2, 3);
                return tuple?[0];
            }
            return ClientWeaponMetadataResolver.FireModeIndices(weapon
                    ?? throw new InvalidDataException($"Missing native weapon '{record}'"))
                .Select(index =>
                {
                    int projectileIndex = (int)(Number($"firemode{index}_projectile_index") ?? 0);
                    string[] ammunition = Values($"firemode{index}_ammo_types");
                    if (ammunition.Length == 0 && string.Equals(
                        Value($"firemode{index}_use_common_ammo"), "true", StringComparison.OrdinalIgnoreCase))
                        ammunition = Values("firemode0_ammo_types");
                    var muzzles = ClientWeaponMetadataResolver.Muzzles(index, source, weapon);
                    var presentation = ClientWeaponMetadataResolver.FirePresentation(index, source, weapon);
                    var muzzleComponents = WeaponComponents(record).Select(c => new VehiclePresentationBindings.MuzzleComponent(
                        c.Index, c.Record, (attachmentResults.GetValueOrDefault(c.Record)?.Nodes ?? [])
                            .Concat(addendum.Bones(c.Record).Select(bone => bone.Name)).ToArray(),
                        effectPackages.GetValueOrDefault(c.Record))).ToArray();
                    var muzzleBindings = muzzleComponents.All(c => c.Nodes.Length == 0)
                        ? [] : VehiclePresentationBindings.Muzzles($"{record} mode {index}", muzzles,
                            presentation.FirstPersonEvent, muzzleComponents);
                    return new
                {
                    index,
                    ammunition = ammunition.Select(name => AmmunitionData(name, projectileIndex)).ToArray(),
                    projectileIndex,
                    muzzles, muzzleBindings, presentation,
                    burst = ClientWeaponMetadataResolver.FireBurst(index, source, weapon),
                    recoil = ClientCombatMetadata.ModeNumber(weapon!, index, "recoil"),
                    burstRefireSlopMs = ClientCombatMetadata.ModeNumber(weapon!, index, "burst_refire_slop"),
                    stamina = ClientCombatMetadata.Stamina(weapon!, index),
                    refireTimeMs = Number($"firemode{index}_refiretime"),
                    fireDelayMs = Number($"firemode{index}_fire_delay"),
                    chargeTimeMs = Number($"firemode{index}_chargetime"),
                    fireSound = Sound($"clientfiremode{index}_fire_sound"),
                    fireSpinDownSound = Sound($"clientfiremode{index}_fire_spindown_sound"),
                    loopingFireSound = string.Equals(Value($"clientfiremode{index}_looping_fire_sound"),
                        "true", StringComparison.OrdinalIgnoreCase),
                    crosshair = Value($"clientfiremode{index}_crosshair"),
                    defaultConeOfFire = Number($"firemode{index}_defaultCOF"),
                    crouchConeOfFire = Number($"firemode{index}_crouchCOF"),
                    maximumConeOfFire = Number($"firemode{index}_maxCOF"),
                    coneOfFireRecoveryMs = Number($"firemode{index}_COFrecovery"),
                    shotsBeforeConeOfFirePenalty = Number(
                        $"firemode{index}_max_shots_per_burst_before_cof_penalty"),
                    shotsPerRound = Number($"firemode{index}_shotsperround") ?? 1,
                    ammunitionPerShot = Number($"firemode{index}_ammopershot") ?? 1,
                    shotSpread = Number($"firemode{index}_shotspread") ?? 0,
                }; }).Cast<object>().ToArray();
        }

        ClientWeaponMetadataResolver.WeaponPoint? ResolveWeaponPoint(
            string vehicleDefinition,
            string sourceRecord,
            string weaponDefinition,
            int occurrence,
            int bindingIndex)
        {
            GameObjectDb.GameObject? vehicle = Object(vehicleDefinition);
            GameObjectDb.GameObject? source = Object(sourceRecord);
            ClientWeaponMetadataResolver.WeaponPoint? weaponPoint =
                ClientWeaponMetadataResolver.ExactWeaponPoint(
                    NativeWeapon(weaponDefinition)?.Name ?? weaponDefinition, occurrence, source, vehicle);
            if (weaponPoint is null)
            {
                int clientIndex = bindingIndex + 1;
                string? clientWeapon = ObjectValue(vehicle, $"weapon{clientIndex}")
                    ?? ObjectValue(source, $"weapon{clientIndex}");
                string[] serverComponents = WeaponComponents(weaponDefinition)
                    .Select(component => component.Record).ToArray();
                string[] clientComponents = WeaponComponents(clientWeapon ?? "")
                    .Select(component => component.Record).ToArray();
                if (serverComponents.Length > 0 && clientComponents.Length > 0
                    && serverComponents.SequenceEqual(clientComponents, StringComparer.OrdinalIgnoreCase))
                {
                    weaponPoint = ClientWeaponMetadataResolver.IndexedWeaponPoint(clientIndex, source, vehicle);
                    Console.WriteLine(
                        $"vehicle '{vehicleDefinition}' maps server weapon '{weaponDefinition}' to native "
                        + $"weapon{clientIndex} '{clientWeapon}' by identical ordered meshsequence");
                }
            }
            if (weaponPoint is null)
            {
                Console.Error.WriteLine(
                    $"warning: vehicle '{vehicleDefinition}' has no exact weaponN/weaponpointN match "
                    + $"for server weapon '{weaponDefinition}' occurrence {occurrence + 1}");
                return null;
            }
            return weaponPoint;
        }

        ClientWeaponMetadataResolver.WeaponPoint? ResolveTurretWeaponPoint(
            string turretDefinition,
            string sourceRecord,
            int weaponPath)
        {
            GameObjectDb.GameObject? turret = Object(turretDefinition);
            GameObjectDb.GameObject? source = Object(sourceRecord);
            ClientWeaponMetadataResolver.WeaponPoint? weaponPoint =
                ClientWeaponMetadataResolver.IndexedWeaponPoint(weaponPath, source, turret);
            if (weaponPoint is null)
            {
                Console.Error.WriteLine(
                    $"warning: turret '{turretDefinition}' has no native weapon{weaponPath}/weaponpoint{weaponPath}");
                return null;
            }
            return weaponPoint;
        }
        string physicsPath = Path.Combine(planetside, "startup.pak-out", "physics.lst");
        if (!File.Exists(physicsPath)) throw new FileNotFoundException("Extracted physics.lst not found", physicsPath);
        var physics = PhysicsListDatabase.ParseDirectory(Path.GetDirectoryName(physicsPath)!);
        var vehiclePhysicsCoverage = vehicleModels
            .Select(pair => VehicleManifestContract.RequireVehiclePhysics(
                physics, pair.Key, PhysicsName(pair.Key, pair.Value)))
            .ToDictionary(coverage => coverage.Definition, StringComparer.OrdinalIgnoreCase);
        VehicleManifestContract.RequireExpectedBodyCounts(
            vehiclePhysicsCoverage, VehicleManifestContract.OrdinaryAircraftBodyPrimitiveCounts);
        var flightControlCoverage = vehicleModels
            .Select(pair => new
            {
                Definition = pair.Key,
                Classification = VehicleManifestContract.ClassifyFlightControl(
                    pair.Key,
                    pair.Value,
                    SourceFlag(pair.Key, pair.Value, "canfly") == true,
                    SourceFlag(pair.Key, pair.Value, "flightisalwaysservercontrolled")),
            })
            .Where(value => value.Classification != null)
            .ToDictionary(
                value => value.Definition,
                value => value.Classification!,
                StringComparer.OrdinalIgnoreCase);
        VehicleManifestContract.RequireExpectedFlightControls(
            flightControlCoverage, VehicleManifestContract.NonBfrFlightControlClassifications);
        // Static turret definitions are a separate export path: several author a same-named identifier that
        // has no physics.lst model. Do not weaken the vehicle invariant to accommodate them. When a turret
        // does resolve native physics (the Spitfire family does), audit that resolved footprint too.
        foreach ((string definition, string sourceRecord) in turretModels)
        {
            string? authoredPhysicsName = PhysicsName(definition, sourceRecord);
            _ = VehicleManifestContract.AuditTurretPhysics(physics, definition, authoredPhysicsName);
        }
        int retainedUnsupportedCount = physics.RetainedUnsupportedCommands.Count;
        Console.WriteLine(
            $"validated native physics for {vehiclePhysicsCoverage.Count} non-BFR vehicles; "
            + $"retained {retainedUnsupportedCount} unsupported physics*.lst commands");
        var modelResults = new Dictionary<string, ExportedAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (string record in vehicleRenderRecords.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string relative = $"models/{record}.glb";
            modelResults[record] = Export(record, relative);
        }

        // Collision companions are part of the vehicle family publication boundary. Bind each render
        // record to the resolved vehicle definition rather than guessing from its mesh name: Battlewagon,
        // for example, renders battlewagontr but inherits mediumtransport physics. Shared render aliases
        // may publish one sidecar only when their active and destroyed physics contracts are identical.
        var collisionDefinitionsByRecord = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var collisionExpectedByRecord = new Dictionary<string,
            IReadOnlyCollection<VehicleManifestContract.ActiveCollisionShape>>(StringComparer.OrdinalIgnoreCase);
        var collisionDestroyedByRecord = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var collisionDeployedByRecord = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var collisionOwnerByRecord = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int collisionDefinitionAudits = 0;
        foreach ((string definition, string sourceRecord) in vehicleModels)
        {
            VehicleManifestContract.PhysicsCoverage coverage = vehiclePhysicsCoverage[definition];
            PhysicsListDatabase.Model model = physics.FindModel(coverage.PhysicsModel)
                ?? throw new InvalidDataException(
                    $"Validated vehicle '{definition}' physics model '{coverage.PhysicsModel}' disappeared");
            VehicleManifestContract.ActiveCollisionShape[] expected =
                VehicleManifestContract.ActiveObjectCollisionShapes(model);
            string? destroyedPhysics = DestroyedPhysics(definition, sourceRecord);
            string? deployedPhysics = DeployedPhysics(definition, sourceRecord);
            string renderRecord = vehicleRenderRecords[definition];

            if (collisionExpectedByRecord.TryGetValue(
                    renderRecord,
                    out IReadOnlyCollection<VehicleManifestContract.ActiveCollisionShape>? sharedExpected))
            {
                VehicleManifestContract.RequireEquivalentActiveCollisionContracts(
                    $"vehicle definitions '{collisionOwnerByRecord[renderRecord]}' and '{definition}' "
                        + $"sharing render record '{renderRecord}'",
                    sharedExpected,
                    expected);
                if (!string.Equals(collisionDeployedByRecord[renderRecord], deployedPhysics,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Vehicle definitions sharing '{renderRecord}' author different deployed physics");
                if (!string.Equals(
                        collisionDestroyedByRecord[renderRecord],
                        destroyedPhysics,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Vehicle definitions '{collisionOwnerByRecord[renderRecord]}' and '{definition}' "
                        + $"share render record '{renderRecord}' but author different destroyed physics "
                        + $"('{collisionDestroyedByRecord[renderRecord] ?? "none"}' and "
                        + $"'{destroyedPhysics ?? "none"}')");
                }
            }
            else
            {
                collisionDefinitionsByRecord[renderRecord] = definition;
                collisionExpectedByRecord[renderRecord] = expected;
                collisionDestroyedByRecord[renderRecord] = destroyedPhysics;
                collisionDeployedByRecord[renderRecord] = deployedPhysics;
                collisionOwnerByRecord[renderRecord] = definition;
            }
            collisionDefinitionAudits++;
        }
        if (collisionDefinitionAudits != vehicleModels.Count)
            throw new InvalidDataException(
                $"Vehicle collision coverage audited {collisionDefinitionAudits} of {vehicleModels.Count} definitions");

        CollisionManifestTool.Result collisionResult = CollisionManifestTool.Run(
            new CollisionManifestTool.Options(
                planetside,
                Path.Combine(output, "models"),
                GameObjectDefinitionsByRecord: collisionDefinitionsByRecord,
                ExpectedActivePhysicsByRecord: collisionExpectedByRecord,
                SelectedRecords: modelResults.Keys.ToArray()),
            progress);
        if (!collisionResult.Complete)
            throw new InvalidDataException(
                "Vehicle collision export failed:\n"
                + string.Join("\n", collisionResult.Failures.Select(
                    failure => $"- {failure.Record}: {failure.Reason}")));
        if (collisionResult.Examined != collisionExpectedByRecord.Count
            || collisionResult.Written != collisionExpectedByRecord.Count)
        {
            throw new InvalidDataException(
                $"Vehicle collision export examined {collisionResult.Examined} and wrote "
                + $"{collisionResult.Written} sidecars for {collisionExpectedByRecord.Count} render records");
        }
        Console.WriteLine(
            $"validated active collision sidecars for {collisionDefinitionAudits} vehicle definitions "
            + $"across {collisionResult.Written} render records");

        string vehicleSource = Path.Combine(psForever,
            "src/main/scala/net/psforever/objects/global/GlobalDefinitionsVehicle.scala");
        if (!File.Exists(vehicleSource)) throw new FileNotFoundException("PSForever vehicle definitions not found", vehicleSource);
        var definitionByVariable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var weaponBindings = new List<WeaponBinding>();
        var mountBindings = new List<VehicleMountBinding>();
        var controlledWeapons = new List<(string Vehicle, int Seat, int Slot)>();
        var namePattern = new Regex("^\\s*(\\w+)\\.Name\\s*=\\s*\"([^\"]+)\"");
        var weaponPattern = new Regex("^\\s*(\\w+)\\.Weapons\\s*\\+=\\s*(\\d+)\\s*->\\s*(\\w+)");
        foreach (string line in File.ReadLines(vehicleSource))
        {
            Match nameMatch = namePattern.Match(line);
            if (nameMatch.Success)
            {
                definitionByVariable[nameMatch.Groups[1].Value] = nameMatch.Groups[2].Value;
                continue;
            }
            Match controlled = Regex.Match(line, @"^\s*(\w+)\.controlledWeapons\(seat\s*=\s*(\d+),\s*weapon\s*=\s*(\d+)\)");
            if (controlled.Success && definitionByVariable.TryGetValue(controlled.Groups[1].Value, out string? owner))
                controlledWeapons.Add((owner, int.Parse(controlled.Groups[2].Value), int.Parse(controlled.Groups[3].Value)));
            Match weaponMatch = weaponPattern.Match(line);
            if (weaponMatch.Success)
            {
                string variable = weaponMatch.Groups[1].Value;
                if (!definitionByVariable.TryGetValue(variable, out string? definition)) continue;
                if (!vehicleModels.ContainsKey(definition)) continue;
                weaponBindings.Add(new WeaponBinding(
                    definition,
                    int.Parse(weaponMatch.Groups[2].Value),
                    weaponMatch.Groups[3].Value));
                continue;
            }
            Match mountMatch = VehicleManifestContract.ServerMountDeclaration(line);
            if (!mountMatch.Success) continue;
            string mountVariable = mountMatch.Groups[1].Value;
            if (!definitionByVariable.TryGetValue(mountVariable, out string? mountDefinition)) continue;
            if (!vehicleModels.ContainsKey(mountDefinition)) continue;
            mountBindings.Add(new VehicleMountBinding(
                mountDefinition,
                int.Parse(mountMatch.Groups[2].Value),
                int.Parse(mountMatch.Groups[3].Value)));
        }

        var turretBindings = new List<TurretWeaponBinding>();
        var turretWeaponPattern = new Regex(
            "^\\s*(\\w+)\\.WeaponPaths\\((\\d+)\\)\\s*\\+=\\s*TurretUpgrade\\.(\\w+)\\s*->\\s*(\\w+)");
        foreach (string source in new[]
        {
            "GlobalDefinitionsMiscellaneous.scala",
            "GlobalDefinitionsDeployable.scala",
        }.Select(name => Path.Combine(psForever, "src/main/scala/net/psforever/objects/global", name)))
        {
            if (!File.Exists(source)) throw new FileNotFoundException("PSForever turret definitions not found", source);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(source))
            {
                Match nameMatch = namePattern.Match(line);
                if (nameMatch.Success)
                {
                    names[nameMatch.Groups[1].Value] = nameMatch.Groups[2].Value;
                    continue;
                }
                Match turretMount = VehicleManifestContract.ServerMountDeclaration(line);
                if (turretMount.Success
                    && names.TryGetValue(turretMount.Groups[1].Value, out string? mountDefinition)
                    && turretModels.ContainsKey(mountDefinition))
                {
                    mountBindings.Add(new VehicleMountBinding(
                        mountDefinition,
                        int.Parse(turretMount.Groups[2].Value),
                        int.Parse(turretMount.Groups[3].Value)));
                    continue;
                }
                Match binding = turretWeaponPattern.Match(line);
                if (!binding.Success || !names.TryGetValue(binding.Groups[1].Value, out string? definition) ||
                    !turretModels.ContainsKey(definition)) continue;
                string weapon = binding.Groups[4].Value;
                turretBindings.Add(new TurretWeaponBinding(
                    definition,
                    int.Parse(binding.Groups[2].Value),
                    binding.Groups[3].Value,
                    weapon,
                    WeaponComponents(weapon)));
            }
        }

        var vehicleWeaponComponentRecords = weaponBindings
            .SelectMany(binding => WeaponComponents(binding.Record).Select(component => component.Record));
        foreach (string record in vehicleWeaponComponentRecords
            .Concat(turretBindings.SelectMany(binding => binding.Components.Select(component => component.Record)))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!records.Contains(record))
            {
                attachmentResults[record] = null;
                continue;
            }
            try
            {
                attachmentResults[record] = Export(record, $"attachments/{record.ToLowerInvariant()}.glb");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"attachment {record} remains body-integrated or non-mesh: {exception.Message}");
                attachmentResults[record] = null;
            }
        }
        foreach (string record in vehicleBodyComponents.Values.SelectMany(components => components).Select(component => component.Record)
            .Distinct(StringComparer.OrdinalIgnoreCase))
            bodyComponentResults[record] = Export(record, $"components/{record.ToLowerInvariant()}.glb");

        foreach (WeaponBinding binding in weaponBindings)
        {
            ClientWeaponMetadataResolver.WeaponComponent[] authored =
                ClientWeaponMetadataResolver.Components(NativeWeapon(binding.Record));
            if (authored.Length == 0) continue;
            string[] missing = authored
                .Where(component => attachmentResults.GetValueOrDefault(component.Record) is null)
                .Select(component => component.Record)
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Vehicle weapon '{binding.Record}' declares meshsequence component(s) that could not be exported: "
                    + string.Join(", ", missing));
            }
        }
        ClientWeaponMetadataResolver.AttachmentBoneResolution? ComponentAttachment(ClientWeaponMetadataResolver.WeaponComponent component)
        {
            ExportedAsset? asset = attachmentResults.GetValueOrDefault(component.Record);
            return asset is null ? null : AssetAttachment(component.WeaponAttachBone, asset, component.Record);
        }
        ClientWeaponMetadataResolver.AttachmentBoneResolution AssetAttachment(string? requested, ExportedAsset asset, string record)
        {
            string[] nodes = asset.Nodes.Concat(addendum.Bones(record).Select(bone => bone.Name)).ToArray();
            string? jointZero = null;
            if (!nodes.Contains(requested, StringComparer.OrdinalIgnoreCase))
            {
                using var reader = new BinaryReader(File.OpenRead(Path.Combine(output, asset.Uri)));
                reader.BaseStream.Position = 12;
                int length = reader.ReadInt32();
                if (reader.ReadUInt32() != 0x4E4F534A) throw new InvalidDataException($"Missing GLB header: {asset.Uri}");
                using var json = JsonDocument.Parse(reader.ReadBytes(length));
                JsonElement root = json.RootElement;
                int[] roots = root.GetProperty("skins").EnumerateArray()
                    .Select(skin => skin.GetProperty("joints")[0].GetInt32()).Distinct().ToArray();
                if (roots.Length == 1) jointZero = root.GetProperty("nodes")[roots[0]].GetProperty("name").GetString();
            }
            return ClientWeaponMetadataResolver.AttachmentBone(requested, nodes, jointZero);
        }
        foreach (TurretWeaponBinding binding in turretBindings)
        {
            ClientWeaponMetadataResolver.WeaponComponent[] authored =
                ClientWeaponMetadataResolver.Components(NativeWeapon(binding.Weapon));
            if (authored.Length == 0) continue;
            string[] missing = authored
                .Where(component => attachmentResults.GetValueOrDefault(component.Record) is null)
                .Select(component => component.Record)
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Turret weapon '{binding.Weapon}' declares meshsequence component(s) that could not be exported: "
                    + string.Join(", ", missing));
            }
        }

        var wreckResults = new Dictionary<string, ExportedAsset?>(StringComparer.OrdinalIgnoreCase);
        foreach (string record in vehicleModels.Concat(turretModels)
            .Select(pair => DestroyedPhysics(pair.Key, pair.Value))
            .Where(record => !string.IsNullOrWhiteSpace(record))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!records.Contains(record))
            {
                wreckResults[record] = null;
                continue;
            }
            try
            {
                wreckResults[record] = Export(record, $"wrecks/{record.ToLowerInvariant()}.glb");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"destroyed model {record} is unavailable: {exception.Message}");
                wreckResults[record] = null;
            }
        }

        var vehicleMetadata = new VehicleMetadataCompiler(
            gameObjects,
            nativeEntries,
            mountBindings,
            controlledWeapons,
            modelResults,
            vehicleRenderRecords,
            interactionAnimationNames,
            physics,
            effectNames,
            vehicleExplosionAliases,
            wreckResults);

        var vehicles = vehicleModels.Select(pair =>
        {
            ExportedAsset asset = modelResults[vehicleRenderRecords[pair.Key]];
            VehicleExportData vehicleData = vehicleMetadata.Compile(pair.Key, pair.Value);
            var weaponOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var exportedWeapons = weaponBindings.Where(binding => binding.Vehicle == pair.Key).Select((binding, bindingIndex) =>
            {
                int occurrence = weaponOccurrences.GetValueOrDefault(binding.Record);
                weaponOccurrences[binding.Record] = occurrence + 1;
                ClientWeaponMetadataResolver.WeaponPoint? weaponPoint =
                    ResolveWeaponPoint(pair.Key, pair.Value, binding.Record, occurrence, bindingIndex);
                ClientWeaponMetadataResolver.WeaponComponent[] weaponComponents = WeaponComponents(binding.Record);
                if (weaponPoint is not null) VehiclePresentationBindings.RequireMounts(
                    $"{pair.Key} slot {binding.Slot}",
                    weaponPoint.AttachBones.Concat(weaponPoint.YawBones).Concat(weaponPoint.PitchBones),
                    asset.Nodes.Concat(addendum.Bones(pair.Value).Select(bone => bone.Name)));
                var components = weaponComponents.Select(component => new
                {
                    index = component.Index,
                    definition = component.Record,
                    model = attachmentResults.GetValueOrDefault(component.Record)?.Uri,
                    platformAttachBone = component.Index < (weaponPoint?.AttachBones.Length ?? 0)
                        ? weaponPoint!.AttachBones[component.Index]
                        : weaponPoint?.AttachBones.Length == 1 ? weaponPoint.AttachBones[0] : null,
                    weaponAttachBone = component.WeaponAttachBone,
                    weaponAttachResolution = ComponentAttachment(component),
                    boneAdditions = addendum.Bones(component.Record),
                }).ToArray();
                string? legacyModel = components.Length == 1 ? components[0].model : null;
                return new
                {
                    slot = binding.Slot,
                    definition = binding.Record,
                    sourceRecord = NativeWeapon(binding.Record)?.Name,
                    optics = ClientCombatMetadata.Optics(NativeWeapon(binding.Record)!),
                    classId = NativeWeapon(binding.Record)?.ClassId,
                    provenance = NativeWeapon(binding.Record)?.Provenance,
                    weaponPoint = weaponPoint?.Index,
                    attachBones = weaponPoint?.AttachBones ?? [],
                    pitchBones = weaponPoint?.PitchBones ?? [],
                    yawBones = weaponPoint?.YawBones ?? [],
                    aim = weaponPoint?.Aim,
                    model = legacyModel,
                    components,
                    representation = components.Any(component => component.model != null)
                        ? "separate-components" : "body-integrated-or-effect",
                    sounds = WeaponSounds(binding.Record),
                    fireModes = WeaponFireModes(binding.Record),
                };
            }).ToArray();
            return new
            {
                definition = pair.Key,
                sourceRecord = pair.Value,
                modelRecord = vehicleRenderRecords[pair.Key],
                bodyComponents = vehicleBodyComponents[pair.Key].Select(component => {
                    var child = bodyComponentResults[component.Record];
                    VehiclePresentationBindings.RequireMounts($"{pair.Key} body component {component.Record}", [component.PlatformBone],
                        asset.Nodes.Concat(addendum.Bones(pair.Value).Select(bone => bone.Name)));
                    var anchor = AssetAttachment(component.ChildBone, child, component.Record);
                    return new { definition = component.Record, model = child.Uri, platformAttachBone = component.PlatformBone,
                        weaponAttachBone = component.ChildBone, weaponAttachResolution = anchor, boneAdditions = addendum.Bones(component.Record) };
                }).ToArray(),
                boneAdditions = addendum.Bones(pair.Value),
                provenance = Object(pair.Key)?.Provenance,
                sourceProvenance = Object(pair.Value)?.Provenance,
                model = asset.Uri,
                triangles = asset.Triangles,
                bones = asset.Bones,
                animationClips = asset.Animations,
                articulationNodes = asset.ArticulationNodes,
                hardpoints = asset.Hardpoints,
                targeting = NativeTargetingMetadata.Resolve(Object(pair.Key)!,
                    asset.Nodes.Concat(addendum.Bones(pair.Value).Select(bone => bone.Name))),
                handling = vehicleData.Handling,
                flightPresentation = vehicleData.FlightPresentation,
                camera = vehicleData.Camera,
                audio = vehicleData.Audio,
                cargo = vehicleData.Cargo,
                destruction = vehicleData.Destruction,
                animationAttachBone = vehicleData.AnimationAttachBone,
                seatMountPoints = vehicleData.SeatMountPoints,
                seatAnimations = vehicleData.SeatAnimations,
                physics = vehicleData.Physics,
                deployedPhysics = vehicleData.DeployedPhysics,
                wheelsCoordinateSystem = VehicleManifestContract.NativeDataCoordinateSystem,
                wheels = vehicleData.Wheels,
                // Only weapons with native mobile/deployed arcs consume this pose contract.
                // Other vehicle deployment mechanisms remain on their existing presentation path.
                deploymentAim = exportedWeapons.Any(weapon => weapon.aim?.LeftDegrees.Length > 1)
                    ? new { source = "game_objects.adb", coordinateSystem = "right-handed-z-up",
                        durationSeconds = float.Parse(ObjectValue(Object(pair.Key), "vehicledeploymenttime") ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                        transforms = VehiclePresentationBindings.Deployment(Object(pair.Key)!) } : null,
                weapons = exportedWeapons,
            };
        }).ToArray();

        var trackedVehiclePresentations = vehicles
            .Where(vehicle => vehicle.wheels.Any(wheel => wheel.Tread != null))
            .ToDictionary(
                vehicle => vehicle.definition,
                vehicle => vehicle.wheels
                    .Where(wheel => wheel.Tread != null)
                    .Select(wheel => new VehicleManifestContract.TrackedTreadPresentation(
                        wheel.Index,
                        wheel.Tread!.Direction,
                        wheel.Tread.RightTread,
                        wheel.Tread.RollLength,
                        wheel.Tread.Material))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        VehicleManifestContract.RequireExpectedTrackedVehiclePresentations(trackedVehiclePresentations);


        var attachments = attachmentResults.Select(pair => new
        {
            definition = pair.Key,
            provenance = Object(pair.Key)?.Provenance,
            model = pair.Value?.Uri,
            triangles = pair.Value?.Triangles,
            bones = pair.Value?.Bones,
            animationClips = pair.Value?.Animations,
            articulationNodes = pair.Value?.ArticulationNodes,
            hardpoints = pair.Value?.Hardpoints,
            representation = pair.Value == null ? "body-integrated-or-effect" : "separate-mesh",
        }).ToArray();

        var turrets = turretModels.Select(pair =>
        {
            VehicleExportData turretData = vehicleMetadata.Compile(pair.Key, pair.Value);
            var exportedWeapons = turretBindings.Where(binding => binding.Turret == pair.Key).Select(binding =>
            {
                // A TurretDefinition WeaponPaths(N) entry is the server identity for the client's
                // weaponN/weaponpointN pair. Upgrade definitions change the equipped tool, not its mount.
                ClientWeaponMetadataResolver.WeaponPoint? weaponPoint =
                    ResolveTurretWeaponPoint(pair.Key, pair.Value, binding.Slot);
                return new
                {
                    slot = binding.Slot,
                    upgrade = binding.Upgrade,
                    definition = binding.Weapon,
                    sourceRecord = NativeWeapon(binding.Weapon)?.Name,
                    optics = ClientCombatMetadata.Optics(NativeWeapon(binding.Weapon)!),
                    classId = NativeWeapon(binding.Weapon)?.ClassId,
                    provenance = NativeWeapon(binding.Weapon)?.Provenance,
                    weaponPoint = weaponPoint?.Index,
                    attachBones = weaponPoint?.AttachBones ?? [],
                    pitchBones = weaponPoint?.PitchBones ?? [],
                    yawBones = weaponPoint?.YawBones ?? [],
                    aim = weaponPoint?.Aim,
                    components = binding.Components.Select(component => new
                    {
                        index = component.Index,
                        definition = component.Record,
                        model = attachmentResults.GetValueOrDefault(component.Record)?.Uri,
                        platformAttachBone = component.Index < (weaponPoint?.AttachBones.Length ?? 0)
                            ? weaponPoint!.AttachBones[component.Index]
                            : weaponPoint?.AttachBones.Length == 1 ? weaponPoint.AttachBones[0] : null,
                        weaponAttachBone = component.WeaponAttachBone,
                        weaponAttachResolution = ComponentAttachment(component),
                        boneAdditions = addendum.Bones(component.Record),
                    }).ToArray(),
                    sounds = WeaponSounds(binding.Weapon),
                    fireModes = WeaponFireModes(binding.Weapon),
                };
            }).ToArray();
            return new
            {
                definition = pair.Key,
                sourceRecord = pair.Value,
                provenance = Object(pair.Key)?.Provenance,
                sourceProvenance = Object(pair.Value)?.Provenance,
                targeting = NativeTargetingMetadata.Resolve(Object(pair.Key)!),
                handling = turretData.Handling,
                camera = turretData.Camera,
                audio = turretData.Audio,
                destruction = turretData.Destruction,
                seatMountPoints = turretData.SeatMountPoints,
                seatAnimations = turretData.SeatAnimations,
                physics = turretData.Physics,
                weapons = exportedWeapons,
            };
        }).ToArray();


        var manifest = new
        {
            schemaVersion = VehicleManifestContract.SchemaVersion,
            coordinateSystems = new
            {
                modelAssets = VehicleManifestContract.ModelAssetCoordinateSystem,
                retainedNativeData = VehicleManifestContract.NativeDataCoordinateSystem,
            },
            source = "PlanetSide client UBR/FLAT assets and PSForever GlobalDefinitionsVehicle",
            gameObjects = gameObjectDatabase.Diagnostics,
            physicsListCommandCoverage = new
            {
                source = "physics*.lst",
                knownCommands = PhysicsListDatabase.KnownCommandNames
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                encounteredCommands = physics.CommandCounts
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new { command = pair.Key, occurrences = pair.Value }).ToArray(),
                retainedUnsupportedCommands = physics.RetainedUnsupportedCommands
                    .GroupBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new { command = group.Key, occurrences = group.Count() }).ToArray(),
            },
            excludedBfrs,
            vehicles,
            turrets,
            attachments,
        };
        // Publish browser models only after every image has a verified shared companion.
        SharedGlbImageExport.Run(output, recursive: true);
        string manifestPath = Path.Combine(output, "manifest.json");
        string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        int generatedCollisionAudits = VehicleManifestContract.RequireGeneratedCollisionCoverage(
            manifestJson, Path.Combine(output, "models"), vehicleModels.Count);
        File.WriteAllText(manifestPath, manifestJson);
        File.WriteAllText(Path.Combine(output, "creation-pads.json"), JsonSerializer.Serialize(new {
            format = "planetside-vehicle-creation-pads", version = 1,
            pads = VehicleCreationPadBindings.Resolve(gameObjects.Values),
            coordinateSystem = VehicleManifestContract.NativeDataCoordinateSystem,
            vehicles = vehicles.Select(vehicle => new {
                vehicle.definition,
                useAlternateAttachment = bool.Parse(GameObjectPropertyReader.Scalar(
                    Object(vehicle.definition), "usealternateattachbonenameifavailable") ?? "false"),
            }).ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine($"wrote {vehicles.Length} non-BFR vehicle definitions and {attachments.Length} weapon definitions -> {manifestPath}");
        Console.WriteLine(
            $"verified serialized manifest/sidecar collision parity for {generatedCollisionAudits} vehicle definitions");
        return 0;

        ExportedAsset Export(string record, string relative)
        {
            string path = Path.Combine(output, relative);
            string materialsPath = Path.ChangeExtension(path, ".materials.json");
            GlbExportTool.Result? result = null;
            var requiredEntries = vehicleRenderRecords.Where(p => p.Value.Equals(record, StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => nativeEntries[p.Key]).ToArray();
            bool entryClipsCurrent = true;
            if (reuseAssets && File.Exists(path) && requiredEntries.Length > 0)
            {
                var existing = VehicleExportSupport.InspectGlb(path);
                entryClipsCurrent = requiredEntries.SelectMany(e => e.Variants)
                    .SelectMany(v => new[] { v.Mount, v.Dismount }).Where(c => c.VehicleTracks.Length > 0)
                    .All(c => existing.Animations.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
            }
            // Reuse is valid only when the GLB and its lossless native-material
            // sidecar both exist. Older exports predate sidecars and otherwise lose
            // animated/emissive/detail stages indefinitely.
            if (!reuseAssets || !entryClipsCurrent || !File.Exists(path) || !File.Exists(materialsPath))
            {
                string[] packageClips = vehicleRenderRecords.Where(p => p.Value.Equals(record, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(p => entryCompiler.PackageClips(entryCompiler.Package(gameObjects[p.Key], Object(vehicleModels[p.Key]))))
                    .Concat(animationCatalog.Where(c => c.Name.StartsWith(record + "_", StringComparison.OrdinalIgnoreCase)).Select(c => c.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var options = new GlbExportTool.Options(planetside, record, path,
                    IncludeAnimations: true, MaxAnimations: int.MaxValue,
                    AnimationPrefixes: [record], AnimationNames: packageClips, AnimationCatalog: animationCatalog);
                result = GlbExportTool.Run(options, progress);
            }
            GlbInspection inspection = VehicleExportSupport.InspectGlb(path);
            string[] articulation = inspection.Nodes.Where(VehicleExportSupport.IsArticulationNode)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] hardpoints = inspection.Nodes.Where(VehicleExportSupport.IsHardpoint)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new ExportedAsset(relative.Replace('\\', '/'), result?.Triangles ?? inspection.Triangles,
                result?.Bones ?? inspection.Bones, inspection.Animations, inspection.Nodes, articulation, hardpoints);
        }

    }

}
