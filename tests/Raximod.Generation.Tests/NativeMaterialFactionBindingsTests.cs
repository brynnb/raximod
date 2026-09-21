using System.Text;
using System.Text.Json;
using Raximod.EngineAssets.Textures;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeMaterialFactionBindingsTests
{
    [Theory]
    [InlineData("ob_redlight", "hart_red")]
    [InlineData("ob_greenlight", "hart_green")]
    public void NativeSignalScopesExportBothIlluminatedSurfacesAndTheirTextures(string record, string color)
    {
        const string client = "/home/brynn/Downloads/PlanetSide";
        if (!File.Exists(Path.Combine(client, "startup.pak-out/epackage.adb"))) return;
        string directory = Path.Combine("/var/tmp", "raximod-signal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string model = Path.Combine(directory, record + ".glb");
            NativeMaterialManifestTool.Write(model, record, [
                new(color + "+_ob_light_off", 1, true, true),
                new("nc_inside_frame+_ob_light_off", 1, true, true)
            ], new TextureProvider(client));
            using var document = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(model, ".materials.json")));
            var root = document.RootElement;
            var swaps = root.GetProperty("stateBindings").GetProperty("swaps").EnumerateArray().ToArray();
            Assert.Equal(3, swaps.Length);
            Assert.All(swaps, swap => Assert.True(swap.GetProperty("resolved").GetBoolean()));
            Assert.Equal(2, swaps.Count(swap => swap.GetProperty("scope").GetString() == color + "_on"));
            var materials = root.GetProperty("materials").EnumerateArray().ToArray();
            Assert.Equal(4, materials.Length);
            foreach (string name in new[] { color + "1", color + "2" })
            {
                var on = materials.Single(material => material.GetProperty("name").GetString() == name);
                var lightmap = on.GetProperty("stages")[0];
                Assert.Equal("_ob_light_" + color.Split('_')[1], lightmap.GetProperty("texture").GetString());
                Assert.True(File.Exists(Path.Combine(directory, lightmap.GetProperty("textureUri").GetString()!)));
                Assert.True(on.GetProperty("hasUv1").GetBoolean());
            }
            Assert.Empty(root.GetProperty("missingTextures").EnumerateArray());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("tr", true)]
    [InlineData("NC", true)]
    [InlineData("vs", true)]
    [InlineData("VS_low", false)]
    [InlineData("blackops", false)]
    [InlineData("map10", false)]
    public void OnlyExplicitEmpireScopesDriveOrdinaryFactionMaterials(string scope, bool expected) =>
        Assert.Equal(expected, NativeMaterialFactionBindings.IsEmpireScope(scope));

    [Fact]
    public void MaterialRefreshPreservesGlbAndDerivesEveryMaterialAndUvSetFromItsPrimitives()
    {
        string directory = Path.Combine("/var/tmp", "raximod-faction-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "fixture.glb");
            byte[] json = Encoding.UTF8.GetBytes("""
                {"materials":[{"name":"one"},{"name":"two"}],"meshes":[{"primitives":[
                {"material":0,"attributes":{"TEXCOORD_0":0}},
                {"material":0,"attributes":{"TEXCOORD_1":1}},
                {"material":1,"attributes":{"TEXCOORD_0":2}}]}]}
                """);
            using (var output = File.Create(path))
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(0x46546c67u); writer.Write(2u); writer.Write((uint)(20 + json.Length));
                writer.Write((uint)json.Length); writer.Write(0x4e4f534au); writer.Write(json);
            }
            byte[] before = File.ReadAllBytes(path);
            NativeMaterialFactionBindings.Refresh(path, "fixture", new TextureProvider(directory));
            Assert.Equal(before, File.ReadAllBytes(path));
            using var sidecar = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".materials.json")));
            var materials = sidecar.RootElement.GetProperty("materials");
            Assert.Equal(2, materials.GetArrayLength());
            Assert.Equal("one", materials[0].GetProperty("name").GetString());
            Assert.Equal(2, materials[0].GetProperty("sections").GetInt32());
            Assert.True(materials[0].GetProperty("hasUv0").GetBoolean());
            Assert.True(materials[0].GetProperty("hasUv1").GetBoolean());
            Assert.False(materials[1].GetProperty("hasUv1").GetBoolean());
            Assert.Equal(JsonValueKind.Null, sidecar.RootElement.GetProperty("factionBindings").ValueKind);
            before[0] = 0; File.WriteAllBytes(path, before);
            Assert.Throws<InvalidDataException>(() =>
                NativeMaterialFactionBindings.Refresh(path, "fixture", new TextureProvider(directory)));
        }
        finally { Directory.Delete(directory, true); }
    }
}
