using System.Security.Cryptography;
using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Assets;

namespace Raximod.Generation.Continents;

/// <summary>Cavern terrain query faces; independent of render chunks and movement collision.</summary>
public static class NativeCavernQueries
{
    public static object Build(string zone, string library, UberModel model, AsciiCommandDatabase materials)
    {
        var tiles = new List<object>();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        // Original 0x8c5e40-0x8c5f37 requests %s_%02d%02d for each 32x32
        // cell, admits existing records to world+0x28 with their native XY offset.
        // No liquid-only section filter: cavern mask 11 queries the whole world.
        for (int row = 0; row < 32; row++)
        for (int column = 0; column < 32; column++)
        {
            string record = $"{zone}_{column:00}{row:00}";
            var matches = model.Records.Select((item, index) => (item, index))
                .Where(pair => pair.item.Name == record).ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length != 1) throw new InvalidDataException($"Ambiguous cavern tile {library}:{record}");
            var system = model.FetchMeshSystemAt(matches[0].index)
                ?? throw new InvalidDataException($"Missing cavern tile {library}:{record}");
            var offset = system.WorldOffset;
            if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y))
                throw new InvalidDataException($"Invalid cavern tile offset {library}:{record}");
            object aab = NativeSpatialAuditTool.BuildAabDocument(record, system)
                ?? throw new InvalidDataException($"Cavern query tile has no native AAB: {library}:{record}");
            foreach (var section in system.Meshes.SelectMany(mesh => mesh.Sections)) names.Add(section.MaterialName);
            tiles.Add(new { record, position = new[] { offset.X, 0, -offset.Y }, aab });
        }
        if (tiles.Count == 0) throw new InvalidDataException($"No native cavern terrain tiles in {library}");
        // ADB overrides are optional: 0x9bd14e initializes surface 0 before
        // command lookup (including cavern_pillars_ul02+_warpgate_cave_ul02).
        // Preserve empty commands; never manufacture a wall/liquid override.
        object Commands(string name) => (materials.Lookup(name) ?? [])
            .Select(command => new { name = command.Name, arguments = command.Arguments }).ToArray();
        return new
        {
            format = "raxicore-cavern-terrain-queries", version = 1,
            coordinateSystem = "right-handed-y-up-local", zone, library,
            source = "native_aab", tiles,
            materials = new { materials = names.Select(name => new
            {
                name, sectionCommands = Commands(name), baseCommands = Commands(name.Split('+')[0]),
            }).ToArray() },
            evidence = new[] { "0x8c5aea", "0x8c5e40-0x8c5f37", "0x86185d-0x861888", "0x86397b", "0x9ab6a1-0x9ab96e" },
        };
    }

    public static string? Export(string planetside, string ubrPath, string output)
    {
        string zone = Path.GetFileNameWithoutExtension(ubrPath);
        // This is the original mode prefix, not a material-name heuristic.
        if (!zone.StartsWith("ugd", StringComparison.Ordinal)) return null;
        byte[] bytes = File.ReadAllBytes(ubrPath);
        var model = UberModel.Load(bytes);
        var materials = AsciiCommandDatabase.TryLoad(planetside, "materials.adb")
            ?? throw new InvalidDataException("Missing native materials.adb for cavern queries");
        string library = Path.GetRelativePath(planetside, ubrPath).Replace('\\', '/');
        var data = JsonSerializer.SerializeToNode(Build(zone, library, model, materials))!.AsObject();
        data["sourceSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string uri = $"terrain-query/{zone}.json", path = Path.Combine(output, uri);
        string text = data.ToJsonString();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
        return uri;
    }
}
