using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class PngEncoderTests
{
    [Fact]
    public void EncodingPreservesChannelsTransparentRgbAndMatchesRecompressedIndependentPng()
    {
        const int width = 32, height = 20;
        var rgba = new byte[width * height * 4];
        var bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)i; rgba[i+1] = (byte)(i / 4); rgba[i+2] = 213; rgba[i+3] = (byte)(i % 3 == 0 ? 0 : 255);
            bgra[i] = rgba[i+2]; bgra[i+1] = rgba[i+1]; bgra[i+2] = rgba[i]; bgra[i+3] = rgba[i+3];
        }
        var original = Fixture(rgba, width, height);
        var encoded = PngEncoder.EncodeBgra(bgra, width, height);
        Assert.True(encoded.Length < original.Length / 2);
        Assert.Equal(encoded, PngRecompression.Convert(original));
        Assert.Equal(encoded, PngRecompression.Convert(encoded));
        Assert.Equal(encoded, PngEncoder.EncodeBgra(bgra, width, height));
    }

    [Fact]
    public void MigrationPreservesColorAndOtherMetadataVerbatim()
    {
        byte[] raw = new byte[64 * 64 * 4];
        var original = Fixture(raw, 64, 64, metadata: true);
        var output = PngRecompression.Convert(original);
        Assert.True(output.Length < original.Length / 2);
        Assert.Equal(NonImageChunks(original), NonImageChunks(output));
        Assert.Equal(output, PngRecompression.Convert(output));
    }

    [Fact]
    public void InvalidAndUnsupportedImagesAreNotSilentlyRepaired()
    {
        var gray = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        Assert.Equal(gray, PngRecompression.Convert(gray));
        var png = Fixture(new byte[256], 8, 8);
        png[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => PngRecompression.Convert(png));
        Assert.Throws<InvalidDataException>(() => PngRecompression.Convert(gray[..30]));
        Assert.Throws<ArgumentException>(() => PngEncoder.EncodeBgra([1,2], 1, 1));
    }

    [Fact]
    public void StagingNeverMutatesSourcesAndApplyRejectsConcurrentChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), $"png-stage-{Guid.NewGuid():N}");
        string source = Path.Combine(root, "input"), stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(source);
        try
        {
            byte[] original = Fixture(new byte[256], 8, 8);
            string file = Path.Combine(source, "image.png");
            File.WriteAllBytes(file, original);
            var result = PngAssetRecompression.Stage(source, stage, 2);
            Assert.Equal(1, result.ChangedFiles);
            Assert.Equal(original, File.ReadAllBytes(file));
            File.WriteAllBytes(file, [1,2,3]);
            Assert.Throws<InvalidDataException>(() => PngAssetRecompression.Apply(stage));
            Assert.Equal(new byte[] {1,2,3}, File.ReadAllBytes(file));
            File.WriteAllBytes(file, original);
            PngAssetRecompression.Apply(stage);
            Assert.Equal(PngRecompression.Convert(original), File.ReadAllBytes(file));
            Assert.Equal(0, PngAssetRecompression.Stage(source, Path.Combine(root, "second"), 2).ChangedFiles);
        }
        finally { Directory.Delete(root, true); }
    }

    // A simple independent PNG writer: unfiltered RGBA and uncompressed zlib.
    internal static byte[] Fixture(byte[] rgba, int width, int height, bool metadata = false)
    {
        using var output = new MemoryStream(); output.Write([137,80,78,71,13,10,26,10]);
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR", header);
        if (metadata)
        {
            Chunk(output, "gAMA", [0,0,177,143]);
            Chunk(output, "sRGB", [0]);
            Chunk(output, "tEXt", Encoding.ASCII.GetBytes("Source\0native fixture"));
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.NoCompression, true))
            for (int y = 0; y < height; y++) { z.WriteByte(0); z.Write(rgba.AsSpan(y * width * 4, width * 4)); }
        byte[] payload = compressed.ToArray(); int split = payload.Length / 2;
        Chunk(output, "IDAT", payload[..split]); Chunk(output, "IDAT", payload[split..]);
        Chunk(output, "IEND", []); return output.ToArray();
    }
    private static void Chunk(Stream output, string name, byte[] data)
    {
        var word = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(word, data.Length); output.Write(word);
        byte[] kind = Encoding.ASCII.GetBytes(name); output.Write(kind); output.Write(data);
        uint crc = 0xffffffff;
        foreach (byte b in kind.Concat(data))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ 0xffffffff); output.Write(word);
    }
    private static byte[] NonImageChunks(byte[] png)
    {
        using var output = new MemoryStream();
        for (int offset = 8; offset < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (Encoding.ASCII.GetString(png, offset + 4, 4) != "IDAT") output.Write(png.AsSpan(offset, length + 12));
            offset += length + 12;
        }
        return output.ToArray();
    }
}
