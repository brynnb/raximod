using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Raximod.Generation.Assets;

/// <summary>Re-encode existing RGBA8 exports with the shared encoder. IHDR and every
/// non-IDAT chunk stay byte-identical, including color profiles, text and timestamps.</summary>
public static class PngRecompression
{
    public static byte[] Convert(byte[] png)
    {
        var chunks = Chunks(png);
        var header = png.AsSpan(chunks[0].Offset + 8, 13);
        // Only the native encoder's non-interlaced RGBA8 format is migrated.
        // Retain palette, grayscale, 16-bit and animated PNGs without conversion.
        if (header[8] != 8 || header[9] != 6 || header[12] != 0
            || chunks.Any(c => c.Type is "acTL" or "fcTL" or "fdAT")) return png;
        int width = BinaryPrimitives.ReadInt32BigEndian(header);
        int height = BinaryPrimitives.ReadInt32BigEndian(header[4..]);
        int stride = checked(width * 4), pixelBytes = checked(stride * height);
        // Bound malformed image allocation independently of compressed size.
        if (pixelBytes > 256 * 1024 * 1024) throw new InvalidDataException("PNG exceeds the 256 MiB decoded-image limit.");
        var rows = new byte[checked(pixelBytes + height)];
        using var compressed = new MemoryStream();
        foreach (var chunk in chunks.Where(c => c.Type == "IDAT"))
            compressed.Write(png.AsSpan(chunk.Offset + 8, chunk.Length));
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            zlib.ReadExactly(rows);
            if (zlib.ReadByte() != -1) throw new InvalidDataException("PNG has excess image data.");
        }
        var rgba = new byte[pixelBytes];
        for (int y = 0; y < height; y++)
        {
            int source = y * (stride + 1), target = y * stride;
            byte filter = rows[source];
            for (int x = 0; x < stride; x++)
                rgba[target + x] = unchecked((byte)(rows[source + x + 1] + PngEncoder.Predictor(filter,
                    x >= 4 ? rgba[target + x - 4] : 0,
                    y > 0 ? rgba[target + x - stride] : 0,
                    y > 0 && x >= 4 ? rgba[target + x - stride - 4] : 0)));
        }
        byte[] encoded = PngEncoder.CompressRgba(rgba, width, height);
        using var output = new MemoryStream();
        output.Write(png.AsSpan(0, 8));
        bool wroteImage = false;
        foreach (var chunk in chunks)
        {
            if (chunk.Type != "IDAT") output.Write(png.AsSpan(chunk.Offset, chunk.Length + 12));
            else if (!wroteImage) { PngEncoder.WriteChunk(output, "IDAT", encoded); wroteImage = true; }
        }
        // Equal-size rewrites also normalize the zlib header to the current
        // encoder; future native exports must match their shared companions.
        return output.Length <= png.Length ? output.ToArray() : png;
    }

    private sealed record Chunk(int Offset, int Length, string Type);
    private static List<Chunk> Chunks(byte[] png)
    {
        if (png.Length < 45 || !png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
            throw new InvalidDataException("Invalid PNG signature.");
        var chunks = new List<Chunk>();
        bool sawData = false, closedData = false, sawEnd = false;
        for (int offset = 8; offset < png.Length;)
        {
            if (offset > png.Length - 12) throw new InvalidDataException("Truncated PNG chunk.");
            int size = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (size < 0 || size > png.Length - offset - 12) throw new InvalidDataException("Invalid PNG chunk length.");
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            uint crc = PngEncoder.Crc(png.AsSpan(offset + 4, size + 4), 0xffffffff) ^ 0xffffffff;
            if (crc != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + size, 4)))
                throw new InvalidDataException($"PNG {type} CRC mismatch.");
            if (chunks.Count == 0 && (type != "IHDR" || size != 13)) throw new InvalidDataException("Invalid PNG header.");
            if (type == "IHDR" && chunks.Count != 0) throw new InvalidDataException("Duplicate PNG header.");
            if (type == "IDAT")
            {
                if (closedData) throw new InvalidDataException("Non-consecutive PNG image chunks.");
                sawData = true;
            }
            else if (sawData) closedData = true;
            if (type == "IEND")
            {
                if (size != 0 || offset + 12 != png.Length) throw new InvalidDataException("Invalid PNG end.");
                sawEnd = true;
            }
            if (char.IsUpper(type[0]) && type is not ("IHDR" or "IDAT" or "IEND" or "PLTE"))
                throw new InvalidDataException($"Unsupported critical PNG chunk {type}.");
            chunks.Add(new(offset, size, type)); offset += size + 12;
        }
        if (!sawData || !sawEnd) throw new InvalidDataException("Incomplete PNG.");
        var header = png.AsSpan(16, 13);
        if (BinaryPrimitives.ReadInt32BigEndian(header) <= 0 || BinaryPrimitives.ReadInt32BigEndian(header[4..]) <= 0
            || header[10] != 0 || header[11] != 0) throw new InvalidDataException("Invalid PNG image parameters.");
        return chunks;
    }
}
