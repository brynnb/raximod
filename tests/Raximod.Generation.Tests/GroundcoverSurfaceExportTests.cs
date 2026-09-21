using Raximod.Generation.Continents;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class GroundcoverSurfaceExportTests
{
    [Fact]
    public void LosslessRunsRetainEmptyCellsAndSplitAtUint16Limit()
    {
        var cells = new byte[512 * 512];
        Array.Fill(cells, (byte)3, 100_000, cells.Length - 100_000);
        byte[] encoded = GroundcoverSurfaceExport.Encode(cells, 512);
        Assert.Equal(encoded, GroundcoverSurfaceExport.Encode(cells, 512));
        using var reader = new BinaryReader(new MemoryStream(encoded));
        Assert.Equal("GCS1"u8.ToArray(), reader.ReadBytes(4));
        Assert.Equal(512, reader.ReadInt32());
        var decoded = new List<byte>();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            ushort count = reader.ReadUInt16();
            Assert.NotEqual(0, count);
            decoded.AddRange(Enumerable.Repeat(reader.ReadByte(), count));
        }
        Assert.Equal(cells, decoded.ToArray());
        Assert.True(encoded.Length < 40);
    }

    [Fact]
    public void InvalidDimensionsCannotOverflowOrBeSilentlyTruncated()
    {
        Assert.Throws<ArgumentException>(() => GroundcoverSurfaceExport.Encode([0, 1, 2], 2));
        Assert.Throws<ArgumentException>(() => GroundcoverSurfaceExport.Encode([], 0));
        Assert.Throws<ArgumentException>(() => GroundcoverSurfaceExport.Encode([], 65536));
    }
}
