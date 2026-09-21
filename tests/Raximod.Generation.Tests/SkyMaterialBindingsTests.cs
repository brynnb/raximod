using Raximod.EngineAssets.Databases;
using Raximod.Generation.Continents;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class SkyMaterialBindingsTests
{
    [Fact]
    public void LayerTuplesPreserveOrderAndRejectDiscardedArguments()
    {
        var dome = new GameObjectDb.GameObject
        {
            Name = "skydome10",
            Properties = new()
            {
                ["layer2"] = ["effect", "theplanet"],
                ["layer1"] = ["mesh", "skydome1", "map10"]
            }
        };
        var layers = SkyDatabase.ParseLayers(dome);
        Assert.Equal([1, 2], layers.Select(layer => layer.Index));
        Assert.Equal("map10", layers[0].Argument);
        Assert.Null(layers[1].Argument);
        dome.Properties["layer1"].Add("unexpected");
        Assert.Throws<InvalidDataException>(() => SkyDatabase.ParseLayers(dome));
        dome.Properties["layer1"] = ["mesh"];
        Assert.Throws<InvalidDataException>(() => SkyDatabase.ParseLayers(dome));
        dome.Properties["layer1"] = [];
        Assert.Throws<InvalidDataException>(() => SkyDatabase.ParseLayers(dome));
    }

    [Fact]
    public void SwapScopeUsesMeshPackageAndKeepsAllSelectedMappings()
    {
        var packages = Packages();
        var result = SkyMaterialBindings.Resolve(new(1, "mesh", "skydome1", "map10"), packages)!;
        Assert.Equal("skydome1", result.Package);
        Assert.Equal("map10", result.Scope);
        Assert.Equal(2, result.MaterialSwaps.Count);
        Assert.All(result.MaterialSwaps, swap => Assert.Equal("skydome_bend_e", swap.Replacement));
        Assert.Equal([100, 104], result.MaterialSwaps.Select(swap => swap.StreamOffset));
        Assert.Equal("efp_begin", result.Provenance.Section);
        Assert.Null(SkyMaterialBindings.Resolve(new(2, "effect", "theplanet", null), packages));
    }

    [Fact]
    public void MissingOrMisappliedScopesFailInsteadOfGuessingTextureNames()
    {
        var packages = Packages();
        Assert.Throws<InvalidDataException>(() =>
            SkyMaterialBindings.Resolve(new(1, "mesh", "skydome10", "map10"), packages));
        Assert.Throws<InvalidDataException>(() =>
            SkyMaterialBindings.Resolve(new(1, "mesh", "skydome1", "missing"), packages));
        Assert.Throws<InvalidDataException>(() =>
            SkyMaterialBindings.Resolve(new(2, "effect", "theplanet", "map10"), packages));
    }

    private static Dictionary<string, NativeEffectPackage> Packages() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["skydome1"] = new("skydome1", [],
            [new("map10", "sky3des_layera+skydes_layer2", "skydome_bend_e", 100),
             new("map10", "sky3des_layera", "skydome_bend_e", 104),
             new("map13", "sky3des_layera", "skydome_bend_i", 108)], [], [], [],
            [new("efp_swap_begin", ["map10"], 96), new("efp_swap_end", [], 106),
             new("efp_swap_begin", ["map13"], 107), new("efp_swap_end", [], 110)],
            false, new("efp_begin", 96, 112, true, 0))
    };
}
