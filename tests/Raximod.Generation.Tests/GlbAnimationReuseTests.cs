using System.Text.Json.Nodes;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class GlbAnimationReuseTests
{
    [Fact]
    public void ReuseRequiresEveryAuthoredClipAndARealSkinWithoutRewritingTheArtifact()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".glb");
        try
        {
            var json = JsonNode.Parse("""
                {"asset":{"version":"2.0"},"skins":[{"joints":[0]}],
                 "animations":[{"name":"fp_alias_idle"},{"name":"fp_alias_equip"}],
                 "images":[{"uri":"../../../../textures/authored.png"}]}
                """)!.AsObject();
            byte[] original = new GlbImagePackaging(json, [], file).Serialize();
            File.WriteAllBytes(file, original);
            Assert.True(GlbBatchExportTool.ExistingAssetPreservesAnimations(file,
                ["FP_ALIAS_IDLE", "fp_alias_equip"]));
            Assert.False(GlbBatchExportTool.ExistingAssetPreservesAnimations(file,
                ["fp_alias_idle", "fp_alias_fire"]));
            Assert.Equal(original, File.ReadAllBytes(file));
            json.Remove("skins");
            File.WriteAllBytes(file, new GlbImagePackaging(json, [], file).Serialize());
            Assert.False(GlbBatchExportTool.ExistingAssetPreservesAnimations(file, ["fp_alias_idle"]));
            File.WriteAllText(file, "broken GLB");
            Assert.False(GlbBatchExportTool.ExistingAssetPreservesAnimations(file, ["fp_alias_idle"]));
        }
        finally { File.Delete(file); }
    }
}
