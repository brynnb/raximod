using System.Numerics;
using Raximod.EngineAssets.Maps;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Assets;

namespace Raximod.Generation.Continents
{
    /// <summary>
    /// Placement-specific foundation geometry used to remove terrain that a structure replaces.
    /// The reusable object mesh remains shared; only the continent's derived terrain is clipped.
    /// </summary>
    internal sealed class TerrainFoundationCutouts
    {
        private const float CellSize = 64f;
        private const float MinimumPlanarArea = 0.002f;
        private const float MaximumHeightAboveTerrain = 0.8f;
        private const float ContactEpsilon = 0.001f;

        private readonly Dictionary<(int X, int Y), List<TerrainTriangleClipper.Foundation>> cells = new();
        private readonly HashSet<int> queryIds = new();
        private readonly List<TerrainTriangleClipper.Foundation> query = new();
        private readonly Dictionary<(int, string), MutablePlacementStats> placementStats = new();
        private readonly HashSet<string> recordsWithoutGeometry = new(StringComparer.OrdinalIgnoreCase);
        private int nextFoundationId;

        private TerrainFoundationCutouts() { }

        internal sealed record PlacementStats(
            int PlacementIndex,
            string Record,
            int FoundationTriangles,
            int ClippedTerrainTriangles,
            double RemovedArea);

        internal sealed record Report(
            string Method,
            string CoordinateSystem,
            int FoundationTriangles,
            int ClippedTerrainTriangles,
            double RemovedArea,
            IReadOnlyList<PlacementStats> Placements,
            IReadOnlyList<string> RecordsWithoutGeometry);

        public static TerrainFoundationCutouts Build(
            UberModel? sharedModels,
            IReadOnlyList<(MapObject Parent, int SourceIndex)> placements,
            IProgress<string>? log = null,
            string? planetSideDirectory = null,
            CompositeObjectCatalog.Catalog? composites = null)
        {
            var result = new TerrainFoundationCutouts();
            if (sharedModels == null)
                throw new InvalidDataException("Native foundation export requires the shared uber.ubr archive.");
            var sourceCache = new Dictionary<string, IReadOnlyList<SourceTriangle>>(
                StringComparer.OrdinalIgnoreCase);
            var patchModels = new Dictionary<string, UberModel>(StringComparer.OrdinalIgnoreCase);
            var compositeLinks = composites?.Links.ToDictionary(link => link.SourceIndex);
            if (compositeLinks != null)
            {
                var missing = compositeLinks.Keys.Except(placements.Select(item => item.SourceIndex)).ToArray();
                if (missing.Length > 0)
                    throw new InvalidDataException($"Composite links lack MPO parents: {string.Join(", ", missing)}.");
            }

            UberModel.MeshSystem? Source(string record)
            {
                var system = sharedModels.FetchMeshSystem(record);
                if (system != null || planetSideDirectory == null) return system;
                string library;
                try { library = GlbExportTool.FindLibrary(planetSideDirectory, null, record); }
                catch (KeyNotFoundException) { return null; }
                // Reuse the GLB exporter's source selection, including patch libraries. Keep one
                // decoded copy per archive during this map, rather than guessing record aliases.
                if (!patchModels.TryGetValue(library, out var model))
                    patchModels[library] = model = UberModel.Load(File.ReadAllBytes(library));
                return model.FetchMeshSystem(record);
            }

            void AddPlacement(string record, Matrix4x4 transform, int placementIndex)
            {
                record = NormaliseRecord(record);
                if (!sourceCache.TryGetValue(record, out IReadOnlyList<SourceTriangle>? source))
                {
                    source = ExtractSourceTriangles(Source(record));
                    sourceCache[record] = source;
                    if (source.Count == 0) result.recordsWithoutGeometry.Add(record);
                }
                foreach (SourceTriangle triangle in source)
                {
                    Vector3 first = Vector3.Transform(triangle.A, transform);
                    Vector3 second = Vector3.Transform(triangle.B, transform);
                    Vector3 third = Vector3.Transform(triangle.C, transform);
                    // Native tower and HART skirts slope below their flat apron. Classify their
                    // actual intersection with the terrain in Clip, not their total vertical span.
                    if (PlanarArea(first, second, third) < MinimumPlanarArea) continue;
                    var foundation = new TerrainTriangleClipper.Foundation(
                        first, second, third, result.nextFoundationId++, record, placementIndex);
                    result.Add(foundation);
                    result.GetStats(placementIndex, record).FoundationTriangles++;
                }
            }

            foreach ((MapObject parent, int placementIndex) in placements)
            {
                string record = NormaliseRecord(parent.Name);
                Vector3 scale = parent.Scale == Vector3.Zero ? Vector3.One : parent.Scale;
                Matrix4x4 transform = Matrix4x4.CreateScale(scale)
                    * Matrix4x4.CreateRotationZ(parent.Yaw) * Matrix4x4.CreateTranslation(parent.Position);
                AddPlacement(record, transform, placementIndex);
                if (compositeLinks?.TryGetValue(placementIndex, out var link) == true)
                {
                    // Audit the native ordinal/type contract. An unexpected list must fail
                    // extraction rather than cut a hole under a guessed neighboring parent.
                    if (!link.Definition.Equals(record, StringComparison.OrdinalIgnoreCase)
                        && !link.Definition.StartsWith(record + "_", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"Composite {link.ObjectName}/{link.Definition} at MPO index {placementIndex} disagrees with {record}.");
                    foreach (var child in composites!.Definitions[link.Definition])
                        AddPlacement(child.Record, child.NativeMatrix * transform, placementIndex);
                }
                // Portal stairs/aprons are separate native meshes. Use their original matrices;
                // selecting only the parent silently lost coverage when retiring the runtime mask.
                var system = Source(record);
                if (system == null) continue;
                foreach (var child in system.PortalMeshItems)
                    AddPlacement(child.AssetName, child.Transform * transform, placementIndex);
            }

            log?.Report(
                $"terrain foundations: {result.placementStats.Values.Sum(value => value.FoundationTriangles)} " +
                $"triangles across {result.placementStats.Count} placements");
            return result;
        }

        private static IReadOnlyList<SourceTriangle> ExtractSourceTriangles(UberModel.MeshSystem? system)
        {
            if (system == null) return Array.Empty<SourceTriangle>();
            HighestQualityMeshSelectionReport selection = HighestQualityMeshSelector.Select(system);
            selection.ThrowIfInvalid();
            bool[] keep = selection.CreateKeepMask();
            var result = new List<SourceTriangle>();
            for (int meshIndex = 0; meshIndex < system.Meshes.Count; meshIndex++)
            {
                if (!keep[meshIndex]) continue;
                foreach (UberModel.MeshSection section in system.Meshes[meshIndex].Sections)
                foreach ((int a, int b, int c) in Triangles(section))
                {
                    Vector3 first = section.Verts[a].Position + system.WorldOffset;
                    Vector3 second = section.Verts[b].Position + system.WorldOffset;
                    Vector3 third = section.Verts[c].Position + system.WorldOffset;
                    result.Add(new SourceTriangle(first, second, third));
                }
            }
            return result;
        }

        public IReadOnlyList<IReadOnlyList<TerrainTriangleClipper.Vertex>> Clip(
            TerrainTriangleClipper.Vertex a,
            TerrainTriangleClipper.Vertex b,
            TerrainTriangleClipper.Vertex c)
        {
            Find(a.Position, b.Position, c.Position);
            if (query.Count == 0)
                return new[] { (IReadOnlyList<TerrainTriangleClipper.Vertex>)new[] { a, b, c } };

            var pieces = new List<IReadOnlyList<TerrainTriangleClipper.Vertex>> { new[] { a, b, c } };
            var affectedPlacements = new HashSet<(int, string)>();
            foreach (TerrainTriangleClipper.Foundation foundation in query)
            {
                var next = new List<IReadOnlyList<TerrainTriangleClipper.Vertex>>();
                double removed = 0;
                foreach (IReadOnlyList<TerrainTriangleClipper.Vertex> piece in pieces)
                {
                    for (int vertex = 1; vertex + 1 < piece.Count; vertex++)
                    {
                        TerrainTriangleClipper.Result result = TerrainTriangleClipper.Subtract(
                            piece[0], piece[vertex], piece[vertex + 1], foundation,
                            MaximumHeightAboveTerrain, ContactEpsilon);
                        removed += result.RemovedArea;
                        next.AddRange(result.Polygons);
                    }
                }
                if (removed > 0)
                {
                    affectedPlacements.Add((foundation.PlacementIndex, foundation.Record));
                    MutablePlacementStats stats = GetStats(foundation.PlacementIndex, foundation.Record);
                    stats.RemovedArea += removed;
                }
                pieces = next;
                if (pieces.Count == 0) break;
            }

            foreach (var placementKey in affectedPlacements)
            {
                placementStats[placementKey].ClippedTerrainTriangles++;
            }
            return pieces;
        }

        public Report CreateReport()
        {
            PlacementStats[] placements = placementStats.Values
                .Where(value => value.RemovedArea > 0)
                .OrderBy(value => value.PlacementIndex)
                .ThenBy(value => value.Record, StringComparer.Ordinal)
                .Select(value => new PlacementStats(
                    value.PlacementIndex,
                    value.Record,
                    value.FoundationTriangles,
                    value.ClippedTerrainTriangles,
                    value.RemovedArea))
                .ToArray();
            return new Report(
                "exact-placed-hq-foundation-triangle-subtraction-v2",
                "right-handed-z-up",
                placementStats.Values.Sum(value => value.FoundationTriangles),
                placements.Sum(value => value.ClippedTerrainTriangles),
                placements.Sum(value => value.RemovedArea),
                placements,
                recordsWithoutGeometry.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private void Add(TerrainTriangleClipper.Foundation foundation)
        {
            float minimumX = MathF.Min(foundation.A.X, MathF.Min(foundation.B.X, foundation.C.X));
            float maximumX = MathF.Max(foundation.A.X, MathF.Max(foundation.B.X, foundation.C.X));
            float minimumY = MathF.Min(foundation.A.Y, MathF.Min(foundation.B.Y, foundation.C.Y));
            float maximumY = MathF.Max(foundation.A.Y, MathF.Max(foundation.B.Y, foundation.C.Y));
            foreach ((int x, int y) in Cells(minimumX, minimumY, maximumX, maximumY))
            {
                if (!cells.TryGetValue((x, y), out List<TerrainTriangleClipper.Foundation>? values))
                    cells[(x, y)] = values = new List<TerrainTriangleClipper.Foundation>();
                values.Add(foundation);
            }
        }

        private void Find(Vector3 a, Vector3 b, Vector3 c)
        {
            query.Clear();
            queryIds.Clear();
            float minimumX = MathF.Min(a.X, MathF.Min(b.X, c.X));
            float maximumX = MathF.Max(a.X, MathF.Max(b.X, c.X));
            float minimumY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
            float maximumY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
            float minimumZ = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
            float maximumZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
            foreach ((int x, int y) in Cells(minimumX, minimumY, maximumX, maximumY))
            {
                if (!cells.TryGetValue((x, y), out List<TerrainTriangleClipper.Foundation>? values)) continue;
                foreach (TerrainTriangleClipper.Foundation value in values)
                {
                    if (!queryIds.Add(value.Id)) continue;
                    // Reject disjoint bounds before polygon clipping. Including sloped surfaces
                    // otherwise makes every upper wall/floor in the same 64 m cell a candidate.
                    if (MathF.Max(value.A.Z, MathF.Max(value.B.Z, value.C.Z)) < minimumZ - ContactEpsilon
                        || MathF.Min(value.A.Z, MathF.Min(value.B.Z, value.C.Z)) > maximumZ + MaximumHeightAboveTerrain
                        || MathF.Max(value.A.X, MathF.Max(value.B.X, value.C.X)) < minimumX
                        || MathF.Min(value.A.X, MathF.Min(value.B.X, value.C.X)) > maximumX
                        || MathF.Max(value.A.Y, MathF.Max(value.B.Y, value.C.Y)) < minimumY
                        || MathF.Min(value.A.Y, MathF.Min(value.B.Y, value.C.Y)) > maximumY) continue;
                    query.Add(value);
                }
            }
            query.Sort((left, right) => left.Id.CompareTo(right.Id));
        }

        private static IEnumerable<(int X, int Y)> Cells(
            float minimumX, float minimumY, float maximumX, float maximumY)
        {
            int firstX = (int)MathF.Floor(minimumX / CellSize);
            int lastX = (int)MathF.Floor(maximumX / CellSize);
            int firstY = (int)MathF.Floor(minimumY / CellSize);
            int lastY = (int)MathF.Floor(maximumY / CellSize);
            for (int y = firstY; y <= lastY; y++)
            for (int x = firstX; x <= lastX; x++)
                yield return (x, y);
        }

        private MutablePlacementStats GetStats(int placementIndex, string record)
        {
            if (!placementStats.TryGetValue((placementIndex, record), out MutablePlacementStats? value))
                placementStats[(placementIndex, record)] = value = new MutablePlacementStats(record, placementIndex);
            return value;
        }

        private static float PlanarArea(Vector3 a, Vector3 b, Vector3 c) =>
            MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) * 0.5f;

        private static IEnumerable<(int A, int B, int C)> Triangles(UberModel.MeshSection section)
        {
            ushort[] indices = section.Indices;
            if (!section.IsTriStrip)
            {
                for (int index = 0; index + 2 < indices.Length; index += 3)
                    yield return (indices[index], indices[index + 1], indices[index + 2]);
                yield break;
            }
            for (int index = 0; index + 2 < indices.Length; index++)
            {
                int a = indices[index], b = indices[index + 1], c = indices[index + 2];
                if (a == b || b == c || a == c) continue;
                yield return index % 2 == 0 ? (a, b, c) : (b, a, c);
            }
        }

        private static string NormaliseRecord(string name) =>
            name.Length > 0 && (name[0] == '@' || name[0] == '!') ? name[1..] : name;

        private sealed class MutablePlacementStats(string record, int placementIndex)
        {
            public string Record { get; } = record;
            public int PlacementIndex { get; } = placementIndex;
            public int FoundationTriangles { get; set; }
            public int ClippedTerrainTriangles { get; set; }
            public double RemovedArea { get; set; }
        }

        private readonly record struct SourceTriangle(Vector3 A, Vector3 B, Vector3 C);
    }
}
