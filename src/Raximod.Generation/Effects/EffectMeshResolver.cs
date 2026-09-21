namespace Raximod.Generation.Effects
{
    /// <summary>
    /// Repairs the small set of effect-mesh references for which the shipped effects database and
    /// installed render libraries disagree. Every entry is exact and evidence-backed; this deliberately
    /// avoids fuzzy matching effect names to arbitrary geometry.
    /// </summary>
    public static class EffectMeshResolver
    {
        public sealed record Resolution(
            string AuthoredMesh,
            string ResolvedMesh,
            string Reason,
            string Kind);

        private static readonly IReadOnlyDictionary<string, Resolution> Repairs =
            new Dictionary<string, Resolution>(StringComparer.OrdinalIgnoreCase)
            {
                ["ancient_door_ef_mesh"] = Archive(
                    "ancient_door_ef_mesh", "Ancient_door_ef_mesh_b",
                    "The expansion archive ships the effect door geometry with the _b suffix."),
                ["ancient_garage_door_ef_mesh"] = Archive(
                    "ancient_garage_door_ef_mesh", "Ancient_garage_door_ef_mesh_b",
                    "The expansion archive ships the effect garage-door geometry with the _b suffix."),
                ["twomanassaultbuggy_bumper_debris"] = Archive(
                    "twomanassaultbuggy_bumper_debris", "Twomanassaultbuggy_bumper_destroyed",
                    "The buggy model ships the detached bumper as the destroyed mesh."),
                ["twomanassaultbuggy_headlight_debris"] = Archive(
                    "twomanassaultbuggy_headlight_debris", "Twomanassaultbuggy_headlight_destroyed",
                    "The buggy model ships the detached headlight as the destroyed mesh."),
                ["twomanassaultbuggy_wheel_debris"] = Archive(
                    "twomanassaultbuggy_wheel_debris", "Twomanassaultbuggy_wheel_destroyed",
                    "The buggy model ships the detached wheel as the destroyed mesh."),
                ["Vanguard_gunner_hatch_debris"] = Archive(
                    "Vanguard_gunner_hatch_debris", "vanguard_hatch_b",
                    "The Vanguard archive ships hatch A and hatch B; the gunner is hatch B, but no separately named gunner-debris record exists."),
                ["vst_muzzle_mesh_b"] = Archive(
                    "vst_muzzle_mesh_b", "Vst_muzzle_mesh_a",
                    "Only one Vanu stationary-turret muzzle geometry is shipped; the A/B/C effect graphs vary timing rather than material or scale."),
                ["vst_muzzle_mesh_c"] = Archive(
                    "vst_muzzle_mesh_c", "Vst_muzzle_mesh_a",
                    "Only one Vanu stationary-turret muzzle geometry is shipped; the A/B/C effect graphs vary timing rather than material or scale."),
                ["wpg_cavern_effect_mesh_a"] = Archive(
                    "wpg_cavern_effect_mesh_a", "Wpg_cavern_arm_effect_mesh",
                    "The cavern warpgate archive names the shipped effect geometry after the arm rather than the graph's _a spelling."),
                ["col_ntusiphon_effect"] = Archive(
                    "col_ntusiphon_effect", "col_ntusiphon_l",
                    "No generic siphon-effect record is shipped; the Colossus archive contains the left and right native siphon meshes, and this graph has one mesh slot."),

                // These records are absent from every installed UBR. Radiation-bomb and Wardog shield are
                // also explicitly named in the retail meshrules_donotload.lst. Preserve their authored
                // layer motion/material/color on a procedural sphere rather than silently dropping them.
                ["radiation_bomb"] = Procedural(
                    "radiation_bomb",
                    "The retail client explicitly marks this mesh do-not-load and ships no render record; its authored smoke-trail material and transform are retained on a procedural volume."),
                ["wardog_shield_mesh"] = Procedural(
                    "wardog_shield_mesh",
                    "The retail client explicitly marks this cut Wardog mesh do-not-load and ships no render record; the authored shield tint is retained on a procedural volume."),
                ["wardog_sonic_bark_projectile"] = Procedural(
                    "wardog_sonic_bark_projectile",
                    "No Wardog render record exists in the installed client; the authored scale, motion, and effect material are retained on a procedural volume."),
            };

        public static Resolution? Resolve(string mesh) =>
            Repairs.TryGetValue(mesh, out Resolution? resolution) ? resolution : null;

        public static IReadOnlyCollection<Resolution> KnownRepairs => Repairs.Values.ToArray();

        private static Resolution Archive(string source, string target, string reason) =>
            new(source, target, reason, "archive-alias");

        private static Resolution Procedural(string source, string reason) =>
            new(source, "sphere", reason, "procedural-fallback");
    }
}
