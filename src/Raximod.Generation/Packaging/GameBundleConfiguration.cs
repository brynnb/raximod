using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raximod.Generation.Packaging;

/// <summary>Versioned inputs for a reproducible full shared-bundle extraction.</summary>
public sealed record GameBundleConfiguration(
    string Format,
    int Version,
    string Source,
    string Output,
    [property: JsonPropertyName("psforever")] string PsForever,
    int Workers = 4,
    bool Overwrite = false,
    bool CrossFamilyTextureDeduplication = true,
    bool FailOnWarning = false,
    string? TextureStage = null)
{
    public const string FormatName = "raximod-game-export";
    public const int CurrentVersion = 1;

    public static GameBundleConfiguration Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        GameBundleConfiguration configuration = JsonSerializer.Deserialize<GameBundleConfiguration>(
            File.ReadAllText(fullPath), JsonOptions)
            ?? throw new InvalidDataException($"Could not decode Raximod configuration '{fullPath}'.");
        if (configuration.Format != FormatName || configuration.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported Raximod configuration contract in '{fullPath}'.");
        if (configuration.Workers is < 1 or > 6)
            throw new InvalidDataException("Raximod configuration workers must be between 1 and 6.");
        string baseDirectory = Path.GetDirectoryName(fullPath)!;
        return configuration with
        {
            Source = Resolve(baseDirectory, configuration.Source, nameof(Source)),
            Output = Resolve(baseDirectory, configuration.Output, nameof(Output)),
            PsForever = Resolve(baseDirectory, configuration.PsForever, nameof(PsForever)),
            TextureStage = string.IsNullOrWhiteSpace(configuration.TextureStage)
                ? null
                : Resolve(baseDirectory, configuration.TextureStage, nameof(TextureStage)),
        };
    }

    private static string Resolve(string baseDirectory, string path, string field)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException($"Raximod configuration {field} is required.");
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
