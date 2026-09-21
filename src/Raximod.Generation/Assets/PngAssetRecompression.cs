using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raximod.Generation.Assets;

/// <summary>Staged, lossless migration of native PNG files and embedded GLB PNGs.
/// Other asset families and derived model manifests retain their own build tools.</summary>
public static class PngAssetRecompression
{
    public sealed record FileResult(string Path, long BeforeBytes, long AfterBytes,
        string BeforeSha256, string AfterSha256, bool Changed);
    public sealed record Result(string Input, int Files, int ChangedFiles, long BeforeBytes,
        long AfterBytes, FileResult[] Entries);

    public static Result Stage(string input, string output, int workers = 4)
    {
        input = Path.GetFullPath(input); output = Path.GetFullPath(output);
        if (output == input || output.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || input.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Input and staging directories must not overlap.");
        if (Directory.Exists(output)) throw new IOException("Staging output must be a new directory.");
        if (workers is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(workers));
        string[] files = Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".glb")
            .Order(StringComparer.Ordinal).ToArray();
        Directory.CreateDirectory(output);
        var entries = new ConcurrentBag<FileResult>();
        int done = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = workers }, file => {
            byte[] before = File.ReadAllBytes(file);
            byte[] after = Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? PngRecompression.Convert(before) : ReencodeGlb(before, file);
            string relative = Path.GetRelativePath(input, file);
            bool changed = !before.AsSpan().SequenceEqual(after);
            if (changed)
            {
                string staged = Path.Combine(output, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.WriteAllBytes(staged, after);
            }
            entries.Add(new(relative, before.LongLength, after.LongLength, Hash(before), Hash(after), changed));
            int count = Interlocked.Increment(ref done);
            if (count % 250 == 0) Console.WriteLine($"PNG packaging: {count}/{files.Length} files staged");
        });
        var sorted = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        var result = new Result(input, sorted.Length, sorted.Count(e => e.Changed),
            sorted.Sum(e => e.BeforeBytes), sorted.Sum(e => e.AfterBytes), sorted);
        File.WriteAllText(Path.Combine(output, "png-recompression.json"), JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    public static void Apply(string stage)
    {
        stage = Path.GetFullPath(stage);
        var result = JsonSerializer.Deserialize<Result>(File.ReadAllText(Path.Combine(stage, "png-recompression.json")))
            ?? throw new InvalidDataException("Missing PNG migration receipt.");
        // Preflight every source, not just changed files. A failed stage or
        // concurrent export must not replace part of a served asset family.
        foreach (var entry in result.Entries)
        {
            string source = SafePath(result.Input, entry.Path);
            if (Hash(File.ReadAllBytes(source)) != entry.BeforeSha256)
                throw new InvalidDataException($"Source changed since staging: {entry.Path}");
            if (entry.Changed && Hash(File.ReadAllBytes(SafePath(stage, entry.Path))) != entry.AfterSha256)
                throw new InvalidDataException($"Staged output changed: {entry.Path}");
        }
        foreach (var entry in result.Entries.Where(e => e.Changed))
        {
            string source = SafePath(result.Input, entry.Path);
            string temporary = source + $".raximod-{Guid.NewGuid():N}.tmp";
            try { File.Copy(SafePath(stage, entry.Path), temporary); File.Move(temporary, source, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public static byte[] ReencodeGlb(byte[] original, string file)
    {
        var document = GlbImagePackaging.Read(original, file);
        var replacements = new Dictionary<int, byte[]>();
        foreach (var image in document.Root["images"]?.AsArray() ?? [])
        {
            if (image?["bufferView"] == null || image["mimeType"]?.GetValue<string>() != "image/png") continue;
            int index = image["bufferView"]!.GetValue<int>();
            byte[] before = document.ImageBytes(index).ToArray();
            byte[] after = PngRecompression.Convert(before);
            if (!before.AsSpan().SequenceEqual(after)) replacements[index] = after;
        }
        if (replacements.Count == 0) return original;
        // Reuse the same relocation primitive as shared-image externalization;
        // keep every mesh, skin, morph, sparse accessor and animation byte.
        return document.Repack([], replacements);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string SafePath(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root), path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Migration path escapes its root.");
        return path;
    }
}
