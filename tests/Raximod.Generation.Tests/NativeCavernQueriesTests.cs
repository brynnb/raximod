using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Raximod.Generation.Continents;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeCavernQueriesTests
{
    [InstalledClientFact("expansion1/ugd04.ubr")]
    public void CanonicalRefreshRetainsCavernFacesMaterialsAndUnrelatedFields()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR") ?? "/home/brynn/Downloads/PlanetSide";
        string source = Path.Combine(client, "expansion1/ugd04.ubr");
        string output = Path.Combine(Path.GetTempPath(), "cavern-queries-" + Guid.NewGuid());
        Directory.CreateDirectory(output);
        string manifest = Path.Combine(output, "ugd04.json");
        File.WriteAllText(manifest, """{"base":"ugd04","coordinateSystem":"right-handed-y-up","sea":0,"objects":[{"record":"keep"}]}""");
        try
        {
            NativeContinentLiquids.Refresh(client, output, [source], "ugd04", new Progress<string>());
            string first = File.ReadAllText(manifest);
            var world = JsonNode.Parse(first)!;
            Assert.Empty(world["liquids"]!.AsArray());
            Assert.Equal("keep", world["objects"]![0]!["record"]!.GetValue<string>());
            string path = Path.Combine(output, world["nativeTerrainQueries"]!.GetValue<string>());
            string text = File.ReadAllText(path); DateTime modified = File.GetLastWriteTimeUtc(path);
            var queries = JsonNode.Parse(text)!;
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))), queries["sourceSha256"]!.GetValue<string>());
            Assert.Equal(113, queries["tiles"]!.AsArray().Count);
            Assert.Equal(118836, queries["tiles"]!.AsArray().Sum(tile => tile!["aab"]!["faceCount"]!.GetValue<int>()));
            var tile = queries["tiles"]!.AsArray().Single(tile => tile!["record"]!.GetValue<string>() == "ugd04_1108")!;
            Assert.Equal(1408.4382f, tile["position"]![0]!.GetValue<float>());
            Assert.Equal(-1024.062f, tile["position"]![2]!.GetValue<float>());
            Assert.Contains(tile["aab"]!["sections"]!.AsArray(), section => section!["materialName"]!.GetValue<string>() == "solsar_cavern_lava_crust+null");
            // The native broader query must retain solid faces too, not only lava.
            Assert.True(tile["aab"]!["sections"]!.AsArray().Count > 2);
            NativeContinentLiquids.Refresh(client, output, [source], "ugd04", new Progress<string>());
            Assert.Equal(first, File.ReadAllText(manifest)); Assert.Equal(text, File.ReadAllText(path));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        }
        finally { Directory.Delete(output, true); }
    }
}
