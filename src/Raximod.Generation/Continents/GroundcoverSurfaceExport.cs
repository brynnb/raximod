using System.Text.Json;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Surfaces;

namespace Raximod.Generation.Continents;

/// <summary>Exact two-metre SRF type selection, separate from the coarse map/gameplay grid.</summary>
public static class GroundcoverSurfaceExport
{
    public sealed record SurfaceMap(string Continent, string Uri, int Size, int CellSize,
        string[] Names, string SourceArchive, int FullTiles, int SparseTiles);

    public static SurfaceMap[] Export(string planetside, string output, IProgress<string> log)
    {
        var maps = new List<SurfaceMap>();
        string directory = Path.Combine(output, "groundcover-surfaces");
        Directory.CreateDirectory(directory);
        foreach (string path in Directory.GetFiles(output, "map??.json").Order(StringComparer.Ordinal))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(path));
            string name = Path.GetFileNameWithoutExtension(path);
            // Battle-island SRFs ship beside their UBR in patchmap/mapNN, not
            // at the install root. Match ContinentExportTool's archive roots.
            string[] candidates = [name + "_srf.pak", $"patchmap/{name}/{name}_srf.pak"];
            string[] present = candidates.Where(p => File.Exists(Path.Combine(planetside, p))).ToArray();
            if (!manifest.RootElement.TryGetProperty("surfaceTypeN", out var types) || types.GetInt32() == 0)
                continue;
            if (present.Length != 1)
                throw new InvalidDataException($"Expected one SRF archive for {name}, found {present.Length}");
            string archive = present[0];
            int size = manifest.RootElement.GetProperty("worldSize").GetInt32() / 2;
            if (size <= 0 || size > 4096) throw new InvalidDataException($"Unexpected surface dimensions: {name}");
            var pak = PakArchive.Load(File.ReadAllBytes(Path.Combine(planetside, archive)));
            var names = ContinentRoads.ReadTypeNames(pak, name);
            var cells = new byte[size * size];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int full = 0, sparse = 0;
            foreach (var entry in pak.Entries)
            {
                if (!entry.Name.EndsWith(".srf", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Equals(name + ".srf", StringComparison.OrdinalIgnoreCase)) continue;
                string stem = entry.Name[..^4];
                if (stem.Length != 9 || !stem.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(stem.AsSpan(5, 2), out int x) || !int.TryParse(stem.AsSpan(7, 2), out int y)
                    || !seen.Add(stem) || x * 128 >= size || y * 128 >= size)
                    throw new InvalidDataException($"Invalid/duplicate SRF tile: {archive}/{entry.Name}");
                var tile = SurfaceTile.Parse(pak.Extract(entry.Name));
                // Sparse tiles carry no full cell grid; preserve no named cover, never fill from neighbours.
                if (!tile.IsFull) { sparse++; continue; }
                full++;
                for (int row = 0; row < 128; row++)
                    for (int column = 0; column < 128; column++)
                    {
                        byte type = tile.GetCell(row, column).Type;
                        if (type > names.Length) throw new InvalidDataException($"Unknown SRF type {type}: {entry.Name}");
                        cells[(y * 128 + row) * size + x * 128 + column] = type;
                    }
            }
            string uri = $"groundcover-surfaces/{name}.bin";
            byte[] encoded = Encode(cells, size);
            string destination = Path.Combine(output, uri);
            if (!File.Exists(destination) || !File.ReadAllBytes(destination).AsSpan().SequenceEqual(encoded))
                File.WriteAllBytes(destination, encoded);
            maps.Add(new(name, uri, size, 2, names, archive, full, sparse));
            log.Report($"groundcover surfaces: {name}, {size}x{size}, {full} full / {sparse} sparse source tiles");
        }
        return maps.ToArray();
    }

    // GCS1, uint32 side, then uint16 run length + uint8 type. Runs include type zero.
    // This avoids PNG colour conversion and preserves boundaries without a continent-sized JSON array.
    public static byte[] Encode(byte[] cells, int size)
    {
        if (size <= 0 || size > 4096 || cells.Length != size * size) throw new ArgumentException("Invalid surface grid");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("GCS1"u8);
        writer.Write(size);
        for (int start = 0; start < cells.Length;)
        {
            int end = start + 1;
            while (end < cells.Length && end - start < ushort.MaxValue && cells[end] == cells[start]) end++;
            writer.Write((ushort)(end - start));
            writer.Write(cells[start]);
            start = end;
        }
        return stream.ToArray();
    }
}
