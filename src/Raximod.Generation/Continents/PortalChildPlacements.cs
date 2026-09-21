using System.Numerics;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Raximod.EngineAssets.Maps;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Continents
{
    /// <summary>
    /// Converts every model instance embedded in a facility portal system into the browser coordinate
    /// system without interpreting its flags as render policy. Client composition data is authoritative
    /// for transforms; a downstream reconciler can attach server identity and state.
    /// </summary>
    internal static class PortalChildPlacements
    {
        internal sealed record Placement(
            [property: JsonPropertyName("record")] string Record,
            [property: JsonPropertyName("uri")] string Uri,
            [property: JsonPropertyName("position")] float[] Position,
            [property: JsonPropertyName("rotation")] float[] Rotation,
            [property: JsonPropertyName("scale")] float[] Scale,
            [property: JsonPropertyName("layer")] string Layer,
            [property: JsonPropertyName("parentRecord")] string ParentRecord,
            [property: JsonPropertyName("parentIndex")] int ParentIndex,
            [property: JsonPropertyName("childIndex")] int ChildIndex,
            [property: JsonPropertyName("instance")] string Instance,
            [property: JsonPropertyName("flags")] uint Flags,
            [property: JsonPropertyName("sourceA")] uint SourceA,
            [property: JsonPropertyName("sourceIndex")] uint SourceIndex,
            [property: JsonPropertyName("sourceId")] uint SourceId,
            [property: JsonPropertyName("regionA")] uint RegionA,
            [property: JsonPropertyName("regionB")] uint RegionB,
            [property: JsonPropertyName("meshIndices")] uint[] MeshIndices,
            [property: JsonPropertyName("sourceMatrix")] float[] SourceMatrix);

        // Groundcover is a placement source, not a guarantee that the model is
        // a leaf. Preserve its native portal children and give its graph a
        // distinct identity after the original MPO index range.
        internal static (MapObject Parent, int SourceIndex)[] Parents(
            MpoFile? mpo, IReadOnlyList<ExactObject> groundcover)
        {
            var map = mpo?.Objects.Select((parent, index) => (Parent: parent, SourceIndex: index))
                ?? Enumerable.Empty<(MapObject Parent, int SourceIndex)>();
            int offset = mpo?.Objects.Count ?? 0;
            return map.Concat(groundcover.Select((item, index) => (
                    Parent: new MapObject(-1, ContinentExportTool.NormaliseObjectRecord(item.Name),
                        item.Position, item.Scale, item.Yaw), SourceIndex: offset + index)))
                .Where(item => !string.IsNullOrWhiteSpace(item.Parent.Name)
                    && ContinentExportTool.IsRenderableSceneRecord(
                        ContinentExportTool.NormaliseObjectRecord(item.Parent.Name)))
                .ToArray();
        }

        internal static int Refresh(string planetside, string output, IEnumerable<string> archives,
            string? selected, UberModel? models, IProgress<string> log)
        {
            if (models == null) throw new InvalidDataException("Native portal models are required");
            int count = 0;
            foreach (string archive in archives)
            {
                string name = Path.GetFileNameWithoutExtension(archive).ToLowerInvariant();
                if (selected != null && !name.Equals(selected, StringComparison.OrdinalIgnoreCase)) continue;
                string path = Path.Combine(output, name + ".json");
                if (!File.Exists(path)) continue;
                string own = Path.Combine(Path.GetDirectoryName(archive)!, name + "_resources.pak");
                string resources = File.Exists(own) ? own : Path.Combine(planetside, "maps", "map_resources.pak");
                var mpo = ContinentExportTool.LoadMpo(resources, name, log);
                var parents = Parents(mpo, ContinentExportTool.LoadGroundcover(planetside, name, log));
                var children = parents.SelectMany(item => Build(item.Parent, item.SourceIndex, models)).ToArray();
                var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                document["portalChildren"] = JsonSerializer.SerializeToNode(children);
                document["portalVisibilityUri"] = PortalVisibilityManifest.Write(output, name, parents, models);
                File.WriteAllText(path, document.ToJsonString());
                log.Report($"{name}: {children.Length} native portal children");
                count++;
            }
            return count;
        }

        public static IReadOnlyList<Placement> Build(MapObject parent, int parentIndex, UberModel? models)
        {
            UberModel.MeshSystem? system = models?.FetchMeshSystem(parent.Name);
            if (system == null || system.PortalMeshItems.Count == 0)
            {
                return Array.Empty<Placement>();
            }

            Vector3 parentScale = parent.Scale == Vector3.Zero ? Vector3.One : parent.Scale;
            Matrix4x4 parentNative =
                Matrix4x4.CreateScale(parentScale) *
                Matrix4x4.CreateRotationZ(parent.Yaw) *
                Matrix4x4.CreateTranslation(parent.Position);
            Matrix4x4 basis = Matrix4x4.CreateRotationX(-MathF.PI / 2f);
            if (!Matrix4x4.Invert(basis, out Matrix4x4 basisInverse))
            {
                throw new InvalidOperationException("PlanetSide browser coordinate basis is not invertible.");
            }

            var placements = new List<Placement>(system.PortalMeshItems.Count);
            for (int childIndex = 0; childIndex < system.PortalMeshItems.Count; childIndex++)
            {
                UberModel.PortalMeshItem child = system.PortalMeshItems[childIndex];
                Matrix4x4 worldBrowser = basisInverse * (child.Transform * parentNative) * basis;
                if (!Matrix4x4.Decompose(
                    worldBrowser,
                    out Vector3 scale,
                    out Quaternion rotation,
                    out Vector3 position))
                {
                    throw new InvalidDataException(
                        $"Portal child {parent.Name}[{parentIndex}]/{child.InstanceName}[{childIndex}] has a non-decomposable transform.");
                }

                placements.Add(new Placement(
                    child.AssetName,
                    $"assets/{child.AssetName}.glb",
                    Vector(position),
                    Quaternion(rotation),
                    Vector(scale),
                    "portal-child",
                    parent.Name,
                    parentIndex,
                    childIndex,
                    child.InstanceName,
                    child.Flags,
                    child.A,
                    child.Index,
                    child.Id,
                    child.RegionA,
                    child.RegionB,
                    child.MeshIndices.ToArray(),
                    Matrix(child.Transform)));
            }
            return placements;
        }

        private static float[] Vector(Vector3 value) => new[] { value.X, value.Y, value.Z };

        private static float[] Quaternion(Quaternion value) =>
            new[] { value.X, value.Y, value.Z, value.W };

        // System.Numerics uses row-vector affine matrices; preserve that exact M11..M44 ordering in
        // addition to the consumer-ready decomposed transform so no source information is lost.
        private static float[] Matrix(Matrix4x4 value) => new[]
        {
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44
        };
    }
}
