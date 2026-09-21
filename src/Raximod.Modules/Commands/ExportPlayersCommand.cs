using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Raximod.Generation;
using Raximod.Generation.Assets;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Meshes;
using Raximod.EngineAssets.Textures;

namespace Raximod.Modules;

public static class ExportPlayersCommand
{
    public static int Run(string[] args)
    {
        if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] is not ("--first-person-only" or "--cosmetics-only" or "--animations-only")))
        {
            Console.Error.WriteLine("usage: <PlanetSideDir> <output-directory> [--first-person-only|--cosmetics-only|--animations-only]");
            return 1;
        }

        string planetside = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        bool firstPersonOnly = args.Length == 3 && args[2] == "--first-person-only";
        bool cosmeticsOnly = args.Length == 3 && args[2] == "--cosmetics-only";
        bool animationsOnly = args.Length == 3 && args[2] == "--animations-only";
        if (animationsOnly && !File.Exists(Path.Combine(output, "manifest.json")))
            throw new FileNotFoundException("--animations-only requires an existing validated player manifest");
        Directory.CreateDirectory(output);
        string sharedTextureDirectory = Path.Combine(output, "material-textures");
        var progress = new SynchronousProgress<string>(Console.WriteLine);
        const string firstPersonManifestUri = "first-person/manifest.json";

        if (firstPersonOnly)
        {
            ExportFirstPersonArms();
            SharedGlbImageExport.Run(output, textureDirectory: sharedTextureDirectory, recursive: true);
            return 0;
        }

        if (!animationsOnly) PlayerCosmeticExport.Run(planetside, output, progress);
        if (cosmeticsOnly)
        {
            SharedGlbImageExport.Run(output, textureDirectory: sharedTextureDirectory, recursive: true);
            return 0;
        }

        var physics = PhysicsListDatabase.ParseDirectory(Path.Combine(planetside, "startup.pak-out"));
        PhysicsListDatabase.Model? playerPhysics = physics.FindModel("player");
        string gameObjectsPath = Path.Combine(planetside, "startup.pak-out", "game_objects.adb");
        GameObjectDb gameObjectDatabase = GameObjectDb.Parse(File.ReadAllBytes(gameObjectsPath));
        var gameObjects = gameObjectDatabase.ResolvedObjects
            .ToDictionary(gameObject => gameObject.Name, StringComparer.OrdinalIgnoreCase);
        string animationPackagePath = Path.Combine(planetside, "startup.pak-out", "apackage.adb");
        var animationPackages = NativeAnimationPackageCatalog.Parse(File.ReadAllBytes(animationPackagePath))
            .ToDictionary(package => package.Name, StringComparer.OrdinalIgnoreCase);

        var models = new List<object>();
        var heads = new List<object>();
        var animations = new Dictionary<string, object>();
        string[] factions = ["nc", "tr", "vs"];
        string[] genders = ["female", "male"];
        var armorRecords = new[]
        {
            (Id: "light", Suffix: "lite"),
            (Id: "medium", Suffix: "med"),
            (Id: "standard", Suffix: "si"),
            (Id: "stealth", Suffix: "sth"),
        };

        if (!animationsOnly)
        {
        foreach (string faction in factions)
        foreach (string gender in genders)
        foreach (var armor in armorRecords)
        {
            string genderCode = gender == "female" ? "f" : "m";
            string record = faction + genderCode + armor.Suffix;
            string sourceMesh = PlayerHqMesh(record);
            string relative = $"models/{faction}/{gender}/{armor.Id}.glb";
            GlbExportTool.Result result = Export(record, relative, false, meshNames: [sourceMesh]);
            models.Add(new
            {
                id = $"{faction}-{gender}-{armor.Id}",
                type = "infantry",
                faction,
                gender,
                armor = armor.Id,
                record,
                sourceMesh,
                provenance = gameObjects.GetValueOrDefault(record)?.Provenance,
                uri = relative,
                animationSet = $"infantry-{gender}",
                triangles = result.Triangles,
                bones = result.Bones,
            });
        }

        foreach (string faction in factions)
        {
            string record = faction + "hev";
            string sourceMesh = PlayerHqMesh(record);
            string relative = $"models/{faction}/max.glb";
            GlbExportTool.Result result = Export(record, relative, false, meshNames: [sourceMesh]);
            models.Add(new
            {
                id = $"{faction}-max",
                type = "max",
                faction,
                gender = (string?)null,
                armor = "max",
                record,
                sourceMesh,
                provenance = gameObjects.GetValueOrDefault(record)?.Provenance,
                uri = relative,
                animationSet = $"max-{faction}",
                triangles = result.Triangles,
                bones = result.Bones,
            });
        }

        // Faces are separate skinned records in the client. Preserve every selectable face for the
        // generic head and every faction/armor-specific head family that actually exists in uber.ubr.
        var headFamilies = new[]
        {
            (Id: "generic-female", Prefix: "female_head_", Faction: (string?)null, Gender: "female", Armor: (string?)null),
            (Id: "generic-male", Prefix: "male_head_", Faction: (string?)null, Gender: "male", Armor: (string?)null),
            (Id: "nc-light-female", Prefix: "nclite_f_head_", Faction: "nc", Gender: "female", Armor: "light"),
            (Id: "nc-light-male", Prefix: "nclite_m_head_", Faction: "nc", Gender: "male", Armor: "light"),
            (Id: "nc-medium-female", Prefix: "ncmed_f_head_", Faction: "nc", Gender: "female", Armor: "medium"),
            (Id: "nc-medium-male", Prefix: "ncmed_m_head_", Faction: "nc", Gender: "male", Armor: "medium"),
            (Id: "nc-max-female", Prefix: "nchev_f_head_", Faction: "nc", Gender: "female", Armor: "max"),
            (Id: "nc-max-male", Prefix: "nchev_m_head_", Faction: "nc", Gender: "male", Armor: "max"),
            (Id: "tr-light-female", Prefix: "trlite_f_head_", Faction: "tr", Gender: "female", Armor: "light"),
            (Id: "tr-light-male", Prefix: "trlite_m_head_", Faction: "tr", Gender: "male", Armor: "light"),
            (Id: "tr-medium-female", Prefix: "trmed_f_head_", Faction: "tr", Gender: "female", Armor: "medium"),
            (Id: "tr-medium-male", Prefix: "trmed_m_head_", Faction: "tr", Gender: "male", Armor: "medium"),
            (Id: "tr-stealth-female", Prefix: "trsth_f_head_", Faction: "tr", Gender: "female", Armor: "stealth"),
            (Id: "tr-stealth-male", Prefix: "trsth_m_head_", Faction: "tr", Gender: "male", Armor: "stealth"),
            (Id: "tr-max-female", Prefix: "trhev_f_head_", Faction: "tr", Gender: "female", Armor: "max"),
            (Id: "tr-max-male", Prefix: "trhev_m_head_", Faction: "tr", Gender: "male", Armor: "max"),
            (Id: "vs-light-female", Prefix: "vslite_f_head_", Faction: "vs", Gender: "female", Armor: "light"),
            (Id: "vs-light-male", Prefix: "vslite_m_head_", Faction: "vs", Gender: "male", Armor: "light"),
        };

        foreach (var family in headFamilies)
        foreach (char face in "abcde")
        {
            string record = family.Prefix + face;
            string relative = $"heads/{family.Id}/{face}.glb";
            // Head geometry is rigidly attached to its native `head` bone even though
            // its vertex sections do not carry deform weights. Preserve that tiny rig:
            // the browser uses it as the deterministic attachment anchor for the body
            // neck instead of guessing a per-head offset.
            GlbExportTool.Result result = Export(record, relative, false, preserveRigidBoneAttachments: true);
            heads.Add(new
            {
                id = $"{family.Id}-{face}",
                family = family.Id,
                faction = family.Faction,
                gender = family.Gender,
                armor = family.Armor,
                face = face.ToString(),
                record,
                provenance = gameObjects.GetValueOrDefault(record)?.Provenance,
                uri = relative,
                triangles = result.Triangles,
                bones = result.Bones,
            });
        }

        // The Vanu MAX has a single enclosed helmet instead of the gender/face variants above.
        {
            const string record = "vshevh";
            const string relative = "heads/vs-max/helmet.glb";
            GlbExportTool.Result result = Export(record, relative, false);
            heads.Add(new
            {
                id = "vs-max-helmet",
                family = "vs-max",
                faction = "vs",
                gender = (string?)null,
                armor = "max",
                face = (string?)null,
                record,
                provenance = gameObjects.GetValueOrDefault(record)?.Provenance,
                uri = relative,
                triangles = result.Triangles,
                bones = result.Bones,
            });
        }
        }

        var animationSources = new[]
        {
            (Id: "infantry-female", Record: "ncflite", Prefix: "ncflite"),
            (Id: "infantry-male", Record: "ncmlite", Prefix: "ncmlite"),
            (Id: "max-nc", Record: "nchev", Prefix: "nchev"),
            (Id: "max-tr", Record: "trhev", Prefix: "trhev"),
            (Id: "max-vs", Record: "vshev", Prefix: "vshev"),
        };
        string[] animationRecordNames = AnimationRecordNames(planetside);
        var vehicleAnimationPackages = gameObjects.Values
            .Where(gameObject => GameObjectPropertyReader.Scalar(gameObject, "animattachbonename") != null
                || gameObject.Properties.Keys.Any(key => key.StartsWith("mountzone", StringComparison.OrdinalIgnoreCase)
                    && key.EndsWith("_name", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(gameObject => new[] { gameObject.Name, GameObjectPropertyReader.Scalar(gameObject, "animationpackage") })
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] interactionAnimationNames = InteractionAnimationNames(
            animationRecordNames, animationPackages.Values, vehicleAnimationPackages);

        foreach (var source in animationSources)
        {
            if (!animationPackages.TryGetValue(source.Record, out NativeAnimationPackage? nativePackage))
                throw new InvalidDataException($"No native animation package exists for player rig '{source.Record}'");
            string relative = $"animations/{source.Id}.glb";
            string path = Path.Combine(output, relative);
            NativeAnimationClipSelection.Binding[] clipBindings = nativePackage.Animations
                .Select(animation => NativeAnimationClipSelection.Resolve(animation, animationRecordNames))
                .ToArray();
            string[] authoredAnimationNames = clipBindings
                .SelectMany(binding => binding.ConcreteClips)
                .Concat(interactionAnimationNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var options = new GlbExportTool.Options(planetside, source.Record, path,
                IncludeAnimations: true, MaxAnimations: int.MaxValue,
                // apackage.adb is the authoritative membership/alias table. Resolve its virtual
                // reference-pose entries to the concrete archive records, including source families
                // whose playback enum is not currently classified.
                AnimationNames: authoredAnimationNames,
                SharedTextureDirectory: sharedTextureDirectory);
            GlbExportTool.Result result = GlbExportTool.Run(options, progress);
            animations[source.Id] = new
            {
                uri = relative,
                sourceRecord = source.Record,
                animationPrefix = source.Prefix,
                nativePackage = new
                {
                    name = nativePackage.Name,
                    fallback = nativePackage.Fallback,
                    provenance = nativePackage.Provenance,
                    animations = nativePackage.Animations,
                    audio = nativePackage.Audio,
                    callbacks = nativePackage.Callbacks,
                },
                // Preserve the exact alias-to-archive-record mapping used by this export. Downstream
                // derivative compilers must not rediscover it with broad clip-name regular expressions.
                clipBindings = clipBindings.Select(binding => new
                {
                    alias = binding.Alias,
                    authoredAnimation = binding.Animation,
                    playbackMode = binding.PlaybackMode,
                    resolution = binding.Resolution,
                    concreteClips = binding.ConcreteClips,
                }),
                clips = result.Animations,
                bones = result.Bones,
            };
        }

        if (animationsOnly)
        {
            SharedGlbImageExport.Run(output, textureDirectory: sharedTextureDirectory, recursive: true);
            string destination = Path.Combine(output, "manifest.json");
            var retained = JsonNode.Parse(File.ReadAllText(destination))!.AsObject();
            retained["animationSets"] = JsonSerializer.SerializeToNode(animations);
            File.WriteAllText(destination, retained.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"refreshed {animations.Count} animation sets; body, head and viewmodel exports retained -> {destination}");
            return 0;
        }

        static string[] AnimationRecordNames(string root)
        {
            string[] libraries =
            [
                "anims.ubr", "patch1/anim_patch1.ubr", "patch2/anim_patch2.ubr",
                "patch3/anim_patch3.ubr", "patch4/anim_patch4.ubr", "patch5/anim_patch5.ubr",
            ];
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string relative in libraries)
            {
                string path = Path.Combine(root, relative);
                if (!File.Exists(path)) continue;
                foreach (AnimRecord clip in AnimDb.Load(File.ReadAllBytes(path)).Records)
                    names.Add(clip.Name);
            }
            return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        static string[] InteractionAnimationNames(
            IReadOnlyList<string> availableClips,
            IEnumerable<NativeAnimationPackage> animationPackages,
            IReadOnlySet<string> vehicleAnimationPackages)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string clipName in availableClips)
            {
                if (!clipName.StartsWith("z_", StringComparison.OrdinalIgnoreCase)
                    && (clipName.EndsWith("_mount", StringComparison.OrdinalIgnoreCase)
                        || clipName.EndsWith("_dismount", StringComparison.OrdinalIgnoreCase)))
                {
                    names.Add(clipName);
                }
            }
            // The mount/dismount clips move the common player rig into place, but the
            // vehicle package's authored refpose is the persistent seated pose. Export
            // both halves of that contract; otherwise an open-seat occupant falls back
            // to the player's T-pose as soon as the one-shot mount clip finishes.
            foreach (NativeAnimationPackage package in animationPackages)
            {
                if (!vehicleAnimationPackages.Contains(package.Name)) continue;
                foreach (var animation in package.Animations)
                {
                    if (animation.Animation.StartsWith("z_", StringComparison.OrdinalIgnoreCase)) continue;
                    // A station can deploy its passenger directly (drop pods), rather than
                    // use its ordinary mount/dismount pair. Recover that relationship from
                    // package aliases; excluding it left only the pod doors in the export.
                    bool passengerDeployment = animation.Alias.EndsWith("_deploy", StringComparison.OrdinalIgnoreCase)
                        && package.Animations.Any(entry => entry.Alias.Equals(
                            animation.Alias[..^7] + "_mount", StringComparison.OrdinalIgnoreCase));
                    if (!NativeAnimationClipSelection.IsVirtualReferencePoseMode(animation.PlaybackMode)
                        && !(animation.Alias.EndsWith("_idle", StringComparison.OrdinalIgnoreCase)
                            && animation.PlaybackMode == "loop") && !passengerDeployment) continue;
                    NativeAnimationClipSelection.Binding binding =
                        NativeAnimationClipSelection.Resolve(animation, availableClips);
                    string? concrete = NativeAnimationClipSelection.NeutralClip(binding);
                    if (concrete is null) throw new InvalidDataException($"{package.Name}/{animation.Alias}: missing neutral interaction clip {animation.Animation}");
                    names.Add(concrete);
                }
            }
            return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var playerCollision = new
        {
            source = "startup.pak-out/physics*.lst",
            model = playerPhysics?.Name ?? "player",
            sharedByArmor = true,
            shapes = playerPhysics?.Shapes
                .Where(shape => shape.Kind != PhysicsListDatabase.ShapeKind.CarWheel)
                .Select(shape => new
                {
                    id = shape.Name,
                    type = shape.Kind.ToString().ToLowerInvariant(),
                    center = new[] {
                        shape.Position.X,
                        shape.Position.Z,
                        shape.Position.Y == 0 ? 0 : -shape.Position.Y,
                    },
                    radius = shape.Kind == PhysicsListDatabase.ShapeKind.Sphere ? shape.Radius : (float?)null,
                    height = shape.Kind == PhysicsListDatabase.ShapeKind.Cylinder ? shape.Length : (float?)null,
                    size = shape.Kind == PhysicsListDatabase.ShapeKind.Box
                        ? new[] { shape.Size.X, shape.Size.Z, shape.Size.Y }
                        : null,
                    material = shape.Material,
                    cookie = shape.Cookie,
                })
                .ToArray() ?? [],
        };

        var armorCameraRecords = new[]
        {
            (Armor: "light", Record: "lite_armor"),
            (Armor: "medium", Record: "med_armor"),
            (Armor: "standard", Record: "standard_issue_armor"),
            (Armor: "stealth", Record: "stealth_armor"),
            (Armor: "max", Record: "ncmhev"),
        };
        float? Number(string record, string property)
        {
            if (!gameObjects.TryGetValue(record, out GameObjectDb.GameObject? gameObject)) return null;
            string? value = GameObjectPropertyReader.Scalar(gameObject, property);
            return float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float number) ? number : null;
        }
        var infantryCameraProfiles = armorCameraRecords.Select(item => new
        {
            armor = item.Armor,
            sourceRecord = item.Record,
            standViewHeight = Number(item.Record, "stand_view_height"),
            crouchViewHeight = Number(item.Record, "crouch_view_height"),
        }).ToArray();
        var playerCamera = new
        {
            source = "startup.pak-out/game_objects.adb",
            sharedByArmor = infantryCameraProfiles
                .Select(item => (item.standViewHeight, item.crouchViewHeight)).Distinct().Count() == 1,
            standViewHeight = infantryCameraProfiles[0].standViewHeight,
            crouchViewHeight = infantryCameraProfiles[0].crouchViewHeight,
            armorProfiles = infantryCameraProfiles,
        };

        ExportFirstPersonArms();

        var manifest = new
        {
            schemaVersion = 5,
            coordinateSystem = "gltf-y-up",
            gameObjects = gameObjectDatabase.Diagnostics,
            notes = new[]
            {
                "MAX denotes PlanetSide's mechanized assault exosuit, not a BFR.",
                "Body and head records are separate; attach the head rig to the body's head bone.",
                "Infantry animations are shared by gender; MAX animations are faction-specific.",
                "The original client uses one shared compound player physics model for every armor class, including MAX.",
            },
            collision = playerCollision,
            camera = playerCamera,
            models,
            heads,
            animationSets = animations,
            firstPerson = new
            {
                manifestUri = firstPersonManifestUri,
                sourceRecord = "fp_bare_hands",
                sharedByGender = true,
            },
        };
        SharedGlbImageExport.Run(output, textureDirectory: sharedTextureDirectory, recursive: true);
        string manifestPath = Path.Combine(output, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"wrote {models.Count} models, {heads.Count} heads, and {animations.Count} animation sets -> {manifestPath}");
        return 0;

        GlbExportTool.Result Export(
            string record,
            string relative,
            bool includeAnimations,
            bool preserveRigidBoneAttachments = false,
            IReadOnlyList<string>? meshNames = null)
        {
            string path = Path.Combine(output, relative);
            var options = new GlbExportTool.Options(
                planetside,
                record,
                path,
                IncludeAnimations: includeAnimations,
                SharedTextureDirectory: sharedTextureDirectory,
                // Player records contain the unsuffixed HQ body plus numbered 01-05
                // continuous-LOD alternatives. They share a rig, bounds, and sometimes
                // even an ordinary (non-clod_) material, so the generic visual selector
                // cannot safely infer this family from materials alone. The player
                // database names the full-quality authored body exactly after its
                // record; selecting it explicitly prevents every LOD shell being
                // skinned and rendered on top of the HQ body.
                MeshNames: meshNames,
                PreserveRigidBoneAttachments: preserveRigidBoneAttachments);
            return GlbExportTool.Run(options, progress);
        }

        string PlayerHqMesh(string record) => record switch
        {
            // This record retains the older retail mesh name instead of mirroring the
            // game-object/database identifier used by every other player body.
            "trmsth" => "terren_stealth",
            _ => record,
        };

        void ExportFirstPersonArms()
        {
            const string record = "fp_bare_hands";
            string libraryPath = Path.Combine(planetside, "uber.ubr");
            var source = UberModel.Load(File.ReadAllBytes(libraryPath));
            UberModel.MeshSystem system = source.FetchMeshSystem(record)
                ?? throw new InvalidOperationException($"Mesh record '{record}' is missing from '{libraryPath}'.");
            var textures = new TextureProvider(planetside);
            var variants = new List<object>();
            var armorMaterials = new[]
            {
                (Armor: "standard", Code: "s"),
                (Armor: "light", Code: "l"),
                (Armor: "medium", Code: "m"),
                (Armor: "stealth", Code: "d"),
            };

            foreach (string faction in new[] { "nc", "tr", "vs" })
            foreach (var armor in armorMaterials)
            {
                string material = $"{faction}_{armor.Code}_skin";
                int meshIndex = system.Meshes.FindIndex(mesh => mesh.Sections.Any(section =>
                    section.MaterialName.Equals(material, StringComparison.OrdinalIgnoreCase)));
                if (meshIndex < 0)
                    throw new InvalidOperationException($"{record} has no native mesh using material '{material}'.");

                string relative = $"first-person/arms/{faction}/{armor.Armor}.glb";
                string path = Path.Combine(output, relative);
                string defaultMaterial = $"fp_{faction}_{armor.Code}_1_skin";
                var options = new GlbExportTool.Options(
                    planetside,
                    record,
                    path,
                    LibraryPath: libraryPath,
                    IncludeAnimations: false,
                    SharedTextureDirectory: sharedTextureDirectory,
                    MeshMaterials: [material],
                    MaterialReplacements: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [material] = defaultMaterial,
                    });
                GlbExportTool.Result result = GlbExportTool.RunDecoded(
                    options, libraryPath, source, textures, progress);
                variants.Add(new
                {
                    id = $"{faction}-{armor.Armor}",
                    faction,
                    armor = armor.Armor,
                    uri = relative["first-person/".Length..],
                    sourceMeshIndex = meshIndex,
                    sourceMaterial = material,
                    defaultMaterial,
                    triangles = result.Triangles,
                    bones = result.Bones,
                });
            }

            string[] animationNames = FirstPersonAnimationNames(planetside);
            // The weapon export already resolves shared source rigs and exact authored
            // overrides. Consume that contract instead of duplicating record switches.
            string weaponManifestPath = Path.Combine(Path.GetDirectoryName(output)!, "weapons", "manifest.json");
            using JsonDocument weaponManifest = JsonDocument.Parse(File.ReadAllText(weaponManifestPath));
            var weaponEntries = weaponManifest.RootElement.GetProperty("weapons").EnumerateArray()
                .Where(entry => entry.TryGetProperty("firstPersonRecord", out var value) && value.ValueKind == JsonValueKind.String)
                .GroupBy(entry => entry.GetProperty("firstPersonRecord").GetString()!, StringComparer.OrdinalIgnoreCase)
                .Where(group => !IsMaxFirstPersonRecord(group.Key)).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
            var packages = NativeAnimationPackageCatalog.Parse(File.ReadAllBytes(Path.Combine(planetside, "startup.pak-out", "apackage.adb")))
                .ToDictionary(package => package.Name, StringComparer.OrdinalIgnoreCase);
            var animationSets = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            const string animationSourceMaterial = "nc_s_skin";
            foreach (var weaponGroup in weaponEntries)
            {
                string weaponRecord = weaponGroup.Key;
                JsonElement[] overrides = weaponGroup.Select(entry => entry.GetProperty("firstPersonAnimationOverrides"))
                    .Where(value => value.ValueKind == JsonValueKind.Object).ToArray();
                if (overrides.Select(value => value.GetRawText()).Distinct().Count() > 1)
                    throw new InvalidOperationException($"Conflicting arm overrides for {weaponRecord}");
                JsonElement? profile = overrides.Length > 0 ? overrides[0] : null;
                string[] sourcePrefixes = profile?.GetProperty("sourcePrefixes").EnumerateArray()
                    .Select(value => value.GetString()!).ToArray() ?? [weaponRecord];
                var explicitStates = profile?.GetProperty("states") is { ValueKind: JsonValueKind.Object } states
                    ? states.EnumerateObject().ToDictionary(property => property.Name,
                        property => property.Value.EnumerateArray().Select(value => value.GetString()!).ToArray()) : null;
                string[] explicitClips = profile?.GetProperty("fireModes") is { ValueKind: JsonValueKind.Object } modes
                    ? modes.EnumerateObject().SelectMany(property => property.Value.EnumerateArray()
                        .Select(value => value.GetString()!)).ToArray() : [];
                var selection = NativeFirstPersonAnimationSelection.Resolve(
                    sourcePrefixes.Where(packages.ContainsKey).SelectMany(prefix => packages[prefix].Animations),
                    animationNames, explicitStates, explicitClips);
                string[] clips = selection.Clips;
                if (clips.Length == 0)
                    throw new InvalidOperationException($"No native arm animations or explicit bindings for {weaponRecord}");

                string relative = $"first-person/animations/{weaponRecord}.glb";
                var animationOptions = new GlbExportTool.Options(
                    planetside,
                    record,
                    Path.Combine(output, relative),
                    LibraryPath: libraryPath,
                    IncludeTextures: false,
                    IncludeAnimations: true,
                    MaxAnimations: int.MaxValue,
                    MeshMaterials: [animationSourceMaterial],
                    AnimationNames: clips);
                GlbExportTool.Result result = GlbExportTool.RunDecoded(
                    animationOptions, libraryPath, source, textures, progress);
                if (result.Animations != clips.Length)
                {
                    throw new InvalidOperationException(
                        $"{weaponRecord} exported {result.Animations} of {clips.Length} requested arm clips.");
                }
                animationSets[weaponRecord] = new
                {
                    uri = relative["first-person/".Length..],
                    sourcePrefixes,
                    clips,
                    states = selection.States,
                    clipBindings = selection.Bindings.Select(binding => new
                    {
                        alias = binding.Alias,
                        authoredAnimation = binding.Animation,
                        playbackMode = binding.PlaybackMode,
                        resolution = binding.Resolution,
                        concreteClips = binding.ConcreteClips,
                    }),
                    bones = result.Bones,
                    targetMapping = "bone-name",
                };
            }

            string manifestPath = Path.Combine(output, firstPersonManifestUri);
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var manifest = new
            {
                schemaVersion = 1,
                coordinateSystem = "gltf-y-up",
                source = new
                {
                    library = "uber.ubr",
                    record,
                    animationLibraries = new[]
                    {
                        "anims.ubr",
                        "patch1/anim_patch1.ubr",
                        "patch2/anim_patch2.ubr",
                        "patch3/anim_patch3.ubr",
                        "patch4/anim_patch4.ubr",
                        "patch5/anim_patch5.ubr",
                    },
                    animationPrefix = record,
                },
                sharedByGender = true,
                genders = new[] { "female", "male" },
                maxUsesDedicatedViewmodels = true,
                animationSets,
                armorMaterialCodes = new Dictionary<string, string>
                {
                    ["standard"] = "s",
                    ["light"] = "l",
                    ["medium"] = "m",
                    ["stealth"] = "d",
                },
                variants,
            };
            File.WriteAllText(manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"wrote {variants.Count} first-person arm variants and " +
                $"{animationSets.Count} lazy animation sets -> {manifestPath}");
        }

        static string[] FirstPersonAnimationNames(string root)
        {
            string[] libraries =
            {
                "anims.ubr",
                "patch1/anim_patch1.ubr",
                "patch2/anim_patch2.ubr",
                "patch3/anim_patch3.ubr",
                "patch4/anim_patch4.ubr",
                "patch5/anim_patch5.ubr",
            };
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string relative in libraries)
            {
                string path = Path.Combine(root, relative);
                if (!File.Exists(path)) continue;
                foreach (AnimRecord clip in AnimDb.Load(File.ReadAllBytes(path)).Records)
                    if (clip.Name.StartsWith("fp_", StringComparison.OrdinalIgnoreCase)) names.Add(clip.Name);
            }
            return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        static bool IsMaxFirstPersonRecord(string record) =>
            record.StartsWith("fp_nchev_", StringComparison.OrdinalIgnoreCase)
            || record.StartsWith("fp_trhev_", StringComparison.OrdinalIgnoreCase)
            || record.StartsWith("fp_vshev_", StringComparison.OrdinalIgnoreCase);
    }
}
