using Raximod.Generation.Packaging;
using System.Text.Json;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class GameBundleConfigurationTests
{
    [Fact]
    public void LoadsVersionedConfigurationAndResolvesRelativePaths()
    {
        string root = Path.Combine(Path.GetTempPath(), $"raximod-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "raximod.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "format": "raximod-game-export",
                  "version": 1,
                  "source": "source",
                  "output": "output",
                  "psforever": "server",
                  "workers": 5
                }
                """);
            GameBundleConfiguration result = GameBundleConfiguration.Load(path);
            Assert.Equal(Path.Combine(root, "source"), result.Source);
            Assert.Equal(Path.Combine(root, "output"), result.Output);
            Assert.Equal(Path.Combine(root, "server"), result.PsForever);
            Assert.Equal(5, result.Workers);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RejectsUnknownFieldsAndWorkerCountsOutsideBound()
    {
        string root = Path.Combine(Path.GetTempPath(), $"raximod-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "raximod.json");
        try
        {
            File.WriteAllText(path, """
                {"format":"raximod-game-export","version":1,"source":"a","output":"b",
                 "psforever":"c","workers":9,"mystery":true}
                """);
            Assert.Throws<JsonException>(() => GameBundleConfiguration.Load(path));
            File.WriteAllText(path, """
                {"format":"raximod-game-export","version":1,"source":"a","output":"b",
                 "psforever":"c","workers":9}
                """);
            Assert.Throws<InvalidDataException>(() => GameBundleConfiguration.Load(path));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
