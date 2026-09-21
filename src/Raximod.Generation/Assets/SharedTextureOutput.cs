using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Raximod.Generation.Assets
{
    /// <summary>
    /// Writes byte-identical exported PNGs once for an asset family. A repeated
    /// native texture name with different bytes is an extraction error rather
    /// than an order-dependent overwrite.
    /// </summary>
    public static class SharedTextureOutput
    {
        private const uint GlbMagic = 0x46546c67;
        private const uint JsonChunk = 0x4e4f534a;
        private const uint BinaryChunk = 0x004e4942;
        private static readonly byte[] PngSignature =
            { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

        public static string SafePngFileName(string name)
        {
            string stem = Regex.Replace(
                Path.GetFileNameWithoutExtension(name),
                "[^A-Za-z0-9_.-]",
                "_");
            return (string.IsNullOrWhiteSpace(stem) ? "texture" : stem) + ".png";
        }

        public static string WritePng(string directory, string name, ReadOnlySpan<byte> png)
        {
            string outputDirectory = Path.GetFullPath(directory);
            Directory.CreateDirectory(outputDirectory);
            string path = Path.Combine(outputDirectory, SafePngFileName(name));
            if (File.Exists(path))
            {
                byte[] existing = File.ReadAllBytes(path);
                if (!png.SequenceEqual(existing))
                {
                    throw new InvalidDataException(
                        $"Shared texture '{Path.GetFileName(path)}' has conflicting PNG payloads.");
                }
                return path;
            }

            string temporary = path + $".raximod-{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporary, png.ToArray());
                File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return path;
        }

        /// <summary>
        /// Restores and validates the external shared-texture companions used by
        /// browser models, whether embedded or externalized. This must also run when a batch
        /// export reuses an existing GLB: otherwise an old model can look valid
        /// while its separately served PNGs are absent.
        /// </summary>
        public static int SynchronizeEmbeddedPngs(string glbPath, string directory)
        {
            byte[] glb = File.ReadAllBytes(glbPath);
            if (glb.Length < 20
                || BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(0, 4)) != GlbMagic
                || BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4, 4)) != 2
                || BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8, 4)) != glb.Length)
            {
                throw new InvalidDataException($"'{glbPath}' is not a valid glTF 2.0 binary.");
            }

            ReadOnlyMemory<byte> json = default;
            ReadOnlyMemory<byte> binary = default;
            int offset = 12;
            while (offset < glb.Length)
            {
                if (offset + 8 > glb.Length)
                    throw new InvalidDataException($"'{glbPath}' has a truncated GLB chunk header.");
                int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(offset, 4)));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(offset + 4, 4));
                int start = offset + 8;
                int end = checked(start + length);
                if (end > glb.Length)
                    throw new InvalidDataException($"'{glbPath}' has a truncated GLB chunk.");
                if (type == JsonChunk) json = glb.AsMemory(start, length);
                else if (type == BinaryChunk) binary = glb.AsMemory(start, length);
                offset = end;
            }
            if (json.IsEmpty) throw new InvalidDataException($"'{glbPath}' has no JSON chunk.");

            using JsonDocument document = JsonDocument.Parse(
                Encoding.UTF8.GetString(json.Span).TrimEnd('\0', ' ', '\t', '\r', '\n'));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("images", out JsonElement images)) return 0;
            if (!root.TryGetProperty("bufferViews", out JsonElement views))
                throw new InvalidDataException($"'{glbPath}' embeds images without buffer views.");
            if (binary.IsEmpty)
                throw new InvalidDataException($"'{glbPath}' embeds images without a BIN chunk.");

            int synchronized = 0;
            foreach (JsonElement image in images.EnumerateArray())
            {
                if (!image.TryGetProperty("bufferView", out JsonElement viewIndexElement)) continue;
                string? name = image.TryGetProperty("name", out JsonElement nameElement)
                    ? nameElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(name) || SafePngFileName(name) != name)
                    throw new InvalidDataException($"'{glbPath}' has an unsafe or missing embedded image name.");
                string? mime = image.TryGetProperty("mimeType", out JsonElement mimeElement)
                    ? mimeElement.GetString()
                    : null;
                if (!string.Equals(mime, "image/png", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"'{glbPath}' embeds unsupported image '{name}' ({mime ?? "no MIME type"}).");

                int viewIndex = viewIndexElement.GetInt32();
                if (viewIndex < 0 || viewIndex >= views.GetArrayLength())
                    throw new InvalidDataException($"'{glbPath}' has an invalid buffer view for '{name}'.");
                JsonElement view = views[viewIndex];
                int bufferIndex = view.TryGetProperty("buffer", out JsonElement bufferElement)
                    ? bufferElement.GetInt32()
                    : 0;
                int imageOffset = view.TryGetProperty("byteOffset", out JsonElement offsetElement)
                    ? offsetElement.GetInt32()
                    : 0;
                int imageLength = view.GetProperty("byteLength").GetInt32();
                if (bufferIndex != 0 || imageOffset < 0 || imageLength <= 0
                    || imageOffset > binary.Length - imageLength)
                {
                    throw new InvalidDataException($"'{glbPath}' has an invalid image range for '{name}'.");
                }
                ReadOnlySpan<byte> png = binary.Span.Slice(imageOffset, imageLength);
                if (png.Length < PngSignature.Length
                    || !png[..PngSignature.Length].SequenceEqual(PngSignature))
                    throw new InvalidDataException($"'{glbPath}' contains an invalid PNG for '{name}'.");
                WritePng(directory, name, png);
                synchronized++;
            }
            // Reused browser GLBs already carry relative URIs. Validate those
            // references too; an absent companion must never pass a reuse export.
            if (images.EnumerateArray().Any(image => !image.TryGetProperty("bufferView", out _)))
                SharedGlbImageExport.Convert(glb, glbPath, directory);
            return synchronized;
        }
    }
}
