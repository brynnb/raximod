using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;

namespace Raximod.Generation.Assets;

/// <summary>Retail outfit IDs and their complete material definitions, independent of faction skins.</summary>
public static class OutfitDecalExport
{
    public sealed record Variant(int Id, string? Faction, string Material);
    private static readonly string[] Factions = ["tr", "nc", "vs"];
    private static readonly Regex MaterialName = new(@"^oi_decal([0-9]+)(?:_(tr|nc|vs))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static IReadOnlyList<Variant> Discover(IEnumerable<string> names)
    {
        // Original 0x69f9e0 enumerates oi_decal materials and parses oi_decal%u_%2s.
        // The UI datasource contains prototype inventory icons, not the live catalog.
        var variants = new List<Variant>();
        foreach (string name in names.Where(name => name.StartsWith("oi_decal", StringComparison.OrdinalIgnoreCase)))
        {
            Match match = MaterialName.Match(name);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out int id) || id <= 0)
                throw new InvalidDataException($"Unsupported native outfit material name: {name}");
            string? faction = match.Groups[2].Success ? match.Groups[2].Value.ToLowerInvariant() : null;
            if (variants.Any(value => value.Id == id && value.Faction == faction))
                throw new InvalidDataException($"Ambiguous native outfit material: {name} (ID {id}, faction {faction ?? "shared"})");
            variants.Add(new(id, faction, name));
        }
        if (variants.Count == 0) throw new InvalidDataException("No native oi_decal materials found");
        return variants.OrderBy(value => value.Id).ThenBy(value => value.Faction, StringComparer.Ordinal).ToArray();
    }

    public static string Resolve(IReadOnlyList<Variant> variants, int id, string faction)
    {
        if (!Factions.Contains(faction)) throw new ArgumentException($"Invalid decal faction: {faction}");
        // Original 0x69be50 first probes the faction suffix, then the authored shared material.
        return (variants.SingleOrDefault(value => value.Id == id && value.Faction == faction)
            ?? variants.SingleOrDefault(value => value.Id == id && value.Faction == null))?.Material
            ?? throw new InvalidDataException($"No native outfit material for ID {id}/{faction}");
    }

    public static void Run(string planetside, string output)
    {
        var textures = new TextureProvider(planetside);
        var database = textures.MaterialCommands ?? throw new InvalidDataException("materials.adb is unavailable");
        var variants = Discover(database.Records.Keys);
        var rawRecords = database.Raw.IndexedRecords.ToDictionary(record => record.IndexEntry.Name, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(output);
        string Flag(string material)
        {
            string name = "flag" + material[2..];
            if (database.Lookup(name) is not { Count: > 0 })
                throw new InvalidDataException($"Missing native outfit flag material: {name}");
            return name;
        }
        string[] names = variants.SelectMany(value => new[] { value.Material, Flag(value.Material) })
            .Order(StringComparer.Ordinal).ToArray();
        // Supply explicit definitions; never infer faction textures inside an outfit material.
        var materials = JsonSerializer.SerializeToElement(NativeMaterialManifestTool.BuildDocument(output,
            "outfit-decals", names.Select(name => new NativeMaterialManifestTool.Usage(name, 0, true, true)).ToArray(),
            textures, includePackageBindings: false));
        if (materials.GetProperty("missingTextures").GetArrayLength() != 0)
            throw new InvalidDataException($"Outfit decal textures are unresolved: {materials.GetProperty("missingTextures")}");
        var definitions = materials.GetProperty("materials").EnumerateArray()
            .ToDictionary(value => value.GetProperty("name").GetString()!, StringComparer.Ordinal);
        object Binding(int id, string faction)
        {
            string name = Resolve(variants, id, faction);
            var stages = definitions[name].GetProperty("stages");
            // The original picker draws the material. Only simplify a verified static,
            // single-texture material to an HTML image; never guess a preview stage.
            bool disabled(JsonElement stage) => stage.GetProperty("commands").EnumerateArray().Any(command =>
                command.GetProperty("name").GetString() == "sc_colorop"
                && command.GetProperty("arguments").GetArrayLength() == 1
                && command.GetProperty("arguments")[0].GetString() == "disable");
            if (stages.GetArrayLength() == 0 || stages[0].GetProperty("resolvedProgram").GetString() != "default0"
                || !stages.EnumerateArray().Skip(1).All(disabled)
                || stages[0].GetProperty("textureKind").GetString() != "2d"
                || stages[0].GetProperty("animation").ValueKind != JsonValueKind.Null
                || stages[0].GetProperty("textureUri").ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Outfit preview requires material rendering: {name}");
            return new { material = name, flagMaterial = Flag(name), previewTextureUri = stages[0].GetProperty("textureUri").GetString() };
        }
        var document = new
        {
            format = "planetside-outfit-decals", version = 1, none = 0,
            selection = new { minimumOutfitPoints = 10000, leaderOnly = true,
                source = "startup.pak/english.str", records = new[] { "HelpHUDOutfitText", "HelpOutfitCreateText" } },
            bindings = new { meshMaterial = "decal" },
            provenance = new
            {
                sourceDatabase = "materials.adb",
                sha256 = Convert.ToHexString(SHA256.HashData(database.Raw.Encode())).ToLowerInvariant(),
                executableSha256 = "7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a",
                enumeration = "0x69f9e0", resolution = "0x69be50",
                records = names.Select(name => rawRecords.TryGetValue(name, out var record) ? new
                    { name, nameIndex = record.IndexEntry.Index, streamStart = record.StreamStart, streamEnd = record.StreamEnd }
                    : throw new InvalidDataException($"Missing raw outfit material provenance: {name}")).ToArray()
            },
            entries = variants.Select(value => value.Id).Distinct().Select(id => new
                { id, factions = Factions.ToDictionary(faction => faction, faction => Binding(id, faction)) }).ToArray(),
            materials = materials.GetProperty("materials")
        };
        string json = JsonSerializer.Serialize(document);
        string path = Path.Combine(output, "outfit-decals.json");
        if (!File.Exists(path) || File.ReadAllText(path) != json) File.WriteAllText(path, json);
        Console.WriteLine($"Outfit decals: {variants.Select(value => value.Id).Distinct().Count()} IDs, {variants.Count} variants, {names.Length} complete materials -> {path}");
    }
}
