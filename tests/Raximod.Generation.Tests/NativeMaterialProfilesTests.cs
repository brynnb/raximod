using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;
using Raximod.Generation.Assets;
using System.Text.Json;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeMaterialProfilesTests
{
    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "_gf3")]
    [InlineData(2, "_gf4,_gf3")]
    [InlineData(3, "_gf2")]
    [InlineData(4, "_at3,_gf3,_at2")]
    [InlineData(5, "_at4,_gf4,_at3,_gf3,_at2")]
    [InlineData(6, "_gf2")]
    [InlineData(7, "_ot3,_gf3,_ot2")]
    [InlineData(8, "_ot4,_gf4,_ot3,_gf3,_ot2")]
    public void LookupOrderMatchesTheRetailDispatch(int profile, string suffixes) =>
        Assert.Equal(suffixes, string.Join(",", NativeMaterialProfiles.Suffixes(profile)));

    [Fact]
    public void ProfileChoosesFirstExistingWholeDefinitionEvenWhenOriginalExists()
    {
        var records = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "wall+lightmap", "wall+lightmap_gf3", "wall+lightmap_ot2", "wall+lightmap_ot3" };
        Assert.Equal("wall+lightmap_ot3", NativeMaterialProfiles.Resolve("wall+lightmap", 8, records.Contains));
        Assert.Equal("wall+lightmap_gf3", NativeMaterialProfiles.Resolve("wall+lightmap", 2, records.Contains));
        Assert.Equal("wall+lightmap", NativeMaterialProfiles.Resolve("wall+lightmap", 0, records.Contains));
        Assert.Equal("missing", NativeMaterialProfiles.Resolve("missing", 8, records.Contains));
        Assert.Equal("wall+lightmap_GF3", NativeMaterialProfiles.Resolve("wall+lightmap_GF3", 8,
            _ => throw new Exception("Explicit variants must not acquire another suffix")));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeMaterialProfiles.Resolve("wall", 9, records.Contains));
    }

    [InstalledClientFact("startup.pak-out/materials.adb")]
    public void InstalledVrGridResolvesItsAnimationAndThreeAuthoredStages()
    {
        string root = Environment.GetEnvironmentVariable("PLANETSIDE_DIR")
            ?? "/home/brynn/Downloads/PlanetSide";
        var materials = AsciiCommandDatabase.TryLoad(root, "materials.adb");
        Assert.NotNull(materials);
        string selected = NativeMaterialProfiles.Resolve("vrgridstatic+fadegridsides",
            NativeMaterialProfiles.GenericFourStageProfile, name => materials.Lookup(name) != null);
        Assert.Equal("vrgridstatic+fadegridsides_gf3", selected);
        var commands = materials.Lookup(selected)!;
        Assert.Contains(commands, c => c.Name == "mat_anim1" && c.Arguments.SequenceEqual(["vrtgrid"]));
        Assert.Equal(["mat_stage1", "mat_stage2", "mat_stage3"], commands
            .Where(c => c.Name.StartsWith("mat_stage") && !c.Arguments.Contains("disable")).Select(c => c.Name));
        Assert.Null(materials.Lookup("vrgridstatic+fadegridsides"));

        string output = Path.Combine("/var/tmp", "native-sky-profile-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            string glb = Path.Combine(output, "sky-profile.glb");
            NativeMaterialManifestTool.Write(glb, "sky-profile", [new(selected, 1, true, true),
                new("skydome20+null_gf3", 1, true, true)], new TextureProvider(root));
            using var document = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(glb, ".materials.json")));
            Assert.Empty(document.RootElement.GetProperty("missingTextures").EnumerateArray());
            var grid = document.RootElement.GetProperty("materials").EnumerateArray()
                .Single(m => m.GetProperty("name").GetString() == selected);
            Assert.Equal("fadegridsides", grid.GetProperty("lightmapTexture").GetString());
            Assert.Equal(16, grid.GetProperty("stages")[0].GetProperty("animationDefinition")
                .GetProperty("frames").GetArrayLength());
            foreach (var frame in grid.GetProperty("stages")[0].GetProperty("animationDefinition").GetProperty("frames").EnumerateArray())
                Assert.True(File.Exists(Path.Combine(output, frame.GetProperty("uri").GetString()!)));
        }
        finally { Directory.Delete(output, true); }
    }
    [InstalledClientFact("planetside.exe", "startup.pak-out/materials.adb")]
    public void EnvironmentRecordsSourceAbsencesSeparatelyFromAvailableAnimationPages()
    {
        string root = Environment.GetEnvironmentVariable("PLANETSIDE_DIR")
            ?? "/home/brynn/Downloads/PlanetSide";
        string output = Path.Combine("/var/tmp", "native-vr-environment-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Continents.EnvironmentCatalog.Export(root, output, ["map14", "map15", "map16"],
                new Raximod.Generation.SynchronousProgress<string>(_ => { }));
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "environment.json")));
            var data = document.RootElement;
            Assert.Equal(["sky3des_layera", "skydes_layer2"], data.GetProperty("unshippedSkyTextures")
                .EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(3, data.GetProperty("zones").EnumerateObject().Count());
            foreach (var zone in data.GetProperty("zones").EnumerateObject())
                Assert.Equal(["skydome1", "vrskydomeb"], zone.Value.GetProperty("layers")
                    .EnumerateArray().Select(value => value.GetProperty("name").GetString()));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }

}
