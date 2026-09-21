using Raximod.Generation.Effects;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class EffectLinkResolverTests
    {
        [Theory]
        [InlineData("collosus_shield_ver_a", "colossus_shield_ver_a")]
        [InlineData("ef_col_ntu_impact", "ef_colossus_ntu_impact")]
        [InlineData("ef_force_dome_amp_once", "ef_force_dome_amp_panels_once")]
        [InlineData("ef_spiker_charge", "ef_spiker_orb_charge")]
        [InlineData("ef_vanu_core_particles_healing", "ef_vanu_core_particles_heal")]
        [InlineData("ef_vanu_tunnel_susp_mesh", "vanu_tunnel_suspension_meshes")]
        [InlineData("fp_thumper_fire2", "fp_thumper_fire1")]
        [InlineData("sythe_muzzleflash", "scythe_muzzleflash")]
        public void VerifiedRetailLinksResolveToShippedGraphs(string authored, string expected)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { expected };
            EffectLinkResolver.Resolution result = EffectLinkResolver.Resolve(authored, names);
            Assert.True(result.Repaired);
            Assert.Equal(expected, result.Target);
        }

        [Fact]
        public void UnknownLinksRemainVisibleRatherThanBeingGuessed()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            EffectLinkResolver.Resolution result = EffectLinkResolver.Resolve("ef_flames_vs", names);
            Assert.False(result.Repaired);
            Assert.Equal("ef_flames_vs", result.Target);
        }
    }
}
