using Raximod.Generation.Packaging;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class BundleManifestToolTests
{
    [Fact]
    public void WritesStableRelativeIntegrityIndexAndVerifiesIt()
    {
        string root = Path.Combine(Path.GetTempPath(), $"raximod-bundle-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "players", "models"));
            File.WriteAllBytes(Path.Combine(root, "players", "models", "body.glb"), [1, 2, 3]);
            File.WriteAllText(Path.Combine(root, "players", "manifest.json"), "{}");

            BundleManifestTool.Document first = BundleManifestTool.Write(root);
            BundleManifestTool.Document second = BundleManifestTool.Write(root);

            Assert.Equal(BundleManifestTool.FormatName, first.Format);
            Assert.Equal(BundleManifestTool.CurrentVersion, first.Version);
            Assert.Equal(new[] { "players/manifest.json", "players/models/body.glb" },
                first.Files.Select(entry => entry.Path));
            Assert.Equal(first.Format, second.Format);
            Assert.Equal(first.Version, second.Version);
            Assert.Equal(first.Profile, second.Profile);
            Assert.Equal(first.TotalBytes, second.TotalBytes);
            Assert.True(first.Files.SequenceEqual(second.Files));
            BundleManifestTool.Verify(root);

            File.AppendAllText(Path.Combine(root, "players", "manifest.json"), "changed");
            Assert.Throws<InvalidDataException>(() => BundleManifestTool.Verify(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RejectsManifestOutsideBundleRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"raximod-bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<ArgumentException>(() =>
                BundleManifestTool.Build(root, Path.Combine(Path.GetDirectoryName(root)!, "outside.json")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
