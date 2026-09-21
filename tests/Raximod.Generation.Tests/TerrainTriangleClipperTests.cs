using System.Numerics;
using Raximod.Generation.Continents;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class TerrainTriangleClipperTests
{
    private static readonly Vector3 Up = Vector3.UnitZ;

    [Fact]
    public void FoundationSubtractionSplitsTerrainWithoutMovingItsPlane()
    {
        var foundation = new TerrainTriangleClipper.Foundation(
            new Vector3(2, 2, 0.05f),
            new Vector3(8, 2, 0.05f),
            new Vector3(2, 8, 0.05f),
            1,
            "tower_b",
            17);

        TerrainTriangleClipper.Result result = TerrainTriangleClipper.Subtract(
            new TerrainTriangleClipper.Vertex(new Vector3(0, 0, 0), Up),
            new TerrainTriangleClipper.Vertex(new Vector3(10, 0, 0), Up),
            new TerrainTriangleClipper.Vertex(new Vector3(0, 10, 0), Up),
            foundation,
            maximumHeightAboveTerrain: 0.8f,
            contactEpsilon: 0.001f);

        Assert.True(result.Changed);
        Assert.Equal(18f, result.RemovedArea, 3);
        Assert.All(result.Polygons.SelectMany(polygon => polygon), vertex =>
        {
            Assert.Equal(0f, vertex.Position.Z, 5);
            Assert.Equal(Up, vertex.Normal);
        });
        Assert.Equal(32f, Area(result.Polygons), 3);
    }

    [Fact]
    public void ElevatedUpperFloorDoesNotCutTerrain()
    {
        var foundation = new TerrainTriangleClipper.Foundation(
            new Vector3(2, 2, 3),
            new Vector3(8, 2, 3),
            new Vector3(2, 8, 3),
            2,
            "upper_floor",
            4);

        TerrainTriangleClipper.Result result = TerrainTriangleClipper.Subtract(
            new TerrainTriangleClipper.Vertex(new Vector3(0, 0, 0), Up),
            new TerrainTriangleClipper.Vertex(new Vector3(10, 0, 0), Up),
            new TerrainTriangleClipper.Vertex(new Vector3(0, 10, 0), Up),
            foundation,
            maximumHeightAboveTerrain: 0.8f,
            contactEpsilon: 0.001f);

        Assert.False(result.Changed);
        Assert.Single(result.Polygons);
        Assert.Equal(50f, Area(result.Polygons), 3);
    }

    [Fact]
    public void FoundationBelowSlopedTerrainOnlyCutsAtTheirIntersection()
    {
        var foundation = new TerrainTriangleClipper.Foundation(
            new Vector3(0, 0, 0),
            new Vector3(10, 0, 0),
            new Vector3(0, 10, 0),
            3,
            "foundation",
            1);

        TerrainTriangleClipper.Result result = TerrainTriangleClipper.Subtract(
            new TerrainTriangleClipper.Vertex(new Vector3(0, 0, -0.5f), Up),
            new TerrainTriangleClipper.Vertex(new Vector3(10, 0, 0.5f), Up),
            new TerrainTriangleClipper.Vertex(new Vector3(0, 10, -0.5f), Up),
            foundation,
            maximumHeightAboveTerrain: 0.8f,
            contactEpsilon: 0);

        Assert.True(result.Changed);
        Assert.Equal(37.5f, result.RemovedArea, 3);
        Assert.Equal(12.5f, Area(result.Polygons), 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8192)]
    public void MillimetreSkirtContactSurvivesContinentCoordinates(float offset)
    {
        // Tower/HART skirts slope below the ground immediately outside their flat apron.
        // This contact strip must survive translation; world-space float area sums lost it.
        Vector3 At(float x, float y, float z) => new(offset + x, offset + y, z);
        var foundation = new TerrainTriangleClipper.Foundation(
            At(0, 0, 0), At(8, 0, 0), At(0, 8, -2), 1, "skirt", 0);
        var result = TerrainTriangleClipper.Subtract(
            new(At(0, 0, 0), Up), new(At(8, 0, 0), Up), new(At(0, 8, 0), Up),
            foundation, 0.8f, 0.001f);

        Assert.True(result.Changed);
        Assert.InRange(result.RemovedArea, 0.0319, 0.0321);
        Assert.All(result.Polygons.SelectMany(polygon => polygon), vertex =>
        {
            Assert.Equal(0, vertex.Position.Z);
            Assert.Equal(Up, vertex.Normal);
            Assert.InRange(vertex.Position.X, offset, offset + 8);
            Assert.InRange(vertex.Position.Y, offset, offset + 8);
        });
        Assert.InRange(Area(result.Polygons), 31.96, 31.98);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SlopingFoundationClipsOnlyContactAndKeepsWinding(bool reverse)
    {
        var vertices = new[] { new Vector3(0, 0, 0), new Vector3(8, 0, 0), new Vector3(0, 8, 0) };
        if (reverse) Array.Reverse(vertices);
        var foundation = new TerrainTriangleClipper.Foundation(
            new(0, 0, -2), new(8, 0, 2), new(0, 8, -2), 1, "skirt", 0);
        var result = TerrainTriangleClipper.Subtract(
            new(vertices[0], Up), new(vertices[1], Up), new(vertices[2], Up),
            foundation, 0.8f, 0);

        Assert.Equal(5.12, result.RemovedArea, 4);
        Assert.Equal(26.88, Area(result.Polygons), 4);
        foreach (var polygon in result.Polygons)
        {
            var normal = Vector3.Cross(polygon[1].Position - polygon[0].Position,
                polygon[2].Position - polygon[0].Position);
            Assert.True(reverse ? normal.Z < 0 : normal.Z > 0);
        }
    }

    private static double Area(IReadOnlyList<IReadOnlyList<TerrainTriangleClipper.Vertex>> polygons)
    {
        double result = 0;
        foreach (IReadOnlyList<TerrainTriangleClipper.Vertex> polygon in polygons)
        {
            double sum = 0;
            for (int index = 0; index < polygon.Count; index++)
            {
                Vector3 a = polygon[index].Position;
                Vector3 b = polygon[(index + 1) % polygon.Count].Position;
                sum += (double)a.X * b.Y - (double)a.Y * b.X;
            }
            result += Math.Abs(sum) * 0.5;
        }
        return result;
    }
}
