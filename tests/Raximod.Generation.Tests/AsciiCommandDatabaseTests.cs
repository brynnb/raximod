using System.Text;
using Raximod.EngineAssets.Databases;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class AsciiCommandDatabaseTests
    {
        [Fact]
        public void RecoversStructurallyCompleteUnindexedPrefixRecord()
        {
            AsciiCommandDatabase database = AsciiCommandDatabase.Parse(Fixture(completePrefix: true))!;

            IReadOnlyList<AsciiCommandDatabase.Command> alpha = database.Lookup("alpha_tfactor")!;
            Assert.Equal("sc_colorop", alpha[0].Name);
            Assert.Equal(new[] { "modulate" }, alpha[0].Arguments);
            Assert.Equal("sc_end", alpha[^1].Name);
            Assert.NotNull(database.Lookup("border"));
        }

        [Fact]
        public void UsesIndexBoundaryRatherThanCommandSuffixForPrefixRecord()
        {
            AsciiCommandDatabase database = AsciiCommandDatabase.Parse(Fixture(completePrefix: false))!;

            IReadOnlyList<AsciiCommandDatabase.Command> alpha = database.Lookup("alpha_tfactor")!;
            Assert.Single(alpha);
            Assert.Equal("sc_colorop", alpha[0].Name);
            Assert.NotNull(database.Lookup("border"));
        }

        [Fact]
        public void NestedPackageEndDoesNotTruncateOwningRecord()
        {
            AsciiCommandDatabase database = AsciiCommandDatabase.Parse(NestedPackageFixture())!;

            IReadOnlyList<AsciiCommandDatabase.Command> package = database.Lookup("order_terminal")!;
            Assert.Equal(new[] {
                "efp_swap_begin", "efp_swap_end", "efp_effect", "efp_end",
            }, package.Select(command => command.Name));
            Assert.Equal(new[] { "ambient", "order_terminal_effect", "order_terminal" }, package[2].Arguments);
        }

        private static byte[] Fixture(bool completePrefix)
        {
            var symbols = new[] { "alpha_tfactor", "sc_colorop", "modulate", "sc_end", "border" };
            var pool = new List<byte>();
            var offsets = new Dictionary<string, uint>();
            foreach (string symbol in symbols)
            {
                offsets[symbol] = (uint)pool.Count;
                pool.AddRange(Encoding.Latin1.GetBytes(symbol));
                pool.Add(0);
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
            writer.Write(Encoding.Latin1.GetBytes("asciidatabase"));
            writer.Write((byte)0);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((byte)0);
            writer.Write((uint)pool.Count);
            writer.Write(pool.ToArray());
            writer.Write(1u); // one indexed record
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(offsets["border"]);
            writer.Write(completePrefix ? 7u : 5u); // one-based indexed record offset

            // Unindexed prefix at command offset 1.
            writer.Write(2u);
            writer.Write(offsets["sc_colorop"]);
            writer.Write(offsets["modulate"]);
            if (completePrefix)
            {
                writer.Write(1u);
                writer.Write(offsets["sc_end"]);
            }
            writer.Write(0u); // alignment/padding

            // Indexed border record.
            writer.Write(1u);
            writer.Write(offsets["sc_end"]);
            return stream.ToArray();
        }

        private static byte[] NestedPackageFixture()
        {
            var symbols = new[] {
                "unused", "order_terminal", "efp_swap_begin", "nc", "efp_swap_end",
                "efp_effect", "ambient", "order_terminal_effect", "efp_end",
            };
            var pool = new List<byte>();
            var offsets = new Dictionary<string, uint>();
            foreach (string symbol in symbols)
            {
                offsets[symbol] = (uint)pool.Count;
                pool.AddRange(Encoding.Latin1.GetBytes(symbol));
                pool.Add(0);
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
            writer.Write(Encoding.Latin1.GetBytes("asciidatabase"));
            writer.Write((byte)0);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((byte)0);
            writer.Write((uint)pool.Count);
            writer.Write(pool.ToArray());
            writer.Write(1u);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(offsets["order_terminal"]);
            writer.Write(1u);

            writer.Write(2u);
            writer.Write(offsets["efp_swap_begin"]);
            writer.Write(offsets["nc"]);
            writer.Write(1u);
            writer.Write(offsets["efp_swap_end"]);
            writer.Write(4u);
            writer.Write(offsets["efp_effect"]);
            writer.Write(offsets["ambient"]);
            writer.Write(offsets["order_terminal_effect"]);
            writer.Write(offsets["order_terminal"]);
            writer.Write(1u);
            writer.Write(offsets["efp_end"]);
            return stream.ToArray();
        }
    }
}
