using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Raximod.EngineAssets.Databases
{
    /// <summary>
    /// Structural, lossless decoder for PlanetSide's chunky/asciidatabase files.
    /// This layer deliberately assigns no meaning to command names: it preserves
    /// the container prefix, string-pool bytes, complete name index, command
    /// ordering, repeated commands, separators, and every original pool offset.
    /// Family-specific adapters must build on this raw view instead of reparsing
    /// or guessing where records end.
    /// </summary>
    public sealed class LosslessAsciiDatabase
    {
        public sealed record StringPoolEntry(int Index, uint Offset, string Value);

        public sealed record NameIndexEntry(
            int Index,
            uint NameOffset,
            string Name,
            bool HasSymbolicName,
            uint RecordOffset,
            int? RecordStreamOffset,
            bool IsCommandStreamSentinel);

        public sealed class RawCommand
        {
            internal RawCommand(int index, int streamOffset, uint[] words, string?[] symbols)
            {
                Index = index;
                StreamOffset = streamOffset;
                Words = Array.AsReadOnly((uint[])words.Clone());
                Symbols = Array.AsReadOnly((string?[])symbols.Clone());
                Fields = Array.AsReadOnly(symbols.Select(symbol => symbol ?? "").ToArray());
            }

            public int Index { get; }
            public int StreamOffset { get; }
            public int EncodedLength => 4 + Words.Count * 4;
            public bool IsSeparator => Words.Count == 0;
            public string Name => Fields.Count == 0 ? "" : Fields[0];
            /// <summary>Original untyped 32-bit words, exactly as encoded.</summary>
            public IReadOnlyList<uint> Words { get; }
            /// <summary>
            /// Candidate symbol interpretation for each word. A null value means
            /// the word is not an offset into the string pool and is therefore a
            /// literal/opaque value to be interpreted by a typed command adapter.
            /// </summary>
            public IReadOnlyList<string?> Symbols { get; }
            /// <summary>Legacy convenience projection; literal words appear as empty strings.</summary>
            public IReadOnlyList<string> Fields { get; }
        }

        public sealed record RawRecord(
            NameIndexEntry IndexEntry,
            int StreamStart,
            int StreamEnd,
            IReadOnlyList<RawCommand> Commands);

        private readonly List<NameIndexEntry> _nameIndex = new();
        private readonly List<RawCommand> _commands = new();
        private readonly List<RawRecord> _indexedRecords = new();
        private readonly List<StringPoolEntry> _stringPool = new();
        private readonly Dictionary<uint, string> _stringsByOffset = new();
        private byte[] _containerPrefix = Array.Empty<byte>();
        private byte[] _stringPoolBytes = Array.Empty<byte>();

        public string SectionKeyword { get; private set; } = "";
        public int RootOffset { get; private set; }
        public uint RootFlags { get; private set; }
        public uint RootPayloadSize { get; private set; }
        public uint NameIndexReserved { get; private set; }
        public uint NameIndexVersion { get; private set; }
        public int CommandStreamOffset { get; private set; }
        public int CommandStreamLength { get; private set; }
        public int DecodedByteLength { get; private set; }
        public bool RoundTripVerified { get; private set; }
        public ReadOnlyMemory<byte> ContainerPrefixBytes => _containerPrefix;
        public ReadOnlyMemory<byte> StringPoolBytes => _stringPoolBytes;
        public IReadOnlyList<StringPoolEntry> StringPool => _stringPool;
        public IReadOnlyList<NameIndexEntry> NameIndex => _nameIndex;
        public IReadOnlyList<RawCommand> Commands => _commands;
        public IReadOnlyList<RawRecord> IndexedRecords => _indexedRecords;

        public static LosslessAsciiDatabase Parse(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            var database = new LosslessAsciiDatabase();
            database.ParseInternal(data);
            if (!data.AsSpan().SequenceEqual(database.Encode()))
                throw new InvalidDataException("ADB failed its structural byte-identical round-trip invariant");
            database.RoundTripVerified = true;
            return database;
        }

        public string Symbol(uint relativeOffset)
        {
            if (relativeOffset >= _stringPoolBytes.Length)
                throw new InvalidDataException($"ADB string offset {relativeOffset} is outside its pool");
            int start = checked((int)relativeOffset);
            int end = start;
            while (end < _stringPoolBytes.Length && _stringPoolBytes[end] != 0) end++;
            if (end == _stringPoolBytes.Length)
                throw new InvalidDataException($"ADB string at {relativeOffset} is not NUL terminated");
            return Encoding.Latin1.GetString(_stringPoolBytes, start, end - start);
        }

        public byte[] Encode()
        {
            int indexBytes = checked(12 + _nameIndex.Count * 8);
            int commandBytes = _commands.Sum(command => command.EncodedLength);
            byte[] output = new byte[checked(
                _containerPrefix.Length + 4 + _stringPoolBytes.Length + indexBytes + commandBytes)];
            int offset = 0;
            Buffer.BlockCopy(_containerPrefix, 0, output, offset, _containerPrefix.Length);
            offset += _containerPrefix.Length;
            WriteU32(output, offset, checked((uint)_stringPoolBytes.Length));
            offset += 4;
            Buffer.BlockCopy(_stringPoolBytes, 0, output, offset, _stringPoolBytes.Length);
            offset += _stringPoolBytes.Length;
            WriteU32(output, offset, checked((uint)_nameIndex.Count));
            WriteU32(output, offset + 4, NameIndexReserved);
            WriteU32(output, offset + 8, NameIndexVersion);
            offset += 12;
            foreach (NameIndexEntry entry in _nameIndex)
            {
                WriteU32(output, offset, entry.NameOffset);
                WriteU32(output, offset + 4, entry.RecordOffset);
                offset += 8;
            }
            foreach (RawCommand command in _commands)
            {
                WriteU32(output, offset, checked((uint)command.Words.Count));
                offset += 4;
                foreach (uint word in command.Words)
                {
                    WriteU32(output, offset, word);
                    offset += 4;
                }
            }
            return output;
        }

        private void ParseInternal(byte[] data)
        {
            int adb = FindBytes(data, "asciidatabase", 0);
            if (adb < 0) throw new InvalidDataException("ADB is missing its asciidatabase root");
            RootOffset = adb;
            int offset = adb + "asciidatabase".Length;
            RequireRange(data.Length, offset, 1 + 4 + 4, "root header");
            if (data[offset] != 0) throw new InvalidDataException("ADB root tag is not NUL terminated");
            offset++;
            RootFlags = ReadU32(data, offset);
            RootPayloadSize = ReadU32(data, offset + 4);
            offset += 8;
            if (RootPayloadSize != 0 && (long)offset + RootPayloadSize != data.Length)
                throw new InvalidDataException(
                    $"ADB root payload size {RootPayloadSize} does not end at file length {data.Length}");

            int keywordStart = offset;
            while (offset < data.Length && data[offset] != 0) offset++;
            if (offset == data.Length) throw new InvalidDataException("ADB section keyword is not NUL terminated");
            SectionKeyword = Encoding.Latin1.GetString(data, keywordStart, offset - keywordStart);
            offset++;
            RequireRange(data.Length, offset, 4, "string-pool length");
            int poolLengthOffset = offset;
            uint poolLength = ReadU32(data, offset);
            offset += 4;
            if (poolLength > int.MaxValue) throw new InvalidDataException("ADB string pool is too large");
            RequireRange(data.Length, offset, (int)poolLength, "string pool");
            _containerPrefix = data[..poolLengthOffset];
            _stringPoolBytes = data[offset..checked(offset + (int)poolLength)];
            BuildStringPool();
            offset += (int)poolLength;

            RequireRange(data.Length, offset, 12, "name-index header");
            uint indexCount = ReadU32(data, offset);
            NameIndexReserved = ReadU32(data, offset + 4);
            NameIndexVersion = ReadU32(data, offset + 8);
            offset += 12;
            if (indexCount > int.MaxValue || (long)offset + (long)indexCount * 8 > data.Length)
                throw new InvalidDataException("ADB name index is truncated");
            int commandStreamStart = checked(offset + (int)indexCount * 8);
            int commandBytes = data.Length - commandStreamStart;
            if ((commandBytes & 3) != 0)
                throw new InvalidDataException("ADB command stream is not word aligned");
            uint expectedSentinel = checked((uint)(commandBytes / 4 + 1));
            for (int index = 0; index < (int)indexCount; index++)
            {
                uint nameOffset = ReadU32(data, offset);
                uint recordOffset = ReadU32(data, offset + 4);
                bool isSentinel = index == (int)indexCount - 1
                    && recordOffset == 0
                    && nameOffset == expectedSentinel;
                string? symbolicName = isSentinel ? null : TrySymbol(nameOffset);
                int? streamOffset = recordOffset == 0
                    ? null
                    : checked(((int)recordOffset - 1) * 4);
                _nameIndex.Add(new NameIndexEntry(
                    index,
                    nameOffset,
                    symbolicName ?? "",
                    symbolicName is not null,
                    recordOffset,
                    streamOffset,
                    isSentinel));
                offset += 8;
            }

            CommandStreamOffset = offset;
            while (offset < data.Length)
            {
                RequireRange(data.Length, offset, 4, "command arity");
                int streamOffset = offset - CommandStreamOffset;
                uint arity = ReadU32(data, offset);
                offset += 4;
                if (arity > int.MaxValue || (long)offset + (long)arity * 4 > data.Length)
                    throw new InvalidDataException($"ADB command at byte {streamOffset} is truncated (arity {arity})");
                var words = new uint[(int)arity];
                var symbols = new string?[(int)arity];
                for (int field = 0; field < (int)arity; field++)
                {
                    uint word = ReadU32(data, offset);
                    offset += 4;
                    words[field] = word;
                    symbols[field] = TrySymbol(word);
                }
                if (arity > 0 && symbols[0] is null)
                    throw new InvalidDataException($"ADB command at byte {streamOffset} has no symbolic command name");
                _commands.Add(new RawCommand(_commands.Count, streamOffset, words, symbols));
            }
            CommandStreamLength = data.Length - CommandStreamOffset;
            DecodedByteLength = data.Length;
            BuildIndexedRecords();
        }

        private void BuildStringPool()
        {
            int offset = 0;
            while (offset < _stringPoolBytes.Length)
            {
                int start = offset;
                while (offset < _stringPoolBytes.Length && _stringPoolBytes[offset] != 0) offset++;
                if (offset == _stringPoolBytes.Length)
                    throw new InvalidDataException($"ADB string pool entry at {start} is not NUL terminated");
                var entry = new StringPoolEntry(
                    _stringPool.Count,
                    checked((uint)start),
                    Encoding.Latin1.GetString(_stringPoolBytes, start, offset - start));
                _stringPool.Add(entry);
                _stringsByOffset.Add(entry.Offset, entry.Value);
                offset++;
            }
        }

        // ADB references point to entries, never arbitrary character offsets.
        // Requiring the exact start prevents a literal word or corrupt pointer
        // from being silently decoded as a plausible suffix of another string.
        private string? TrySymbol(uint relativeOffset) =>
            _stringsByOffset.GetValueOrDefault(relativeOffset);

        private void BuildIndexedRecords()
        {
            int[] starts = _nameIndex
                .Where(entry => entry.RecordStreamOffset.HasValue)
                .Select(entry => entry.RecordStreamOffset!.Value)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
            foreach (NameIndexEntry entry in _nameIndex)
            {
                if (entry.IsCommandStreamSentinel) continue;
                if (entry.RecordStreamOffset is not int start) continue;
                if (start < 0 || start >= CommandStreamLength || (start & 3) != 0)
                    throw new InvalidDataException($"ADB index '{entry.Name}' points outside/alignment of command stream: {start}");
                int end = starts.FirstOrDefault(candidate => candidate > start, CommandStreamLength);
                IReadOnlyList<RawCommand> commands = _commands
                    .Where(command => command.StreamOffset >= start && command.StreamOffset < end)
                    .ToArray();
                if (commands.Count == 0 || commands[0].StreamOffset != start)
                    throw new InvalidDataException($"ADB index '{entry.Name}' points into the middle of a command");
                _indexedRecords.Add(new RawRecord(entry, start, end, commands));
            }
        }

        private static void RequireRange(int length, int offset, int count, string description)
        {
            if (offset < 0 || count < 0 || (long)offset + count > length)
                throw new InvalidDataException($"ADB has a truncated {description}");
        }

        private static uint ReadU32(byte[] bytes, int offset) =>
            (uint)(bytes[offset] | (bytes[offset + 1] << 8) |
                   (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

        private static void WriteU32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static int FindBytes(byte[] haystack, string needle, int from)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(needle);
            for (int index = from; index + bytes.Length <= haystack.Length; index++)
            {
                if (haystack.AsSpan(index, bytes.Length).SequenceEqual(bytes)) return index;
            }
            return -1;
        }
    }
}
