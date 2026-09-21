using Raximod.Generation.Packaging;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class ExportProfileTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("standalone")]
    [InlineData("STANDALONE")]
    public void ParseDefaultsToStandalone(string? value)
    {
        Assert.Equal(ExportProfile.Standalone, ExportProfiles.Parse(value));
    }

    [Fact]
    public void ParseAcceptsShared()
    {
        Assert.Equal(ExportProfile.Shared, ExportProfiles.Parse("shared"));
    }

    [Fact]
    public void ParseRejectsUnknownProfile()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => ExportProfiles.Parse("portable-ish"));
        Assert.Contains("standalone", error.Message, StringComparison.Ordinal);
        Assert.Contains("shared", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiptDistinguishesEmbeddedAndSharedTextures()
    {
        string root = Path.Combine(Path.GetTempPath(), $"raximod-receipt-{Guid.NewGuid():N}");
        string output = Path.Combine(root, "exports", "model.glb");
        string library = Path.Combine(root, "patch1", "patch1.ubr");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            Directory.CreateDirectory(Path.GetDirectoryName(library)!);
            File.WriteAllBytes(output, [1, 2, 3]);
            File.WriteAllBytes(library, [4]);
            var result = new GlbExportTool.Result(
                library, "model", output, 1, 2, 3, 4, 5, 6, 7, default);

            ExportReceipt standalone = ExportReceipt.From(
                ExportProfile.Standalone, result, root, Path.Combine(root, "exports", "model.export.json"));
            ExportReceipt shared = ExportReceipt.From(
                ExportProfile.Shared, result, root, Path.Combine(root, "exports", "model.export.json"));

            Assert.Equal("patch1/patch1.ubr", standalone.SourceLibrary);
            Assert.Equal(5, standalone.Content.EmbeddedTextures);
            Assert.Equal(0, standalone.Content.SharedTextures);
            Assert.Equal(0, shared.Content.EmbeddedTextures);
            Assert.Equal(5, shared.Content.SharedTextures);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
