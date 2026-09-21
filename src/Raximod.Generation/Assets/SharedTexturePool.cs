using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raximod.Generation.Assets;

/// <summary>Final browser packaging: share identical texture bytes, never material definitions.
/// Stage and audit before applying; native names remain in material records and provenance.</summary>
public static class SharedTexturePool
{
    public const string Manifest = "textures/shared-textures.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public sealed record Image(string Sha256, string Uri);
    public sealed record Catalog(int Version, SortedDictionary<string, Image> Aliases);
    public sealed record Entry(string Path, string? BeforeSha256, string? AfterSha256, long BeforeBytes, long AfterBytes);
    public sealed record Receipt(string Input, Entry[] Entries, int SharedImages, int RemovedCopies, long SavedImageBytes);
    private sealed record Reference(JsonObject Parent, string Key, string Path);

    public static Receipt Stage(string input, string stage, int workers = 4)
    {
        input = Path.GetFullPath(input); stage = Path.GetFullPath(stage);
        if (!Directory.Exists(input)) throw new DirectoryNotFoundException(input);
        if (input == stage || input.StartsWith(stage + Path.DirectorySeparatorChar) || stage.StartsWith(input + Path.DirectorySeparatorChar))
            throw new ArgumentException("Source and stage must not overlap.");
        if (Directory.Exists(stage)) throw new IOException("Stage must be a new directory.");
        if (workers is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(workers));
        var files = Directory.GetFiles(input, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".dds" or ".glb" or ".json")
            .Order(StringComparer.Ordinal).ToArray();
        var originals = new ConcurrentDictionary<string, Entry>();
        var references = new ConcurrentDictionary<string, byte>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers };
        // Record every input, not just rewritten files: a concurrent family export
        // invalidates the staged dependency graph instead of publishing mixed output.
        Parallel.ForEach(files, parallel, file => {
            byte[] bytes = File.ReadAllBytes(file);
            string relative = Relative(input, file), hash = Hash(bytes);
            originals[relative] = new(relative, hash, hash, bytes.Length, bytes.Length);
            if (relative == Manifest || Path.GetExtension(file) is not (".glb" or ".json")) return;
            JsonNode root = file.EndsWith(".glb") ? GlbImagePackaging.Read(bytes, file).Root : JsonNode.Parse(bytes)!;
            foreach (var reference in References(root, file, input)) references.TryAdd(reference.Path, 0);
        });
        foreach (string path in references.Keys)
            if (!originals.ContainsKey(path)) throw new FileNotFoundException($"Texture reference is missing: {path}");

        var aliases = File.Exists(SafePath(input, Manifest))
            ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(SafePath(input, Manifest)), Json)!
            : new Catalog(1, new(StringComparer.Ordinal));
        if (aliases.Version != 1) throw new InvalidDataException("Unsupported shared texture catalog.");
        aliases = aliases with { Aliases = new(aliases.Aliases, StringComparer.Ordinal) };
        foreach (var alias in aliases.Aliases.Values.Distinct())
            ValidateImage(SafePath(input, alias.Uri), alias.Sha256);
        foreach (string name in aliases.Aliases.Keys.ToArray())
            if (originals.TryGetValue(name, out var current) && current.BeforeSha256 != aliases.Aliases[name].Sha256)
                aliases.Aliases.Remove(name);

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in references.Keys.GroupBy(p => originals[p].BeforeSha256!))
        {
            var paths = group.Order(StringComparer.Ordinal).ToArray();
            // A single already-pooled image stays pooled across partial/reuse exports.
            if (paths.Length < 2 && !paths[0].StartsWith("textures/", StringComparison.Ordinal)) continue;
            string extension = Path.GetExtension(paths[0]);
            if (paths.Any(p => Path.GetExtension(p) != extension))
                throw new InvalidDataException("Identical bytes have conflicting texture formats.");
            string target = $"textures/{group.Key}{extension}";
            byte[] first = File.ReadAllBytes(SafePath(input, paths[0]));
            foreach (string source in paths)
            {
                if (!first.AsSpan().SequenceEqual(File.ReadAllBytes(SafePath(input, source))))
                    throw new InvalidDataException("Texture digest collision.");
                if (source == target) continue;
                replacements[source] = target;
                aliases.Aliases[source] = new(group.Key, target);
            }
            copies[target] = paths[0];
        }
        Directory.CreateDirectory(stage);
        var results = new ConcurrentDictionary<string, Entry>(originals);
        foreach (var (target, source) in copies)
        {
            byte[] bytes = File.ReadAllBytes(SafePath(input, source));
            if (originals.TryGetValue(target, out var existing) && existing.BeforeSha256 != Hash(bytes))
                throw new InvalidDataException($"Conflicting shared image: {target}");
            Write(target, bytes);
        }
        Parallel.ForEach(files.Where(f => f.EndsWith(".glb") || f.EndsWith(".json")), parallel, file => {
            string relative = Relative(input, file);
            if (relative == Manifest) return;
            byte[] before = File.ReadAllBytes(file);
            var glb = file.EndsWith(".glb") ? GlbImagePackaging.Read(before, file) : null;
            JsonNode root = glb?.Root ?? JsonNode.Parse(before)!;
            bool changed = false;
            foreach (var reference in References(root, file, input))
                if (replacements.TryGetValue(reference.Path, out string? target))
                {
                    reference.Parent[reference.Key] = Relative(Path.GetDirectoryName(file)!, SafePath(input, target));
                    changed = true;
                }
            // Most material sidecars are intentionally compact. URI packaging must
            // not inflate their metadata by pretty-printing the entire corpus.
            if (changed) Write(relative, glb?.Serialize() ?? JsonSerializer.SerializeToUtf8Bytes(root,
                new JsonSerializerOptions(Json) { WriteIndented = before.AsSpan().Contains((byte)'\n') }));
        });
        Write(Manifest, JsonSerializer.SerializeToUtf8Bytes(aliases, Json));
        // Only remove explicitly referenced copies after their consumers have been
        // rewritten. Undeclared/diagnostic files are not guessed to be unused.
        foreach (string source in replacements.Keys)
        {
            var old = originals[source];
            results[source] = old with { AfterSha256 = null, AfterBytes = 0 };
        }
        var receipt = new Receipt(input, results.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray(),
            copies.Count, replacements.Count, replacements.Keys.Sum(p => originals[p].BeforeBytes)
                - copies.Keys.Where(p => !originals.ContainsKey(p)).Sum(p => results[p].AfterBytes));
        File.WriteAllText(Path.Combine(stage, "texture-pool-receipt.json"), JsonSerializer.Serialize(receipt, Json));
        return receipt;

        void Write(string relative, byte[] bytes)
        {
            originals.TryGetValue(relative, out var old);
            string hash = Hash(bytes);
            results[relative] = new(relative, old?.BeforeSha256, hash, old?.BeforeBytes ?? 0, bytes.Length);
            if (hash == old?.BeforeSha256) return;
            string target = SafePath(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
    }

    public static void Apply(string stage)
    {
        stage = Path.GetFullPath(stage);
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(Path.Combine(stage, "texture-pool-receipt.json")), Json)
            ?? throw new InvalidDataException("Missing texture pool receipt.");
        var currentFiles = Directory.GetFiles(receipt.Input, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".dds" or ".glb" or ".json")
            .Select(p => Relative(receipt.Input, p)).ToHashSet(StringComparer.Ordinal);
        if (!currentFiles.SetEquals(receipt.Entries.Where(e => e.BeforeSha256 != null).Select(e => e.Path)))
            throw new InvalidDataException("Source file inventory changed since staging.");
        foreach (var entry in receipt.Entries)
        {
            string source = SafePath(receipt.Input, entry.Path);
            string? hash = File.Exists(source) ? Hash(File.ReadAllBytes(source)) : null;
            if (hash != entry.BeforeSha256) throw new InvalidDataException($"Source changed since staging: {entry.Path}");
            if (entry.AfterSha256 != null && entry.AfterSha256 != entry.BeforeSha256
                && Hash(File.ReadAllBytes(SafePath(stage, entry.Path))) != entry.AfterSha256)
                throw new InvalidDataException($"Staged output changed: {entry.Path}");
        }
        // Publish image bytes before consumers. Delete superseded copies last.
        foreach (var entry in receipt.Entries.Where(e => e.AfterSha256 != null && e.AfterSha256 != e.BeforeSha256)
                     .OrderBy(e => e.Path.StartsWith("textures/") ? 0 : 1))
        {
            string target = SafePath(receipt.Input, entry.Path), temporary = target + $".{Guid.NewGuid():N}.tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try { File.Copy(SafePath(stage, entry.Path), temporary); File.Move(temporary, target, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        foreach (var entry in receipt.Entries.Where(e => e.AfterSha256 == null)) File.Delete(SafePath(receipt.Input, entry.Path));
    }

    public static void ValidateImage(string path, string? expectedHash = null)
    {
        string hash = Hash(File.ReadAllBytes(path));
        if (Path.GetFileNameWithoutExtension(path) != hash || (expectedHash != null && expectedHash != hash))
            throw new InvalidDataException($"Shared texture does not match its content address: {path}");
    }

    private static IEnumerable<Reference> References(JsonNode root, string file, string input)
    {
        return Walk(root, "");
        IEnumerable<Reference> Walk(JsonNode? node, string parent)
        {
            if (node is JsonArray array) foreach (var child in array) foreach (var r in Walk(child, parent)) yield return r;
            if (node is not JsonObject obj) yield break;
            foreach (var (key, value) in obj.ToArray())
            {
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out string? uri)
                    && (key.EndsWith("Uri", StringComparison.Ordinal) || key == "uri" || parent is "textures" or "textureUris")
                    && (uri.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || uri.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)))
                {
                    if (uri.Contains('\\') || uri.Contains(':') || uri.StartsWith('/')) throw new InvalidDataException($"Non-relative texture URI in {file}: {uri}");
                    string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, uri));
                    string relative = Relative(input, path); SafePath(input, relative);
                    yield return new(obj, key, relative);
                }
                else foreach (var r in Walk(value, key)) yield return r;
            }
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string SafePath(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Texture packaging path escapes root: {relative}");
        return full;
    }
}
