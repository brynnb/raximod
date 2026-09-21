using System.Collections.Concurrent;
using System.Security.Cryptography;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Maps;

namespace Raximod.Generation.Continents;

/// <summary>Native weather birth heights, separate from rendered terrain and movement collision.</summary>
public static class WeatherTerrainMapExport
{
    public sealed record Definition(string uri, int size, int cellSize,
        string sourceArchive, string sourceRecord, string sourceSha256, int sourceBytes);

    public static SortedDictionary<string, Definition> Export(string planetside, string output, IEnumerable<string> zones)
    {
        var generated = new ConcurrentDictionary<string, (Definition Definition, byte[] Bytes)>(StringComparer.Ordinal);
        var maps = zones.Where(z => z.StartsWith("map", StringComparison.Ordinal)).Distinct().Order().ToArray();
        var groups = maps.GroupBy(map => File.Exists(Path.Combine(planetside, $"patchmap/{map}/{map}_resources.pak"))
            ? $"patchmap/{map}/{map}_resources.pak" : "maps/map_resources.pak");
        foreach (var group in groups)
        {
            var archive = PakArchive.Load(File.ReadAllBytes(Path.Combine(planetside, group.Key)));
            Parallel.ForEach(group, new ParallelOptions { MaxDegreeOfParallelism = 4 }, map =>
            {
                string record = map + ".trn";
                if (archive.Entries.Count(e => e.Name == record) != 1)
                    throw new InvalidDataException($"Expected one native weather map: {group.Key}:{record}");
                byte[] raw = archive.Extract(record), encoded = Encode(TerrainMap.Parse(raw));
                string uri = $"environment/weather/terrain/{map}.bin";
                generated[map] = (new(uri, TerrainMap.Size, TerrainMap.CellSize, group.Key, record,
                    Convert.ToHexStringLower(SHA256.HashData(raw)), raw.Length), encoded);
            });
        }
        // Validate the entire source set before replacing any published payload.
        Directory.CreateDirectory(Path.Combine(output, "environment/weather/terrain"));
        var result = new SortedDictionary<string, Definition>(StringComparer.Ordinal);
        foreach (var (map, item) in generated.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string path = Path.Combine(output, item.Definition.uri);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(item.Bytes)) File.WriteAllBytes(path, item.Bytes);
            result.Add(map, item.Definition);
        }
        return result;
    }

    // Same count-prefixed run convention as groundcover grids, with uint16
    // values retaining all source flags. TRN1, uint32 side, (uint16 count,value).
    // Avoid image colour conversion and preserve the exact native cells.
    public static byte[] Encode(TerrainMap map)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("TRN1"u8); writer.Write(TerrainMap.Size);
        for (int start = 0; start < map.Cells.Length;)
        {
            int end = start + 1;
            while (end < map.Cells.Length && end - start < ushort.MaxValue && map.Cells[end] == map.Cells[start]) end++;
            writer.Write((ushort)(end - start)); writer.Write(map.Cells[start]); start = end;
        }
        return stream.ToArray();
    }
}
