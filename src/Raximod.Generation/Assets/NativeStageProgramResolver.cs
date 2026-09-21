using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets
{
    /// <summary>
    /// Resolves authored fixed-function stage programs and the small set of verified retail-data
    /// mistakes. Repairs are deliberately exact-name aliases: unknown programs remain unresolved
    /// and visible to extraction diagnostics instead of being silently guessed.
    /// </summary>
    public static class NativeStageProgramResolver
    {
        public sealed record Resolution(
            string AuthoredProgram,
            string ResolvedProgram,
            IReadOnlyList<AsciiCommandDatabase.Command> Commands,
            string? RepairReason)
        {
            public bool Repaired => RepairReason != null;
        }

        private static readonly IReadOnlyDictionary<string, (string Program, string Reason)> Repairs =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["cs1_circuits02_null"] = (
                    "stage1",
                    "Retail materials reference a missing circuits02 second-stage program; " +
                    "the matching circuits01 material uses the native stage1 modulation program."),
                ["cs0_adv_med_term_lites_null"] = (
                    "cs0_capture_term_null",
                    "The advanced medical terminal uses the same first-stage terminal gradient as the " +
                    "otherwise identical capture-terminal material."),
                ["cs1_adv_med_term_lites_null"] = (
                    "stage1",
                    "The advanced medical terminal's second texture is the same ordinary UV1 modulation " +
                    "stage used by the matching terminal family."),
                ["cs0__vehic_blinkers_vehicterm_fx1"] = (
                    "cs0_orderterminal_fx_null",
                    "The vehicle-terminal blinker uses the same primary-gradient scrolling stage as the " +
                    "order-terminal effect; its authored second stage is already present."),
                ["cs0_ef_blue_death"] = (
                    "cs0_mat_bluedeath_a",
                    "Both programs bind ef_blue_death in the same additive alpha-sorted effect family."),
                ["cs0_ef_flor_alpha1"] = (
                    "cs0_flor",
                    "The alpha-sorted ef_flor material references the missing alpha1 spelling while the " +
                    "same texture has a valid native flor stage."),
                ["cs0_ef_shield_hit"] = (
                    "cs0_decal_frame_shield",
                    "The shield-hit animation uses the same warp-impact first-stage program as the valid " +
                    "shield-frame decal family."),
                ["cs0_ef_tracer_vs"] = (
                    "cs0_ef_tracer",
                    "Faction tracer materials share the generic tracer stage; only their texture differs."),
                ["cs0_ef_zipline_player"] = (
                    "cs0_ef_player_zipline",
                    "The retail material transposes the two words in the existing player-zipline program."),
                ["cs0__frame_mb_int_in1_null"] = (
                    "stage0",
                    "The adjacent frame_mb_int_in2 lightmap uses the standard first texture stage."),
                ["cs0__repair_silo_1"] = (
                    "default0",
                    "The same spawn_door texture uses the default first stage in the other repair/spawn " +
                    "silo materials."),
                ["cs1_stair-signs_null"] = (
                    "cs1_stair_signs5five_null",
                    "All stair-sign variants use the same second-stage sign/lightmap modulation program."),
                ["cs1_decal_lasher_hit"] = (
                    "disable",
                    "The Lasher decal has no second texture; other single-animation impact decals explicitly " +
                    "disable stage two, including the matching warp-impact shield decal family.")
            };

        public static Resolution Resolve(AsciiCommandDatabase? database, string? authoredProgram)
            => Resolve(database == null ? null : database.Lookup, authoredProgram);

        public static Resolution Resolve(
            Func<string, IReadOnlyList<AsciiCommandDatabase.Command>?>? lookup,
            string? authoredProgram)
        {
            string authored = authoredProgram ?? "";
            IReadOnlyList<AsciiCommandDatabase.Command> commands =
                lookup?.Invoke(authored) ?? Array.Empty<AsciiCommandDatabase.Command>();
            if (commands.Count > 0 || !Repairs.TryGetValue(authored, out var repair))
                return new Resolution(authored, authored, commands, null);

            IReadOnlyList<AsciiCommandDatabase.Command> repaired =
                lookup?.Invoke(repair.Program) ?? Array.Empty<AsciiCommandDatabase.Command>();
            return repaired.Count > 0
                ? new Resolution(authored, repair.Program, repaired, repair.Reason)
                : new Resolution(authored, authored, commands, null);
        }
    }
}
