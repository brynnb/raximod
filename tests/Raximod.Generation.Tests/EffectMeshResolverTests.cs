using Raximod.Generation.Effects;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class EffectMeshResolverTests
    {
        [Theory]
        [InlineData("ancient_door_ef_mesh", "Ancient_door_ef_mesh_b", "archive-alias")]
        [InlineData("twomanassaultbuggy_wheel_debris", "Twomanassaultbuggy_wheel_destroyed", "archive-alias")]
        [InlineData("Vanguard_gunner_hatch_debris", "vanguard_hatch_b", "archive-alias")]
        [InlineData("vst_muzzle_mesh_c", "Vst_muzzle_mesh_a", "archive-alias")]
        [InlineData("wpg_cavern_effect_mesh_a", "Wpg_cavern_arm_effect_mesh", "archive-alias")]
        [InlineData("radiation_bomb", "sphere", "procedural-fallback")]
        [InlineData("wardog_shield_mesh", "sphere", "procedural-fallback")]
        public void ResolvesOnlyEvidenceBackedEffectMeshes(string source, string target, string kind)
        {
            EffectMeshResolver.Resolution repair = Assert.IsType<EffectMeshResolver.Resolution>(
                EffectMeshResolver.Resolve(source));

            Assert.Equal(target, repair.ResolvedMesh, ignoreCase: true);
            Assert.Equal(kind, repair.Kind);
            Assert.NotEmpty(repair.Reason);
        }

        [Fact]
        public void UnknownMeshIsNotFuzzilyRepaired()
        {
            Assert.Null(EffectMeshResolver.Resolve("something_that_looks_close"));
        }
    }
}
