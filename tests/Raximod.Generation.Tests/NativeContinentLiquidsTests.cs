using System.Text.Json.Nodes;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Continents;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeContinentLiquidsTests
{
    [Fact]
    public void UsesNativeOffsetOnceAndRetainsSourceOrderIncludingRepeatedNames()
    {
        var mesh = new UberModel.MeshSystem
        {
            A = BitConverter.SingleToUInt32Bits(120.25f), B = BitConverter.SingleToUInt32Bits(370.5f),
        };
        var placements = NativeContinentLiquids.Build(["pool", "other", "pool"], "map09.ubr", _ => mesh);
        Assert.Equal(new[] { "pool", "other", "pool" }, placements.Select(p => p.record));
        Assert.Equal(new[] { 0, 1, 2 }, placements.Select(p => p.sourceIndex));
        Assert.All(placements, placement =>
        {
            Assert.Equal(new[] { 120.25f, 0f, -370.5f }, placement.position);
            Assert.Equal(new[] { 0f, 0f, 0f, 1f }, placement.rotation);
            Assert.Equal(new[] { 1f, 1f, 1f }, placement.scale);
            Assert.Equal("map_lakes", placement.sourceSection);
            Assert.Equal("liquid", placement.layer);
        });
    }

    [Fact]
    public void RejectsMissingRecordsAndMalformedOffsets()
    {
        Assert.Throws<InvalidDataException>(() => NativeContinentLiquids.Build(["absent"], "map09.ubr", _ => null));
        var mesh = new UberModel.MeshSystem { A = BitConverter.SingleToUInt32Bits(float.NaN) };
        Assert.Throws<InvalidDataException>(() => NativeContinentLiquids.Build(["bad"], "map09.ubr", _ => mesh));
    }

    [Fact]
    public void SearhusRefreshIsDeterministicAndPreservesUnrelatedManifestData()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR") ?? "/home/brynn/Downloads/PlanetSide";
        string library = Path.Combine(client, "map09.ubr");
        if (!File.Exists(library)) return;
        string directory = Path.Combine(Path.GetTempPath(), "native-liquid-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "map09.json");
            File.WriteAllText(path, """{"base":"map09","coordinateSystem":"right-handed-y-up","terrain":{"keep":[1,2,3]},"objects":[{"record":"keep"}]}""");
            var log = new Progress<string>();
            NativeContinentLiquids.Refresh(client, directory, [library], null, log);
            string first = File.ReadAllText(path);
            var document = JsonNode.Parse(first)!;
            Assert.Equal(8, document["liquids"]!.AsArray().Count);
            Assert.Equal("keep", document["objects"]![0]!["record"]!.GetValue<string>());
            Assert.Equal(3, document["terrain"]!["keep"]![2]!.GetValue<int>());
            var last = document["liquids"]![7]!;
            Assert.Equal("map09_7", last["record"]!.GetValue<string>());
            Assert.Equal(3652.6592f, last["position"]![0]!.GetValue<float>());
            Assert.Equal(-3906.7354f, last["position"]![2]!.GetValue<float>());
            NativeContinentLiquids.Refresh(client, directory, [library], null, log);
            Assert.Equal(first, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
