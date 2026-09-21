using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace Raximod.EngineAssets.Databases
{
    /// <summary>
    /// Lossless reader for the structured <c>game_objects.adb</c> registry.
    /// It preserves the complete name index and command stream while exposing
    /// separate direct and cycle-checked inherited object views.
    /// </summary>
    public sealed class GameObjectDb
    {
        public sealed class GameObject
        {
            public int ClassId { get; init; }
            public string Name { get; init; } = "";
            public string Type { get; init; } = "";
            public string? ParentName { get; init; }
            public bool IsResolved { get; init; }
            public IReadOnlyList<string> InheritanceChain { get; init; } = Array.Empty<string>();
            public Dictionary<string, List<string>> Properties { get; init; } = new(StringComparer.Ordinal);
            /// <summary>Every direct add_property operation, including overwritten repeats.</summary>
            [JsonIgnore]
            public IReadOnlyList<GameObjectPropertyOperation> DirectPropertyOperations { get; init; } =
                Array.Empty<GameObjectPropertyOperation>();
            /// <summary>Exact defining record/command for each final direct or inherited property.</summary>
            [JsonIgnore]
            public IReadOnlyDictionary<string, GameObjectPropertySource> PropertySources { get; init; } =
                new Dictionary<string, GameObjectPropertySource>(StringComparer.Ordinal);

            /// <summary>Compact source evidence suitable for generated manifests.</summary>
            [JsonIgnore]
            public GameObjectProvenance Provenance => new(Name, ParentName, InheritanceChain);
        }

        public sealed record GameObjectProvenance(
            [property: JsonPropertyName("definition")] string Definition,
            [property: JsonPropertyName("parent")] string? Parent,
            [property: JsonPropertyName("inheritanceChain")] IReadOnlyList<string> InheritanceChain);

        public sealed record DecoderDiagnostics(
            [property: JsonPropertyName("decodedBytes")] int DecodedBytes,
            [property: JsonPropertyName("nameIndexEntries")] int NameIndexEntries,
            [property: JsonPropertyName("commandNodes")] int CommandNodes,
            [property: JsonPropertyName("parentCommands")] int ParentCommands,
            [property: JsonPropertyName("roundTripVerified")] bool RoundTripVerified);

        public sealed record NameIndexEntry(
            int Index,
            uint NameOffset,
            string Name,
            uint RecordOffset);

        public sealed record ResourceParentLink(
            int CommandIndex,
            string Child,
            string? Resource,
            string? Parent);
        public sealed record GameObjectPropertyOperation(
            int CommandIndex,
            int StreamOffset,
            string? Name,
            IReadOnlyList<string> Values);
        public sealed record GameObjectPropertySource(
            string DefinedBy,
            int CommandIndex,
            int StreamOffset);

        /// <summary>
        /// One command-stream node. Separators retain their original four-byte
        /// zero node; non-separators retain every original string-pool offset,
        /// so repeated properties and otherwise unknown commands round-trip.
        /// </summary>
        public sealed class RawCommand
        {
            internal RawCommand(
                int streamOffset,
                uint[] symbolOffsets,
                string[] fields)
            {
                StreamOffset = streamOffset;
                SymbolOffsets = Array.AsReadOnly((uint[])symbolOffsets.Clone());
                Fields = Array.AsReadOnly((string[])fields.Clone());
            }

            public int StreamOffset { get; }
            public bool IsSeparator => SymbolOffsets.Count == 0;
            public string Name => Fields.Count == 0 ? "" : Fields[0];
            public IReadOnlyList<uint> SymbolOffsets { get; }
            public IReadOnlyList<string> Fields { get; }
        }

        /// <summary>
        /// Do not consume an unspecified object view. Exporters must use
        /// <see cref="ResolvedObjects"/>; diagnostic tools may explicitly use
        /// <see cref="DirectObjects"/>.
        /// </summary>
        [Obsolete("Choose DirectObjects or ResolvedObjects explicitly.", error: true)]
        public IReadOnlyList<GameObject> Objects => _objects;

        /// <summary>Alias that makes the distinction from <see cref="ResolvedObjects"/> explicit.</summary>
        public IReadOnlyList<GameObject> DirectObjects => _objects;

        /// <summary>Parent-first merged definitions. Child properties override inherited values.</summary>
        public IReadOnlyList<GameObject> ResolvedObjects => _resolvedObjects;

        public IReadOnlyList<NameIndexEntry> NameIndex => _nameIndex;
        public IReadOnlyList<RawCommand> Commands => _commands;
        public IReadOnlyList<ResourceParentLink> ParentLinks => _parentLinks;
        /// <summary>Shared byte-preserving source used to audit this typed adapter.</summary>
        public LosslessAsciiDatabase Structural { get; private set; } = null!;
        public uint NameIndexCount => (uint)_nameIndex.Count;
        public uint NameIndexReserved { get; private set; }
        public uint NameIndexVersion { get; private set; }
        public int CommandStreamOffset { get; private set; }
        public int CommandStreamLength { get; private set; }
        public int DecodedByteLength { get; private set; }
        public ReadOnlyMemory<byte> StringPoolBytes => _stringPoolBytes;
        public DecoderDiagnostics Diagnostics => new(
            DecodedByteLength,
            _nameIndex.Count,
            _commands.Count,
            _parentLinks.Count,
            _roundTripVerified);

        private readonly List<GameObject> _objects = new();
        private readonly List<GameObject> _resolvedObjects = new();
        private readonly List<NameIndexEntry> _nameIndex = new();
        private readonly List<RawCommand> _commands = new();
        private readonly List<ResourceParentLink> _parentLinks = new();
        private readonly Dictionary<string, GameObject> _directByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GameObject> _resolvedByName = new(StringComparer.Ordinal);
        private byte[] _containerPrefix = Array.Empty<byte>();
        private byte[] _stringPoolBytes = Array.Empty<byte>();
        private bool _roundTripVerified;

        public static bool IsChunky(ReadOnlySpan<byte> data) =>
            data.Length >= 6
            && data[0] == 'c'
            && data[1] == 'h'
            && data[2] == 'u'
            && data[3] == 'n'
            && data[4] == 'k'
            && data[5] == 'y';

        public static bool IsGameObjects(byte[] data) =>
            IsChunky(data) && FindBytes(data, "add_resource", 0) >= 0;

        public static GameObjectDb Parse(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            var database = new GameObjectDb();
            database.Structural = LosslessAsciiDatabase.Parse(data);
            database.ParseInternal(data);
            if (!data.AsSpan().SequenceEqual(database.Encode()))
                throw new InvalidDataException(
                    "game_objects.adb failed its structural byte-identical round-trip invariant");
            database._roundTripVerified = true;
            database.ValidateAgainstStructuralSource();
            return database;
        }

        private void ValidateAgainstStructuralSource()
        {
            if (!Structural.SectionKeyword.Equals("add_resource", StringComparison.Ordinal))
                throw new InvalidDataException($"game_objects.adb has unexpected section '{Structural.SectionKeyword}'");
            if (Structural.Commands.Count != _commands.Count)
                throw new InvalidDataException(
                    $"game_objects.adb typed/raw command mismatch: {_commands.Count} != {Structural.Commands.Count}");
            for (int index = 0; index < _commands.Count; index++)
            {
                RawCommand typed = _commands[index];
                LosslessAsciiDatabase.RawCommand raw = Structural.Commands[index];
                if (typed.StreamOffset != raw.StreamOffset
                    || !typed.SymbolOffsets.SequenceEqual(raw.Words)
                    || !typed.Fields.SequenceEqual(raw.Fields))
                    throw new InvalidDataException($"game_objects.adb typed/raw mismatch at command {index}");
            }
        }

        public GameObject Resolve(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (_resolvedByName.TryGetValue(name, out GameObject? value)) return value;
            throw new KeyNotFoundException($"Unknown game object '{name}'");
        }

        public bool TryResolve(string name, out GameObject? value) =>
            _resolvedByName.TryGetValue(name, out value);

        /// <summary>
        /// Re-encodes the parsed structure. Opaque container bytes and the raw
        /// string pool are retained exactly; the pool length, complete name
        /// index, and every command node are written again from decoded fields.
        /// A byte-identical result therefore verifies both indexed metadata and
        /// the command stream instead of merely returning the source buffer.
        /// </summary>
        public byte[] Encode()
        {
            int commandBytes = _commands.Sum(command =>
                4 + checked(command.SymbolOffsets.Count * 4));
            int indexBytes = checked(12 + _nameIndex.Count * 8);
            byte[] result = new byte[checked(
                _containerPrefix.Length + 4 + _stringPoolBytes.Length + indexBytes + commandBytes)];
            Buffer.BlockCopy(_containerPrefix, 0, result, 0, _containerPrefix.Length);
            int offset = _containerPrefix.Length;
            WriteU32(result, offset, checked((uint)_stringPoolBytes.Length));
            offset += 4;
            Buffer.BlockCopy(_stringPoolBytes, 0, result, offset, _stringPoolBytes.Length);
            offset += _stringPoolBytes.Length;
            WriteU32(result, offset, checked((uint)_nameIndex.Count));
            WriteU32(result, offset + 4, NameIndexReserved);
            WriteU32(result, offset + 8, NameIndexVersion);
            offset += 12;
            foreach (NameIndexEntry entry in _nameIndex)
            {
                WriteU32(result, offset, entry.NameOffset);
                WriteU32(result, offset + 4, entry.RecordOffset);
                offset += 8;
            }
            foreach (RawCommand command in _commands)
            {
                WriteU32(result, offset, (uint)command.SymbolOffsets.Count);
                offset += 4;
                foreach (uint symbolOffset in command.SymbolOffsets)
                {
                    WriteU32(result, offset, symbolOffset);
                    offset += 4;
                }
            }
            return result;
        }

        private void ParseInternal(byte[] data)
        {
            if (!Structural.SectionKeyword.Equals("add_resource", StringComparison.Ordinal))
                throw new InvalidDataException($"game_objects.adb has unexpected section '{Structural.SectionKeyword}'");
            _containerPrefix = Structural.ContainerPrefixBytes.ToArray();
            _stringPoolBytes = Structural.StringPoolBytes.ToArray();
            NameIndexReserved = Structural.NameIndexReserved;
            NameIndexVersion = Structural.NameIndexVersion;
            CommandStreamOffset = Structural.CommandStreamOffset;
            CommandStreamLength = Structural.CommandStreamLength;
            DecodedByteLength = Structural.DecodedByteLength;
            foreach (LosslessAsciiDatabase.NameIndexEntry entry in Structural.NameIndex)
                _nameIndex.Add(new NameIndexEntry(
                    entry.Index, entry.NameOffset, entry.Name, entry.RecordOffset));
            foreach (LosslessAsciiDatabase.RawCommand source in Structural.Commands)
            {
                var command = new RawCommand(
                    source.StreamOffset,
                    source.Words.ToArray(),
                    source.Fields.ToArray());
                int commandIndex = _commands.Count;
                _commands.Add(command);
                if (command.Name != "set_resource_parent") continue;
                if (command.Fields.Count != 2 && command.Fields.Count != 4)
                    throw new InvalidDataException(
                        $"set_resource_parent at {command.StreamOffset} has {command.Fields.Count} fields");
                _parentLinks.Add(new ResourceParentLink(
                    commandIndex,
                    command.Fields[1],
                    command.Fields.Count == 4 ? command.Fields[2] : null,
                    command.Fields.Count == 4 ? command.Fields[3] : null));
            }
            BuildDirectObjects();
            BuildResolvedObjects();
        }

        private void BuildDirectObjects()
        {
            var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
            bool hasCurrent = false;
            string currentName = "";
            Dictionary<string, List<string>> currentProperties = new(StringComparer.Ordinal);
            List<GameObjectPropertyOperation> currentOperations = new();
            Dictionary<string, GameObjectPropertySource> currentSources = new(StringComparer.Ordinal);

            void Flush()
            {
                if (!hasCurrent) return;
                string type = PropertyValue(currentProperties, "type") ?? "";
                var value = new GameObject
                {
                    ClassId = indexOf.TryGetValue(currentName, out int existing) ? existing : _objects.Count,
                    Name = currentName,
                    Type = type,
                    ParentName = null,
                    IsResolved = false,
                    InheritanceChain = new[] { currentName },
                    Properties = CloneProperties(currentProperties),
                    DirectPropertyOperations = currentOperations.ToArray(),
                    PropertySources = CloneSources(currentSources),
                };
                if (indexOf.TryGetValue(currentName, out int slot)) _objects[slot] = value;
                else
                {
                    indexOf[currentName] = value.ClassId;
                    _objects.Add(value);
                }
            }

            for (int commandIndex = 0; commandIndex < _commands.Count; commandIndex++)
            {
                RawCommand command = _commands[commandIndex];
                if (command.Name != "add_property") continue;
                if (command.Fields.Count < 2)
                    throw new InvalidDataException($"add_property at {command.StreamOffset} has too few fields");
                string name = command.Fields[1];
                if (!hasCurrent || name != currentName)
                {
                    Flush();
                    hasCurrent = true;
                    currentName = name;
                    currentProperties = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    currentOperations = new List<GameObjectPropertyOperation>();
                    currentSources = new Dictionary<string, GameObjectPropertySource>(StringComparer.Ordinal);
                }
                // A two-field add_property creates/redefines an empty object.
                // Four such records exist in the retail database and remain
                // explicit operations rather than disappearing from this view.
                if (command.Fields.Count == 2)
                {
                    currentOperations.Add(new GameObjectPropertyOperation(
                        commandIndex, command.StreamOffset, null, Array.Empty<string>()));
                    continue;
                }
                string property = command.Fields[2];
                string[] values = command.Fields.Skip(3).ToArray();
                currentOperations.Add(new GameObjectPropertyOperation(
                    commandIndex, command.StreamOffset, property, values));
                currentProperties[property] = values.ToList();
                currentSources[property] = new GameObjectPropertySource(
                    currentName, commandIndex, command.StreamOffset);
            }
            Flush();

            var finalParents = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ResourceParentLink link in _parentLinks)
            {
                if (link.Parent is null) finalParents.Remove(link.Child);
                else finalParents[link.Child] = link.Parent;
            }
            for (int index = 0; index < _objects.Count; index++)
            {
                GameObject value = _objects[index];
                string? parent = finalParents.GetValueOrDefault(value.Name);
                GameObject withParent = new()
                {
                    ClassId = value.ClassId,
                    Name = value.Name,
                    Type = value.Type,
                    ParentName = parent,
                    IsResolved = false,
                    InheritanceChain = value.InheritanceChain,
                    Properties = value.Properties,
                    DirectPropertyOperations = value.DirectPropertyOperations,
                    PropertySources = value.PropertySources,
                };
                _objects[index] = withParent;
                _directByName[withParent.Name] = withParent;
            }
        }

        private void BuildResolvedObjects()
        {
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var path = new List<string>();

            GameObject ResolveOne(GameObject direct)
            {
                if (_resolvedByName.TryGetValue(direct.Name, out GameObject? cached)) return cached;
                if (!visiting.Add(direct.Name))
                {
                    int cycleStart = path.FindIndex(item => item == direct.Name);
                    IEnumerable<string> cycle = cycleStart >= 0 ? path.Skip(cycleStart) : path;
                    throw new InvalidDataException(
                        $"game_objects.adb inheritance cycle: {string.Join(" -> ", cycle.Append(direct.Name))}");
                }
                path.Add(direct.Name);
                try
                {
                    Dictionary<string, List<string>> properties;
                    Dictionary<string, GameObjectPropertySource> propertySources;
                    List<string> chain;
                    if (direct.ParentName is null)
                    {
                        properties = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                        propertySources = new Dictionary<string, GameObjectPropertySource>(StringComparer.Ordinal);
                        chain = new List<string>();
                    }
                    else
                    {
                        if (!_directByName.TryGetValue(direct.ParentName, out GameObject? parent))
                            throw new InvalidDataException(
                                $"game object '{direct.Name}' has missing parent '{direct.ParentName}'");
                        GameObject resolvedParent = ResolveOne(parent);
                        properties = CloneProperties(resolvedParent.Properties);
                        propertySources = CloneSources(resolvedParent.PropertySources);
                        chain = resolvedParent.InheritanceChain.ToList();
                    }
                    foreach ((string key, List<string> values) in direct.Properties)
                    {
                        properties[key] = new List<string>(values);
                        propertySources[key] = direct.PropertySources[key];
                    }
                    chain.Add(direct.Name);
                    var resolved = new GameObject
                    {
                        ClassId = direct.ClassId,
                        Name = direct.Name,
                        Type = PropertyValue(properties, "type") ?? direct.Type,
                        ParentName = direct.ParentName,
                        IsResolved = true,
                        InheritanceChain = chain,
                        Properties = properties,
                        DirectPropertyOperations = direct.DirectPropertyOperations,
                        PropertySources = propertySources,
                    };
                    _resolvedByName[resolved.Name] = resolved;
                    return resolved;
                }
                finally
                {
                    path.RemoveAt(path.Count - 1);
                    visiting.Remove(direct.Name);
                }
            }

            foreach (GameObject direct in _objects) _resolvedObjects.Add(ResolveOne(direct));
        }

        private static Dictionary<string, List<string>> CloneProperties(
            IReadOnlyDictionary<string, List<string>> source)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach ((string key, List<string> values) in source)
                result[key] = new List<string>(values);
            return result;
        }

        private static Dictionary<string, GameObjectPropertySource> CloneSources(
            IReadOnlyDictionary<string, GameObjectPropertySource> source) =>
            source.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        private static string? PropertyValue(
            IReadOnlyDictionary<string, List<string>> properties,
            string name) => GameObjectPropertyReader.Scalar(properties, "resolved inheritance", name);

        private static string PoolString(byte[] data, int poolBase, uint poolSize, uint relativeOffset)
        {
            if (relativeOffset >= poolSize)
                throw new InvalidDataException($"game_objects.adb string offset {relativeOffset} is outside its pool");
            int start = checked(poolBase + (int)relativeOffset);
            int limit = checked(poolBase + (int)poolSize);
            int end = start;
            while (end < limit && data[end] != 0) end++;
            if (end == limit)
                throw new InvalidDataException($"game_objects.adb string at {relativeOffset} is not NUL terminated");
            return Encoding.Latin1.GetString(data, start, end - start);
        }

        private static void RequireRange(int length, int offset, int count, string description)
        {
            if (offset < 0 || count < 0 || (long)offset + count > length)
                throw new InvalidDataException($"game_objects.adb has a truncated {description}");
        }

        private static uint ReadU32(byte[] bytes, int offset) =>
            (uint)(bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24));

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
                bool matches = true;
                for (int offset = 0; offset < bytes.Length; offset++)
                {
                    if (haystack[index + offset] == bytes[offset]) continue;
                    matches = false;
                    break;
                }
                if (matches) return index;
            }
            return -1;
        }
    }
}
