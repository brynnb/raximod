using System.Numerics;
using System.Text.Json;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Continents;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class PortalVisibilityManifestTests
    {
        [Fact]
        public void CompleteDefinitionRetainsPortalGeometryConnectivityAndSpatialData()
        {
            var visibility = new PortalVisibilityData
            {
                PresenceMarker = 1,
                BoundsMinimum = new Vector3(-2f, -3f, -4f),
                BoundsMaximum = new Vector3(5f, 6f, 7f),
                MeshItemAabvMarker = 1,
                MeshItemAabv = new PortalVisibilityAabv
                {
                    DeclaredNodeCount = 1,
                    DecodedNodeCount = 1,
                    Root = new PortalVisibilityAabvNode
                    {
                        Flags = 1,
                        BoundsMinimum = Vector3.Zero,
                        BoundsMaximum = Vector3.One,
                        LeafIndices = new ushort[] { 4, 9 },
                    },
                },
            };
            visibility.ExteriorPortals.Add(new PortalVisibilityPortal
            {
                RawFlags = 3,
                RegionA = 10,
                RegionB = uint.MaxValue,
                MeshItemId = 7,
                Points = new[]
                {
                    new Vector3(1f, 2f, 3f),
                    new Vector3(1f, 4f, 3f),
                    new Vector3(1f, 4f, 5f),
                    new Vector3(1f, 2f, 5f),
                },
                Plane = new PortalVisibilityPlane
                {
                    Normal = Vector3.UnitX,
                    Distance = 1f,
                    Epsilon = 0.01f,
                },
            });
            var region = new PortalVisibilityRegion
            {
                Name = "room",
                Id = 10,
                BoundsMinimum = Vector3.Zero,
                BoundsMaximum = new Vector3(4f),
                RawLinks = new uint[] { 7 },
            };
            region.ConvexHulls.Add(new PortalVisibilityConvexHull
            {
                BoundsMinimum = Vector3.Zero,
                BoundsMaximum = new Vector3(4f),
                Vertices = new[] { Vector3.Zero, Vector3.One },
            });
            visibility.Regions.Add(region);

            object definition = PortalVisibilityManifest.Definition("facility", visibility, out string[] failures);
            using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(definition));

            Assert.Empty(failures);
            Assert.True(json.RootElement.GetProperty("complete").GetBoolean());
            Assert.Equal(10u,
                json.RootElement.GetProperty("exteriorPortals")[0].GetProperty("regionA").GetUInt32());
            Assert.Equal(new[] { 1f, 3f, -2f },
                json.RootElement.GetProperty("exteriorPortals")[0].GetProperty("points")[0]
                    .EnumerateArray().Select(value => value.GetSingle()).ToArray());
            Assert.Equal(new[] { -2f, -4f, -6f },
                json.RootElement.GetProperty("bounds").GetProperty("minimum")
                    .EnumerateArray().Select(value => value.GetSingle()).ToArray());
            Assert.Equal(new[] { 5f, 7f, 3f },
                json.RootElement.GetProperty("bounds").GetProperty("maximum")
                    .EnumerateArray().Select(value => value.GetSingle()).ToArray());
            Assert.Equal(new ushort[] { 4, 9 },
                json.RootElement.GetProperty("meshItemAabv").GetProperty("root")
                    .GetProperty("leafIndices").EnumerateArray()
                    .Select(value => value.GetUInt16()).ToArray());
            Assert.Equal("room", json.RootElement.GetProperty("regions")[0].GetProperty("name").GetString());
        }

        [Fact]
        public void MismatchedNativeSpatialTreeMakesDefinitionFailOpen()
        {
            var visibility = new PortalVisibilityData
            {
                PresenceMarker = 1,
                MeshItemAabv = new PortalVisibilityAabv
                {
                    DeclaredNodeCount = 2,
                    DecodedNodeCount = 1,
                },
            };

            object definition = PortalVisibilityManifest.Definition("facility", visibility, out string[] failures);
            using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(definition));

            Assert.Single(failures);
            Assert.False(json.RootElement.GetProperty("complete").GetBoolean());
        }
    }
}
