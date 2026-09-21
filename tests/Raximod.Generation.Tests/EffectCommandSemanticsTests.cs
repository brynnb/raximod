using Raximod.Generation.Effects;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class EffectCommandSemanticsTests
{
    [Theory]
    [InlineData("ef_effect_on", "scoped-action", "implemented")]
    [InlineData("ef_decal", "decal", "partial")]
    [InlineData("ef_attached_light", "light", "partial")]
    [InlineData("ef_collision", "collision", "implemented")]
    public void ClassifiesRuntimeCoverage(string command, string category, string support)
    {
        EffectCommandSemantics.Definition definition = EffectCommandSemantics.For(command);
        Assert.Equal(category, definition.Category);
        Assert.Equal(support, definition.RuntimeSupport);
    }

    [Fact]
    public void UnknownCommandsRemainVisible()
    {
        EffectCommandSemantics.Definition definition = EffectCommandSemantics.For("ef_future_command");
        Assert.Equal("unknown", definition.Category);
        Assert.Equal("preserved", definition.RuntimeSupport);
    }
}
