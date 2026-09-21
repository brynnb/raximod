using System.Numerics;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Continents;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeOceanExportTests
{
    [Fact]
    public void PackedCellsResolveExactRecordsAndPreserveNativeGeometryAttributes()
    {
        var source = Source();
        var requested = new List<string>();
        var tiles = NativeOceanExport.Resolve("map12", [33, 1023], name => { requested.Add(name); return source; });
        Assert.Equal(new[] { "map12_oc0101", "map12_oc3131" }, requested);
        Assert.Equal(new[] { 0, 1 }, tiles.Select(t => t.SourceIndex));
        var combined = NativeOceanExport.Combine("ocean", tiles);
        var section = combined.Meshes.Single().Sections.First();
        // Native Z already carries sea20; source offset applies to horizontal axes only.
        Assert.Equal(new Vector3(266, 532, 20), section.Verts[0].Position);
        Assert.Equal(new Vector3(10, 20, 20), source.Meshes[0].Sections[0].Verts[0].Position);
        Assert.Equal(Vector3.Zero, combined.WorldOffset);
        Assert.Equal(new Vector2(.2f, .4f), section.Verts[0].Uv0);
        Assert.Equal(new Vector2(.6f, .8f), section.Verts[0].Uv1);
        Assert.Equal(0x8cabcdefu, section.Verts[0].Diffuse);
        Assert.Equal(new ushort[] { 0, 1, 2 }, section.Indices);
        Assert.Equal("water", section.MaterialName);
        Assert.True(section.HasUv1 && section.HasColor);
        Assert.Equal(2, combined.Meshes[0].Sections.Count);
    }

    [Fact]
    public void MissingDuplicatedMalformedOrSkinnedOceanFailsExplicitly()
    {
        Assert.Throws<InvalidDataException>(() => NativeOceanExport.Resolve("map12", [0], _ => null));
        Assert.Throws<InvalidDataException>(() => NativeOceanExport.Resolve("map12", [1024], _ => Source()));
        Assert.Throws<InvalidDataException>(() => NativeOceanExport.Resolve("map12", [0, 0], _ => Source()));
        var source = Source();
        source.Meshes[0].Sections[0].HasSkin = true;
        Assert.Throws<InvalidDataException>(() => NativeOceanExport.Resolve("map12", [0], _ => source));
        source.Meshes[0].Sections[0].HasSkin = false;
        source.A = BitConverter.SingleToUInt32Bits(float.NaN);
        Assert.Throws<InvalidDataException>(() => NativeOceanExport.Resolve("map12", [0], _ => source));
    }

    [Fact]
    public void ExplicitSourceGapReportingNeverFabricatesGeometryOrRenumbersAdmissions()
    {
        var missing = new List<NativeOceanExport.MissingTile>();
        var tiles = NativeOceanExport.Resolve("map16", [0, 1],
            name => name == "map16_oc0000" ? null : Source(), missing.Add);
        Assert.Equal("map16_oc0000", Assert.Single(missing).Record);
        Assert.Equal(0, missing[0].SourceIndex);
        Assert.Equal(1, Assert.Single(tiles).SourceIndex);
    }

    private static UberModel.MeshSystem Source()
    {
        var system = new UberModel.MeshSystem { Name = "ocean",
            A = BitConverter.SingleToUInt32Bits(256), B = BitConverter.SingleToUInt32Bits(512) };
        var mesh = new UberModel.Mesh { Name = "ocean", ModelName = "ocean" };
        mesh.Sections.Add(new UberModel.MeshSection
        {
            MaterialName = "water", Flags = 1, VertexCount = 3, IndexCount = 3, Indices = [0, 1, 2],
            HasNormal = true, HasUv0 = true, HasUv1 = true, HasColor = true,
            Verts = Enumerable.Range(0, 3).Select(i => new UberModel.UberVert
            {
                Position = new Vector3(10 + i, 20, 20), Normal = Vector3.UnitZ,
                Uv0 = new(.2f, .4f), Uv1 = new(.6f, .8f), Diffuse = 0x8cabcdef,
            }).ToArray(),
        });
        system.Meshes.Add(mesh);
        return system;
    }
}
