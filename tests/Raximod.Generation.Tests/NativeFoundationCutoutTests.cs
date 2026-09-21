using System.Numerics;
using System.Text.RegularExpressions;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Maps;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Assets;
using Raximod.Generation.Continents;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeFoundationCutoutTests
{
    [Fact]
    public void NativeCompositeLinkOrdinalsMatchTheirMpoParentTypesAcrossTheInstalledMaps()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR")
            ?? "/home/brynn/Downloads/PlanetSide";
        string sharedPath = Path.Combine(client, "maps", "map_resources.pak");
        if (!File.Exists(sharedPath)) return;
        var shared = PakArchive.Load(File.ReadAllBytes(sharedPath));
        var resources = new List<PakArchive> { shared };
        string patchMaps = Path.Combine(client, "patchmap");
        if (Directory.Exists(patchMaps))
            resources.AddRange(Directory.EnumerateFiles(patchMaps, "*_resources.pak", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).Select(path => PakArchive.Load(File.ReadAllBytes(path))));
        int checkedLinks = 0;
        foreach (var pak in resources)
        foreach (var entry in pak.Entries.Where(entry => Regex.IsMatch(entry.Name, @"^contents_map\d{2}\.mpo$")))
        {
            string map = entry.Name.Substring(9, 5);
            var parents = MpoFile.Parse(pak.Extract(entry.Name)).Objects;
            var catalog = CompositeObjectCatalog.Build(new[] { shared, pak }.Distinct().ToArray(), pak, map);
            foreach (var link in catalog.Links)
            {
                Assert.InRange(link.SourceIndex, 0, parents.Count - 1);
                string record = parents[link.SourceIndex].Name.TrimStart('@', '!');
                Assert.StartsWith(record + "_", link.Definition, StringComparison.OrdinalIgnoreCase);
                Assert.NotEmpty(catalog.Definitions[link.Definition]);
                checkedLinks++;
            }
        }
        Assert.True(checkedLinks >= 100, $"Only {checkedLinks} native composite links were exercised.");
    }

    [Fact]
    public void AllNativeTowerAndHartFamiliesCutTheirSlopingApronEdges()
    {
        string path = Path.Combine(Environment.GetEnvironmentVariable("PLANETSIDE_DIR")
            ?? "/home/brynn/Downloads/PlanetSide", "uber.ubr");
        if (!File.Exists(path)) return; // Optional installed-client integration fixture, like other native audits.
        var models = UberModel.Load(File.ReadAllBytes(path));
        foreach (string record in new[] { "tower_a", "tower_b", "tower_c",
                     "orbital_building_nc", "orbital_building_tr", "orbital_building_vs" })
        {
            var system = models.FetchMeshSystem(record)!;
            Assert.NotNull(system);
            var selected = HighestQualityMeshSelector.Select(system).CreateKeepMask();
            var skirts = system.Meshes.SelectMany((mesh, index) => selected[index]
                ? mesh.Sections.SelectMany(Triangles) : Enumerable.Empty<Vector3[]>());
            var skirt = skirts.Where(vertices =>
                    vertices.Count(v => Math.Abs(v.Z) < 0.0001f) == 2
                    && vertices.Min(v => v.Z) < -1)
                .OrderByDescending(vertices => Math.Abs(Vector3.Cross(
                    vertices[1] - vertices[0], vertices[2] - vertices[0]).Z))
                .First();
            var top = skirt.Where(v => Math.Abs(v.Z) < 0.0001f).ToArray();
            var bottom = skirt.MinBy(v => v.Z);
            Vector3 point = Vector3.Lerp((top[0] + top[1]) * 0.5f, bottom, 0.0002f);
            point.Z = 0;
            var first = new TerrainTriangleClipper.Vertex(point + new Vector3(-0.01f, -0.01f, 0), Vector3.UnitZ);
            var second = new TerrainTriangleClipper.Vertex(point + new Vector3(0.01f, -0.01f, 0), Vector3.UnitZ);
            var third = new TerrainTriangleClipper.Vertex(point + new Vector3(0, 0.01f, 0), Vector3.UnitZ);
            var cuts = TerrainFoundationCutouts.Build(models,
                new[] { (new MapObject(0, record, Vector3.Zero, Vector3.One, 0), 0) });
            var remaining = cuts.Clip(first, second, third);
            Assert.False(remaining.Any(polygon => Contains(polygon, point)), record);
            Assert.True(cuts.CreateReport().RemovedArea > 0, record);
            Assert.All(remaining.SelectMany(polygon => polygon), v => Assert.Equal(0, v.Position.Z));
            // The same footprint high above the building is unaffected.
            var raised = cuts.Clip(first with { Position = first.Position + Vector3.UnitZ * 100 },
                second with { Position = second.Position + Vector3.UnitZ * 100 },
                third with { Position = third.Position + Vector3.UnitZ * 100 });
            Assert.Single(raised);
            Assert.Equal(3, raised[0].Count);
        }
    }

    private static IEnumerable<Vector3[]> Triangles(UberModel.MeshSection section)
    {
        int stride = section.IsTriStrip ? 1 : 3;
        for (int i = 0; i + 2 < section.Indices.Length; i += stride)
            yield return new[] { section.Verts[section.Indices[i]].Position,
                section.Verts[section.Indices[i + 1]].Position, section.Verts[section.Indices[i + 2]].Position };
    }

    private static bool Contains(IReadOnlyList<TerrainTriangleClipper.Vertex> polygon, Vector3 point)
    {
        var signs = polygon.Select((vertex, i) =>
        {
            var next = polygon[(i + 1) % polygon.Count].Position;
            return (double)(next.X - vertex.Position.X) * (point.Y - vertex.Position.Y)
                - (double)(next.Y - vertex.Position.Y) * (point.X - vertex.Position.X);
        }).ToArray();
        return signs.All(v => v >= -1e-10) || signs.All(v => v <= 1e-10);
    }
}
