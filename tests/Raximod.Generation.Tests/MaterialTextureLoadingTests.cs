using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class MaterialTextureLoadingTests
{
    [InstalledClientFact("startup.pak-out/materials.adb", "dds_vehicles.fat")]
    public void ExtractedAndPackedDatabasesResolveTheSameAuthoredTreadTextures()
    {
        string root = Path.Combine("/var/tmp", "raximod-material-loading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "startup.pak-out"));
        try
        {
            byte[] data = File.ReadAllBytes(Path.Combine(InstalledClient.Root, "startup.pak-out/materials.adb"));
            string extracted = Path.Combine(root, "startup.pak-out/materials.adb");
            File.WriteAllBytes(extracted, data);
            File.Copy(Path.Combine(InstalledClient.Root, "dds_vehicles.fat"), Path.Combine(root, "dds_vehicles.fat"));

            var unpacked = new TextureProvider(root);
            Assert.NotNull(MaterialsAdb.TryLoad(root));
            Assert.NotNull(unpacked.Materials);
            File.WriteAllBytes(Path.Combine(root, "startup.pak"),
                PakArchive.Build([new() { Name = "materials.adb", Data = data }]));
            File.Delete(extracted);
            var packed = new TextureProvider(root);

            foreach (string material in new[] { "vehiclegentread_3a", "vehiclegentread_3b" })
            {
                Assert.Equal("vehiclegentread_3", unpacked.Materials.Lookup(material)?.Texture);
                var resolved = unpacked.ResolveNamed(material);
                Assert.Equal("vehiclegentread_3", resolved.Key);
                Assert.NotNull(resolved.Image);
                Assert.Equal(packed.ResolveNamed(material).Image!.Bgra, resolved.Image.Bgra);
                Assert.Equal(packed.Materials!.Lookup(material), unpacked.Materials.Lookup(material));
            }

            // Both views must honor the same extracted override and reject a broken one,
            // rather than silently use the packed database for only the portable materials.
            File.WriteAllBytes(extracted, [0, 1, 2, 3]);
            Assert.ThrowsAny<Exception>(() => MaterialsAdb.TryLoad(root));
            Assert.ThrowsAny<Exception>(() => new TextureProvider(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingDatabaseIsOptionalButMalformedExtractedDataIsNot()
    {
        string root = Path.Combine("/var/tmp", "raximod-material-loading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "startup.pak-out"));
        try
        {
            Assert.Null(MaterialsAdb.TryLoad(root));
            File.WriteAllBytes(Path.Combine(root, "startup.pak-out/materials.adb"), [0, 1, 2, 3]);
            Assert.ThrowsAny<Exception>(() => MaterialsAdb.TryLoad(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
