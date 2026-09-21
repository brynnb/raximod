using System.Text;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Maps;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class MpoFileTests
{
    [Fact]
    public void LakeNamesHaveIndependentSectionBoundariesFromPackedWaterCells()
    {
        var names = new[] { "map09_0", "map09_1", "long_record_name", "map09_0" };
        var result = MpoFile.Parse(Document(
            ("map_lakes", Payload(writer =>
            {
                writer.Write((uint)names.Length);
                foreach (string name in names) writer.Write(Encoding.ASCII.GetBytes(name + "\0"));
            })),
            ("map_water", Payload(writer => { writer.Write(2u); writer.Write(0u); writer.Write(1023u); }))));
        Assert.Equal(names, result.LakeRecords); // Preserve authored order and repeated names.
        Assert.Equal(new uint[] { 0, 1023 }, result.WaterCellIds);
    }

    [Fact]
    public void EmptyLakeListIsValid()
    {
        Assert.Empty(MpoFile.Parse(Document(("map_lakes", new byte[4]))).LakeRecords);
    }

    [Theory]
    [InlineData("map_water")]
    [InlineData("map_sections")]
    public void PackedCellsCannotReadTheNextSectionOrSilentlyTruncate(string section)
    {
        foreach (byte[] payload in new[] { new byte[3], Payload(w => w.Write(1u)),
            Payload(w => { w.Write(uint.MaxValue); w.Write(0u); }),
            Payload(w => { w.Write(0u); w.Write(0u); }) })
            Assert.Throws<InvalidDataException>(() => MpoFile.Parse(Document(
                (section, payload), ("map_header", new byte[8]))));
        var document = Document((section, Payload(w => { w.Write(1u); w.Write(0u); })));
        Assert.Throws<InvalidDataException>(() => MpoFile.Parse(document[..^1]));
    }

    [Theory]
    [MemberData(nameof(MalformedLakePayloads))]
    public void RejectsMalformedLakePayloadWithoutReadingTheNextSection(byte[] payload)
    {
        Assert.Throws<InvalidDataException>(() => MpoFile.Parse(Document(
            ("map_lakes", payload), ("map_header", new byte[8]))));
    }

    public static IEnumerable<object[]> MalformedLakePayloads()
    {
        yield return [new byte[3]]; // Missing count.
        yield return [Payload(w => { w.Write(uint.MaxValue); w.Write((byte)0); })];
        yield return [Payload(w => { w.Write(1u); w.Write(Encoding.ASCII.GetBytes("map09_0")); })];
        yield return [Payload(w => { w.Write(1u); w.Write(new byte[2]); })]; // Empty record.
        yield return [Payload(w => { w.Write(0u); w.Write((byte)0); })]; // Unaccounted byte.
    }

    [Fact]
    public void RejectsLakeSectionTruncatedByEndOfFile()
    {
        byte[] document = Document(("map_lakes", Payload(w =>
        {
            w.Write(1u); w.Write(Encoding.ASCII.GetBytes("map09_0\0"));
        })));
        Assert.Throws<InvalidDataException>(() => MpoFile.Parse(document[..^1]));
    }

    [InstalledClientFact("maps/map_resources.pak")]
    public void OriginalSearhusListNamesAllEightLocalLakeRecords()
    {
        string root = Environment.GetEnvironmentVariable("PLANETSIDE_DIR") ?? "/home/brynn/Downloads/PlanetSide";
        var pak = PakArchive.Load(File.ReadAllBytes(Path.Combine(root, "maps", "map_resources.pak")));
        var map = MpoFile.Parse(pak.Extract("contents_map09.mpo"));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"map09_{i}"), map.LakeRecords);
        Assert.Equal(30f, map.HeaderA);
        Assert.Equal(700, map.WaterCellIds.Count);
        Assert.Equal(1024, map.TerrainTileIds.Count);
    }

    private static byte[] Payload(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        write(writer);
        return stream.ToArray();
    }

    private static byte[] Document(params (string Name, byte[] Payload)[] sections) => Payload(w =>
    {
        Field(w, "chunky"); w.Write((ushort)1); w.Write((uint)sections.Length);
        foreach (var section in sections)
        {
            Field(w, section.Name); w.Write((ushort)1); w.Write((uint)section.Payload.Length);
            w.Write(section.Payload);
        }
    });

    private static void Field(BinaryWriter writer, string value)
    {
        var bytes = new byte[16];
        Encoding.ASCII.GetBytes(value).CopyTo(bytes, 0);
        writer.Write(bytes);
    }
}
