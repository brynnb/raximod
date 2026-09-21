using System.Numerics;
using System.Text.RegularExpressions;
using Raximod.EngineAssets.Meshes;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Raximod.Generation.Continents
{
    using TerrainMesh = MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;
    using TerrainVertex = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

    /// <summary>Exports the reference client's exact terrain TIN in spatially cullable GLB chunks.</summary>
    internal static class NativeTerrainChunkExportTool
    {
        // 8x8 native tiles produce sixteen 2048-unit chunks per standard
        // continent: few enough HTTP requests while still giving Babylon
        // useful terrain-level frustum culling.
        private const int TilesPerChunk = 8;
        private static readonly Regex TileName = new(
            @"^((?:map|ugd)\d{2})_?(\d{2})(\d{2})$", RegexOptions.IgnoreCase);

        internal sealed record Chunk(string Uri, int Column, int Row, int Tiles, int Triangles);
        internal sealed record Export(
            IReadOnlyList<Chunk> Chunks,
            TerrainFoundationCutouts.Report FoundationCutouts);

        public static Export Run(
            string ubrPath,
            string baseName,
            int worldSize,
            string outputRoot,
            TerrainFoundationCutouts foundations,
            IProgress<string>? log = null)
        {
            var model = UberModel.Load(File.ReadAllBytes(ubrPath));
            var records = model.Records
                .Select((record, index) => (record.Name, Index: index, Match: TileName.Match(record.Name)))
                .Where(entry => entry.Match.Success
                    && entry.Match.Groups[1].Value.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                .Select(entry => (
                    entry.Name,
                    entry.Index,
                    Column: int.Parse(entry.Match.Groups[2].Value),
                    Row: int.Parse(entry.Match.Groups[3].Value)))
                .GroupBy(entry => (entry.Column / TilesPerChunk, entry.Row / TilesPerChunk))
                .OrderBy(group => group.Key.Item2)
                .ThenBy(group => group.Key.Item1);

            string continentOutput = Path.Combine(outputRoot, baseName);
            Directory.CreateDirectory(continentOutput);
            var chunks = new List<Chunk>();
            var material = new MaterialBuilder(baseName + "_native_terrain")
                .WithMetallicRoughnessShader()
                .WithMetallicRoughness(0, 1)
                .WithAlpha(AlphaMode.OPAQUE);
            // Exact axis permutation. cos(float PI/2) leaves a residual that turns world northing
            // into a sub-millimetre height error beside locally transformed building foundations.
            var zUpToYUp = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);

            foreach (var group in records)
            {
                int chunkColumn = group.Key.Item1;
                int chunkRow = group.Key.Item2;
                var mesh = new TerrainMesh($"{baseName}_terrain_{chunkColumn:00}_{chunkRow:00}");
                var primitive = mesh.UsePrimitive(material);
                int triangles = 0;
                foreach (var tile in group)
                {
                    UberModel.MeshSystem? system = model.FetchMeshSystemAt(tile.Index);
                    if (system == null) continue;
                    Vector3 offset = system.WorldOffset;
                    foreach (UberModel.Mesh sourceMesh in system.Meshes)
                    foreach (UberModel.MeshSection section in sourceMesh.Sections)
                    {
                        if (section.VertexCount == 0 || section.IndexCount < 3
                            || section.MaterialName.Equals("ocean_floor", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach ((int a, int b, int c) in Triangles(section))
                        {
                            Vector3 fallback = FaceNormal(
                                section.Verts[a].Position,
                                section.Verts[b].Position,
                                section.Verts[c].Position);
                            TerrainTriangleClipper.Vertex first = SourceVertex(section.Verts[a], offset, fallback);
                            TerrainTriangleClipper.Vertex second = SourceVertex(section.Verts[b], offset, fallback);
                            TerrainTriangleClipper.Vertex third = SourceVertex(section.Verts[c], offset, fallback);
                            foreach (IReadOnlyList<TerrainTriangleClipper.Vertex> polygon in
                                     foundations.Clip(first, second, third))
                            {
                                for (int vertex = 1; vertex + 1 < polygon.Count; vertex++)
                                {
                                    primitive.AddTriangle(
                                        Vertex(polygon[0], worldSize),
                                        Vertex(polygon[vertex], worldSize),
                                        Vertex(polygon[vertex + 1], worldSize));
                                    triangles++;
                                }
                            }
                        }
                    }
                }
                if (triangles == 0) continue;
                var scene = new SceneBuilder(mesh.Name);
                scene.AddRigidMesh(mesh, zUpToYUp);
                string filename = $"{baseName}_{chunkColumn:00}_{chunkRow:00}.glb";
                string output = Path.Combine(continentOutput, filename);
                string temporary = output + $".raximod-{Guid.NewGuid():N}.tmp";
                try
                {
                    scene.ToGltf2().SaveGLB(temporary);
                    File.Move(temporary, output, true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                chunks.Add(new Chunk(
                    $"terrain-native/{baseName}/{filename}",
                    chunkColumn,
                    chunkRow,
                    group.Count(),
                    triangles));
            }
            log?.Report($"{baseName}: exported {chunks.Count} native terrain chunks");
            return new Export(chunks, foundations.CreateReport());
        }

        private static TerrainTriangleClipper.Vertex SourceVertex(
            UberModel.UberVert vertex,
            Vector3 offset,
            Vector3 fallback)
        {
            Vector3 position = vertex.Position + offset;
            Vector3 normal = vertex.Normal.LengthSquared() > 1e-8f
                ? Vector3.Normalize(vertex.Normal)
                : fallback;
            return new TerrainTriangleClipper.Vertex(position, normal);
        }

        private static TerrainVertex Vertex(TerrainTriangleClipper.Vertex vertex, int worldSize)
        {
            Vector3 position = vertex.Position;
            var colorUv = new VertexColor1Texture1(
                Vector4.One,
                new Vector2(position.X / worldSize, 1 - position.Y / worldSize));
            return new TerrainVertex(new VertexPositionNormal(position, vertex.Normal), colorUv);
        }

        private static Vector3 FaceNormal(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            return normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ;
        }

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
    }
}
