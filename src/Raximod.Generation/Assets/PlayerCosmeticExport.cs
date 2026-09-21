using System.Text.Json;
using System.Text.RegularExpressions;

namespace Raximod.Generation.Assets;

/// <summary>Retail Patch 5 head accessories, separate from body/animation exports.</summary>
public static class PlayerCosmeticExport
{
    public sealed record Entry(string Id, string Kind, string Gender, string? Faction,
        string Record, string Uri, string Library, int Triangles, int Bones);

    public static IReadOnlyList<(string Record, string Kind, string Gender, string? Faction)> Discover(IEnumerable<string> records)
    {
        var accessory = new Regex(@"^(beret|hat|shades|earpiece)_([fm])_(nc|tr|vs)$", RegexOptions.IgnoreCase);
        var head = new Regex(@"^female_headhat_([a-e])$", RegexOptions.IgnoreCase);
        var selected = new List<(string Record, string Kind, string Gender, string? Faction)>();
        foreach (string record in records.Order(StringComparer.OrdinalIgnoreCase))
        {
            Match match = accessory.Match(record);
            if (match.Success)
                selected.Add((record, match.Groups[1].Value.ToLowerInvariant(),
                    match.Groups[2].Value.Equals("f", StringComparison.OrdinalIgnoreCase) ? "female" : "male",
                    match.Groups[3].Value.ToLowerInvariant()));
            else if (head.IsMatch(record)) selected.Add((record, "hat-head", "female", null));
        }
        // A source change needs review, not an invisible accessory or a guessed alias.
        foreach (string kind in new[] { "beret", "hat", "shades", "earpiece" })
        foreach (string gender in new[] { "female", "male" })
        foreach (string faction in new[] { "nc", "tr", "vs" })
            if (selected.Count(entry => entry.Kind == kind && entry.Gender == gender && entry.Faction == faction) != 1)
                throw new InvalidDataException($"Expected one native {kind}/{gender}/{faction} cosmetic record");
        foreach (char face in "abcde")
            if (selected.Count(entry => entry.Record.Equals($"female_headhat_{face}", StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidDataException($"Expected one native female hat-head {face}");
        return selected;
    }

    public static IReadOnlyList<Entry> Run(string planetside, string output, IProgress<string>? log = null)
    {
        const string library = "patch5/patch5.ubr";
        var records = Discover(GlbExportTool.ListRecords(Path.Combine(planetside, library)));
        var entries = new List<Entry>();
        foreach (var source in records)
        {
            string id = source.Record.ToLowerInvariant();
            string uri = $"cosmetics/{id}.glb";
            var result = GlbExportTool.Run(new GlbExportTool.Options(planetside, source.Record,
                Path.Combine(output, uri), LibraryPath: Path.Combine(planetside, library),
                IncludeAnimations: false, PreserveRigidBoneAttachments: true,
                SharedTextureDirectory: Path.Combine(output, "material-textures")), log);
            if (result.Triangles == 0) throw new InvalidDataException($"Cosmetic {source.Record} has no geometry");
            entries.Add(new Entry(id, source.Kind, source.Gender, source.Faction, source.Record,
                uri, library, result.Triangles, result.Bones));
        }
        var manifest = new { format = "planetside-player-cosmetics", version = 1,
            coordinateSystem = "right-handed-y-up", source = library, entries };
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "cosmetics.json");
        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        if (!File.Exists(path) || File.ReadAllText(path) != json) File.WriteAllText(path, json);
        return entries;
    }
}
