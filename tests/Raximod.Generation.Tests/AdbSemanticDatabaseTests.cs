using System.Text;
using Raximod.EngineAssets.Databases;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class AdbSemanticDatabaseTests
    {
        [Fact]
        public void RecoversPrefixAndIndexedRecordsWithTypedValuesAndProvenance()
        {
            byte[] fixture = Fixture();
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(fixture);

            Assert.Equal(fixture, database.Raw.Encode());
            Assert.Collection(database.Records,
                prefix =>
                {
                    Assert.Equal("prefix", prefix.Name);
                    Assert.False(prefix.IsIndexed);
                    Assert.Equal(0, prefix.StreamStart);
                    Assert.Equal(AdbScalarKind.Integer, prefix.Commands[0].Arguments[0].Kind);
                    Assert.Equal(42, prefix.Commands[0].Arguments[0].Integer);
                },
                indexed =>
                {
                    Assert.Equal("indexed", indexed.Name);
                    Assert.True(indexed.IsIndexed);
                    Assert.Equal(0, indexed.NameIndex);
                    Assert.Equal(AdbScalarKind.Boolean, indexed.Commands[0].Arguments[0].Kind);
                    Assert.True(indexed.Commands[0].Arguments[0].Boolean);
                });
        }

        private static byte[] Fixture()
        {
            string[] symbols = ["prefix", "indexed", "value", "42", "true"];
            var offsets = new Dictionary<string, uint>();
            using var pool = new MemoryStream();
            foreach (string symbol in symbols)
            {
                offsets[symbol] = checked((uint)pool.Position);
                pool.Write(Encoding.Latin1.GetBytes(symbol));
                pool.WriteByte(0);
            }
            using var output = new MemoryStream();
            output.Write(Encoding.ASCII.GetBytes("chunky"));
            output.Write(Encoding.ASCII.GetBytes("asciidatabase"));
            output.WriteByte(0);
            Write(output, 0x10000);
            long payloadOffset = output.Position;
            Write(output, 0);
            output.Write(Encoding.ASCII.GetBytes("value"));
            output.WriteByte(0);
            Write(output, checked((uint)pool.Length));
            pool.Position = 0;
            pool.CopyTo(output);
            Write(output, 2);
            Write(output, 0);
            Write(output, 1);
            Write(output, offsets["indexed"]);
            Write(output, 5); // one-based word offset; second command begins at zero-based word 4
            Write(output, 8); // one-past-end one-based command word sentinel
            Write(output, 0);
            Command(output, offsets["value"], offsets["42"]);
            Write(output, 0);
            Command(output, offsets["value"], offsets["true"]);
            byte[] result = output.ToArray();
            Set(result, checked((int)payloadOffset), checked((uint)(result.Length - payloadOffset - 4)));
            return result;
        }

        private static void Command(Stream output, params uint[] words)
        {
            Write(output, checked((uint)words.Length));
            foreach (uint word in words) Write(output, word);
        }

        private static void Write(Stream output, uint value)
        {
            output.WriteByte((byte)value);
            output.WriteByte((byte)(value >> 8));
            output.WriteByte((byte)(value >> 16));
            output.WriteByte((byte)(value >> 24));
        }

        private static void Set(byte[] output, int offset, uint value)
        {
            output[offset] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
            output[offset + 2] = (byte)(value >> 16);
            output[offset + 3] = (byte)(value >> 24);
        }
    }
}
