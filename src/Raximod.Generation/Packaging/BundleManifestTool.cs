using System.Security.Cryptography;
using System.Text.Json;

namespace Raximod.Generation.Packaging;

/// <summary>
/// Publishes a stable integrity index over an already generated shared bundle. Family manifests keep
/// their domain-specific contracts; this root document supplies versioning and file-level integrity.
/// </summary>
public static class BundleManifestTool
{
    public const string FormatName = "raximod-shared-bundle";
    public const int CurrentVersion = 1;
    public const string DefaultFileName = "bundle.manifest.json";

    public sealed record Entry(string Path, long Bytes, string Sha256);

    public sealed record Document(
        string Format,
        int Version,
        string Profile,
        IReadOnlyList<Entry> Files,
        long TotalBytes);

    public static Document Build(string rootDirectory, string? outputPath = null)
    {
        string root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        string manifestPath = Path.GetFullPath(outputPath ?? Path.Combine(root, DefaultFileName));
        EnsureWithinRoot(root, manifestPath);
        var entries = new List<Entry>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Select(Path.GetFullPath)
                     .Where(path => !path.Equals(manifestPath, StringComparison.Ordinal))
                     .OrderBy(path => Normalize(Path.GetRelativePath(root, path)), StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            using FileStream stream = File.OpenRead(file);
            string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            entries.Add(new Entry(Normalize(Path.GetRelativePath(root, file)), info.Length, hash));
        }

        return new Document(FormatName, CurrentVersion, "shared", entries, entries.Sum(entry => entry.Bytes));
    }

    public static Document Write(string rootDirectory, string? outputPath = null)
    {
        string root = Path.GetFullPath(rootDirectory);
        string manifestPath = Path.GetFullPath(outputPath ?? Path.Combine(root, DefaultFileName));
        Document document = Build(root, manifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        string temporaryPath = manifestPath + ".raximod.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporaryPath, manifestPath, true);
        return document;
    }

    public static void Verify(string rootDirectory, string? manifestPath = null)
    {
        string root = Path.GetFullPath(rootDirectory);
        string path = Path.GetFullPath(manifestPath ?? Path.Combine(root, DefaultFileName));
        EnsureWithinRoot(root, path);
        Document expected = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Could not decode bundle manifest '{path}'.");
        if (expected.Format != FormatName || expected.Version != CurrentVersion || expected.Profile != "shared")
            throw new InvalidDataException($"Unsupported bundle manifest contract in '{path}'.");

        Document actual = Build(root, path);
        if (expected.TotalBytes != actual.TotalBytes || !expected.Files.SequenceEqual(actual.Files))
            throw new InvalidDataException("Shared bundle files do not match the published manifest.");
    }

    private static void EnsureWithinRoot(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new ArgumentException("The bundle manifest must be written inside the bundle root.");
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
