using System.Text.Json.Nodes;
using System.Text.Json;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class SharedTexturePoolTests
{
    [Fact]
    public void IdenticalImagesShareBytesButMaterialsNamesStagesAndVariantsRemainIndependent()
    {
        Fixture((root, stage) => {
            var result = SharedTexturePool.Stage(root, stage);
            Assert.Equal(1, result.SharedImages);
            Assert.Equal(2, result.RemovedCopies);
            Assert.True(result.SavedImageBytes > 0);
            Assert.True(File.Exists(Path.Combine(root, "assets/material-textures/a.png")));
            SharedTexturePool.Apply(stage);
            Assert.DoesNotContain("\n", File.ReadAllText(Path.Combine(root, "assets/world.materials.json")));
            var world = Read(root, "assets/world.materials.json");
            var vehicle = Read(root, "vehicles/body.materials.json");
            Assert.Equal("world", world["materials"]![0]!["name"]!.GetValue<string>());
            Assert.Equal("vehicle", vehicle["materials"]![0]!["name"]!.GetValue<string>());
            Assert.Equal("alpha", world["materials"]![0]!["pipeline"]!.GetValue<string>());
            Assert.Equal("opaque", vehicle["materials"]![0]!["pipeline"]!.GetValue<string>());
            Assert.Equal(2, vehicle["materials"]![0]!["stages"]!.AsArray().Count);
            Assert.Equal("a", world["materials"]![0]!["stages"]![0]!["texture"]!.GetValue<string>());
            var uri = world["materials"]![0]!["stages"]![0]!["textureUri"]!.GetValue<string>();
            Assert.Equal(uri, vehicle["materials"]![0]!["stages"]![0]!["textureUri"]!.GetValue<string>());
            Assert.Equal(uri, vehicle["materials"]![0]!["stages"]![1]!["empireVariants"]!["tr"]!["textureUri"]!.GetValue<string>());
            Assert.Equal("material-textures/different.png", vehicle["materials"]![0]!["stages"]![1]!["textureUri"]!.GetValue<string>());
            Assert.False(File.Exists(Path.Combine(root, "assets/material-textures/a.png")));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "textures"), "*.png"));
            var again = SharedTexturePool.Stage(root, stage + "-again");
            Assert.All(again.Entries, e => Assert.Equal(e.BeforeSha256, e.AfterSha256));
        });
    }

    [Fact]
    public void MissingReferencesAndConcurrentSourceChangesCannotPartiallyPublish()
    {
        Fixture((root, stage) => {
            SharedTexturePool.Stage(root, stage);
            File.WriteAllText(Path.Combine(root, "assets/world.materials.json"), "{}");
            Assert.Throws<InvalidDataException>(() => SharedTexturePool.Apply(stage));
            Assert.False(Directory.Exists(Path.Combine(root, "textures")));
            Assert.True(File.Exists(Path.Combine(root, "vehicles/material-textures/b.png")));
        });
        Fixture((root, stage) => {
            File.Delete(Path.Combine(root, "assets/material-textures/a.png"));
            Assert.Throws<FileNotFoundException>(() => SharedTexturePool.Stage(root, stage));
        });
        Fixture((root, stage) => {
            SharedTexturePool.Stage(root, stage);
            File.WriteAllText(Path.Combine(root, "new.json"), "{}");
            Assert.Throws<InvalidDataException>(() => SharedTexturePool.Apply(stage));
        });
    }

    [Fact]
    public void ChangedSameNameTextureIsNotMergedWithItsPreviousVersionOnPartialReexport()
    {
        Fixture((root, stage) => {
            SharedTexturePool.Stage(root, stage); SharedTexturePool.Apply(stage);
            var definition = Read(root, "assets/world.materials.json");
            definition["materials"]![0]!["stages"]![0]!["textureUri"] = "material-textures/a.png";
            File.WriteAllText(Path.Combine(root, "assets/world.materials.json"), definition.ToJsonString());
            File.WriteAllBytes(Path.Combine(root, "assets/material-textures/a.png"), PngEncoder.EncodeBgra([0, 0, 255, 255], 1, 1));
            SharedTexturePool.Stage(root, stage + "-again"); SharedTexturePool.Apply(stage + "-again");
            var manifest = Read(root, SharedTexturePool.Manifest);
            Assert.Null(manifest["aliases"]!["assets/material-textures/a.png"]);
            Assert.NotNull(manifest["aliases"]!["vehicles/material-textures/b.png"]);
            Assert.Equal("material-textures/a.png", Read(root, "assets/world.materials.json")["materials"]![0]!["stages"]![0]!["textureUri"]!.GetValue<string>());
        });
    }

    private static JsonNode Read(string root, string path) => JsonNode.Parse(File.ReadAllText(Path.Combine(root, path)))!;
    private static void Fixture(Action<string, string> run)
    {
        string dir = Path.Combine(Path.GetTempPath(), "raximod-pool-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(dir, "input"), stage = Path.Combine(dir, "stage");
        Directory.CreateDirectory(Path.Combine(root, "assets/material-textures"));
        Directory.CreateDirectory(Path.Combine(root, "vehicles/material-textures"));
        byte[] png = PngEncoder.EncodeBgra([0, 0, 0, 255], 1, 1);
        File.WriteAllBytes(Path.Combine(root, "assets/material-textures/a.png"), png);
        File.WriteAllBytes(Path.Combine(root, "vehicles/material-textures/b.png"), png);
        File.WriteAllBytes(Path.Combine(root, "vehicles/material-textures/different.png"), PngEncoder.EncodeBgra([255, 0, 0, 255], 1, 1));
        File.WriteAllText(Path.Combine(root, "assets/world.materials.json"), """
            {"materials":[{"name":"world","pipeline":"alpha","stages":[{"texture":"a","textureUri":"material-textures/a.png"}]}]}
            """);
        File.WriteAllText(Path.Combine(root, "vehicles/body.materials.json"), """
            {"materials":[{"name":"vehicle","pipeline":"opaque","stages":[{"texture":"b","textureUri":"material-textures/b.png"},{"textureUri":"material-textures/different.png","empireVariants":{"tr":{"textureUri":"material-textures/b.png"}}}]}]}
            """);
        try { run(root, stage); } finally { Directory.Delete(dir, true); }
    }
}
