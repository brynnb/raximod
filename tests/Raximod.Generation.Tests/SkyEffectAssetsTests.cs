using Raximod.Generation.Continents;
using Raximod.Generation.Effects;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class SkyEffectAssetsTests
{
    [Fact]
    public void SelectionKeepsDependencyClosureAndSourceObjectsWithoutDuplicateCycles()
    {
        var planet = Effect("planet", "glow");
        var glow = Effect("glow", "planet");
        var unrelated = Effect("weapon");
        var selected = SkyEffectAssets.Select([planet, glow, unrelated], ["PLANET", "planet"]);
        Assert.Equal(["glow", "planet"], selected.Select(effect => effect.Name));
        Assert.Same(glow, selected[0]);
        Assert.Same(planet, selected[1]);
    }

    [Fact]
    public void MissingRootOrChildCannotPublishASeeminglyCompleteSky()
    {
        Assert.Throws<InvalidDataException>(() => SkyEffectAssets.Select([], ["missing"]));
        Assert.Throws<InvalidDataException>(() => SkyEffectAssets.Select([Effect("planet", "missing")], ["planet"]));
    }

    private static EffectGraphExportTool.Effect Effect(string name, params string[] children) =>
        new(name, [], [], children.Select(child => new EffectGraphExportTool.Link("ef_effect", child, [])).ToArray(),
            [], [], [], new("ef_begin", 0, 0, true, 0));
}
