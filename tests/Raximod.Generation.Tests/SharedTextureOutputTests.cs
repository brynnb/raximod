using Raximod.Generation.Assets;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class SharedTextureOutputTests
    {
        [Fact]
        public void RepeatedIdenticalTexturesShareOneValidatedFile()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"raximod-textures-{Guid.NewGuid():N}");
            try
            {
                byte[] png = [0x89, 0x50, 0x4e, 0x47, 1, 2, 3];
                string first = SharedTextureOutput.WritePng(directory, "armor/tr?body.png", png);
                string second = SharedTextureOutput.WritePng(directory, "armor/tr?body.png", png);

                Assert.Equal(first, second);
                Assert.Equal("tr_body.png", Path.GetFileName(first));
                Assert.Equal(png, File.ReadAllBytes(first));
                Assert.Single(Directory.GetFiles(directory));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void ConflictingTextureNamesFailInsteadOfOverwriting()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"raximod-textures-{Guid.NewGuid():N}");
            try
            {
                SharedTextureOutput.WritePng(directory, "armor.png", [1, 2, 3]);

                InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                    SharedTextureOutput.WritePng(directory, "armor.png", [4, 5, 6]));

                Assert.Contains("conflicting PNG payloads", error.Message);
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(directory, "armor.png")));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void ExistingGlbRestoresItsRedirectedTextureCompanion()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"raximod-textures-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(directory);
                string glb = Path.Combine(directory, "attachment.glb");
                string textures = Path.Combine(directory, "material-textures");
                byte[] png = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                WriteEmbeddedImageGlb(glb, "particle_beam.png", png);

                Assert.Equal(1, SharedTextureOutput.SynchronizeEmbeddedPngs(glb, textures));
                Assert.Equal(png, File.ReadAllBytes(Path.Combine(textures, "particle_beam.png")));

                // Re-validating an already synchronized output is deterministic.
                Assert.Equal(1, SharedTextureOutput.SynchronizeEmbeddedPngs(glb, textures));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void WriteEmbeddedImageGlb(string path, string name, byte[] png)
        {
            string rawJson = $$"""
                {"asset":{"version":"2.0"},"buffers":[{"byteLength":{{png.Length}}}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":{{png.Length}}}],"images":[{"name":"{{name}}","mimeType":"image/png","bufferView":0}]}
                """;
            byte[] json = Encoding.UTF8.GetBytes(rawJson);
            int jsonLength = (json.Length + 3) & ~3;
            int binaryLength = (png.Length + 3) & ~3;
            byte[] glb = new byte[12 + 8 + jsonLength + 8 + binaryLength];
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0, 4), 0x46546c67);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)glb.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12, 4), (uint)jsonLength);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16, 4), 0x4e4f534a);
            glb.AsSpan(20, jsonLength).Fill(0x20);
            json.CopyTo(glb.AsSpan(20));
            int binaryHeader = 20 + jsonLength;
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binaryHeader, 4), (uint)binaryLength);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binaryHeader + 4, 4), 0x004e4942);
            png.CopyTo(glb.AsSpan(binaryHeader + 8));
            File.WriteAllBytes(path, glb);
        }
    }
}
