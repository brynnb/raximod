using System.Numerics;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Maps;
using Raximod.Generation.Continents;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class WarpgateBarrierCatalogTests
{
    [Fact]
    public void ResolvesAllNativeDomeFamiliesAndAppliesLocalBarrierOffset()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR")
            ?? "/home/brynn/Downloads/PlanetSide";
        string path = Path.Combine(client, "startup.pak-out", "game_objects.adb");
        if (!File.Exists(path)) return;

        GameObjectDb database = GameObjectDb.Parse(File.ReadAllBytes(path));
        IReadOnlyDictionary<string, WarpgateBarrierCatalog.Definition> definitions =
            WarpgateBarrierCatalog.Read(database);

        Assert.Equal("dome3", definitions["warpgate"].Visual);
        Assert.Equal(227f, definitions["warpgate"].Radius);
        Assert.Equal("warpgate_small_dome", definitions["warpgate_small"].Visual);
        Assert.Equal(90.5f, definitions["warpgate_small"].Radius);
        Assert.Equal("warpgate_cavern_dome", definitions["warpgate_cavern"].Visual);
        Assert.Equal(42.662f, definitions["warpgate_cavern"].Radius);

        var parent = new MapObject(
            0,
            "warpgate",
            new Vector3(100, 200, 30),
            Vector3.One,
            MathF.PI / 2);
        WarpgateBarrierCatalog.Placement placement = Assert.Single(
            WarpgateBarrierCatalog.Build([parent], definitions));
        Assert.True(Vector3.Distance(new Vector3(100, 38, -200), placement.Position) < 0.0001f);
        Assert.Equal(new Vector3(0, 0, 8), placement.Definition.SourceOffset);
    }
}
