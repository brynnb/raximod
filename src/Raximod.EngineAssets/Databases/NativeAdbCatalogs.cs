using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Raximod.EngineAssets.Databases
{
    public sealed record NativeAdbProvenance(
        string Section,
        int StreamStart,
        int StreamEnd,
        bool IsIndexed,
        int? NameIndex);

    public static class NativeAdbValue
    {
        public static NativeAdbProvenance Provenance(
            AdbSemanticDatabase database,
            AdbSemanticRecord record) => new(
                database.Raw.SectionKeyword,
                record.StreamStart,
                record.StreamEnd,
                record.IsIndexed,
                record.NameIndex);

        public static string Text(AdbSemanticCommand command, int index) =>
            command.Arguments.ElementAtOrDefault(index)?.Text
            ?? throw new InvalidDataException($"{command.Name} argument {index} is not textual");

        public static int Integer(AdbSemanticCommand command, int index) =>
            int.TryParse(Text(command, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : throw new InvalidDataException($"{command.Name} argument {index} is not an integer");

        public static double Real(AdbSemanticCommand command, int index) =>
            double.TryParse(Text(command, index), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : throw new InvalidDataException($"{command.Name} argument {index} is not a number");

        public static bool Boolean(AdbSemanticCommand command, int index) =>
            bool.TryParse(Text(command, index), out bool value)
                ? value
                : throw new InvalidDataException($"{command.Name} argument {index} is not a boolean");

        public static void Arity(AdbSemanticCommand command, params int[] allowed)
        {
            if (!allowed.Contains(command.Arguments.Count))
                throw new InvalidDataException(
                    $"{command.Name} at {command.StreamOffset} has {command.Arguments.Count} arguments; " +
                    $"expected {string.Join("/", allowed)}");
        }
    }

    public sealed record NativeTextureAnimation(
        string Name, int FrameCount, int Columns, int Rows, double FramesPerSecond, bool Loop,
        NativeAdbProvenance Provenance);

    public static class NativeTextureAnimationCatalog
    {
        public static IReadOnlyList<NativeTextureAnimation> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                AdbSemanticCommand command = SingleCommand(record, "anc_anim");
                NativeAdbValue.Arity(command, 5);
                return new NativeTextureAnimation(
                    record.Name,
                    NativeAdbValue.Integer(command, 0),
                    NativeAdbValue.Integer(command, 1),
                    NativeAdbValue.Integer(command, 2),
                    NativeAdbValue.Real(command, 3),
                    NativeAdbValue.Boolean(command, 4),
                    NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }

        private static AdbSemanticCommand SingleCommand(AdbSemanticRecord record, string name)
        {
            AdbSemanticCommand[] commands = record.Commands.Where(command => command.Name == name).ToArray();
            if (commands.Length != 1 || record.Commands.Count != 1)
                throw new InvalidDataException($"animation '{record.Name}' has an unexpected command shape");
            return commands[0];
        }
    }

    /// <summary>
    /// Raw authored integer parameters for the native LOD evaluator. They are
    /// intentionally not called distances: several retail curves are not
    /// monotonic, proving that the positions have distinct evaluator roles.
    /// </summary>
    public sealed record NativeLodCurve(
        string Name, IReadOnlyList<int> AuthoredValues, NativeAdbProvenance Provenance);

    public static class NativeLodCurveCatalog
    {
        public static IReadOnlyList<NativeLodCurve> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                if (record.Commands.Count != 1 || record.Commands[0].Name != "lod_curve")
                    throw new InvalidDataException($"LOD curve '{record.Name}' has an unexpected command shape");
                int[] values = record.Commands[0].Arguments.Select((_, index) =>
                    NativeAdbValue.Integer(record.Commands[0], index)).ToArray();
                return new NativeLodCurve(record.Name, values, NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }

    public sealed record NativeBoneWeight(string Bone, double Weight);
    public sealed record NativeBoneTemplate(
        string Name, IReadOnlyList<NativeBoneWeight> Bones, NativeAdbProvenance Provenance);

    public static class NativeBoneTemplateCatalog
    {
        public static IReadOnlyList<NativeBoneTemplate> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                var bones = new List<NativeBoneWeight>();
                foreach (AdbSemanticCommand command in record.Commands)
                {
                    if (command.Name == "btc_end")
                    {
                        NativeAdbValue.Arity(command, 0);
                        continue;
                    }
                    if (command.Name != "btc_bone")
                        throw new InvalidDataException($"bone template '{record.Name}' has unknown {command.Name}");
                    NativeAdbValue.Arity(command, 2);
                    bones.Add(new NativeBoneWeight(
                        NativeAdbValue.Text(command, 0),
                        NativeAdbValue.Real(command, 1)));
                }
                return new NativeBoneTemplate(record.Name, bones, NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }

    public sealed record NativePhysicsMaterial(
        string Name,
        double Adhesion,
        int Dimensions,
        double Friction,
        double PrimarySlip,
        double Restitution,
        double SecondarySlip,
        double Softness,
        double? TensileStrength,
        NativeAdbProvenance Provenance);

    public static class NativePhysicsMaterialCatalog
    {
        public static IReadOnlyList<NativePhysicsMaterial> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                var values = record.Commands.ToDictionary(command => command.Name, StringComparer.Ordinal);
                foreach (AdbSemanticCommand command in record.Commands) NativeAdbValue.Arity(command, 1);
                double Required(string name) => NativeAdbValue.Real(values.TryGetValue(name, out AdbSemanticCommand? value)
                    ? value : throw new InvalidDataException($"physics material '{record.Name}' lacks {name}"), 0);
                double? Optional(string name) => values.TryGetValue(name, out AdbSemanticCommand? value)
                    ? NativeAdbValue.Real(value, 0) : null;
                string[] known =
                [
                    "phys_material_adhesion", "phys_material_dimensions", "phys_material_friction",
                    "phys_material_primaryslip", "phys_material_restitution", "phys_material_secondaryslip",
                    "phys_material_softness", "phys_material_tensilestrength",
                ];
                string? unknown = values.Keys.FirstOrDefault(name => !known.Contains(name, StringComparer.Ordinal));
                if (unknown != null) throw new InvalidDataException($"physics material '{record.Name}' has unknown {unknown}");
                return new NativePhysicsMaterial(
                    record.Name,
                    Required("phys_material_adhesion"),
                    checked((int)Required("phys_material_dimensions")),
                    Required("phys_material_friction"),
                    Required("phys_material_primaryslip"),
                    Required("phys_material_restitution"),
                    Required("phys_material_secondaryslip"),
                    Required("phys_material_softness"),
                    Optional("phys_material_tensilestrength"),
                    NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }

    public sealed record NativeWheel(
        string Name,
        bool Brakes,
        bool Drives,
        bool LocksSteering,
        bool Steers,
        double ChassisHeight,
        double Damping,
        double Softness,
        double Travel,
        double ZToS,
        NativeAdbProvenance Provenance);

    public static class NativeWheelCatalog
    {
        public static IReadOnlyList<NativeWheel> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                var values = record.Commands.ToDictionary(command => command.Name, StringComparer.Ordinal);
                if (values.Count != 9)
                    throw new InvalidDataException($"wheel '{record.Name}' has {values.Count} fields instead of 9");
                AdbSemanticCommand Field(string name) => values.TryGetValue(name, out AdbSemanticCommand? value)
                    ? value : throw new InvalidDataException($"wheel '{record.Name}' lacks {name}");
                bool Flag(string suffix) { AdbSemanticCommand value = Field("phys_carwheel_" + suffix); NativeAdbValue.Arity(value, 1); return NativeAdbValue.Boolean(value, 0); }
                double Number(string suffix) { AdbSemanticCommand value = Field("phys_carwheel_suspension_" + suffix); NativeAdbValue.Arity(value, 1); return NativeAdbValue.Real(value, 0); }
                return new NativeWheel(
                    record.Name,
                    Flag("brakes"),
                    Flag("drives"),
                    Flag("locksteering"),
                    Flag("steers"),
                    Number("chassisheight"),
                    Number("damping"),
                    Number("softness"),
                    Number("travel"),
                    Number("ztos"),
                    NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }

    public sealed record NativeWaveSet(
        string Name, IReadOnlyList<string> Files, NativeAdbProvenance Provenance);

    public static class NativeWaveCatalog
    {
        public static IReadOnlyList<NativeWaveSet> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                var files = new List<string>();
                foreach (AdbSemanticCommand command in record.Commands)
                {
                    if (command.Name == "wav_end") { NativeAdbValue.Arity(command, 0); continue; }
                    if (command.Name != "wav_file")
                        throw new InvalidDataException($"wave set '{record.Name}' has unknown {command.Name}");
                    NativeAdbValue.Arity(command, 1);
                    files.Add(NativeAdbValue.Text(command, 0));
                }
                return new NativeWaveSet(record.Name, files, NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }

    public sealed record NativeTimeOfDayKeyframe(string Light, string AuthoredTime, double Hour, double Blend);
    public sealed record NativeTimeOfDayCycle(
        string Name, IReadOnlyList<NativeTimeOfDayKeyframe> Keyframes, NativeAdbProvenance Provenance);

    public static class NativeTimeOfDayCatalog
    {
        public static IReadOnlyList<NativeTimeOfDayCycle> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                var frames = new List<NativeTimeOfDayKeyframe>();
                foreach (AdbSemanticCommand command in record.Commands)
                {
                    if (command.Name == "tod_end") { NativeAdbValue.Arity(command, 0); continue; }
                    if (command.Name != "tod_time")
                        throw new InvalidDataException($"time cycle '{record.Name}' has unknown {command.Name}");
                    NativeAdbValue.Arity(command, 3);
                    string time = NativeAdbValue.Text(command, 1);
                    frames.Add(new NativeTimeOfDayKeyframe(
                        NativeAdbValue.Text(command, 0),
                        time,
                        ParseHour(time),
                        NativeAdbValue.Real(command, 2)));
                }
                return new NativeTimeOfDayCycle(
                    record.Name, frames, NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }

        private static double ParseHour(string value)
        {
            string text = value.Trim().ToLowerInvariant();
            bool pm = text.EndsWith("pm", StringComparison.Ordinal);
            bool am = text.EndsWith("am", StringComparison.Ordinal);
            if (pm || am) text = text[..^2];
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double hour))
                throw new InvalidDataException($"invalid authored time '{value}'");
            if (pm && hour < 12) hour += 12;
            if (am && hour >= 12) hour -= 12;
            return hour;
        }
    }

    public sealed record NativeLightingProperties(
        string Name, uint Diffuse, uint Ambient, uint Specular, uint Emissive, double Power,
        NativeAdbProvenance Provenance);

    public static class NativeLightingPropertiesCatalog
    {
        public static IReadOnlyList<NativeLightingProperties> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                if (record.Commands.Count != 1 || record.Commands[0].Name != "lpc_set")
                    throw new InvalidDataException($"lighting properties '{record.Name}' have an unexpected command shape");
                AdbSemanticCommand command = record.Commands[0];
                NativeAdbValue.Arity(command, 5);
                uint Color(int index) => uint.TryParse(
                    NativeAdbValue.Text(command, index), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
                    ? value : throw new InvalidDataException($"lighting properties '{record.Name}' have invalid color");
                return new NativeLightingProperties(
                    record.Name, Color(0), Color(1), Color(2), Color(3), NativeAdbValue.Real(command, 4),
                    NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }

    public sealed record NativeAnimationEntry(
        string Animation,
        string Alias,
        bool? AuthoredFlag,
        string? ReservedOperand,
        string? PlaybackMode,
        double? AuthoredTimingScalar,
        IReadOnlyList<string> IgnoredTrailingArguments);
    public sealed record NativeAnimationAudio(
        string Animation, IReadOnlyList<string> AuthoredArguments);
    public sealed record NativeAnimationCallback(string Animation, string Callback);
    public sealed record NativeAnimationPackage(
        string Name,
        IReadOnlyList<NativeAnimationEntry> Animations,
        IReadOnlyList<NativeAnimationAudio> Audio,
        IReadOnlyList<NativeAnimationCallback> Callbacks,
        string? Fallback,
        NativeAdbProvenance Provenance);

    public static class NativeAnimationPackageCatalog
    {
        public static IReadOnlyList<NativeAnimationPackage> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                var animations = new List<NativeAnimationEntry>();
                var audio = new List<NativeAnimationAudio>();
                var callbacks = new List<NativeAnimationCallback>();
                string? fallback = null;
                foreach (AdbSemanticCommand command in record.Commands)
                {
                    switch (command.Name)
                    {
                        case "ap_add":
                            // Retail executable handler 0x009cf26f reads clip,
                            // alias, boolean, playback enum, and optional float.
                            // It deliberately skips operand 3 and never reads
                            // trailing operand 6; retain both without assigning
                            // behavior the original handler did not perform.
                            NativeAdbValue.Arity(command, 5, 6, 7);
                            string? flagText = command.Arguments.ElementAtOrDefault(2)?.Text;
                            bool? authoredFlag = bool.TryParse(flagText, out bool parsedFlag) ? parsedFlag : null;
                            double? timingScalar = double.TryParse(
                                command.Arguments.ElementAtOrDefault(5)?.Text,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out double parsedScalar) ? parsedScalar : null;
                            animations.Add(new NativeAnimationEntry(
                                NativeAdbValue.Text(command, 0),
                                NativeAdbValue.Text(command, 1),
                                authoredFlag,
                                command.Arguments.ElementAtOrDefault(3)?.Text,
                                command.Arguments.ElementAtOrDefault(4)?.Text,
                                timingScalar,
                                command.Arguments.Skip(6).Select(value => value.Text ?? $"0x{value.Word:x8}").ToArray()));
                            break;
                        case "ap_audio":
                            NativeAdbValue.Arity(command, 3, 4);
                            audio.Add(new NativeAnimationAudio(
                                NativeAdbValue.Text(command, 0),
                                command.Arguments.Skip(1).Select(value => value.Text ?? $"0x{value.Word:x8}").ToArray()));
                            break;
                        case "ap_audio_callback":
                            NativeAdbValue.Arity(command, 2);
                            callbacks.Add(new NativeAnimationCallback(
                                NativeAdbValue.Text(command, 0), NativeAdbValue.Text(command, 1)));
                            break;
                        case "ap_fallback":
                            NativeAdbValue.Arity(command, 1);
                            fallback = NativeAdbValue.Text(command, 0);
                            break;
                        case "ap_end":
                            NativeAdbValue.Arity(command, 0);
                            break;
                        default:
                            throw new InvalidDataException($"animation package '{record.Name}' has unknown {command.Name}");
                    }
                }
                return new NativeAnimationPackage(
                    record.Name, animations, audio, callbacks, fallback,
                    NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }

    public sealed record NativeAnimationRule(
        string Name,
        IReadOnlyList<string> AuthoredHexArguments,
        IReadOnlyList<uint> AuthoredRotationWords,
        IReadOnlyList<int> SignedTurnUnits,
        IReadOnlyList<double> Radians,
        NativeAdbProvenance Provenance);

    public static class NativeAnimationRuleCatalog
    {
        public static IReadOnlyList<NativeAnimationRule> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                AdbSemanticCommand[] rotations = record.Commands.Where(command => command.Name == "ar_rot").ToArray();
                if (rotations.Length != 1 || record.Commands.Any(command => command.Name is not ("ar_rot" or "ar_end")))
                    throw new InvalidDataException($"animation rule '{record.Name}' has an unexpected command shape");
                NativeAdbValue.Arity(rotations[0], 3);
                string[] authored = rotations[0].Arguments
                    .Select(value => value.Text ?? $"{value.Word:x8}").ToArray();
                uint[] words = authored.Select(value => uint.TryParse(
                    value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint word)
                    ? word
                    : throw new InvalidDataException(
                        $"animation rule '{record.Name}' has invalid hexadecimal rotation word '{value}'"))
                    .ToArray();
                // The retail angle path masks with 0x3fff, subtracts 0x4000
                // above half a turn (client 0x40edd0-0x40edeb), and multiplies
                // atan2 radians by 8192/pi (constant at 0xb7d958). One complete
                // turn is therefore exactly 16384 authored units.
                int[] signed = words.Select(value =>
                {
                    int wrapped = checked((int)(value & 0x3fff));
                    return wrapped >= 0x2000 ? wrapped - 0x4000 : wrapped;
                }).ToArray();
                double[] radians = signed.Select(value => value * Math.PI / 8192.0).ToArray();
                return new NativeAnimationRule(
                    record.Name,
                    authored,
                    words,
                    signed,
                    radians,
                    NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }

    public sealed record NativeAttachmentOperation(
        bool Add,
        string Model,
        string SourceBone,
        string TargetBone,
        string? Mode);
    public sealed record NativeAttachmentSet(
        string Name, IReadOnlyList<NativeAttachmentOperation> Operations, NativeAdbProvenance Provenance);

    public static class NativeAttachmentCatalog
    {
        public static IReadOnlyList<NativeAttachmentSet> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                var operations = new List<NativeAttachmentOperation>();
                foreach (AdbSemanticCommand command in record.Commands)
                {
                    if (command.Name == "at_end") { NativeAdbValue.Arity(command, 0); continue; }
                    if (command.Name is not ("at_add" or "at_rem"))
                        throw new InvalidDataException($"attachment set '{record.Name}' has unknown {command.Name}");
                    NativeAdbValue.Arity(command, 3, 4);
                    operations.Add(new NativeAttachmentOperation(
                        command.Name == "at_add",
                        NativeAdbValue.Text(command, 0),
                        NativeAdbValue.Text(command, 1),
                        NativeAdbValue.Text(command, 2),
                        command.Arguments.ElementAtOrDefault(3)?.Text));
                }
                return new NativeAttachmentSet(
                    record.Name, operations, NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }

    public sealed record NativeEffectPackageCommand(
        string Name, IReadOnlyList<string> Arguments, int StreamOffset);
    public sealed record NativeEffectBinding(
        int Slot,
        string? Event,
        string Effect,
        string? Attachment,
        string? SwapScope,
        int StreamOffset);
    public sealed record NativeEffectMaterialSwap(
        string Scope, string Source, string Replacement, int StreamOffset);
    public sealed record NativeEffectLightingSwap(
        string Scope, string Source, string Replacement, int StreamOffset);
    public sealed record NativeEffectHiddenPart(string Part, string? SwapScope, int StreamOffset);
    public sealed record NativeEffectLodRange(int Near, int Far, int StreamOffset);
    public sealed record NativeEffectPackage(
        string Name,
        IReadOnlyList<NativeEffectBinding> Effects,
        IReadOnlyList<NativeEffectMaterialSwap> MaterialSwaps,
        IReadOnlyList<NativeEffectLightingSwap> LightingSwaps,
        IReadOnlyList<NativeEffectHiddenPart> HiddenParts,
        IReadOnlyList<NativeEffectLodRange> LodRanges,
        IReadOnlyList<NativeEffectPackageCommand> Commands,
        bool VectorTerminated,
        NativeAdbProvenance Provenance);

    public static class NativeEffectPackageCatalog
    {
        private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
        {
            "efp_effect", "efp_end", "efp_endv", "efp_hidden", "efp_lod",
            "efp_swap_begin", "efp_swap_end", "efp_swap_lighting", "efp_swap_material",
        };

        public static IReadOnlyList<NativeEffectPackage> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                string? unknown = record.Commands.Select(command => command.Name).FirstOrDefault(name => !Known.Contains(name));
                if (unknown != null) throw new InvalidDataException($"effect package '{record.Name}' has unknown {unknown}");
                var effects = new List<NativeEffectBinding>();
                var materialSwaps = new List<NativeEffectMaterialSwap>();
                var lightingSwaps = new List<NativeEffectLightingSwap>();
                var hiddenParts = new List<NativeEffectHiddenPart>();
                var lodRanges = new List<NativeEffectLodRange>();
                string? scope = null;
                bool vectorTerminated = false;
                foreach (AdbSemanticCommand command in record.Commands)
                {
                    switch (command.Name)
                    {
                        case "efp_effect":
                            NativeAdbValue.Arity(command, 1, 2, 3);
                            string? eventName = command.Arguments.Count >= 2
                                ? NativeAdbValue.Text(command, 0) : null;
                            string effect = NativeAdbValue.Text(command, command.Arguments.Count >= 2 ? 1 : 0);
                            effects.Add(new NativeEffectBinding(
                                effects.Count,
                                eventName,
                                effect,
                                command.Arguments.Count == 3 ? NativeAdbValue.Text(command, 2) : null,
                                scope,
                                command.StreamOffset));
                            break;
                        case "efp_swap_begin":
                            NativeAdbValue.Arity(command, 1);
                            if (scope != null)
                                throw new InvalidDataException($"effect package '{record.Name}' nests swap scopes");
                            scope = NativeAdbValue.Text(command, 0);
                            break;
                        case "efp_swap_end":
                            NativeAdbValue.Arity(command, 0);
                            if (scope == null)
                                throw new InvalidDataException($"effect package '{record.Name}' closes no swap scope");
                            scope = null;
                            break;
                        case "efp_swap_material":
                            NativeAdbValue.Arity(command, 2);
                            if (scope == null)
                                throw new InvalidDataException($"effect package '{record.Name}' has an unscoped material swap");
                            materialSwaps.Add(new NativeEffectMaterialSwap(
                                scope, NativeAdbValue.Text(command, 0), NativeAdbValue.Text(command, 1),
                                command.StreamOffset));
                            break;
                        case "efp_swap_lighting":
                            NativeAdbValue.Arity(command, 2);
                            if (scope == null)
                                throw new InvalidDataException($"effect package '{record.Name}' has an unscoped lighting swap");
                            lightingSwaps.Add(new NativeEffectLightingSwap(
                                scope, NativeAdbValue.Text(command, 0), NativeAdbValue.Text(command, 1),
                                command.StreamOffset));
                            break;
                        case "efp_hidden":
                            NativeAdbValue.Arity(command, 1);
                            hiddenParts.Add(new NativeEffectHiddenPart(
                                NativeAdbValue.Text(command, 0), scope, command.StreamOffset));
                            break;
                        case "efp_lod":
                            NativeAdbValue.Arity(command, 2);
                            lodRanges.Add(new NativeEffectLodRange(
                                NativeAdbValue.Integer(command, 0), NativeAdbValue.Integer(command, 1),
                                command.StreamOffset));
                            break;
                        case "efp_end":
                            NativeAdbValue.Arity(command, 0);
                            break;
                        case "efp_endv":
                            NativeAdbValue.Arity(command, 0);
                            vectorTerminated = true;
                            break;
                    }
                }
                if (scope != null)
                    throw new InvalidDataException($"effect package '{record.Name}' leaves swap scope '{scope}' open");
                return new NativeEffectPackage(
                    record.Name,
                    effects,
                    materialSwaps,
                    lightingSwaps,
                    hiddenParts,
                    lodRanges,
                    record.Commands.Select(command => new NativeEffectPackageCommand(
                        command.Name,
                        command.Arguments.Select(value => value.Text ?? $"0x{value.Word:x8}").ToArray(),
                        command.StreamOffset)).ToArray(),
                    vectorTerminated,
                    NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }

    public sealed record NativeTimedHelpStep(string TextAndBindings, double DelaySeconds, double LifetimeSeconds);
    public sealed record NativeTimedHelp(
        string Name, IReadOnlyList<NativeTimedHelpStep> Steps, NativeAdbProvenance Provenance);

    public static class NativeTimedHelpCatalog
    {
        public static IReadOnlyList<NativeTimedHelp> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                var steps = new List<NativeTimedHelpStep>();
                foreach (AdbSemanticCommand command in record.Commands)
                {
                    if (command.Name == "timedhelp_end") { NativeAdbValue.Arity(command, 0); continue; }
                    if (command.Name != "timedhelp_step")
                        throw new InvalidDataException($"timed help '{record.Name}' has unknown {command.Name}");
                    NativeAdbValue.Arity(command, 3);
                    steps.Add(new NativeTimedHelpStep(
                        NativeAdbValue.Text(command, 0),
                        NativeAdbValue.Real(command, 1),
                        NativeAdbValue.Real(command, 2)));
                }
                return new NativeTimedHelp(
                    record.Name, steps, NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }

    public sealed record NativeAwardCommand(string Name, IReadOnlyList<string> Arguments, int StreamOffset);
    public sealed record NativeAwardColor(int Index, uint Color, int StreamOffset);
    public sealed record NativeAwardProperty(
        string Name, IReadOnlyList<string> Values, int StreamOffset);
    public sealed record NativeAward(
        string Name,
        IReadOnlyList<NativeAwardColor> Colors,
        IReadOnlyList<NativeAwardProperty> Properties,
        IReadOnlyList<NativeAwardCommand> Commands,
        NativeAdbProvenance Provenance);

    public static class NativeAwardCatalog
    {
        public static IReadOnlyList<NativeAward> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                var colors = new List<NativeAwardColor>();
                var properties = new List<NativeAwardProperty>();
                foreach (AdbSemanticCommand command in record.Commands)
                {
                    if (command.Name == "award_color")
                    {
                        NativeAdbValue.Arity(command, 2);
                        colors.Add(new NativeAwardColor(
                            NativeAdbValue.Integer(command, 0),
                            checked((uint)NativeAdbValue.Integer(command, 1)),
                            command.StreamOffset));
                    }
                    else if (command.Name == "award_property")
                    {
                        NativeAdbValue.Arity(command, 2, 4);
                        properties.Add(new NativeAwardProperty(
                            NativeAdbValue.Text(command, 0),
                            command.Arguments.Skip(1)
                                .Select(value => value.Text ?? $"0x{value.Word:x8}").ToArray(),
                            command.StreamOffset));
                    }
                    else throw new InvalidDataException($"award '{record.Name}' has unknown {command.Name}");
                }
                return new NativeAward(
                    record.Name,
                    colors,
                    properties,
                    record.Commands.Select(command => new NativeAwardCommand(
                        command.Name,
                        command.Arguments.Select(value => value.Text ?? $"0x{value.Word:x8}").ToArray(),
                        command.StreamOffset)).ToArray(),
                    NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }

    public sealed record NativeVector3(double X, double Y, double Z);
    public sealed record NativeLightCycleReference(string Name, IReadOnlyList<string> AuthoredArguments);
    public sealed record NativeLightDefinition(
        string Name,
        string? Type,
        string? FogColor,
        double? Yon,
        double? FogStart,
        double? FogEnd,
        string? Diffuse,
        string? Ambient,
        string? Specular,
        NativeVector3? Position,
        NativeVector3? Direction,
        double? Range,
        double? Falloff,
        double? Theta,
        double? Phi,
        double? Attenuation0,
        double? Attenuation1,
        double? Attenuation2,
        IReadOnlyList<NativeLightCycleReference> Cycles,
        NativeAdbProvenance Provenance);

    public static class NativeLightCatalog
    {
        public static IReadOnlyList<NativeLightDefinition> Parse(byte[] data)
        {
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record => ParseRecord(database, record)).ToArray();
        }

        private static NativeLightDefinition ParseRecord(
            AdbSemanticDatabase database,
            AdbSemanticRecord record)
        {
            var scalar = new Dictionary<string, string>(StringComparer.Ordinal);
            NativeVector3? position = null, direction = null;
            var cycles = new List<NativeLightCycleReference>();
            foreach (AdbSemanticCommand command in record.Commands)
            {
                switch (command.Name)
                {
                    case "lc_end": NativeAdbValue.Arity(command, 0); break;
                    case "lc_position": NativeAdbValue.Arity(command, 3); position = Vector(command); break;
                    case "lc_direction": NativeAdbValue.Arity(command, 3); direction = Vector(command); break;
                    case "lc_light_cycle":
                        NativeAdbValue.Arity(command, 3, 4);
                        cycles.Add(new NativeLightCycleReference(
                            NativeAdbValue.Text(command, 0),
                            command.Arguments.Skip(1).Select(value => value.Text ?? $"0x{value.Word:x8}").ToArray()));
                        break;
                    case "lc_type":
                        NativeAdbValue.Arity(command, 0, 1);
                        scalar[command.Name] = command.Arguments.FirstOrDefault()?.Text ?? "";
                        break;
                    case "lc_ambient": case "lc_attenuation0": case "lc_attenuation1": case "lc_attenuation2":
                    case "lc_diffuse": case "lc_falloff": case "lc_fogcolor": case "lc_fogend": case "lc_fogstart":
                    case "lc_phi": case "lc_range": case "lc_specular": case "lc_theta": case "lc_yon":
                        NativeAdbValue.Arity(command, 1);
                        scalar[command.Name] = NativeAdbValue.Text(command, 0);
                        break;
                    default: throw new InvalidDataException($"light '{record.Name}' has unknown {command.Name}");
                }
            }
            double? Number(string name) => scalar.TryGetValue(name, out string? text)
                ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) : null;
            string? Text(string name) => scalar.GetValueOrDefault(name);
            return new NativeLightDefinition(
                record.Name, Text("lc_type"), Text("lc_fogcolor"), Number("lc_yon"), Number("lc_fogstart"),
                Number("lc_fogend"), Text("lc_diffuse"), Text("lc_ambient"), Text("lc_specular"), position,
                direction, Number("lc_range"), Number("lc_falloff"), Number("lc_theta"), Number("lc_phi"),
                Number("lc_attenuation0"), Number("lc_attenuation1"), Number("lc_attenuation2"), cycles,
                NativeAdbValue.Provenance(database, record));
        }

        private static NativeVector3 Vector(AdbSemanticCommand command) => new(
            NativeAdbValue.Real(command, 0), NativeAdbValue.Real(command, 1), NativeAdbValue.Real(command, 2));
    }

    public sealed record NativeAuthoredArgument(
        uint Word,
        string? Text,
        string Kind,
        bool? Boolean,
        long? Integer,
        double? Real);
    public sealed record NativeAuthoredCommand(
        string Name, IReadOnlyList<NativeAuthoredArgument> Arguments, int StreamOffset);
    public sealed record NativeAuthoredRecord(
        string Name, IReadOnlyList<NativeAuthoredCommand> Commands, NativeAdbProvenance Provenance);

    /// <summary>
    /// Strict typed-preservation adapter for command families whose values are
    /// useful as authored intent but are executed by a replacement subsystem.
    /// A caller supplies the complete allowed command vocabulary; unknown future
    /// commands fail extraction instead of disappearing.
    /// </summary>
    public static class NativeAuthoredCatalog
    {
        public static IReadOnlyList<NativeAuthoredRecord> Parse(byte[] data, params string[] allowedCommands)
        {
            var allowed = allowedCommands.ToHashSet(StringComparer.Ordinal);
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(data);
            return database.Records.Select(record =>
            {
                string? unknown = record.Commands.Select(command => command.Name).FirstOrDefault(name => !allowed.Contains(name));
                if (unknown != null) throw new InvalidDataException($"authored record '{record.Name}' has unknown {unknown}");
                return new NativeAuthoredRecord(record.Name, record.Commands.Select(command => new NativeAuthoredCommand(
                    command.Name,
                    command.Arguments.Select(value => new NativeAuthoredArgument(
                        value.Word,
                        value.Text,
                        value.Kind.ToString().ToLowerInvariant(),
                        value.Boolean,
                        value.Integer,
                        value.Real)).ToArray(),
                    command.StreamOffset)).ToArray(),
                    NativeAdbValue.Provenance(database, record));
            }).ToArray();
        }
    }
}
