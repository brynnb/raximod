using System.Text;
using Raximod.EngineAssets.Databases;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class LosslessAsciiDatabaseTests
    {
        [Fact]
        public void PreservesHeaderPoolIndexSeparatorsAndRepeatedOffsets()
        {
            byte[] fixture = Fixture();
            LosslessAsciiDatabase database = LosslessAsciiDatabase.Parse(fixture);

            Assert.True(database.RoundTripVerified);
            Assert.Equal(fixture, database.Encode());
            Assert.Equal("thing_begin", database.SectionKeyword);
            Assert.Equal(0x01020304u, database.RootFlags);
            Assert.Equal((uint)(fixture.Length - ("opaque-prefix:chunky".Length + "asciidatabase".Length + 1 + 8)), database.RootPayloadSize);
            Assert.Equal("first", database.StringPool[0].Value);
            Assert.Equal(0x11223344u, database.NameIndexReserved);
            Assert.Equal(9u, database.NameIndexVersion);
            Assert.Equal(2, database.NameIndex.Count);
            Assert.Equal("first", database.NameIndex[0].Name);
            Assert.Equal(0, database.NameIndex[0].RecordStreamOffset);
            Assert.Equal("second", database.NameIndex[1].Name);
            Assert.Equal(12, database.NameIndex[1].RecordStreamOffset);
            Assert.Equal(4, database.Commands.Count);
            Assert.True(database.Commands[1].IsSeparator);
            Assert.Equal(new[] { "value", "same", "same", "" }, database.Commands[2].Fields);
            Assert.Equal(database.Commands[2].Words[1], database.Commands[2].Words[2]);
            Assert.Null(database.Commands[2].Symbols[3]);
            Assert.Equal(2, database.IndexedRecords.Count);
            Assert.Single(database.IndexedRecords[0].Commands, command => !command.IsSeparator);
            Assert.Equal(2, database.IndexedRecords[1].Commands.Count(command => !command.IsSeparator));
        }

        private static byte[] Fixture()
        {
            string[] symbols = ["first", "second", "end", "value", "same"];
            var offsets = new Dictionary<string, uint>();
            using var pool = new MemoryStream();
            foreach (string symbol in symbols)
            {
                offsets[symbol] = checked((uint)pool.Position);
                pool.Write(Encoding.Latin1.GetBytes(symbol));
                pool.WriteByte(0);
            }
            using var output = new MemoryStream();
            output.Write(Encoding.ASCII.GetBytes("opaque-prefix:chunky"));
            output.Write(Encoding.ASCII.GetBytes("asciidatabase"));
            output.WriteByte(0);
            WriteU32(output, 0x01020304);
            long payloadSizeOffset = output.Position;
            WriteU32(output, 0);
            output.Write(Encoding.ASCII.GetBytes("thing_begin"));
            output.WriteByte(0);
            WriteU32(output, checked((uint)pool.Length));
            pool.Position = 0;
            pool.CopyTo(output);
            WriteU32(output, 2);
            WriteU32(output, 0x11223344);
            WriteU32(output, 9);
            WriteU32(output, offsets["first"]);
            WriteU32(output, 1);
            WriteU32(output, offsets["second"]);
            WriteU32(output, 4);
            Command(output, offsets["end"]);
            WriteU32(output, 0);
            Command(output, offsets["value"], offsets["same"], offsets["same"], offsets["same"] + 1);
            Command(output, offsets["end"]);
            byte[] result = output.ToArray();
            uint payloadSize = checked((uint)(result.Length - payloadSizeOffset - 4));
            int payloadOffset = checked((int)payloadSizeOffset);
            result[payloadOffset] = (byte)payloadSize;
            result[payloadOffset + 1] = (byte)(payloadSize >> 8);
            result[payloadOffset + 2] = (byte)(payloadSize >> 16);
            result[payloadOffset + 3] = (byte)(payloadSize >> 24);
            return result;
        }

        private static void Command(Stream output, params uint[] fields)
        {
            WriteU32(output, checked((uint)fields.Length));
            foreach (uint field in fields) WriteU32(output, field);
        }

        private static void WriteU32(Stream output, uint value)
        {
            output.WriteByte((byte)value);
            output.WriteByte((byte)(value >> 8));
            output.WriteByte((byte)(value >> 16));
            output.WriteByte((byte)(value >> 24));
        }
    }
}
