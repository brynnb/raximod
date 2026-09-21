using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class NativeStageProgramResolverTests
    {
        [Fact]
        public void MissingCircuits02SecondStageUsesVerifiedNativeModulationProgram()
        {
            NativeStageProgramResolver.Resolution result =
                NativeStageProgramResolver.Resolve(Lookup, "cs1_circuits02_null");

            Assert.True(result.Repaired);
            Assert.Equal("cs1_circuits02_null", result.AuthoredProgram);
            Assert.Equal("stage1", result.ResolvedProgram);
            Assert.Contains(result.Commands, command =>
                command.Name == "sc_colorop" && command.Arguments.SequenceEqual(new[] { "modulate2x" }));
        }

        [Fact]
        public void UnknownMissingProgramsRemainUnresolved()
        {
            NativeStageProgramResolver.Resolution result =
                NativeStageProgramResolver.Resolve(Lookup, "missing_unverified_stage");

            Assert.False(result.Repaired);
            Assert.Empty(result.Commands);
            Assert.Equal("missing_unverified_stage", result.ResolvedProgram);
        }

        [Theory]
        [InlineData("cs0_adv_med_term_lites_null", "cs0_capture_term_null")]
        [InlineData("cs1_adv_med_term_lites_null", "stage1")]
        [InlineData("cs0__vehic_blinkers_vehicterm_fx1", "cs0_orderterminal_fx_null")]
        [InlineData("cs0_ef_blue_death", "cs0_mat_bluedeath_a")]
        [InlineData("cs0_ef_flor_alpha1", "cs0_flor")]
        [InlineData("cs0_ef_shield_hit", "cs0_decal_frame_shield")]
        [InlineData("cs0_ef_tracer_vs", "cs0_ef_tracer")]
        [InlineData("cs0_ef_zipline_player", "cs0_ef_player_zipline")]
        [InlineData("cs0__frame_mb_int_in1_null", "stage0")]
        [InlineData("cs0__repair_silo_1", "default0")]
        [InlineData("cs1_stair-signs_null", "cs1_stair_signs5five_null")]
        public void VerifiedRetailAliasesResolveToExistingAnalogousPrograms(string authored, string expected)
        {
            NativeStageProgramResolver.Resolution result =
                NativeStageProgramResolver.Resolve(name =>
                    name.Equals(expected, StringComparison.OrdinalIgnoreCase)
                        ? new[] { new AsciiCommandDatabase.Command("resolved", new[] { name }) }
                        : null, authored);

            Assert.True(result.Repaired);
            Assert.Equal(expected, result.ResolvedProgram);
            Assert.Equal(expected, result.Commands.Single().Arguments.Single());
        }

        private static IReadOnlyList<AsciiCommandDatabase.Command>? Lookup(string name) =>
            name.Equals("stage1", StringComparison.OrdinalIgnoreCase)
                ? new[] { new AsciiCommandDatabase.Command("sc_colorop", new[] { "modulate2x" }) }
                : null;
    }
}
