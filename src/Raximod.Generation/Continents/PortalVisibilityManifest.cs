using System.Numerics;
using System.Text.Json;
using Raximod.EngineAssets.Maps;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Continents
{
    /// <summary>
    /// Serializes the complete retained native portal graph without interpreting unknown flags.
    /// Definitions are shared by record; placed instances carry a browser-space matrix. Consumers
    /// must fail open whenever <c>complete</c> is false or an expected definition is absent.
    /// </summary>
    public static class PortalVisibilityManifest
    {
        public static string Write(
            string outDir,
            string baseName,
            IEnumerable<(MapObject Parent, int SourceIndex)> parents,
            UberModel? models)
        {
            var definitionUris = new List<string>();
            var instances = new List<object>();
            var diagnostics = new List<string>();
            string definitionsDir = Path.Combine(outDir, "portal-visibility");
            Directory.CreateDirectory(definitionsDir);
            foreach (IGrouping<string, (MapObject Parent, int SourceIndex)> group in
                parents.GroupBy(value => value.Parent.Name, StringComparer.OrdinalIgnoreCase))
            {
                UberModel.MeshSystem? system = models?.FetchMeshSystem(group.Key);
                PortalVisibilityData? visibility = system?.PortalVisibility;
                if (visibility == null || !visibility.Present) continue;
                object definition = Definition(group.Key, visibility, out string[] failures);
                diagnostics.AddRange(failures.Select(failure => $"{group.Key}: {failure}"));
                string safeRecord = SafeRecord(group.Key);
                string relativeUri = $"portal-visibility/{safeRecord}.json";
                definitionUris.Add(relativeUri);
                File.WriteAllText(
                    Path.Combine(definitionsDir, safeRecord + ".json"),
                    JsonSerializer.Serialize(new
                    {
                        format = "raxicore-portal-visibility-definition",
                        version = 1,
                        coordinateSystem = "right-handed-y-up-local",
                        definition,
                    }));
                foreach ((MapObject parent, int sourceIndex) in group)
                    AddInstance(instances, diagnostics, parent, sourceIndex);
            }
            string indexFile = baseName + ".portal-visibility.json";
            File.WriteAllText(
                Path.Combine(outDir, indexFile),
                JsonSerializer.Serialize(new
                {
                    format = "raxicore-portal-visibility",
                    version = 2,
                    coordinateSystem = "right-handed-y-up-local",
                    policy = "conservative-fail-open",
                    complete = diagnostics.Count == 0,
                    definitionUris = definitionUris.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    instances = instances.ToArray(),
                    diagnostics = diagnostics.ToArray(),
                }));
            return indexFile;
        }

        public static object Build(
            IEnumerable<(MapObject Parent, int SourceIndex)> parents,
            UberModel? models)
        {
            var definitions = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var instances = new List<object>();
            var diagnostics = new List<string>();
            foreach ((MapObject parent, int sourceIndex) in parents)
            {
                UberModel.MeshSystem? system = models?.FetchMeshSystem(parent.Name);
                PortalVisibilityData? visibility = system?.PortalVisibility;
                if (visibility == null || !visibility.Present) continue;
                if (!definitions.ContainsKey(parent.Name))
                {
                    object definition = Definition(parent.Name, visibility, out string[] failures);
                    definitions.Add(parent.Name, definition);
                    diagnostics.AddRange(failures.Select(failure => $"{parent.Name}: {failure}"));
                }
                AddInstance(instances, diagnostics, parent, sourceIndex);
            }
            return new
            {
                format = "raxicore-portal-visibility",
                version = 1,
                coordinateSystem = "right-handed-y-up-local",
                policy = "conservative-fail-open",
                complete = diagnostics.Count == 0,
                definitions = definitions.Values.ToArray(),
                instances = instances.ToArray(),
                diagnostics = diagnostics.ToArray(),
            };
        }

        private static void AddInstance(
            List<object> instances,
            List<string> diagnostics,
            MapObject parent,
            int sourceIndex)
        {
            Matrix4x4 browserMatrix = ParentBrowserMatrix(parent);
            if (!Matrix4x4.Decompose(browserMatrix, out Vector3 scale, out Quaternion rotation,
                out Vector3 position))
            {
                diagnostics.Add($"{parent.Name}[{sourceIndex}]: placement matrix is not decomposable");
                return;
            }
            instances.Add(new
            {
                record = parent.Name,
                parentIndex = sourceIndex,
                position = Array3(position),
                rotation = new[] { rotation.X, rotation.Y, rotation.Z, rotation.W },
                scale = Array3(scale),
                matrix = Matrix(browserMatrix),
            });
        }

        private static string SafeRecord(string record)
        {
            char[] value = record.ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) || character is '_' or '-'
                    ? character
                    : '_')
                .ToArray();
            return new string(value);
        }

        public static object Definition(
            string record,
            PortalVisibilityData visibility,
            out string[] failures)
        {
            var diagnostics = new List<string>();
            ValidateAabv("meshItems", visibility.MeshItemAabv, diagnostics);
            ValidateAabv("rawIds", visibility.RawIdAabv, diagnostics);
            for (int i = 0; i < visibility.RegionSpatialBindings.Count; i++)
                ValidateAabv($"regionBindings[{i}]", visibility.RegionSpatialBindings[i].Aabv, diagnostics);
            if (!visibility.Present) diagnostics.Add("portal-system presence marker is zero");
            failures = diagnostics.ToArray();
            return new
            {
                record,
                complete = diagnostics.Count == 0,
                presenceMarker = visibility.PresenceMarker,
                bounds = Bounds(visibility.BoundsMinimum, visibility.BoundsMaximum),
                meshDescriptors = visibility.MeshDescriptors.Select(value => new
                {
                    name = value.Name,
                    rawA = value.RawA,
                    rawB = value.RawB,
                }).ToArray(),
                exteriorPortals = visibility.ExteriorPortals.Select(Portal).ToArray(),
                regions = visibility.Regions.Select(region => new
                {
                    name = region.Name,
                    id = region.Id,
                    bounds = Bounds(region.BoundsMinimum, region.BoundsMaximum),
                    portals = region.Portals.Select(Portal).ToArray(),
                    convexHulls = region.ConvexHulls.Select(hull => new
                    {
                        rawA = hull.RawA,
                        bounds = Bounds(hull.BoundsMinimum, hull.BoundsMaximum),
                        planes = hull.Planes.Select(Plane).ToArray(),
                        vertices = hull.Vertices.Select(Vector).ToArray(),
                    }).ToArray(),
                    rawLinks = region.RawLinks,
                    substructures = region.Substructures.Select(Substructure).ToArray(),
                }).ToArray(),
                names = visibility.Names.ToArray(),
                meshItems = visibility.MeshItems.Select(item => new
                {
                    rawA = item.A,
                    index = item.Index,
                    id = item.Id,
                    flags = item.Flags,
                    regionA = item.RegionA,
                    regionB = item.RegionB,
                    instance = item.InstanceName,
                    record = item.AssetName,
                    meshIndices = item.MeshIndices,
                    sourceMatrix = Matrix(item.Transform),
                }).ToArray(),
                meshItemAabvMarker = visibility.MeshItemAabvMarker,
                meshItemAabv = Aabv(visibility.MeshItemAabv),
                regionSpatialBindings = visibility.RegionSpatialBindings.Select(binding => new
                {
                    portalId = binding.PortalId,
                    hasAabvMarker = binding.HasAabvMarker,
                    aabv = Aabv(binding.Aabv),
                }).ToArray(),
                rawIds = visibility.RawIds,
                rawIdAabvMarker = visibility.RawIdAabvMarker,
                rawIdAabv = Aabv(visibility.RawIdAabv),
                trailingSubstructures = visibility.TrailingSubstructures.Select(Substructure).ToArray(),
            };
        }

        private static object Portal(PortalVisibilityPortal portal) => new
        {
            rawFlags = portal.RawFlags,
            regionA = portal.RegionA,
            regionB = portal.RegionB,
            points = portal.Points.Select(Vector).ToArray(),
            plane = Plane(portal.Plane),
            meshItemId = portal.MeshItemId,
        };

        private static object Plane(PortalVisibilityPlane plane) => new
        {
            normal = Vector(plane.Normal),
            distance = plane.Distance,
            epsilon = plane.Epsilon,
        };

        private static object Substructure(PortalVisibilityRegionSubstructure value) => new
        {
            rawA = value.RawA,
            points = value.Points.Select(Vector).ToArray(),
        };

        private static object? Aabv(PortalVisibilityAabv? value) => value == null ? null : new
        {
            declaredNodeCount = value.DeclaredNodeCount,
            decodedNodeCount = value.DecodedNodeCount,
            nodeCountMatches = value.NodeCountMatches,
            root = AabvNode(value.Root),
        };

        private static object? AabvNode(PortalVisibilityAabvNode? node) => node == null ? null : new
        {
            flags = node.Flags,
            bounds = Bounds(node.BoundsMinimum, node.BoundsMaximum),
            leafIndices = node.LeafIndices,
            child10 = AabvNode(node.Child10),
            child20 = AabvNode(node.Child20),
        };

        private static void ValidateAabv(string name, PortalVisibilityAabv? value, List<string> failures)
        {
            if (value != null && !value.NodeCountMatches)
                failures.Add($"{name} AABV declares {value.DeclaredNodeCount} nodes but decoded {value.DecodedNodeCount}");
        }

        private static object Bounds(Vector3 minimum, Vector3 maximum)
        {
            Vector3 browserA = BrowserVector(minimum);
            Vector3 browserB = BrowserVector(maximum);
            return new
            {
                minimum = Array3(Vector3.Min(browserA, browserB)),
                maximum = Array3(Vector3.Max(browserA, browserB)),
            };
        }

        private static float[] Vector(Vector3 value) =>
            new[] { value.X, value.Z, -value.Y };

        private static float[] Array3(Vector3 value) =>
            new[] { value.X, value.Y, value.Z };

        private static Vector3 BrowserVector(Vector3 value) =>
            new(value.X, value.Z, -value.Y);

        private static Matrix4x4 ParentBrowserMatrix(MapObject parent)
        {
            Vector3 scale = parent.Scale == Vector3.Zero ? Vector3.One : parent.Scale;
            Matrix4x4 native =
                Matrix4x4.CreateScale(scale) *
                Matrix4x4.CreateRotationZ(parent.Yaw) *
                Matrix4x4.CreateTranslation(parent.Position);
            Matrix4x4 basis = Matrix4x4.CreateRotationX(-MathF.PI / 2f);
            if (!Matrix4x4.Invert(basis, out Matrix4x4 inverse))
                throw new InvalidOperationException("PlanetSide browser coordinate basis is not invertible.");
            return inverse * native * basis;
        }

        private static float[] Matrix(Matrix4x4 value) => new[]
        {
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44,
        };
    }
}
