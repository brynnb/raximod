using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class SharedGlbImageExportTests
{
    [Fact]
    public void ReuseAcceptsVerifiedPoolUrisAndRetainsTheCompleteBinaryChunk()
    {
        WithFixture((file, input) => {
            byte[] original = SharedGlbImageExport.Convert(input, file);
            string root = Path.GetDirectoryName(file)!;
            byte[] image = File.ReadAllBytes(Path.Combine(root, "material-textures/image.png"));
            string hash = System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image));
            Directory.CreateDirectory(Path.Combine(root, "textures"));
            string shared = Path.Combine(root, "textures", hash + ".png");
            File.WriteAllBytes(shared, image);
            var document = GlbImagePackaging.Read(original, file);
            document.Root["images"]![0]!["uri"] = "textures/" + hash + ".png";
            byte[] pooled = document.Serialize();
            Assert.Equal(original.AsSpan(20 + Read(original, 12)).ToArray(), pooled.AsSpan(20 + Read(pooled, 12)).ToArray());
            Assert.Equal("image.png", document.Root["images"]![0]!["name"]!.GetValue<string>());
            Assert.Equal(pooled, SharedGlbImageExport.Convert(pooled, file));
            File.WriteAllBytes(Path.Combine(root, "material-textures/image.png"), [7, 8, 9]);
            Assert.Throws<InvalidDataException>(() => SharedGlbImageExport.Convert(pooled, file));
            File.Delete(Path.Combine(root, "material-textures/image.png"));
            Assert.Equal(pooled, SharedGlbImageExport.Convert(pooled, file));
            File.WriteAllBytes(file, pooled);
            Assert.Equal(0, SharedTextureOutput.SynchronizeEmbeddedPngs(file, Path.Combine(root, "material-textures")));
            File.WriteAllBytes(shared, [1, 2, 3]);
            Assert.Throws<InvalidDataException>(() => SharedGlbImageExport.Convert(pooled, file));
        });
    }

    [Fact]
    public void ExternalizationPreservesPayloadsReferencesAndMetadataAndIsIdempotent()
    {
        WithFixture((file, input) => {
            var output = SharedGlbImageExport.Convert(input, file);
            Assert.True(output.Length < input.Length);
            var json = JsonNode.Parse(output.AsSpan(20, Read(output, 12)))!;
            Assert.Equal("material-textures/image.png", json["images"]![0]!["uri"]!.GetValue<string>());
            Assert.Null(json["images"]![0]!["bufferView"]);
            Assert.Equal(0, json["accessors"]![0]!["bufferView"]!.GetValue<int>());
            Assert.Equal(1, json["accessors"]![1]!["bufferView"]!.GetValue<int>());
            Assert.Equal(1, json["accessors"]![1]!["sparse"]!["values"]!["bufferView"]!.GetValue<int>());
            Assert.Equal(1, json["extras"]!["bufferView"]!.GetValue<int>());
            Assert.Equal("native_joint", json["nodes"]![0]!["name"]!.GetValue<string>());
            Assert.Equal(1, json["skins"]![0]!["inverseBindMatrices"]!.GetValue<int>());
            Assert.Equal(1, json["animations"]![0]!["samplers"]![0]!["input"]!.GetValue<int>());
            Assert.Equal(new byte[] {1,2,3,4,5,6,7,8}, output.AsSpan(28 + Read(output, 12)).ToArray());
            Assert.Equal(output, SharedGlbImageExport.Convert(output, file));
            File.WriteAllBytes(file, output);
            Assert.Equal(1, SharedGlbImageExport.Run(Path.GetDirectoryName(file)!, check: true).Files);
        });
    }

    [Fact]
    public void MissingOrDifferentCompanionCannotOverwriteAnyModel()
    {
        WithFixture((file, input) => {
            string texture = Path.Combine(Path.GetDirectoryName(file)!, "material-textures", "image.png");
            File.WriteAllBytes(file, input);
            Assert.Throws<AggregateException>(() => SharedGlbImageExport.Run(Path.GetDirectoryName(file)!, check: true));
            File.WriteAllBytes(texture, [0]);
            Assert.Throws<InvalidDataException>(() => SharedGlbImageExport.Convert(input, file));
            Assert.Throws<AggregateException>(() => SharedGlbImageExport.Run(Path.GetDirectoryName(file)!));
            Assert.Equal(input, File.ReadAllBytes(file));
            File.Delete(texture);
            Assert.Throws<FileNotFoundException>(() => SharedGlbImageExport.Convert(input, file));
        });
    }

    [Fact]
    public void AnImageViewReferencedByGeometryMustFailClosed()
    {
        WithFixture((file, input) => {
            var json = JsonNode.Parse(input.AsSpan(20, Read(input, 12)))!;
            json["accessors"]![0]!["bufferView"] = 1;
            Assert.Throws<InvalidDataException>(() => SharedGlbImageExport.Convert(
                Pack(json.ToJsonString(), input.AsSpan(28 + Read(input, 12)).ToArray()), file));
        });
    }

    [Fact]
    public void ImageRecompressionRetainsAllOtherBufferViewsAndRejectsAliasedGeometry()
    {
        WithFixture((file, input) => {
            var document = GlbImagePackaging.Read(input, file);
            byte[] png = PngEncoderTests.Fixture(new byte[32 * 32 * 4], 32, 32);
            byte[] original = document.Repack([], new Dictionary<int, byte[]> { [1] = png });
            var output = PngAssetRecompression.ReencodeGlb(original, file);
            Assert.True(output.Length < original.Length);
            var result = GlbImagePackaging.Read(output, file);
            Assert.Equal(new byte[] {1,2,3,4}, result.ImageBytes(0).ToArray());
            Assert.Equal(new byte[] {5,6,7,8}, result.ImageBytes(2).ToArray());
            Assert.Equal(PngRecompression.Convert(png), result.ImageBytes(1).ToArray());
            Assert.Equal(output, PngAssetRecompression.ReencodeGlb(output, file));
            result.Root["accessors"]![0]!["bufferView"] = 1;
            Assert.Throws<InvalidDataException>(() => result.Repack([], new Dictionary<int, byte[]> { [1] = png }));
        });
    }

    [Fact]
    public void NestedModelsShareOnePoolAndReuseRejectsMissingCompanions()
    {
        WithFixture((file, input) => {
            string root = Path.GetDirectoryName(file)!;
            string pool = Path.Combine(root, "material-textures");
            string nested = Path.Combine(root, "models", "tr", "male", "body.glb");
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            File.WriteAllBytes(nested, input);
            var result = SharedGlbImageExport.Run(root, textureDirectory: pool, recursive: true);
            Assert.Equal(1, result.Files);
            byte[] output = File.ReadAllBytes(nested);
            var json = JsonNode.Parse(output.AsSpan(20, Read(output, 12)))!;
            Assert.Equal("../../../material-textures/image.png", json["images"]![0]!["uri"]!.GetValue<string>());
            Assert.Equal(output, SharedGlbImageExport.Convert(output, nested, pool));
            Assert.Equal(0, SharedTextureOutput.SynchronizeEmbeddedPngs(nested, pool));
            File.Delete(Path.Combine(pool, "image.png"));
            Assert.Throws<FileNotFoundException>(() => SharedTextureOutput.SynchronizeEmbeddedPngs(nested, pool));
            Assert.Equal(output, File.ReadAllBytes(nested));
        });
    }

    private static void WithFixture(Action<string, byte[]> run)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"raximod-shared-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "material-textures"));
        try {
            byte[] image = new byte[256];
            File.WriteAllBytes(Path.Combine(dir, "material-textures", "image.png"), image);
            byte[] binary = new byte[264];
            new byte[] {1,2,3,4}.CopyTo(binary, 0);
            new byte[] {5,6,7,8}.CopyTo(binary, 260);
            string json = """
                {"asset":{"version":"2.0"},"buffers":[{"byteLength":264}],
                 "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":4},{"buffer":0,"byteOffset":4,"byteLength":256},{"buffer":0,"byteOffset":260,"byteLength":4}],
                 "images":[{"name":"image.png","bufferView":1,"mimeType":"image/png"}],
                 "accessors":[{"bufferView":0},{"bufferView":2,"sparse":{"values":{"bufferView":2}}}],
                 "nodes":[{"name":"native_joint"}],"skins":[{"inverseBindMatrices":1}],
                 "animations":[{"samplers":[{"input":1,"output":0}]}],"extras":{"bufferView":1}}
                """;
            run(Path.Combine(dir, "model.glb"), Pack(json, binary));
        } finally { Directory.Delete(dir, true); }
    }
    private static int Read(byte[] b, int offset) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset, 4));
    private static byte[] Pack(string text, byte[] binary)
    {
        byte[] json = Encoding.UTF8.GetBytes(text);
        int size = (json.Length + 3) & ~3;
        byte[] output = new byte[28 + size + binary.Length];
        void Put(int offset, int n) => BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(offset,4), n);
        Put(0,0x46546c67);Put(4,2);Put(8,output.Length);Put(12,size);Put(16,0x4e4f534a);
        output.AsSpan(20,size).Fill(32);json.CopyTo(output,20);
        Put(20+size,binary.Length);Put(24+size,0x004e4942);binary.CopyTo(output,28+size);
        return output;
    }
}
