namespace Raximod.Generation.Effects
{
    /// <summary>Repairs exact, evidence-backed spelling/name mistakes in the retail effect graph.</summary>
    public static class EffectLinkResolver
    {
        public sealed record Resolution(string AuthoredTarget, string Target, string? RepairReason)
        {
            public bool Repaired => RepairReason != null;
        }

        private static readonly IReadOnlyDictionary<string, (string Target, string Reason)> Repairs =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["collosus_shield_ver_a"] = ("colossus_shield_ver_a", "Corrects the retail collosus typo."),
                ["collosus_shield_ver_b"] = ("colossus_shield_ver_b", "Corrects the retail collosus typo."),
                ["collosus_shield_ver_c"] = ("colossus_shield_ver_c", "Corrects the retail collosus typo."),
                ["ef_col_ntu_impact"] = ("ef_colossus_ntu_impact",
                    "The existing Colossus NTU graph uses the full vehicle name."),
                ["ef_col_ntu_suckin"] = ("ef_colossus_ntu_suckin",
                    "The existing Colossus NTU graph uses the full vehicle name."),
                ["ef_force_dome_amp_once"] = ("ef_force_dome_amp_panels_once",
                    "The shipped one-shot AMP dome graph includes the panels qualifier."),
                ["ef_spiker_charge"] = ("ef_spiker_orb_charge",
                    "The shipped Spiker charge graph is named ef_spiker_orb_charge."),
                ["ef_vanu_core_particles_b_healing"] = ("ef_vanu_core_particles_b_heal",
                    "The shipped Vanu-core graph uses heal rather than healing."),
                ["ef_vanu_core_particles_healing"] = ("ef_vanu_core_particles_heal",
                    "The shipped Vanu-core graph uses heal rather than healing."),
                ["ef_vanu_tunnel_susp_mesh"] = ("vanu_tunnel_suspension_meshes",
                    "The shipped suspension graph uses its expanded plural name."),
                ["fp_thumper_fire2"] = ("fp_thumper_fire1",
                    "Both Thumper fire modes use the same shipped first-person muzzle graph."),
                ["sythe_muzzleflash"] = ("scythe_muzzleflash", "Corrects the retail sythe typo.")
            };

        public static Resolution Resolve(string target, IReadOnlySet<string> existingTargets)
        {
            if (existingTargets.Contains(target)) return new Resolution(target, target, null);
            if (Repairs.TryGetValue(target, out var repair) && existingTargets.Contains(repair.Target))
                return new Resolution(target, repair.Target, repair.Reason);
            return new Resolution(target, target, null);
        }
    }
}
