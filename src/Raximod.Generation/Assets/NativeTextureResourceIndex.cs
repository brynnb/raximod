using System.Security.Cryptography;
using System.Text;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Textures;

namespace Raximod.Generation.Assets;

/// <summary>Exact static DDS lookup outside FLAT. Absence is publishable only
/// after every source directory was read; a decode/index error is not absence.</summary>
internal sealed class NativeTextureResourceIndex
{
    internal sealed record Search(int ArchiveCount, int LooseFileCount, string IndexSha256, string[] NumericDataPacks);
    private sealed record Source(string Path, string? Entry);
    private readonly Dictionary<string, List<Source>> sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root;
    internal Search Evidence { get; }

    internal NativeTextureResourceIndex(TextureProvider textures)
    {
        root = textures.AssetDirectory ?? throw new InvalidDataException("Native texture source directory is unavailable");
        if (textures.IndexErrors.Count > 0)
            throw new InvalidDataException("Incomplete FLAT texture index: " + string.Join("; ", textures.IndexErrors));
        var options = new EnumerationOptions { RecurseSubdirectories = true,
            IgnoreInaccessible = false, MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint };
        var archives = Directory.GetFiles(root, "*.pak", options).Order(StringComparer.Ordinal).ToArray();
        var loose = Directory.GetFiles(root, "*.dds", options).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = textures.SourceArchives.Order(StringComparer.Ordinal).Select(path =>
            $"FLAT:{Relative(path)}:{new FileInfo(path).Length}").ToList();
        fingerprint.AddRange(textures.TextureNames.Order(StringComparer.Ordinal).Select(key => "FLAT-key:" + key));
        var numericPacks = new List<string>();
        foreach (string path in archives)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != 0x4b434150) // PACK
            {
                if (!IsNumericDataPack(stream))
                    throw new InvalidDataException($"Unsupported archive prevents native texture absence proof: {path}");
                numericPacks.Add(Relative(path));
                fingerprint.Add($"numeric-datapack:{Relative(path)}:{stream.Length}");
                continue;
            }
            stream.Position = 0;
            foreach (var entry in PakArchive.ReadDirectory(stream))
            {
                fingerprint.Add($"PACK:{Relative(path)}:{entry.Name}:{entry.Offset}:{entry.StoredSize}:{entry.Hash}");
                if (Path.GetExtension(entry.Name).Equals(".dds", StringComparison.OrdinalIgnoreCase))
                    Add(Path.GetFileNameWithoutExtension(entry.Name.Replace('\\', '/')), new(path, entry.Name));
            }
        }
        foreach (string path in loose)
        {
            Add(Path.GetFileNameWithoutExtension(path), new(path, null));
            fingerprint.Add($"loose:{Relative(path)}:{new FileInfo(path).Length}");
        }
        Evidence = new(textures.SourceArchives.Count + archives.Length - numericPacks.Count, loose.Length,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", fingerprint)))), numericPacks.ToArray());
    }

    // LaunchPad includes Chromium DataPack v4 files with the same .pak suffix.
    // Validate their numeric-ID directory; they cannot supply named DDS records.
    // Format: Chromium tools/grit/grit/format/data_pack.py, ReadDataPackFromString.
    private static bool IsNumericDataPack(Stream stream)
    {
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 9 || reader.ReadUInt32() != 4) return false;
        uint count = reader.ReadUInt32();
        if (reader.ReadByte() > 2 || 9L + 6L * (count + 1L) > stream.Length) return false;
        long previousOffset = 9L + 6L * (count + 1L);
        int previousId = -1;
        for (long i = 0; i <= count; i++)
        {
            int id = reader.ReadUInt16(); long offset = reader.ReadUInt32();
            if (offset < previousOffset || offset > stream.Length
                || (i < count ? id <= previousId : id != 0 || offset != stream.Length)) return false;
            previousId = id; previousOffset = offset;
        }
        return true;
    }

    private string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private void Add(string key, Source source)
    {
        if (!sources.TryGetValue(key, out var values)) sources[key] = values = [];
        values.Add(source);
    }

    internal (byte[]? Data, string? Source) Read(string key)
    {
        if (!sources.TryGetValue(key, out var values)) return (null, null);
        byte[]? result = null;
        foreach (var source in values)
        {
            byte[] data = source.Entry == null ? File.ReadAllBytes(source.Path)
                : PakArchive.Load(File.ReadAllBytes(source.Path)).Extract(source.Entry);
            if (result != null && !result.AsSpan().SequenceEqual(data))
                throw new InvalidDataException($"Ambiguous native DDS '{key}': " +
                    string.Join(", ", values.Select(v => Relative(v.Path) + (v.Entry == null ? "" : ":" + v.Entry))));
            result = data;
        }
        // Identical copies share the bytes; their complete source identities
        // remain provenance. No filename, resolution or directory-order guess.
        return (result, string.Join(";", values.Select(v => Relative(v.Path) + (v.Entry == null ? "" : ":" + v.Entry))));
    }
}
