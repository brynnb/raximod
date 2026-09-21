using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Maps;

namespace Raximod.Generation.Continents;

/// <summary>Publishes the original coordinate lookup, byte-identically, after a full
/// structural audit. No procedural terrain/navmesh substitute is generated.</summary>
public static class DropPodLocationExport
{
    public sealed record Entry(string Map, string Uri, int Extent, int CellSize, int Side,
        int Cells, int AccountedBytes, string Sha256, string SourceArchive, string SourceEntry);

    public static Entry[] Export(string planetside, string output)
    {
        const string sourceArchive = "maps/map_resources.pak";
        var archive = PakArchive.Load(File.ReadAllBytes(Path.Combine(planetside, sourceArchive)));
        var selected = archive.Entries.Where(e => Regex.IsMatch(e.Name, @"^map\d+\.droppod$"))
            .OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) throw new InvalidDataException($"No drop location tables in {sourceArchive}");
        if (selected.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new InvalidDataException("Ambiguous drop location archive entries");
        var pending = selected.Select(source =>
        {
            byte[] bytes = archive.Extract(source.Name);
            var table = DropPodLocationTable.Parse(bytes);
            if (!bytes.AsSpan().SequenceEqual(table.Encode()))
                throw new InvalidDataException($"Drop location round-trip failed: {source.Name}");
            // A destination is a fixed point in the installed corpus. Revalidation
            // must not relocate an already resolved preview to a different cell.
            for (int y = 0; y < table.Side; y++)
            for (int x = 0; x < table.Side; x++)
            {
                var destination = table.Cell(x, y);
                if (table.Lookup(destination) != destination)
                    throw new InvalidDataException($"{source.Name}: landing at cell {x},{y} is not stable under revalidation");
            }
            string map = Path.GetFileNameWithoutExtension(source.Name);
            return (Bytes: bytes, Entry: new Entry(map, $"drop-locations/{map}.bin", table.Extent,
                table.CellSize, table.Side, table.CellCount, table.AccountedBytes,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), sourceArchive, source.Name));
        }).ToArray();
        // Validate the entire small corpus before publishing any consumer or binary.
        foreach (var item in pending) WriteIfChanged(Path.Combine(output, item.Entry.Uri), item.Bytes);
        return pending.Select(item => item.Entry).ToArray();
    }

    internal static void WriteJson(string path, object value) => WriteIfChanged(path,
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        }) + "\n"));

    private static void WriteIfChanged(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
