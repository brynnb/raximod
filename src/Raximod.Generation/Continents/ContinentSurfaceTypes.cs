using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Surfaces;

namespace Raximod.Generation.Continents
{
    /// <summary>
    /// Coarse majority-voted native surface names for gameplay/map consumers, separate from
    /// simplified biome classes. Procedural foliage uses GroundcoverSurfaceExport instead:
    /// downsampling here changes boundaries and is not exact distribution.lst placement data.
    /// </summary>
    public sealed class ContinentSurfaceTypes
    {
        public int N { get; private init; }
        public string[] Names { get; private init; } = Array.Empty<string>();
        /// <summary>Native type byte per output cell. Zero means no named surface; type N maps to Names[N-1].</summary>
        public byte[] Cells { get; private init; } = Array.Empty<byte>();

        public static ContinentSurfaceTypes? Build(string srfPakPath, string baseName, int n, int worldSize)
        {
            PakArchive pak;
            try { pak = PakArchive.Load(File.ReadAllBytes(srfPakPath)); }
            catch { return null; }

            string[] names = ContinentRoads.ReadTypeNames(pak, baseName);
            int cellSize = worldSize / n;
            if (names.Length == 0 || cellSize <= 0) return null;

            // Vote rather than point-sample so narrow native patches do not alias unpredictably when the
            // 4096-square source grid is reduced to the browser's bounded 512-square control grid.
            var votes = new ushort[n * n * 256];
            foreach (PakEntry entry in pak.Entries)
            {
                if (!entry.Name.EndsWith(".srf", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Equals(baseName + ".srf", StringComparison.OrdinalIgnoreCase)) continue;
                string stem = entry.Name[..^4];
                if (stem.Length < 9
                    || !int.TryParse(stem.AsSpan(5, 2), out int column)
                    || !int.TryParse(stem.AsSpan(7, 2), out int row)) continue;
                SurfaceTile tile;
                try { tile = SurfaceTile.Parse(pak.Extract(entry.Name)); }
                catch { continue; }
                if (!tile.IsFull) continue;

                for (int sourceRow = 0; sourceRow < SurfaceTile.GridDim; sourceRow++)
                {
                    int targetRow = Math.Min(n - 1, (row * 256 + sourceRow * 2) / cellSize);
                    for (int sourceColumn = 0; sourceColumn < SurfaceTile.GridDim; sourceColumn++)
                    {
                        int targetColumn = Math.Min(n - 1, (column * 256 + sourceColumn * 2) / cellSize);
                        byte type = tile.GetCell(sourceRow, sourceColumn).Type;
                        int vote = (targetRow * n + targetColumn) * 256 + type;
                        if (votes[vote] < ushort.MaxValue) votes[vote]++;
                    }
                }
            }

            var cells = new byte[n * n];
            bool any = false;
            for (int index = 0; index < cells.Length; index++)
            {
                int bestType = 0;
                int bestVotes = 0;
                for (int type = 1; type <= Math.Min(255, names.Length); type++)
                {
                    int count = votes[index * 256 + type];
                    if (count <= bestVotes) continue;
                    bestVotes = count;
                    bestType = type;
                }
                cells[index] = (byte)bestType;
                any |= bestType != 0;
            }
            return any ? new ContinentSurfaceTypes { N = n, Names = names, Cells = cells } : null;
        }
    }
}
