using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Raximod.Generation.Awards;
using Raximod.Generation.Assets;

namespace Raximod.Modules;

public static class ExportNativeAdbCatalogsCommand
{
    public static int Run(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Console.Error.WriteLine(
                "usage: ExportNativeAdbCatalogs <startup.pak-out> <output-directory> [server-award-runtime-output]");
            return 1;
        }

        string source = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        Directory.CreateDirectory(output);
        byte[] Read(string name) => File.ReadAllBytes(Path.Combine(source, name));
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        void WritePath(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)
                ?? throw new InvalidDataException($"output path has no parent directory: {path}"));
            string content = JsonSerializer.Serialize(value, json);
            if (!File.Exists(path) || !File.ReadAllText(path).Equals(content, StringComparison.Ordinal))
            {
                File.WriteAllText(path, content);
                Console.WriteLine($"wrote {path} ({new FileInfo(path).Length:N0} bytes)");
            }
            else Console.WriteLine($"unchanged {path} ({new FileInfo(path).Length:N0} bytes)");
        }
        void Write(string name, object value) => WritePath(Path.Combine(output, name), value);

        NativeAuthoredRecord[] primitives = NativeAuthoredCatalog.Parse(Read("primitive.adb"),
            "phys_aggregate", "phys_bonewrite", "phys_box", "phys_carwheel", "phys_collisionboneoffset", "phys_com_offset",
            "phys_constrain", "phys_cookie", "phys_material", "phys_model_collides_with_objects", "phys_offset_highlimit",
            "phys_offset_lowlimit", "phys_orientation", "phys_raytrace_excludedimensions", "phys_rpro",
            "phys_rpro_angularstrength", "phys_rpro_linearstrength", "phys_sphere").ToArray();
        GameObjectDb gameObjects = GameObjectDb.Parse(Read("game_objects.adb"));
        Write("infantry-gameplay.json", ClientCombatMetadata.InfantryCatalog(gameObjects.ResolvedObjects));
        NativeAward[] rawAwards = NativeAwardCatalog.Parse(Read("awards.adb")).ToArray();
        NativeStringTable english = NativeStringTable.Parse(Read("english.str"));
        CompiledAwardCatalog awards = AwardCatalogCompiler.Compile(rawAwards, english, gameObjects);
        var awardRuntimeCatalog = new
        {
            format = "planetside-native-award-runtime-catalog",
            version = 1,
            source = new[] { "awards.adb", "game_objects.adb", "english.str" },
            note = "Generated server contract. Definitions and indexes are authoritative; clients do not author award facts.",
            awards.Audit,
            awards.Diagnostics,
            awards.Definitions,
            awards.ObjectGroups,
            awards.FirstTimeEvents,
        };

        Write("animation-catalog.json", new
        {
            format = "planetside-native-animation-catalog",
            version = 2,
            source = new[] { "anims.adb", "apackage.adb", "arule.adb", "attach.adb", "bone_templates.adb" },
            textureAnimations = NativeTextureAnimationCatalog.Parse(Read("anims.adb")),
            packages = NativeAnimationPackageCatalog.Parse(Read("apackage.adb")),
            rules = NativeAnimationRuleCatalog.Parse(Read("arule.adb")),
            attachments = NativeAttachmentCatalog.Parse(Read("attach.adb")),
            boneTemplates = NativeBoneTemplateCatalog.Parse(Read("bone_templates.adb")),
        });

        Write("physics-catalog.json", new
        {
            format = "planetside-native-physics-catalog",
            version = 2,
            source = new[] { "physmaterial.adb", "primitive.adb", "wheel.adb" },
            coordinateSystem = "native-planetside-z-up",
            note = "Authored shapes and coefficients are inputs; TerraSunder does not emulate the retail physics solver.",
            materials = NativePhysicsMaterialCatalog.Parse(Read("physmaterial.adb")),
            primitives,
            wheels = NativeWheelCatalog.Parse(Read("wheel.adb")),
        });

        Write("environment-catalog.json", new
        {
            format = "planetside-native-environment-catalog",
            version = 2,
            source = new[] { "light3d.adb", "lprops.adb", "timeofday.adb" },
            note = "Authored lighting intent is translated to Babylon; the retail lighting engine is not emulated.",
            lights = NativeLightCatalog.Parse(Read("light3d.adb")),
            lightingProperties = NativeLightingPropertiesCatalog.Parse(Read("lprops.adb")),
            timeOfDay = NativeTimeOfDayCatalog.Parse(Read("timeofday.adb")),
        });

        Write("audio-catalog.json", new
        {
            format = "planetside-native-audio-catalog",
            version = 2,
            source = "waves.adb",
            note = "Authored wave groups are preserved; Web Audio performs playback and mixing.",
            waveSets = NativeWaveCatalog.Parse(Read("waves.adb")),
        });

        Write("award-catalog.json", new
        {
            format = "planetside-native-award-catalog",
            version = 3,
            source = new[] { "awards.adb", "game_objects.adb", "english.str" },
            note = "Definitions are the typed runtime contract. Raw awards.adb records remain preserved for provenance and unsupported-property research.",
            awards.Audit,
            awards.Diagnostics,
            awards.Palette,
            awards.Definitions,
            awards.ObjectGroups,
            awards.FirstTimeEvents,
            rawAwards,
        });
        Write("award-runtime-catalog.json", awardRuntimeCatalog);
        if (args.Length == 3)
            WritePath(Path.GetFullPath(args[2]), awardRuntimeCatalog);

        Write("localization-en.json", new
        {
            format = "planetside-native-localization-catalog",
            version = 1,
            source = "english.str",
            encoding = "ISO-8859-1",
            entries = english.Entries,
            diagnostics = english.Diagnostics,
        });

        Write("help-catalog.json", new
        {
            format = "planetside-native-help-catalog",
            version = 2,
            source = "timedhelp.adb",
            note = "Authored timing and binding-bearing text are retained for optional modern help; the retail UI scheduler is not emulated.",
            entries = NativeTimedHelpCatalog.Parse(Read("timedhelp.adb")),
        });

        Write("effect-package-catalog.json", new
        {
            format = "planetside-native-effect-package-catalog",
            version = 2,
            source = "epackage.adb",
            packages = NativeEffectPackageCatalog.Parse(Read("epackage.adb")),
        });

        Write("lod-curve-catalog.json", new
        {
            format = "planetside-native-lod-curve-catalog",
            version = 2,
            source = "lodcurve.adb",
            policy = "TerraSunder renders HQ meshes only; native curve values are retained for family evidence and auditing.",
            curves = NativeLodCurveCatalog.Parse(Read("lodcurve.adb")),
        });

        Write("game-object-catalog.json", new
        {
            format = "planetside-native-game-object-catalog",
            version = 2,
            source = "game_objects.adb",
            note = "Direct operations preserve authored repeats; resolved properties carry their exact defining record and command offset.",
            diagnostics = gameObjects.Diagnostics,
            parentOperations = gameObjects.ParentLinks,
            directObjects = gameObjects.DirectObjects.Select(value => new
            {
                value.ClassId,
                value.Name,
                value.Type,
                value.ParentName,
                value.Properties,
                propertyOperations = value.DirectPropertyOperations,
                propertySources = value.PropertySources,
            }),
            resolvedObjects = gameObjects.ResolvedObjects.Select(value => new
            {
                value.ClassId,
                value.Name,
                value.Type,
                value.ParentName,
                value.InheritanceChain,
                value.Properties,
                propertySources = value.PropertySources,
            }),
        });

        return 0;
    }
}
