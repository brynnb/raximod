using System.Text.Json;
using System.Text.Json.Nodes;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Maps;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Continents;

/// <summary>Admits named lakes from the MPO, not water-looking records found in an archive.</summary>
public static class NativeContinentLiquids
{
    public sealed record Placement(
        string record, string uri, string library, string layer,
        float[] position, float[] rotation, float[] scale, string sourceSection, int sourceIndex);

    public static Placement[] Build(
        IReadOnlyList<string> records, string library, Func<string, UberModel.MeshSystem?> resolve)
    {
        return records.Select((record, index) =>
        {
            var system = resolve(record)
                ?? throw new InvalidDataException($"Missing map_lakes record {library}:{record}");
            var offset = system.WorldOffset;
            if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y))
                throw new InvalidDataException($"Non-finite map_lakes offset {library}:{record}");
            // Retail 0x8c63bf-0x8c64f8 obtains X/Y from 0x9707c0/0x9d87e0 and
            // uses unit scale and zero rotation. GLBs stay local; convert once here.
            return new Placement(record, $"assets/{record}.glb", library, "liquid",
                [offset.X, 0, -offset.Y], [0, 0, 0, 1], [1, 1, 1], "map_lakes", index);
        }).ToArray();
    }

    public static Placement[] Read(string planetside, string ubrPath)
    {
        var records = ReadMap(planetside, ubrPath)?.LakeRecords ?? [];
        if (records.Count == 0) return [];
        var model = UberModel.Load(File.ReadAllBytes(ubrPath));
        string library = Path.GetRelativePath(planetside, ubrPath).Replace('\\', '/');
        return Build(records, library, record =>
        {
            int[] matches = model.Records.Select((value, index) => (value, index))
                .Where(pair => pair.value.Name.Equals(record, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.index).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException($"Expected one map_lakes record {library}:{record}; found {matches.Length}");
            return model.FetchMeshSystemAt(matches[0]);
        });
    }

    internal static MpoFile? ReadMap(string planetside, string ubrPath)
    {
        string map = Path.GetFileNameWithoutExtension(ubrPath);
        string resources = Path.Combine(Path.GetDirectoryName(ubrPath)!, map + "_resources.pak");
        if (!File.Exists(resources)) resources = Path.Combine(planetside, "maps", "map_resources.pak");
        var pack = PakArchive.Load(File.ReadAllBytes(resources));
        int entry = pack.IndexOf($"contents_{map}.mpo");
        // Cavern terrain is admitted by a different path; no MPO lake list exists there.
        if (entry < 0)
        {
            if (map.StartsWith("ugd", StringComparison.Ordinal)) return null;
            throw new InvalidDataException($"Missing contents_{map}.mpo in {resources}");
        }
        return MpoFile.Parse(pack.Extract(entry));
    }

    /// <summary>Refresh just the discovered placements, preserving existing terrain and other metadata.</summary>
    public static int Refresh(
        string planetside, string outDir, IEnumerable<string> ubrPaths, string? onlyMap,
        IProgress<string> log, CancellationToken ct = default)
    {
        int count = 0;
        foreach (string path in ubrPaths.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string map = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (onlyMap != null && !map.Equals(onlyMap, StringComparison.OrdinalIgnoreCase)) continue;
            string manifest = Path.Combine(outDir, map + ".json");
            var document = JsonNode.Parse(File.ReadAllText(manifest))?.AsObject()
                ?? throw new InvalidDataException($"Invalid continent manifest {manifest}");
            if (document["base"]?.GetValue<string>() != map
                || document["coordinateSystem"]?.GetValue<string>() != "right-handed-y-up")
                throw new InvalidDataException($"Mismatched continent manifest {manifest}");
            var liquids = Read(planetside, path);
            string? terrainQueries = NativeCavernQueries.Export(planetside, path, outDir);
            var node = JsonSerializer.SerializeToNode(liquids);
            if (!JsonNode.DeepEquals(document["liquids"], node)
                || document["nativeTerrainQueries"]?.GetValue<string>() != terrainQueries)
            {
                document["liquids"] = node;
                if (terrainQueries != null) document["nativeTerrainQueries"] = terrainQueries;
                File.WriteAllText(manifest, document.ToJsonString());
            }
            count++;
            log.Report($"{map}: {liquids.Length} native MPO lake placements");
        }
        return count;
    }
}
