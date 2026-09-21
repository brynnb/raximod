using System.IO.Compression;
using System.Text;

namespace Raximod.Generation.Assets
{
    /// <summary>Lossless PNG encoding shared by native textures, terrain, effects and GLB images.</summary>
    public static class PngEncoder
    {
        public static byte[] EncodeBgra(byte[] bgra, int width, int height)
        {
            if (width <= 0 || height <= 0 || bgra.Length != checked(width * height * 4))
            {
                throw new ArgumentException("Invalid BGRA image dimensions.");
            }

            using var output = new MemoryStream();
            output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

            var ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, width);
            WriteBigEndian(ihdr, 4, height);
            ihdr[8] = 8;
            ihdr[9] = 6; // RGBA8
            WriteChunk(output, "IHDR", ihdr);

            var rgba = new byte[bgra.Length];
            for (int y = 0; y < height; y++)
            {
                int src = y * width * 4;
                int dst = src;
                for (int x = 0; x < width; x++)
                {
                    rgba[dst + x * 4] = bgra[src + x * 4 + 2];
                    rgba[dst + x * 4 + 1] = bgra[src + x * 4 + 1];
                    rgba[dst + x * 4 + 2] = bgra[src + x * 4];
                    rgba[dst + x * 4 + 3] = bgra[src + x * 4 + 3];
                }
            }

            WriteChunk(output, "IDAT", CompressRgba(rgba, width, height));
            WriteChunk(output, "IEND", Array.Empty<byte>());
            return output.ToArray();
        }

        internal static byte[] CompressRgba(byte[] rgba, int width, int height)
        {
            int stride = checked(width * 4);
            var unfiltered = new byte[checked(height * (stride + 1))];
            var adaptive = new byte[unfiltered.Length];
            var trial = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                int row = y * stride, target = y * (stride + 1);
                rgba.AsSpan(row, stride).CopyTo(unfiltered.AsSpan(target + 1));
                long best = long.MaxValue;
                // PNG's recommended signed-residual score chooses among the
                // five reversible row predictors. It never changes pixels,
                // including RGB hidden beneath alpha=0. Stable ties choose the
                // lowest filter number. https://www.w3.org/TR/png-3/#12Filter-selection
                for (byte filter = 0; filter <= 4; filter++)
                {
                    long score = 0;
                    for (int x = 0; x < stride; x++)
                    {
                        int left = x >= 4 ? rgba[row + x - 4] : 0;
                        int above = y > 0 ? rgba[row + x - stride] : 0;
                        int upperLeft = y > 0 && x >= 4 ? rgba[row + x - stride - 4] : 0;
                        byte residual = unchecked((byte)(rgba[row + x] - Predictor(filter, left, above, upperLeft)));
                        trial[x] = residual;
                        score += Math.Abs((int)unchecked((sbyte)residual));
                    }
                    if (score >= best) continue;
                    best = score;
                    adaptive[target] = filter;
                    trial.CopyTo(adaptive, target + 1);
                }
            }
            // Try the old encoding too: noisy/tiny images must not grow merely
            // because adaptive filtering usually wins. All effort is offline.
            byte[] smallest = Deflate(unfiltered, CompressionLevel.Fastest);
            foreach (byte[] candidate in new[] {
                Deflate(unfiltered, CompressionLevel.SmallestSize),
                Deflate(adaptive, CompressionLevel.SmallestSize) })
                if (candidate.Length < smallest.Length) smallest = candidate;
            return smallest;
        }

        internal static int Predictor(byte filter, int left, int above, int upperLeft)
        {
            if (filter == 0) return 0;
            if (filter == 1) return left;
            if (filter == 2) return above;
            if (filter == 3) return (left + above) / 2;
            if (filter != 4) throw new InvalidDataException($"Invalid PNG row filter {filter}.");
            int p = left + above - upperLeft;
            int a = Math.Abs(p - left), b = Math.Abs(p - above), c = Math.Abs(p - upperLeft);
            return a <= b && a <= c ? left : b <= c ? above : upperLeft;
        }

        private static byte[] Deflate(byte[] data, CompressionLevel level)
        {
            using var output = new MemoryStream();
            using (var stream = new ZLibStream(output, level, leaveOpen: true))
            {
                stream.Write(data);
            }
            return output.ToArray();
        }

        internal static void WriteChunk(Stream output, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBigEndian(length, 0, data.Length);
            output.Write(length);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            output.Write(typeBytes);
            output.Write(data);
            uint crc = Crc(data, Crc(typeBytes, 0xFFFFFFFFu)) ^ 0xFFFFFFFFu;
            var crcBytes = new byte[4];
            WriteBigEndian(crcBytes, 0, unchecked((int)crc));
            output.Write(crcBytes);
        }

        internal static uint Crc(ReadOnlySpan<byte> data, uint crc)
        {
            foreach (byte value in data)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }
            return crc;
        }

        private static void WriteBigEndian(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }
    }
}
