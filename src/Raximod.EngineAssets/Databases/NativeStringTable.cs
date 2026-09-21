using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Raximod.EngineAssets.Databases
{
    /// <summary>
    /// One ordered entry from a native PlanetSide <c>.str</c> localization table.
    /// Duplicate keys remain separate entries because later source definitions are
    /// not guaranteed to be equivalent to earlier ones.
    /// </summary>
    public sealed record NativeStringTableEntry(string Key, string Value, int LineNumber);

    public sealed record NativeStringTableDiagnostic(
        string Code,
        int LineNumber,
        string Message,
        string SourceLine);

    /// <summary>
    /// Ordered, provenance-bearing reader for the single-byte localization tables
    /// distributed in startup.pak. It deliberately does not flatten duplicate keys.
    /// </summary>
    public sealed class NativeStringTable
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<NativeStringTableEntry>> _byKey;

        private NativeStringTable(
            IReadOnlyList<NativeStringTableEntry> entries,
            IReadOnlyList<NativeStringTableDiagnostic> diagnostics)
        {
            Entries = entries;
            Diagnostics = diagnostics;
            _byKey = entries
                .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<NativeStringTableEntry>)group.ToArray(),
                    StringComparer.Ordinal);
        }

        public IReadOnlyList<NativeStringTableEntry> Entries { get; }
        public IReadOnlyList<NativeStringTableDiagnostic> Diagnostics { get; }

        public static NativeStringTable Parse(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            // The installed English table contains ISO-8859-1 characters and no C1
            // code-page bytes. Latin-1 therefore preserves every source byte exactly.
            string text = Encoding.Latin1.GetString(data);
            string[] lines = text.Split('\n');
            var entries = new List<NativeStringTableEntry>();
            var diagnostics = new List<NativeStringTableDiagnostic>();

            for (int index = 0; index < lines.Length; index++)
            {
                string sourceLine = lines[index].TrimEnd('\r');
                string classification = sourceLine.TrimStart();
                if (classification.Length == 0 || classification.StartsWith('#'))
                    continue;

                int separator = sourceLine.IndexOf('=');
                if (separator <= 0)
                {
                    diagnostics.Add(new NativeStringTableDiagnostic(
                        "malformed-line",
                        index + 1,
                        "Non-comment localization line has no key/value separator.",
                        sourceLine));
                    continue;
                }

                string key = sourceLine[..separator].Trim();
                if (key.Length == 0)
                {
                    diagnostics.Add(new NativeStringTableDiagnostic(
                        "empty-key",
                        index + 1,
                        "Localization line has an empty key.",
                        sourceLine));
                    continue;
                }

                // Do not trim the value. Authored trailing spaces and literal escape
                // sequences are part of the retail string-table contract.
                entries.Add(new NativeStringTableEntry(
                    key,
                    sourceLine[(separator + 1)..],
                    index + 1));
            }

            foreach (IGrouping<string, NativeStringTableEntry> duplicate in entries
                         .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
            {
                NativeStringTableEntry first = duplicate.First();
                diagnostics.Add(new NativeStringTableDiagnostic(
                    "duplicate-key",
                    first.LineNumber,
                    $"Localization key '{duplicate.Key}' occurs on lines {string.Join(", ", duplicate.Select(entry => entry.LineNumber))}.",
                    duplicate.Key));
            }

            return new NativeStringTable(entries.ToArray(), diagnostics.ToArray());
        }

        public IReadOnlyList<NativeStringTableEntry> Find(string key) =>
            _byKey.TryGetValue(key, out IReadOnlyList<NativeStringTableEntry>? entries)
                ? entries
                : Array.Empty<NativeStringTableEntry>();

        /// <summary>
        /// Resolve a referenced localization key without silently selecting one of
        /// multiple different definitions. Identical duplicate definitions are safe.
        /// </summary>
        public NativeStringTableEntry ResolveRequired(string key, string owner)
        {
            IReadOnlyList<NativeStringTableEntry> entries = Find(key);
            if (entries.Count == 0)
                throw new InvalidDataException($"{owner} references missing localization key '{key}'");

            string value = entries[0].Value;
            if (entries.Any(entry => !entry.Value.Equals(value, StringComparison.Ordinal)))
                throw new InvalidDataException(
                    $"{owner} references ambiguous localization key '{key}' on lines " +
                    string.Join(", ", entries.Select(entry => entry.LineNumber)));

            return entries[0];
        }
    }
}
