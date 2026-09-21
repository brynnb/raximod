using Raximod.Generation.Assets;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Textures;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class NativeTextureSourceResolverTests
    {
        [Theory]
        [InlineData("em_star", "em_star_001")]
        [InlineData("drivermount001", "drivemount")]
        [InlineData("passengermount001", "passenmount")]
        [InlineData("ef_moon_one", "ef_moon_one")]
        [InlineData("sandbags3_lm", "sandbags3_lm")]
        public void OnlyVerifiedAliasesChangeTheAuthoredTextureKey(string authored, string expected)
        {
            Assert.Equal(expected, NativeTextureSourceResolver.ResolveKnownAlias(authored));
        }

        [Fact]
        public void ExactPackAndLooseResourcesResolveWithoutANameOrResolutionGuess()
        {
            using var source = new Sources();
            byte[] bytes = Dds(61);
            var pack = PakArchive.Build([new() { Name = "folder\\Native.DDS", Data = bytes }]);
            File.WriteAllBytes(Path.Combine(source.Root, "source.PAK"), pack);
            File.WriteAllBytes(Path.Combine(source.Root, "native.dds"), bytes);
            var found = NativeTextureSourceResolver.Resolve(new(source.Root), "NATIVE");
            Assert.Equal(new byte[] { 61, 0, 0, 255 }, found.Image!.Bgra);
            Assert.Contains("source.PAK:folder\\Native.DDS", found.SourceArchive);
            Assert.Contains("native.dds", found.SourceArchive);
            Assert.Null(found.MissingSource);
            File.WriteAllBytes(Path.Combine(source.Root, "native.dds"), Dds(62));
            Assert.Contains("Ambiguous", Assert.Throws<InvalidDataException>(() =>
                NativeTextureSourceResolver.Resolve(new(source.Root), "native")).Message);
        }

        [Fact]
        public void AbsenceRequiresACompleteInstalledIndexAndDoesNotLeakAcrossProviders()
        {
            using var source = new Sources();
            var absent = NativeTextureSourceResolver.Resolve(new(source.Root), "absent");
            Assert.Null(absent.Image);
            Assert.Equal(1, absent.MissingSource!.ArchiveCount);
            Assert.Matches("^[0-9a-f]{64}$", absent.MissingSource.IndexSha256);
            Assert.Equal(absent.MissingSource.IndexSha256,
                NativeTextureSourceResolver.Resolve(new(source.Root), "different").MissingSource!.IndexSha256);
            Assert.Null(NativeTextureSourceResolver.Resolve(new(null), "absent").MissingSource);
            File.Delete(Path.Combine(source.Root, "textures.FAT"));
            Assert.Null(NativeTextureSourceResolver.Resolve(new(source.Root), "absent").MissingSource);
        }

        [Theory]
        [InlineData("corrupt.FAT")]
        [InlineData("unknown.PAK")]
        [InlineData("broken.dds")]
        public void BrokenSourceIsAnErrorRatherThanAProvenAbsence(string filename)
        {
            using var source = new Sources();
            File.WriteAllBytes(Path.Combine(source.Root, filename), new byte[32]);
            Assert.ThrowsAny<Exception>(() => NativeTextureSourceResolver.Resolve(new(source.Root),
                filename.EndsWith("dds") ? "broken" : "absent"));
        }

        [Fact]
        public void NumericLauncherDataPackIsValidatedAndRecordedButCannotSupplyNamedDds()
        {
            using var source = new Sources();
            // Chromium DataPack v4: one numeric resource and the terminal entry.
            var pack = new byte[22];
            BitConverter.GetBytes(4u).CopyTo(pack, 0); BitConverter.GetBytes(1u).CopyTo(pack, 4);
            BitConverter.GetBytes((ushort)5).CopyTo(pack, 9); BitConverter.GetBytes(21u).CopyTo(pack, 11);
            BitConverter.GetBytes(22u).CopyTo(pack, 17); pack[21] = 7;
            string path = Path.Combine(source.Root, "launcher.pak"); File.WriteAllBytes(path, pack);
            var evidence = NativeTextureSourceResolver.Resolve(new(source.Root), "absent").MissingSource!;
            Assert.Equal(new[] { "launcher.pak" }, evidence.NumericDataPacks); Assert.Equal(1, evidence.ArchiveCount);
            pack[17] = 23; File.WriteAllBytes(path, pack);
            Assert.Throws<InvalidDataException>(() => NativeTextureSourceResolver.Resolve(new(source.Root), "absent"));
        }

        [Fact]
        public void PackDirectoryReadChecksPayloadBoundsWithoutReadingPayloads()
        {
            byte[] bytes = PakArchive.Build([new() { Name = "sample.dds", Data = Dds(1) }]);
            using var stream = new MemoryStream(bytes);
            var entries = PakArchive.ReadDirectory(stream);
            Assert.Equal("sample.dds", Assert.Single(entries).Name);
            Assert.True(stream.Position < stream.Length);
            using var truncated = new MemoryStream(bytes[..^1]);
            Assert.Throws<InvalidDataException>(() => PakArchive.ReadDirectory(truncated));
            using var headerOnly = new MemoryStream(bytes[..28]);
            Assert.Throws<InvalidDataException>(() => PakArchive.ReadDirectory(headerOnly));
        }

        private static byte[] Dds(byte blue)
        {
            var bytes = new byte[132]; "DDS "u8.CopyTo(bytes);
            foreach (var (offset, value) in new[] { (4, 124u), (12, 1u), (16, 1u), (76, 32u), (80, 0x41u), (88, 32u) })
                BitConverter.GetBytes(value).CopyTo(bytes, offset);
            bytes[128] = blue; bytes[131] = 255; return bytes;
        }

        private sealed class Sources : IDisposable
        {
            internal string Root { get; } = Path.Combine("/var/tmp", "native-texture-source-" + Guid.NewGuid().ToString("N"));
            internal Sources()
            {
                Directory.CreateDirectory(Root);
                // A well-formed empty FLAT archive allows an explicit installed-index test.
                var header = new byte[20]; "FLAT"u8.CopyTo(header);
                File.WriteAllBytes(Path.Combine(Root, "textures.FAT"), header);
            }
            public void Dispose() => Directory.Delete(Root, true);
        }
    }
}
