using System.Globalization;
using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;

namespace Raximod.Generation.Assets;

/// <summary>Native terrain detail identity and UV scale, alongside the stitched colour atlas.</summary>
public static class TerrainMaterialExportTool
{
    public sealed record Detail(string Texture, float TileRate);

    public static Detail ReadDetail(string map, IReadOnlyList<AsciiCommandDatabase.Command> commands)
    {
        string Scalar(string name)
        {
            var matches = commands.Where(command => command.Name == name).ToArray();
            if (matches.Length != 1 || matches[0].Arguments.Count != 1)
                throw new InvalidDataException($"Terrain {map}: expected one scalar {name}.");
            return matches[0].Arguments[0];
        }

        string texture = Scalar("mat_detail");
        if (texture.Length == 0 || texture.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new InvalidDataException($"Terrain {map}: invalid mat_detail '{texture}'.");
        if (!float.TryParse(Scalar("mat_tilerate"), NumberStyles.Float, CultureInfo.InvariantCulture,
            out float tileRate) || !float.IsFinite(tileRate) || tileRate <= 0)
            throw new InvalidDataException($"Terrain {map}: mat_tilerate must be finite and positive.");
        return new Detail(texture, tileRate);
    }

    public static string Export(TerrainAtlasExportTool.Result atlas, TextureProvider textures)
    {
        var commands = textures.MaterialCommands?.Lookup(atlas.Map)
            ?? throw new InvalidDataException($"Missing materials.adb terrain record {atlas.Map}.");
        Detail detail = ReadDetail(atlas.Map, commands);
        DdsImage image = textures.Get(detail.Texture)
            ?? throw new InvalidDataException($"Terrain {atlas.Map}: missing native texture {detail.Texture}.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(atlas.Output))!;
        // Reuse the established raw-detail output, not a second copy of every image.
        string detailUri = $"../terrain-textures/{atlas.Map}.png";
        Directory.CreateDirectory(Path.Combine(directory, "../terrain-textures"));
        File.WriteAllBytes(Path.Combine(directory, detailUri), PngEncoder.EncodeBgra(image.Bgra, image.Width, image.Height));
        string output = Path.ChangeExtension(atlas.Output, ".material.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            format = "planetside-terrain-material", version = 1, continent = atlas.Map,
            macro = new { uri = Path.GetFileName(atlas.Output), atlas.TilesWide, atlas.TilesHigh,
                atlas.TileWidth, atlas.TileHeight },
            detail = new { uri = detailUri, texture = detail.Texture, tileRate = detail.TileRate },
            // Preserve the complete record and referenced stages; the browser's close-detail
            // adapter does not claim to reconstruct retail medium-distance pass switching.
            provenance = new { materialDatabase = "startup.pak/materials.adb", material = atlas.Map,
                atlasArchive = Path.GetFileName(atlas.Source), sectionCommands = commands,
                stages = commands.Where(command => command.Name.StartsWith("mat_stage", StringComparison.Ordinal)
                    && command.Arguments.Count == 1 && command.Arguments[0] != "disable")
                    .Select(command => new { program = command.Arguments[0],
                        commands = textures.StageCommands?.Lookup(command.Arguments[0]) }).ToArray() }
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return output;
    }
}
