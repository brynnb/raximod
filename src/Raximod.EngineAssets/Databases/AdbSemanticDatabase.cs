using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Raximod.EngineAssets.Databases
{
    public enum AdbScalarKind
    {
        Text,
        Boolean,
        Integer,
        Real,
        OpaqueWord,
    }

    /// <summary>
    /// Non-destructive lexical interpretation of one ADB argument. The original
    /// word and text are always retained; family schemas may add stronger roles
    /// (reference, enum, distance, color, and so on) without reparsing bytes.
    /// </summary>
    public sealed record AdbScalar(
        uint Word,
        string? Text,
        AdbScalarKind Kind,
        bool? Boolean,
        long? Integer,
        double? Real);

    public sealed record AdbSemanticCommand(
        int RawCommandIndex,
        int StreamOffset,
        string Name,
        IReadOnlyList<AdbScalar> Arguments);

    public sealed record AdbSemanticRecord(
        string Name,
        int StreamStart,
        int StreamEnd,
        bool IsIndexed,
        int? NameIndex,
        IReadOnlyList<AdbSemanticCommand> Commands);

    /// <summary>
    /// Complete record-oriented and lexically typed view over a verified raw
    /// database. It recovers the compiler's prefix record structurally and does
    /// not use command-name heuristics or discard separator/opaque evidence from
    /// the underlying <see cref="LosslessAsciiDatabase"/>.
    /// </summary>
    public sealed class AdbSemanticDatabase
    {
        private readonly List<AdbSemanticRecord> _records = new();
        private readonly Dictionary<string, List<AdbSemanticRecord>> _byName =
            new(StringComparer.OrdinalIgnoreCase);

        public LosslessAsciiDatabase Raw { get; }
        public IReadOnlyList<AdbSemanticRecord> Records => _records;
        public IReadOnlyDictionary<string, List<AdbSemanticRecord>> RecordsByName => _byName;

        private AdbSemanticDatabase(LosslessAsciiDatabase raw)
        {
            Raw = raw;
            BuildRecords();
        }

        public static AdbSemanticDatabase Parse(byte[] data) =>
            new(LosslessAsciiDatabase.Parse(data));

        public IReadOnlyList<AdbSemanticRecord> Lookup(string name) =>
            _byName.TryGetValue(name, out List<AdbSemanticRecord>? records)
                ? records
                : Array.Empty<AdbSemanticRecord>();

        private void BuildRecords()
        {
            int firstIndexed = Raw.IndexedRecords.Count == 0
                ? Raw.CommandStreamLength
                : Raw.IndexedRecords.Min(record => record.StreamStart);
            LosslessAsciiDatabase.RawCommand[] prefix = Raw.Commands
                .Where(command => command.StreamOffset < firstIndexed && !command.IsSeparator)
                .ToArray();
            if (prefix.Length > 0 && Raw.StringPool.Count > 0)
                Add(new AdbSemanticRecord(
                    Raw.StringPool[0].Value,
                    0,
                    firstIndexed,
                    false,
                    null,
                    prefix.Select(Convert).ToArray()));

            foreach (LosslessAsciiDatabase.RawRecord record in Raw.IndexedRecords)
            {
                Add(new AdbSemanticRecord(
                    record.IndexEntry.Name,
                    record.StreamStart,
                    record.StreamEnd,
                    true,
                    record.IndexEntry.Index,
                    record.Commands.Where(command => !command.IsSeparator).Select(Convert).ToArray()));
            }
        }

        private void Add(AdbSemanticRecord record)
        {
            _records.Add(record);
            if (!_byName.TryGetValue(record.Name, out List<AdbSemanticRecord>? values))
                _byName[record.Name] = values = new List<AdbSemanticRecord>();
            values.Add(record);
        }

        private static AdbSemanticCommand Convert(LosslessAsciiDatabase.RawCommand command)
        {
            var arguments = new AdbScalar[Math.Max(0, command.Words.Count - 1)];
            for (int index = 1; index < command.Words.Count; index++)
                arguments[index - 1] = Scalar(command.Words[index], command.Symbols[index]);
            return new AdbSemanticCommand(
                command.Index,
                command.StreamOffset,
                command.Name,
                arguments);
        }

        private static AdbScalar Scalar(uint word, string? text)
        {
            if (text is null)
                return new AdbScalar(word, null, AdbScalarKind.OpaqueWord, null, null, null);
            if (bool.TryParse(text, out bool boolean))
                return new AdbScalar(word, text, AdbScalarKind.Boolean, boolean, null, null);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
                return new AdbScalar(word, text, AdbScalarKind.Integer, null, integer, integer);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
                return new AdbScalar(word, text, AdbScalarKind.Real, null, null, real);
            return new AdbScalar(word, text, AdbScalarKind.Text, null, null, null);
        }
    }
}
