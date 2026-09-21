using System.Text.Json;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class OutfitDecalExportTests
{
    [Fact]
    public void DiscoversMaterialIdsAndResolvesOnlyAuthoredFactionOrSharedVariants()
    {
        var variants = OutfitDecalExport.Discover(["medkit", "oi_decal01", "oi_decal01_tr", "flag_decal01", "oi_decal02"]);
        Assert.Equal(3, variants.Count);
        Assert.Equal("oi_decal01_tr", OutfitDecalExport.Resolve(variants, 1, "tr"));
        Assert.Equal("oi_decal01", OutfitDecalExport.Resolve(variants, 1, "nc"));
        Assert.Equal("oi_decal02", OutfitDecalExport.Resolve(variants, 2, "vs"));
        Assert.Throws<InvalidDataException>(() => OutfitDecalExport.Resolve(variants, 0, "tr"));
        Assert.Throws<InvalidDataException>(() => OutfitDecalExport.Resolve(variants, 3, "tr"));
        Assert.Throws<ArgumentException>(() => OutfitDecalExport.Resolve(variants, 1, "ne"));
        Assert.Throws<InvalidDataException>(() => OutfitDecalExport.Resolve(
            OutfitDecalExport.Discover(["oi_decal01_tr"]), 1, "nc"));
    }

    [Theory]
    [InlineData("oi_decal00")]
    [InlineData("oi_decal27_ne")]
    [InlineData("oi_decal_blue")]
    public void UnexpectedSourceNamesAreReportedRatherThanSilentlyDropped(string name) =>
        Assert.Throws<InvalidDataException>(() => OutfitDecalExport.Discover([name]));

    [Fact]
    public void DuplicateNativeIdsFailInsteadOfDependingOnCatalogOrder() =>
        Assert.Throws<InvalidDataException>(() => OutfitDecalExport.Discover(["oi_decal1_tr", "oi_decal01_tr"]));

    [Fact]
    public void InstalledCatalogHasCompleteFactionMaterialsTexturesAndStableProvenance()
    {
        const string client = "/home/brynn/Downloads/PlanetSide";
        if (!File.Exists(Path.Combine(client, "startup.pak-out/materials.adb"))) return;
        string directory = Path.Combine("/var/tmp", "raximod-outfit-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            OutfitDecalExport.Run(client, directory);
            string path = Path.Combine(directory, "outfit-decals.json");
            byte[] first = File.ReadAllBytes(path);
            DateTime modified = File.GetLastWriteTimeUtc(path);
            OutfitDecalExport.Run(client, directory);
            Assert.Equal(first, File.ReadAllBytes(path));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
            using var json = JsonDocument.Parse(first);
            var root = json.RootElement;
            Assert.Equal(0, root.GetProperty("none").GetInt32());
            Assert.Equal(10000, root.GetProperty("selection").GetProperty("minimumOutfitPoints").GetInt32());
            var entries = root.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Equal(Enumerable.Range(1, 26), entries.Select(entry => entry.GetProperty("id").GetInt32()));
            var definitions = root.GetProperty("materials").EnumerateArray()
                .ToDictionary(value => value.GetProperty("name").GetString()!);
            Assert.Equal(100, definitions.Count);
            Assert.Equal(100, root.GetProperty("provenance").GetProperty("records").GetArrayLength());
            foreach (var entry in entries)
            foreach (string faction in new[] { "tr", "nc", "vs" })
            {
                var binding = entry.GetProperty("factions").GetProperty(faction);
                string name = binding.GetProperty("material").GetString()!;
                Assert.True(definitions.ContainsKey(binding.GetProperty("flagMaterial").GetString()!));
                Assert.True(File.Exists(Path.Combine(directory, binding.GetProperty("previewTextureUri").GetString()!)));
                var stages = definitions[name].GetProperty("stages").EnumerateArray().ToArray();
                var commands = stages[0].GetProperty("commands").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
                foreach (string channel in new[] { "color", "alpha" })
                {
                    Assert.Equal("modulate", commands[$"sc_{channel}op"].GetProperty("arguments")[0].GetString());
                    Assert.Equal("texture", commands[$"sc_{channel}arg1"].GetProperty("arguments")[0].GetString());
                    Assert.Equal("current", commands[$"sc_{channel}arg2"].GetProperty("arguments")[0].GetString());
                }
                Assert.Equal("disable", commands["sc_texturetransformflags"].GetProperty("arguments")[0].GetString());
                Assert.All(stages, stage => Assert.Empty(stage.GetProperty("empireVariants").EnumerateObject()));
            }
            foreach (var definition in definitions.Values)
            foreach (var stage in definition.GetProperty("stages").EnumerateArray())
                if (stage.GetProperty("textureUri").ValueKind == JsonValueKind.String)
                    Assert.True(File.Exists(Path.Combine(directory, stage.GetProperty("textureUri").GetString()!)));
            Assert.DoesNotContain("oi_ange_blue", root.GetRawText());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
