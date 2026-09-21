using System.Text.RegularExpressions;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Textures;

namespace Raximod.Generation.Assets
{
    /// <summary>Builds a continent-scale diffuse map from PlanetSide's tiled mapXX_dxt1 FLAT archive.</summary>
    public static partial class TerrainAtlasExportTool
    {
        public sealed record Result(string Map, string Source, string Output, int TilesWide,
            int TilesHigh, int TileWidth, int TileHeight, int TileCount);

        public static Result Export(string fatPath, string outputPath)
        {
            string archiveName = Path.GetFileNameWithoutExtension(fatPath);
            Match archiveMatch = ArchiveName().Match(archiveName);
            if (!archiveMatch.Success)
            {
                throw new ArgumentException($"Expected a mapXX_dxt1.fat archive, got {fatPath}.");
            }

            string map = archiveMatch.Groups[1].Value.ToLowerInvariant();
            FlatArchive archive = FlatArchive.Load(File.ReadAllBytes(fatPath));
            var tiles = new List<(int X, int Y, DdsImage Image)>();
            var coordinates = new HashSet<(int X, int Y)>();
            int maxX = -1;
            int maxY = -1;
            int tileWidth = 0;
            int tileHeight = 0;

            foreach (FlatEntry entry in archive.Entries)
            {
                Match tileMatch = TileName().Match(entry.Name);
                if (!tileMatch.Success || !tileMatch.Groups[1].Value.Equals(map, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int x = int.Parse(tileMatch.Groups[2].Value);
                int y = int.Parse(tileMatch.Groups[3].Value);
                if (!coordinates.Add((x, y)))
                    throw new InvalidDataException($"Duplicate terrain tile coordinate {map}/{x}/{y}.");
                DdsImage image = DdsImage.Decode(archive.Extract(entry.Name));
                if (tileWidth == 0)
                {
                    tileWidth = image.Width;
                    tileHeight = image.Height;
                }
                else if (image.Width != tileWidth || image.Height != tileHeight)
                {
                    throw new InvalidDataException($"Terrain tile {entry.Name} is {image.Width}x{image.Height}; expected {tileWidth}x{tileHeight}.");
                }

                tiles.Add((x, y, image));
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            if (tiles.Count == 0)
            {
                throw new InvalidDataException($"No {map} terrain DDS tiles were found in {fatPath}.");
            }

            int tilesWide = maxX + 1;
            int tilesHigh = maxY + 1;
            if (tiles.Count != checked(tilesWide * tilesHigh))
            {
                throw new InvalidDataException($"{fatPath} contains {tiles.Count} terrain tiles but its {tilesWide}x{tilesHigh} grid requires {tilesWide * tilesHigh}.");
            }

            int atlasWidth = checked(tilesWide * tileWidth);
            int atlasHeight = checked(tilesHigh * tileHeight);
            var atlas = new byte[checked(atlasWidth * atlasHeight * 4)];
            foreach ((int x, int y, DdsImage image) in tiles)
            {
                // PlanetSide's tile Y increases southward. PNG rows increase downward, but Babylon
                // uploads with invertY=false, so placing Y=0 at the bottom makes UV v=1 address the
                // continent's north edge. This is the vertical flip required by the original tools.
                for (int sourceY = 0; sourceY < tileHeight; sourceY++)
                {
                    int sourceOffset = sourceY * tileWidth * 4;
                    int sourceAtlasY = y * tileHeight + sourceY;
                    int destinationY = atlasHeight - 1 - sourceAtlasY;
                    int destinationOffset = (destinationY * atlasWidth + x * tileWidth) * 4;
                    Buffer.BlockCopy(image.Bgra, sourceOffset, atlas, destinationOffset, tileWidth * 4);
                }
            }

            string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (outputDirectory != null) Directory.CreateDirectory(outputDirectory);
            File.WriteAllBytes(outputPath, PngEncoder.EncodeBgra(atlas, atlasWidth, atlasHeight));
            return new Result(map, fatPath, outputPath, tilesWide, tilesHigh, tileWidth, tileHeight, tiles.Count);
        }

        public static string FindArchive(string planetSideRoot, string map)
        {
            string wanted = $"{map}_dxt1.fat";
            string[] matches = Directory.EnumerateFiles(planetSideRoot, wanted, SearchOption.AllDirectories).ToArray();
            return matches.Length switch
            {
                1 => matches[0],
                0 => throw new FileNotFoundException($"Could not find {wanted} beneath {planetSideRoot}."),
                _ => throw new InvalidOperationException($"Found multiple {wanted} archives: {string.Join(", ", matches)}"),
            };
        }

        [GeneratedRegex(@"^(map\d{2})_dxt1$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ArchiveName();

        [GeneratedRegex(@"^(map\d{2})(\d{2})(\d{2})_@\.dds$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex TileName();
    }
}
