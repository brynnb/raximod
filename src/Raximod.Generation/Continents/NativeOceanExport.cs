using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Raximod.EngineAssets.Maps;
using Raximod.EngineAssets.Meshes;
using Raximod.EngineAssets.Textures;
using Raximod.Generation.Assets;

namespace Raximod.Generation.Continents;

/// <summary>Static render derivative of MPO-admitted ocean tiles, retaining exact native triangles.</summary>
public static class NativeOceanExport
{
    // Match terrain's spatial batching granularity. This is an output partition, not water physics.
    private const int TilesPerChunk = 8;
    public sealed record Tile(string Record, int SourceIndex, int Column, int Row, UberModel.MeshSystem System);
    public sealed record MissingTile(string Record, int SourceIndex, string Reason);
    public sealed record Chunk(string Uri, int SpatialChunks, int Tiles, int Triangles);
    public sealed record Manifest(string Format, int Version, string CoordinateSystem, string SourceUri, Chunk[] Chunks);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Tile[] Resolve(string map, IReadOnlyList<uint> cells, Func<string, UberModel.MeshSystem?> resolve,
        Action<MissingTile>? reportMissing = null)
    {
        var seen = new HashSet<uint>();
        return cells.Select((id, index) =>
        {
            if (id > 1023 || !seen.Add(id)) throw new InvalidDataException($"{map}: invalid/duplicate map_water cell {id}");
            var (column, row) = MpoFile.UnpackCell(id);
            // Original executable 0x8c62a0..0x8c62c3: unpack the MPO ID, format %s_oc%02d%02d.
            string record = $"{map}_oc{column:00}{row:00}";
            var system = resolve(record);
            if (system == null)
            {
                if (reportMissing == null) throw new InvalidDataException($"Missing map_water record {record}");
                // Some shipped MPO lists reference absent UBR records (map16 has four).
                // Preserve those admissions as source gaps; never fabricate replacement water tiles.
                reportMissing(new(record, index, "MPO references a record absent from its continent UBR"));
                return null;
            }
            if (system.Skeletons.Count != 0 || system.Meshes.Any(m => m.Sections.Any(s => s.HasSkin)))
                throw new InvalidDataException($"{record}: animated/skinned ocean cannot be flattened");
            if (!Finite(system.WorldOffset)) throw new InvalidDataException($"{record}: invalid world offset");
            return new Tile(record, index, column, row, system);
        }).OfType<Tile>().ToArray();
    }

    public static UberModel.MeshSystem Combine(string name, IEnumerable<Tile> tiles)
    {
        var result = new UberModel.MeshSystem { Name = name };
        var mesh = new UberModel.Mesh { Name = name, ModelName = name };
        result.Meshes.Add(mesh);
        foreach (var tile in tiles)
        {
            // Keep the same highest-detail admission as ordinary native export before batching.
            var selection = HighestQualityMeshSelector.Select(tile.System);
            selection.ThrowIfInvalid();
            bool[] keep = selection.CreateKeepMask();
            foreach (var source in tile.System.Meshes.Where((_, index) => keep[index]).SelectMany(m => m.Sections))
            {
                if (source.Verts.Length != source.VertexCount || source.Indices.Length != source.IndexCount
                    || source.Indices.Any(i => i >= source.Verts.Length))
                    throw new InvalidDataException($"{tile.Record}: malformed ocean geometry");
                var vertices = source.Verts.Select(vertex =>
                {
                    // Sea height is already in Position.Z; never add MPO HeaderA to it again.
                    vertex.Position += tile.System.WorldOffset;
                    if (!Finite(vertex.Position)) throw new InvalidDataException($"{tile.Record}: invalid ocean vertex");
                    return vertex;
                }).ToArray();
                mesh.Sections.Add(new UberModel.MeshSection
                {
                    MaterialName = source.MaterialName, Flags = source.Flags, Id = source.Id,
                    MeshId = source.MeshId, Lod = source.Lod, Type = source.Type,
                    BbMin = source.BbMin + tile.System.WorldOffset, BbMax = source.BbMax + tile.System.WorldOffset,
                    IndexCount = source.IndexCount, Indices = source.Indices, VertexCount = source.VertexCount,
                    Verts = vertices, HasNormal = source.HasNormal, HasUv0 = source.HasUv0,
                    HasUv1 = source.HasUv1, HasColor = source.HasColor,
                });
            }
        }
        mesh.MeshSectionCount = mesh.MeshSectionCount2 = (uint)mesh.Sections.Count;
        if (mesh.Sections.Count == 0) throw new InvalidDataException($"{name}: empty ocean chunk");
        mesh.BbMin = result.BbMin = mesh.Sections.Select(s => s.BbMin).Aggregate(Vector3.Min);
        mesh.BbMax = result.BbMax = mesh.Sections.Select(s => s.BbMax).Aggregate(Vector3.Max);
        return result;
    }

    public static Manifest? Export(string planetside, string ubrPath, string outDir, CancellationToken ct = default)
    {
        var mpo = NativeContinentLiquids.ReadMap(planetside, ubrPath);
        if (mpo == null || mpo.WaterCellIds.Count == 0) return null;
        string map = Path.GetFileNameWithoutExtension(ubrPath);
        var model = UberModel.Load(File.ReadAllBytes(ubrPath));
        var records = model.Records.Select((r, i) => (r.Name, Index: i))
            .ToLookup(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var missing = new List<MissingTile>();
        var tiles = Resolve(map, mpo.WaterCellIds, record =>
        {
            var matches = records[record].ToArray();
            if (matches.Length > 1) throw new InvalidDataException($"{map}: ambiguous ocean record {record}");
            if (matches.Length == 0) return null;
            return model.FetchMeshSystemAt(matches[0].Index)
                ?? throw new InvalidDataException($"{map}: could not decode ocean record {record}: {model.RefuseReason}");
        }, missing.Add);
        foreach (var gap in missing) Console.Error.WriteLine($"warning: {gap.Record}: {gap.Reason}");
        string directory = Path.Combine(outDir, "ocean", map);
        Directory.CreateDirectory(directory);
        var textures = new TextureProvider(planetside);
        // One model request per continent, with separate cullable meshes for its spatial chunks.
        // Material/animation sidecars are shared by the whole bundle instead of repeated per tile.
        string name = map + "_ocean";
        string output = Path.Combine(directory, name + ".glb");
        var combined = new UberModel.MeshSystem { Name = name };
        foreach (var group in tiles.GroupBy(t => (Column: t.Column / TilesPerChunk, Row: t.Row / TilesPerChunk))
            .OrderBy(g => g.Key.Row).ThenBy(g => g.Key.Column))
        {
            ct.ThrowIfCancellationRequested();
            combined.Meshes.AddRange(Combine($"{name}_{group.Key.Column:00}_{group.Key.Row:00}", group).Meshes);
        }
        if (combined.Meshes.Count == 0) throw new InvalidDataException($"{map}: no resolved ocean geometry");
        combined.BbMin = combined.Meshes.Select(m => m.BbMin).Aggregate(Vector3.Min);
        combined.BbMax = combined.Meshes.Select(m => m.BbMax).Aggregate(Vector3.Max);
        var result = GlbExportTool.RunMeshSystem(new(planetside, name, output, IncludeAnimations: false),
            ubrPath, combined, textures, ct: ct);
        File.WriteAllBytes(output, SharedGlbImageExport.Convert(File.ReadAllBytes(output), output));
        Chunk[] chunks = [new($"ocean/{map}/{name}.glb", result.Meshes, tiles.Length, result.Triangles)];
        string provenance = "sources.json";
        File.WriteAllText(Path.Combine(directory, provenance), JsonSerializer.Serialize(new
        {
            format = "raxicore-native-ocean-sources", version = 1,
            library = Path.GetRelativePath(planetside, ubrPath).Replace('\\', '/'),
            sourceSection = "map_water", sourceMpo = $"contents_{map}.mpo",
            admittedCells = mpo.WaterCellIds.Count, missing,
            adaptation = "Static authored ocean tiles; not a reconstruction of the later retail procedural ocean renderer.",
            tiles = tiles.Select(t => new { t.Record, t.SourceIndex, t.Column, t.Row,
                position = new[] { t.System.WorldOffset.X, 0, -t.System.WorldOffset.Y } }),
        }, Json));
        return new("raxicore-native-ocean", 1, "right-handed-y-up", $"ocean/{map}/{provenance}", chunks);
    }

    public static int Refresh(string planetside, string outDir, IEnumerable<string> paths, string? onlyMap,
        IProgress<string> log, CancellationToken ct = default)
    {
        int count = 0;
        foreach (string path in paths.Order(StringComparer.Ordinal))
        {
            string map = Path.GetFileNameWithoutExtension(path);
            if (onlyMap != null && !map.Equals(onlyMap, StringComparison.OrdinalIgnoreCase)) continue;
            string manifest = Path.Combine(outDir, map + ".json");
            // Discovery includes source test maps; only refresh already exported continents.
            if (!File.Exists(manifest)) continue;
            var document = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
            if (document["base"]?.GetValue<string>() != map || document["coordinateSystem"]?.GetValue<string>() != "right-handed-y-up")
                throw new InvalidDataException($"Mismatched continent manifest {manifest}");
            var ocean = Export(planetside, path, outDir, ct);
            document["ocean"] = JsonSerializer.SerializeToNode(ocean, Json);
            File.WriteAllText(manifest, document.ToJsonString());
            log.Report($"{map}: {ocean?.Chunks.Length ?? 0} native ocean chunks");
            count++;
        }
        return count;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
