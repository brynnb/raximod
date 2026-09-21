using Xunit;

namespace Raximod.Generation.Tests;

/// <summary>
/// Marks an integration test that reads data from an installed PlanetSide client.
/// The public repository and CI do not distribute that data, so these tests run
/// only when every declared source file is available.
/// </summary>
internal sealed class InstalledClientFactAttribute : FactAttribute
{
    public InstalledClientFactAttribute(params string[] requiredFiles)
    {
        foreach (string relativePath in requiredFiles)
        {
            if (File.Exists(Path.Combine(InstalledClient.Root, relativePath))) continue;
            Skip = $"Requires PlanetSide client file '{relativePath}'. Set PLANETSIDE_DIR to an installed client.";
            break;
        }
    }
}

internal static class InstalledClient
{
    public static string Root => Environment.GetEnvironmentVariable("PLANETSIDE_DIR")
        ?? "/home/brynn/Downloads/PlanetSide";
}
