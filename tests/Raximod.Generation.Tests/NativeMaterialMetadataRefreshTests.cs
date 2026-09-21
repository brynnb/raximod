using System.Text.Json.Nodes;
using Raximod.EngineAssets.Textures;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeMaterialMetadataRefreshTests
{
    [Fact]
    public void NativePresetRefreshPreservesUnrelatedDataAndIsIdempotent()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR") ?? "/home/brynn/Downloads/PlanetSide";
        if (!File.Exists(Path.Combine(client, "startup.pak-out", "renderstate.adb"))) return;
        string path = Path.Combine("/var/tmp", "material-refresh-" + Guid.NewGuid() + ".json");
        var original = JsonNode.Parse("""
            {"format":"raxicore-native-materials","record":"fixture","extra":{"keep":true},"materials":[
              {"name":"ef_ot_grid","sectionCommands":[{"name":"mat_state","arguments":["cull_reversed"]}],
               "baseCommands":[],"stages":[{"textureUri":"../textures/unchanged.png","unknown":19}],
               "unknownProperty":[1,2,3]}]}
            """)!;
        try
        {
            File.WriteAllText(path, original.ToJsonString());
            var textures = new TextureProvider(client);
            Assert.True(NativeMaterialMetadataRefresh.RenderStates(path, textures));
            var result = JsonNode.Parse(File.ReadAllText(path))!;
            var material = result["materials"]![0]!;
            Assert.True(material["sectionRenderStates"]![0]!["resolved"]!.GetValue<bool>());
            Assert.NotEmpty(material["sectionRenderStates"]![0]!["commands"]!.AsArray());
            material.AsObject().Remove("sectionRenderStates");
            material.AsObject().Remove("baseRenderStates");
            Assert.True(JsonNode.DeepEquals(original, result));
            string accepted = File.ReadAllText(path);
            Assert.False(NativeMaterialMetadataRefresh.RenderStates(path, textures));
            Assert.Equal(accepted, File.ReadAllText(path));
            var invalid = JsonNode.Parse(accepted)!;
            invalid["materials"]![0]!["sectionCommands"]![0]!["arguments"]![0] = "nonexistent_fixture_preset";
            File.WriteAllText(path, invalid.ToJsonString());
            string beforeFailure = File.ReadAllText(path);
            Assert.Throws<InvalidDataException>(() => NativeMaterialMetadataRefresh.RenderStates(path, textures));
            Assert.Equal(beforeFailure, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
