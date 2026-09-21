using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Raximod.EngineAssets.Archives;

namespace Raximod.EngineAssets.Databases
{
    /// <summary>
    /// Friendly record adapter over <see cref="LosslessAsciiDatabase"/>. The raw
    /// database remains available so callers never have to confuse this semantic
    /// convenience view with structural preservation.
    /// </summary>
    public sealed class AsciiCommandDatabase
    {
        public readonly record struct Command(string Name, IReadOnlyList<string> Arguments);

        private readonly Dictionary<string, IReadOnlyList<Command>> _records =
            new(StringComparer.OrdinalIgnoreCase);

        public LosslessAsciiDatabase Raw { get; private init; } = null!;

        public int Count => _records.Count;
        public IReadOnlyDictionary<string, IReadOnlyList<Command>> Records => _records;

        public IReadOnlyList<Command>? Lookup(string name) =>
            !string.IsNullOrEmpty(name) && _records.TryGetValue(name, out IReadOnlyList<Command>? commands)
                ? commands
                : null;

        public static AsciiCommandDatabase? TryLoad(string? assetDir, string entryName)
        {
            if (string.IsNullOrEmpty(assetDir)) return null;
            string extracted = Path.Combine(assetDir, "startup.pak-out", entryName);
            if (File.Exists(extracted)) return Parse(File.ReadAllBytes(extracted));
            string pakPath = Path.Combine(assetDir, "startup.pak");
            if (!File.Exists(pakPath)) return null;
            PakArchive pak = PakArchive.Load(File.ReadAllBytes(pakPath));
            PakEntry? entry = null;
            foreach (PakEntry candidate in pak.Entries)
            {
                if (candidate.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                {
                    entry = candidate;
                    break;
                }
            }
            return entry == null ? null : Parse(pak.Extract(entry.Name));
        }

        public static AsciiCommandDatabase? Parse(byte[] data)
        {
            LosslessAsciiDatabase raw = LosslessAsciiDatabase.Parse(data);
            var database = new AsciiCommandDatabase { Raw = raw };
            foreach (LosslessAsciiDatabase.RawRecord record in raw.IndexedRecords)
            {
                if (record.IndexEntry.Name.Length == 0 || database._records.ContainsKey(record.IndexEntry.Name))
                    continue;
                database._records[record.IndexEntry.Name] = Commands(record.Commands);
            }

            // The retail compiler stores the first record at command word 1, then
            // indexes subsequent records and terminates the index with the one-past-
            // end command word. The first record name is string-pool symbol zero.
            // Recover it from those structural boundaries; command suffixes are not
            // reliable because many record families have no explicit *_end node.
            int firstIndexedOffset = raw.IndexedRecords.Count == 0
                ? raw.CommandStreamLength
                : raw.IndexedRecords.Min(record => record.StreamStart);
            string prefixName = raw.StringPoolBytes.Length == 0 ? "" : raw.Symbol(0);
            LosslessAsciiDatabase.RawCommand[] prefixCommands = raw.Commands
                .Where(command => command.StreamOffset < firstIndexedOffset && !command.IsSeparator)
                .ToArray();
            if (prefixName.Length > 0
                && !database._records.ContainsKey(prefixName)
                && prefixCommands.Length > 0)
                database._records[prefixName] = Commands(prefixCommands);
            return database;
        }

        private static IReadOnlyList<Command> Commands(
            IEnumerable<LosslessAsciiDatabase.RawCommand> source) =>
            source.Where(command => !command.IsSeparator)
                .Select(command => new Command(command.Name, command.Fields.Skip(1).ToArray()))
                .ToArray();
    }
}
