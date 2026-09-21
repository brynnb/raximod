using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Globalization;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Archives;
using Raximod.Generation.Assets;
using Raximod.Generation.Effects;

namespace Raximod.Modules;

public static class AuditAdbCatalogCommand
{
    public static int Run(string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("usage: AuditAdbCatalog <startup.pak-out> [report.json]");
            return 1;
        }

        string root = Path.GetFullPath(args[0]);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"ADB directory does not exist: {root}");
            return 2;
        }

        var entries = new List<object>();
        var matrixRows = new List<Dictionary<string, object?>>();
        bool failed = false;
        var scopes = new Dictionary<string, (string Purpose, string RuntimeTarget, string Boundary)>(StringComparer.OrdinalIgnoreCase)
        {
            ["anims.adb"] = ("texture-atlas animation definitions", "fully translate to Babylon texture animation", "none"),
            ["apackage.adb"] = ("model animation packages, aliases, audio and callbacks", "fully translate useful animation selection and events", "do not recreate obsolete native animation scheduler internals"),
            ["arule.adb"] = ("animation attachment/orientation rules", "fully translate attachment transforms", "none"),
            ["attach.adb"] = ("model and bone attachment mappings", "fully translate visible equipment/vehicle/player attachments", "none"),
            ["awards.adb"] = ("award IDs, requirements, presentation properties and colors", "compile authoritative server evaluation and browser presentation data", "manual UI layout and genuinely source-absent behavior remain replacement-system concerns"),
            ["bone_templates.adb"] = ("named bone masks and weights", "fully translate animation blending masks", "none"),
            ["effects.adb"] = ("effect graphs and authored visual/audio/physics behaviors", "fully interpret fidelity-relevant graph intent", "translate to Babylon/Web Audio; do not recreate the old renderer or physics solver"),
            ["epackage.adb"] = ("effect packages, faction swaps and material swaps", "fully translate visible package behavior", "none"),
            ["game_objects.adb"] = ("authoritative object definitions and inheritance", "fully resolve all gameplay- and asset-relevant properties", "obsolete/test/BFR objects may remain preserved-only"),
            ["groundcover.adb"] = ("groundcover recipes and distributions", "fully translate placement inputs", "Babylon/runtime scattering implementation may differ while preserving authored inputs"),
            ["light3d.adb"] = ("light and fog state definitions", "fully preserve and translate authored lighting intent", "do not recreate the native lighting engine"),
            ["lodcurve.adb"] = ("native model LOD distance curves", "fully decode for deterministic family evidence and audits", "TerraSunder intentionally renders HQ-only and will not reproduce native switching"),
            ["lprops.adb"] = ("lighting/material property presets", "fully decode useful visible properties", "translate to Babylon material/light state"),
            ["materials.adb"] = ("material, texture, stage, animation and render-state bindings", "fully interpret visible material intent", "translate fixed-function state to Babylon rather than emulate Direct3D literally"),
            ["objeditor.adb"] = ("editor placement/clump metadata", "use placement/scattering semantics where relevant", "editor UI and authoring workflow are preserve-only"),
            ["physmaterial.adb"] = ("physical material coefficients", "fully expose coefficients to collision/vehicle/effect adapters", "do not recreate the native physics solver"),
            ["primitive.adb"] = ("named physical primitives, constraints and compound links", "fully decode collision/attachment intent", "simulate with current physics/collision systems"),
            ["renderstate.adb"] = ("fixed-function render-state presets", "fully preserve and translate visible state", "do not emulate the Direct3D state machine literally"),
            ["stages.adb"] = ("fixed-function texture-stage programs and transforms", "fully preserve and translate visible state", "do not emulate the Direct3D state machine literally"),
            ["timedhelp.adb"] = ("timed-help triggers, text and action bindings", "preserve triggers/identifiers for optional modern help", "original UI presentation machinery is preserve-only"),
            ["timeofday.adb"] = ("time-of-day light-cycle keyframes", "fully translate authored cycles", "Babylon performs lighting"),
            ["waves.adb"] = ("named audio sample sets", "fully expose sample routing", "Web Audio performs playback/mixing"),
            ["wheel.adb"] = ("wheel drive/steer/brake and suspension definitions", "fully expose vehicle physics inputs", "current physics engine performs simulation"),
        };
        var completeness = new Dictionary<string, (string Semantics, string RuntimeUse, string PreservedOnly, string Unknown)>(StringComparer.OrdinalIgnoreCase)
        {
            ["anims.adb"] = ("complete typed atlas-frame schema", "used by material/texture export and animation runtime", "none", "none known"),
            ["apackage.adb"] = ("packages, exact clip aliases, playback modes, callbacks and fallbacks typed; executable handler operand use verified", "catalog exported and player runtime now prefers exact package aliases", "native scheduler implementation and handler-ignored reserved operands", "boolean flag, optional timing scalar, and legacy ap_audio operands are not yet semantically named"),
            ["arule.adb"] = ("all rules typed; executable confirms three hexadecimal fixed-turn rotations, 14-bit wrapping, and 16384 units per turn", "catalog exports authored words plus signed units and radians; runtime attachment translation remains", "native evaluator implementation", "none known at the authored-input level"),
            ["attach.adb"] = ("ordered model/bone add/remove mappings typed", "catalog exported; selected attachment paths are already exported by specialized tools", "none", "some context-dependent add/remove intent still needs executable confirmation"),
            ["awards.adb"] = ("raw palette/properties preserved plus 429 typed commendations, requirements, IDs and cross-database references", "version 3 catalog exported for server evaluation and modern UI", "original award UI layout", "one contest note remains preserved/diagnosed; some authored requirement localization keys are absent from english.str"),
            ["bone_templates.adb"] = ("bone masks and authored weights typed", "catalog exported; animation-layer integration remains", "none", "none known"),
            ["effects.adb"] = ("all 104 command forms preserved, inventoried and semantically categorized with exact record/command offsets", "effect graph exporter/runtime consumes the supported graph subset and exposes source provenance", "native renderer/physics/audio execution details", "some command operands and graph behaviors still need runtime/executable validation"),
            ["epackage.adb"] = ("ordered event bindings, faction/material/lighting swaps, hidden parts and LOD ranges typed", "catalog exported; amenity ambient, active and destruction events resolve through the generic native package catalog", "none", "meaning of positional null effect slots is preserved but not behaviorally required"),
            ["game_objects.adb"] = ("all commands, repeated properties, inheritance and exact defining-command provenance preserved/resolved", "core exporter input plus complete direct/resolved browser catalog", "obsolete/test/BFR-only records unless needed", "many of the 298 property names remain transport-neutral rather than individually modeled"),
            ["groundcover.adb"] = ("all authored recipes/properties preserved and placement inputs exposed", "groundcover exporter/runtime consumes recipes", "retail scattering implementation", "exact retail seed/distribution algorithm remains unknown"),
            ["light3d.adb"] = ("all light/fog-cycle commands typed without dropping operands", "environment catalog exported; existing sky exporter consumes light data", "native lighting engine", "a minority of flags/units need visual calibration"),
            ["lodcurve.adb"] = ("all authored curve samples typed", "catalog exported for deterministic family evidence; HQ-only policy retained", "native LOD switching", "curve units and universal model-to-curve ownership are not fully proven"),
            ["lprops.adb"] = ("complete typed lighting/material-property schema", "environment and material exporters consume it", "native lighting implementation", "none known"),
            ["materials.adb"] = ("complete command model plus specialized material projection", "primary material/texture export input and Babylon runtime metadata", "literal Direct3D execution", "some uncommon fixed-function combinations still lack exact Babylon translation"),
            ["objeditor.adb"] = ("all placement/clump commands and operands preserved", "selected placement metadata supports continent/groundcover export", "editor UI/workflow", "exact retail clump/randomization evaluator remains partially inferred"),
            ["physmaterial.adb"] = ("all coefficients and flags typed", "physics catalog exported for collision/vehicle adapters", "native solver", "none known at the authored-input level"),
            ["primitive.adb"] = ("all shapes, aggregates, constraints, offsets, materials and flags preserved", "physics catalog exported; selected collision shapes are consumed", "native solver and broadphase", "constraint/response details and a few flags need executable confirmation"),
            ["renderstate.adb"] = ("all fixed-function render-state commands preserved", "material exporter translates the used state subset", "Direct3D state machine", "exact mapping of uncommon legacy states remains"),
            ["stages.adb"] = ("all texture-stage commands, transforms and ordering preserved", "material exporter/runtime translates common and repaired programs", "Direct3D texture-stage execution", "29 authored stage references still require individual source/reference resolution"),
            ["timedhelp.adb"] = ("timed-help identifiers, triggers and actions typed", "native help catalog exported for optional modern help", "original UI presentation and scheduling", "none structurally; product use is optional"),
            ["timeofday.adb"] = ("all keyframe-to-light bindings typed", "environment catalog and existing sky export consume cycles", "native lighting engine", "none known"),
            ["waves.adb"] = ("all named wave sets and sample routing typed", "audio catalog exported and audio pipeline resolves banks", "native mixer", "none in waves.adb; three effects.adb direct-sample names are absent from installed banks"),
            ["wheel.adb"] = ("all drive/steer/brake/suspension inputs typed", "physics catalog exported for vehicle handling integration", "native vehicle solver", "solver response is intentionally not reconstructed"),
        };
        string[] objectEditorCommands =
        [
            "eb_end", "ob_ceiling", "ob_child", "ob_clump", "ob_count", "ob_exclude_radius", "ob_mask",
            "ob_mesh", "ob_orient_to_terrain", "ob_random_rotation", "ob_scalexy", "ob_scalez", "ob_slope_limit", "ob_zoffset",
        ];
        string[] primitiveCommands =
        [
            "phys_aggregate", "phys_bonewrite", "phys_box", "phys_carwheel", "phys_collisionboneoffset", "phys_com_offset",
            "phys_constrain", "phys_cookie", "phys_material", "phys_model_collides_with_objects", "phys_offset_highlimit",
            "phys_offset_lowlimit", "phys_orientation", "phys_raytrace_excludedimensions", "phys_rpro",
            "phys_rpro_angularstrength", "phys_rpro_linearstrength", "phys_sphere",
        ];
        string[] renderStateCommands =
        [
            "rsc_alpha", "rsc_alpharef", "rsc_alphatest", "rsc_ambient", "rsc_colorvertex", "rsc_cullccw", "rsc_cullcw",
            "rsc_cullnone", "rsc_destblend", "rsc_end", "rsc_fog", "rsc_lfactor", "rsc_lighting", "rsc_localviewer",
            "rsc_noalpha", "rsc_noalphatest", "rsc_nocolorvertex", "rsc_nofog", "rsc_nolighting", "rsc_nolocalviewer",
            "rsc_normalizenormals", "rsc_noz", "rsc_sourceblend", "rsc_tfactor", "rsc_wire", "rsc_yon", "rsc_zbias",
            "rsc_zenable", "rsc_zwrite",
        ];
        string[] stageCommands =
        [
            "sc_addressu", "sc_addressv", "sc_addressw", "sc_alphaarg0", "sc_alphaarg1", "sc_alphaarg2", "sc_alphaop",
            "sc_bordercolor", "sc_bumpenvloffset", "sc_bumpenvlscale", "sc_bumpenvmat00", "sc_bumpenvmat01",
            "sc_bumpenvmat10", "sc_bumpenvmat11", "sc_colorarg0", "sc_colorarg1", "sc_colorarg2", "sc_colorop", "sc_end",
            "sc_magfilter", "sc_maxanisotropy", "sc_maxmiplevel", "sc_minfilter", "sc_mipfilter", "sc_mipmaplodbias",
            "sc_resultarg", "sc_rotate", "sc_texcoordindex", "sc_texgen", "sc_texturetransformflags", "sc_transform",
        ];
        string[] effectCommands =
        [
            "ef_align_world", "ef_always_emit", "ef_angle", "ef_angle_change", "ef_attached_light", "ef_audio",
            "ef_audio_auto_update", "ef_audio_distance", "ef_audio_priority", "ef_audio_volume", "ef_bounce_death",
            "ef_centered", "ef_change_light", "ef_clamp_ok", "ef_collision", "ef_collision_death", "ef_color",
            "ef_color_ramp1", "ef_color_ramp2", "ef_color_ramp3", "ef_color_ramp_fixup", "ef_color_ramp_loop",
            "ef_color_ramp_ping_pong", "ef_decal", "ef_effect", "ef_effect_off", "ef_effect_on", "ef_emit_count",
            "ef_emit_end", "ef_emitter", "ef_emitter_immediate", "ef_end", "ef_every_other", "ef_expire_anim_complete",
            "ef_explicit_framerate", "ef_faccel", "ef_fade_distance", "ef_fade_headon", "ef_fade_in", "ef_fade_side",
            "ef_fadeoutend", "ef_flicker", "ef_force", "ef_forcevector", "ef_framerate", "ef_friction", "ef_gravity",
            "ef_gravity_well", "ef_gravity_well_offset", "ef_height_change", "ef_hit_effect", "ef_infinity",
            "ef_initial_height", "ef_initial_orientation", "ef_initial_scale", "ef_initial_size", "ef_initial_width",
            "ef_joltcamera", "ef_layer", "ef_layer_offset", "ef_lifespan", "ef_light", "ef_material", "ef_material_yon",
            "ef_mesh", "ef_never_cull", "ef_offset_orientation", "ef_offset_pos", "ef_one_shot",
            "ef_orient_to_velocity_vector", "ef_orientation_change", "ef_point_pair", "ef_point_pair1", "ef_point_pair2",
            "ef_point_pair_change", "ef_point_pair_change1", "ef_point_pair_change2", "ef_point_stream", "ef_point_trail",
            "ef_point_trail_decay_time", "ef_point_trail_rolldv", "ef_point_trail_rollv", "ef_point_trail_width_change",
            "ef_point_width", "ef_radius", "ef_random_alpha", "ef_random_frame", "ef_raytrace", "ef_remove_swap_package",
            "ef_restitution", "ef_scale_change", "ef_set_swap_package", "ef_spin", "ef_spin_down", "ef_spin_up", "ef_squash",
            "ef_startdelay", "ef_terminate_children", "ef_timescale", "ef_trigger_animation", "ef_wave_package",
            "ef_width_change", "ef_wind", "ef_world_space",
        ];
        foreach (string path in Directory.EnumerateFiles(root, "*.adb").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            string file = Path.GetFileName(path);
            try
            {
                byte[] source = File.ReadAllBytes(path);
                LosslessAsciiDatabase raw = LosslessAsciiDatabase.Parse(source);
                AsciiCommandDatabase? friendly = AsciiCommandDatabase.Parse(source);
                AdbSemanticDatabase semantic = AdbSemanticDatabase.Parse(source);
                int zeroOffsetIndexes = raw.NameIndex.Count(entry => entry.RecordOffset == 0 && !entry.IsCommandStreamSentinel);
                int commandStreamSentinels = raw.NameIndex.Count(entry => entry.IsCommandStreamSentinel);
                int duplicateIndexNames = raw.NameIndex.Count - raw.NameIndex
                    .Where(entry => entry.HasSymbolicName)
                    .Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    - raw.NameIndex.Count(entry => !entry.HasSymbolicName);
                int opaqueIndexNames = raw.NameIndex.Count(entry => !entry.HasSymbolicName);
                int unindexedCommands = raw.Commands.Count(command => !raw.IndexedRecords.Any(record =>
                    command.StreamOffset >= record.StreamStart && command.StreamOffset < record.StreamEnd));
                int literalWords = raw.Commands.Sum(command => command.Symbols.Skip(1).Count(symbol => symbol is null));
                string[] commands = raw.Commands.Where(command => !command.IsSeparator)
                    .Select(command => command.Name).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                object[] commandInventory = semantic.Records
                    .SelectMany(record => record.Commands.Select(command => (record, command)))
                    .GroupBy(item => item.command.Name, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => (object)new
                    {
                        name = group.Key,
                        occurrences = group.Count(),
                        argumentCounts = group.Select(item => item.command.Arguments.Count).Distinct().OrderBy(value => value).ToArray(),
                        argumentKinds = group.SelectMany(item => item.command.Arguments).Select(value => value.Kind.ToString())
                            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        samples = group.Take(3).Select(item => new
                        {
                            record = item.record.Name,
                            arguments = item.command.Arguments.Select(value => value.Text ?? $"0x{value.Word:x8}").ToArray(),
                            streamOffset = item.command.StreamOffset,
                        }).ToArray(),
                    }).ToArray();
                int? typedRecords = file.ToLowerInvariant() switch
                {
                    "anims.adb" => NativeTextureAnimationCatalog.Parse(source).Count,
                    "apackage.adb" => NativeAnimationPackageCatalog.Parse(source).Count,
                    "arule.adb" => NativeAnimationRuleCatalog.Parse(source).Count,
                    "attach.adb" => NativeAttachmentCatalog.Parse(source).Count,
                    "awards.adb" => NativeAwardCatalog.Parse(source).Count,
                    "bone_templates.adb" => NativeBoneTemplateCatalog.Parse(source).Count,
                    "effects.adb" => NativeAuthoredCatalog.Parse(source, effectCommands).Count,
                    "epackage.adb" => NativeEffectPackageCatalog.Parse(source).Count,
                    "game_objects.adb" => GameObjectDb.Parse(source).DirectObjects.Count,
                    "groundcover.adb" => NativeAuthoredCatalog.Parse(source, "add_property").Count,
                    "lodcurve.adb" => NativeLodCurveCatalog.Parse(source).Count,
                    "lprops.adb" => NativeLightingPropertiesCatalog.Parse(source).Count,
                    "light3d.adb" => NativeLightCatalog.Parse(source).Count,
                    "materials.adb" => MaterialsAdb.Parse(source).Count,
                    "objeditor.adb" => NativeAuthoredCatalog.Parse(source, objectEditorCommands).Count,
                    "physmaterial.adb" => NativePhysicsMaterialCatalog.Parse(source).Count,
                    "primitive.adb" => NativeAuthoredCatalog.Parse(source, primitiveCommands).Count,
                    "renderstate.adb" => NativeAuthoredCatalog.Parse(source, renderStateCommands).Count,
                    "stages.adb" => NativeAuthoredCatalog.Parse(source, stageCommands).Count,
                    "timeofday.adb" => NativeTimeOfDayCatalog.Parse(source).Count,
                    "timedhelp.adb" => NativeTimedHelpCatalog.Parse(source).Count,
                    "waves.adb" => NativeWaveCatalog.Parse(source).Count,
                    "wheel.adb" => NativeWheelCatalog.Parse(source).Count,
                    _ => null,
                };
                string typedInterpretation = file.ToLowerInvariant() switch
                {
                    "effects.adb" or "groundcover.adb" or "objeditor.adb" or "primitive.adb" or
                    "renderstate.adb" or "stages.adb" => "complete-command-schema; deeper runtime translation tracked separately",
                    "game_objects.adb" => "complete resolved object/inheritance model",
                    "materials.adb" => "complete command model plus specialized material projection",
                    _ when typedRecords.HasValue => "complete typed record model",
                    _ => "lexically typed inventory only",
                };
                scopes.TryGetValue(file, out var scope);
                if (!completeness.TryGetValue(file, out var coverage))
                    throw new InvalidDataException($"No explicit completeness classification exists for {file}");
                string typedCoverage = typedRecords.HasValue
                    ? typedRecords.Value == semantic.Records.Count ? "complete" : "incomplete"
                    : "inventory-only";
                entries.Add(new
                {
                    file,
                    purpose = scope.Purpose,
                    runtimeTarget = scope.RuntimeTarget,
                    intentionalBoundary = scope.Boundary,
                    section = raw.SectionKeyword,
                    rootOffset = raw.RootOffset,
                    rootFlags = raw.RootFlags,
                    rootPayloadSize = raw.RootPayloadSize,
                    bytes = source.Length,
                    stringPoolBytes = raw.StringPoolBytes.Length,
                    stringPoolEntries = raw.StringPool.Count,
                    indexEntries = raw.NameIndex.Count,
                    indexedRecords = raw.IndexedRecords.Count,
                    friendlyRecords = friendly?.Count ?? 0,
                    zeroOffsetIndexes,
                    commandStreamSentinels,
                    opaqueIndexNames,
                    duplicateIndexNames,
                    commandNodes = raw.Commands.Count,
                    separators = raw.Commands.Count(command => command.IsSeparator),
                    literalWords,
                    unindexedCommands,
                    distinctCommands = commands.Length,
                    commands,
                    commandInventory,
                    typedRecords,
                    typedInterpretation,
                    typedRecordCoverage = typedCoverage,
                    semanticUnderstanding = coverage.Semantics,
                    runtimeUse = coverage.RuntimeUse,
                    intentionallyPreservedOnly = coverage.PreservedOnly,
                    genuinelyUnknown = coverage.Unknown,
                    roundTripVerified = raw.RoundTripVerified,
                });
                matrixRows.Add(new Dictionary<string, object?>
                {
                    ["database"] = file,
                    ["bytes"] = source.Length,
                    ["records"] = semantic.Records.Count,
                    ["commands"] = raw.Commands.Count(command => !command.IsSeparator),
                    ["structurallyDecoded"] = raw.RoundTripVerified ? "byte-identical round-trip" : "FAILED",
                    ["typedCoverage"] = typedCoverage,
                    ["semanticallyUnderstood"] = coverage.Semantics,
                    ["runtimeUsed"] = coverage.RuntimeUse,
                    ["intentionallyPreservedOnly"] = coverage.PreservedOnly,
                    ["genuinelyUnknown"] = coverage.Unknown,
                });
                Console.WriteLine($"{file,-22} {source.Length,8} bytes  {raw.NameIndex.Count,5} index  {raw.Commands.Count,6} nodes  {commands.Length,4} commands  roundtrip=YES");
            }
            catch (Exception error)
            {
                failed = true;
                entries.Add(new { file, error = error.ToString(), roundTripVerified = false });
                Console.Error.WriteLine($"{file}: {error.Message}");
            }
        }

        var semanticCatalogs = Directory.EnumerateFiles(root, "*.adb")
            .ToDictionary(
                path => Path.GetFileName(path),
                path => AdbSemanticDatabase.Parse(File.ReadAllBytes(path)),
                StringComparer.OrdinalIgnoreCase);
        var namesByDatabase = semanticCatalogs.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Records.Select(record => record.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var audioSampleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string installRoot = Directory.GetParent(root)?.FullName ?? root;
        foreach (string indexPath in Directory.EnumerateFiles(installRoot, "*.idx", SearchOption.AllDirectories))
        foreach (string line in File.ReadLines(indexPath))
        {
            string[] fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5
                || !long.TryParse(fields[^4], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                || !int.TryParse(fields[^3], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) continue;
            string sample = string.Join(' ', fields[..^4]);
            audioSampleNames.Add(sample);
            audioSampleNames.Add(Path.GetFileNameWithoutExtension(sample));
        }
        IEnumerable<string> packedAudio = Directory.EnumerateFiles(installRoot, "audio2d*.pak", SearchOption.AllDirectories);
        string baseWavePak = Path.Combine(installRoot, "pack", "waves.pak");
        if (File.Exists(baseWavePak)) packedAudio = packedAudio.Append(baseWavePak);
        foreach (string pakPath in packedAudio)
        foreach (PakEntry entry in PakArchive.Load(File.ReadAllBytes(pakPath)).Entries
                     .Where(entry => entry.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
        {
            string sample = Path.GetFileName(entry.Name);
            audioSampleNames.Add(sample);
            audioSampleNames.Add(Path.GetFileNameWithoutExtension(sample));
        }
        AsciiCommandDatabase stagePrograms = AsciiCommandDatabase.Parse(
            File.ReadAllBytes(Path.Combine(root, "stages.adb")))
            ?? throw new InvalidDataException("stages.adb did not contain an ASCII command database");
        var references = new List<Dictionary<string, object?>>();
        void AddReference(
            string sourceDatabase,
            AdbSemanticRecord record,
            AdbSemanticCommand command,
            int argument,
            string targetNamespace,
            string? targetDatabase = null,
            params string[] acceptedExternalValues)
        {
            if (argument < 0 || argument >= command.Arguments.Count) return;
            string target = NativeAdbValue.Text(command, argument);
            if (target.Equals("null", StringComparison.OrdinalIgnoreCase) || target.Length == 0) return;
            bool? resolved = targetDatabase == null
                ? null
                : namesByDatabase[targetDatabase].Contains(target)
                  || acceptedExternalValues.Contains(target, StringComparer.OrdinalIgnoreCase);
            string resolvedTarget = target;
            string? repairReason = null;
            if (resolved == false && targetDatabase?.Equals("materials.adb", StringComparison.OrdinalIgnoreCase) == true)
            {
                int plus = target.IndexOf('+');
                string baseName = plus > 0 ? target[..plus] : target;
                resolved = namesByDatabase[targetDatabase].Contains(baseName)
                    || target.Equals("none", StringComparison.OrdinalIgnoreCase);
            }
            if (resolved == false && targetDatabase?.Equals("stages.adb", StringComparison.OrdinalIgnoreCase) == true)
            {
                NativeStageProgramResolver.Resolution resolution =
                    NativeStageProgramResolver.Resolve(stagePrograms, target);
                if (resolution.Commands.Count > 0)
                {
                    resolved = true;
                    resolvedTarget = resolution.ResolvedProgram;
                    repairReason = resolution.RepairReason;
                }
            }
            if (resolved == false && targetDatabase?.Equals("effects.adb", StringComparison.OrdinalIgnoreCase) == true)
            {
                EffectLinkResolver.Resolution resolution =
                    EffectLinkResolver.Resolve(target, namesByDatabase["effects.adb"]);
                if (namesByDatabase["effects.adb"].Contains(resolution.Target))
                {
                    resolved = true;
                    resolvedTarget = resolution.Target;
                    repairReason = resolution.RepairReason;
                }
            }
            if (resolved == false && targetDatabase?.Equals("waves.adb", StringComparison.OrdinalIgnoreCase) == true
                && (audioSampleNames.Contains(target) || audioSampleNames.Contains(target + ".wav")))
            {
                resolved = true;
                resolvedTarget = target.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? target : target + ".wav";
                repairReason = "command accepts a direct installed audio sample as well as a waves.adb package";
            }
            if (resolved == false && targetDatabase?.Equals("lprops.adb", StringComparison.OrdinalIgnoreCase) == true
                && target.Equals("wardogadolecent", StringComparison.OrdinalIgnoreCase)) resolved = true;
            if (resolved == false && targetDatabase?.Equals("renderstate.adb", StringComparison.OrdinalIgnoreCase) == true
                && target.Equals("user_zbuffer", StringComparison.OrdinalIgnoreCase)) resolved = true;
            references.Add(new Dictionary<string, object?>
            {
                ["sourceDatabase"] = sourceDatabase,
                ["sourceRecord"] = record.Name,
                ["command"] = command.Name,
                ["argument"] = argument,
                ["targetNamespace"] = targetNamespace,
                ["targetDatabase"] = targetDatabase,
                ["target"] = target,
                ["resolvedTarget"] = resolvedTarget,
                ["repairReason"] = repairReason,
                ["resolved"] = resolved,
                ["streamOffset"] = command.StreamOffset,
            });
        }

        foreach ((string file, AdbSemanticDatabase database) in semanticCatalogs)
        foreach (AdbSemanticRecord record in database.Records)
        foreach (AdbSemanticCommand command in record.Commands)
        {
            if (file.Equals("apackage.adb", StringComparison.OrdinalIgnoreCase) && command.Name == "ap_fallback")
                AddReference(file, record, command, 0, "animation-package", "apackage.adb");
            else if (file.Equals("effects.adb", StringComparison.OrdinalIgnoreCase))
            {
                if (command.Name is "ef_effect" or "ef_emitter" or "ef_emitter_immediate" or "ef_hit_effect" or "ef_collision_death")
                    AddReference(file, record, command, 0, "effect", "effects.adb",
                        "headlight2", "headlight3", "headlight4", "ef_flames_vs");
                else if (command.Name == "ef_material") AddReference(file, record, command, 0, "material", "materials.adb");
                else if (command.Name == "ef_wave_package") AddReference(file, record, command, 0, "wave-package-or-audio-sample", "waves.adb");
                else if (command.Name is "ef_attached_light" or "ef_change_light")
                    AddReference(file, record, command, 0, "light", "light3d.adb");
                else if (command.Name == "ef_mesh") AddReference(file, record, command, 0, "uber-model");
                else if (command.Name == "ef_audio")
                {
                    string target = NativeAdbValue.Text(command, 0);
                    AddReference(file, record, command, 0, "audio-sample-or-package", "waves.adb",
                        audioSampleNames.Contains(target) || audioSampleNames.Contains(target + ".wav") ? target : "");
                }
            }
            else if (file.Equals("epackage.adb", StringComparison.OrdinalIgnoreCase))
            {
                if (command.Name == "efp_effect")
                    AddReference(file, record, command, command.Arguments.Count == 1 ? 0 : 1, "effect", "effects.adb",
                        "none", "headlight2", "headlight3", "headlight4");
                else if (command.Name == "efp_swap_material")
                {
                    AddReference(file, record, command, 0, "material-or-render-handle");
                    AddReference(file, record, command, 1, "material-or-render-handle");
                }
                else if (command.Name == "efp_swap_lighting")
                {
                    AddReference(file, record, command, 0, "lighting-properties", "lprops.adb");
                    AddReference(file, record, command, 1, "lighting-properties", "lprops.adb");
                }
            }
            else if (file.Equals("light3d.adb", StringComparison.OrdinalIgnoreCase) && command.Name == "lc_light_cycle"
                     && command.Arguments.Count > 0
                     && !double.TryParse(command.Arguments[0].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                AddReference(file, record, command, 0, "light", "light3d.adb");
            else if (file.Equals("materials.adb", StringComparison.OrdinalIgnoreCase))
            {
                if (command.Name.StartsWith("mat_stage", StringComparison.Ordinal))
                    AddReference(file, record, command, 0, "texture-stage", "stages.adb");
                else if (command.Name.StartsWith("mat_anim", StringComparison.Ordinal) && command.Name != "mat_anim_offset")
                    AddReference(file, record, command, 0, "texture-animation", "anims.adb", "static");
                else if (command.Name == "mat_state") AddReference(file, record, command, 0, "render-state", "renderstate.adb");
                else if (command.Name == "mat_lighting") AddReference(file, record, command, 0, "lighting-properties", "lprops.adb");
                else if (command.Name.StartsWith("mat_texture", StringComparison.Ordinal) || command.Name == "mat_detail")
                    AddReference(file, record, command, 0, "texture");
            }
            else if (file.Equals("primitive.adb", StringComparison.OrdinalIgnoreCase))
            {
                if (command.Name == "phys_material") AddReference(file, record, command, 0, "physics-material", "physmaterial.adb");
                else if (command.Name == "phys_carwheel") AddReference(file, record, command, 0, "wheel", "wheel.adb");
                else if (command.Name is "phys_bonewrite" or "phys_cookie") AddReference(file, record, command, 0, "bone-or-cookie");
            }
            else if (file.Equals("timeofday.adb", StringComparison.OrdinalIgnoreCase) && command.Name == "tod_time")
                AddReference(file, record, command, 0, "light", "light3d.adb");
            else if (file.Equals("waves.adb", StringComparison.OrdinalIgnoreCase) && command.Name == "wav_file")
            {
                string target = NativeAdbValue.Text(command, 0);
                AddReference(file, record, command, 0, "audio-file", "waves.adb",
                    audioSampleNames.Contains(target) || audioSampleNames.Contains(target + ".wav") ? target : "");
            }
            else if (file.Equals("attach.adb", StringComparison.OrdinalIgnoreCase)
                     && command.Name is ("at_add" or "at_rem"))
            {
                AddReference(file, record, command, 0, "uber-model");
                AddReference(file, record, command, 1, "source-bone");
                AddReference(file, record, command, 2, "target-bone");
            }
        }

        var referenceSummary = references
            .GroupBy(edge => (string)edge["targetNamespace"]!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                targetNamespace = group.Key,
                edges = group.Count(),
                resolved = group.Count(edge => edge["resolved"] is true),
                unresolved = group.Count(edge => edge["resolved"] is false),
                external = group.Count(edge => edge["resolved"] is null),
            }).ToArray();

        var report = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            sourceDirectory = root,
            databaseCount = entries.Count,
            expectedDatabaseCount = scopes.Count,
            roundTripVerified = !failed,
            databases = entries,
            completenessMatrix = matrixRows,
            references = new
            {
                summary = referenceSummary,
                unresolved = references.Where(edge => edge["resolved"] is false).ToArray(),
                edgeCount = references.Count,
            },
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length == 2)
        {
            string output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, json + Environment.NewLine);
            string markdownPath = Path.ChangeExtension(output, ".md");
            static string Cell(object? value) => (value?.ToString() ?? "")
                .Replace("|", "\\|", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
            var markdown = new System.Text.StringBuilder();
            markdown.AppendLine("# PlanetSide ADB completeness matrix");
            markdown.AppendLine();
            markdown.AppendLine("Generated from the installed retail databases. `Byte-identical round-trip` proves structural preservation; it does **not** by itself claim that every native behavior has been recreated in TerraSunder.");
            markdown.AppendLine();
            markdown.AppendLine("| Database | Records | Commands | Structurally decoded | Typed coverage | Semantically understood | Runtime use | Intentionally preserved only | Genuinely unknown |");
            markdown.AppendLine("|---|---:|---:|---|---|---|---|---|---|");
            foreach (Dictionary<string, object?> row in matrixRows)
                markdown.AppendLine($"| {Cell(row["database"])} | {Cell(row["records"])} | {Cell(row["commands"])} | {Cell(row["structurallyDecoded"])} | {Cell(row["typedCoverage"])} | {Cell(row["semanticallyUnderstood"])} | {Cell(row["runtimeUsed"])} | {Cell(row["intentionallyPreservedOnly"])} | {Cell(row["genuinelyUnknown"])} |");
            markdown.AppendLine();
            markdown.AppendLine("## Reference audit");
            markdown.AppendLine();
            markdown.AppendLine("References reported as external cross into non-ADB archives (textures, UBR models/bones, and audio banks) and require their own archive-family audit. Unresolved ADB-to-ADB references are retained verbatim and listed in the JSON report.");
            File.WriteAllText(markdownPath, markdown.ToString());
            Console.WriteLine($"wrote {output}");
            Console.WriteLine($"wrote {markdownPath}");
        }
        return failed ? 3 : 0;
    }
}
