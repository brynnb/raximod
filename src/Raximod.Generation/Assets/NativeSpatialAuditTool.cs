using System.Numerics;
using System.Text.Json;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Assets
{
    /// <summary>
    /// Audits the exact native record ranges behind exported GLBs and publishes AAB faces as
    /// debug-only geometry. This phase is intentionally independent of collision export: native AAB
    /// membership is evidence about spatial/HQ geometry, not permission to make it a player blocker.
    /// </summary>
    public static class NativeSpatialAuditTool
    {
        public sealed record Options(
            string PlanetSideDir,
            string GlbDirectory,
            IReadOnlyCollection<string>? Records = null);

        public sealed record Failure(string Record, string Reason);
        public sealed record Result(int Examined, int Complete, int AabRecords, IReadOnlyList<Failure> Failures);

        private static readonly string[] SharedLibraries =
        {
            "uber.ubr",
            "patch1/patch1.ubr",
            "patch2/patch2.ubr",
            "patch3/patch3.ubr",
            "patch4/patch4.ubr",
            "patch5/patch5.ubr",
            "expansion1/expansion1.ubr",
        };

        public static Result Run(Options options, IProgress<string>? log = null, CancellationToken ct = default)
        {
            string root = Path.GetFullPath(options.PlanetSideDir);
            string output = Path.GetFullPath(options.GlbDirectory);
            string[] requested = (options.Records ?? Directory.EnumerateFiles(output, "*.glb")
                    .Select(Path.GetFileNameWithoutExtension).Where(value => value != null).Cast<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var unresolved = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failures = new List<Failure>();
            var aabRecords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int complete = 0;

            string[] installed = Directory.EnumerateFiles(root, "*.ubr", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] canonical = SharedLibraries.Where(relative => File.Exists(Path.Combine(root, relative))).ToArray();

            void AuditLibraries(IEnumerable<string> libraries, bool requireUnique)
            {
                var sourceCandidates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string relative in libraries)
                {
                    ct.ThrowIfCancellationRequested();
                    string path = Path.Combine(root, relative);
                    IReadOnlyList<string> names;
                    try { names = GlbExportTool.ListRecords(path); }
                    catch (InvalidOperationException) { continue; }
                    var available = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (string record in unresolved.Where(available.Contains))
                    {
                        if (!sourceCandidates.TryGetValue(record, out List<string>? sources))
                            sourceCandidates[record] = sources = new List<string>();
                        sources.Add(relative);
                    }
                    if (!requireUnique)
                    {
                        string[] records = unresolved.Where(available.Contains).ToArray();
                        if (records.Length == 0) continue;
                        AuditDecodedLibrary(path, relative, records);
                    }
                }
                if (!requireUnique) return;
                var uniqueByLibrary = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach ((string record, List<string> sources) in sourceCandidates)
                {
                    if (sources.Count != 1)
                    {
                        failures.Add(new Failure(record,
                            $"record exists in {sources.Count} fallback UBRs: {string.Join(", ", sources)}"));
                        unresolved.Remove(record);
                        continue;
                    }
                    if (!uniqueByLibrary.TryGetValue(sources[0], out List<string>? records))
                        uniqueByLibrary[sources[0]] = records = new List<string>();
                    records.Add(record);
                }
                foreach ((string relative, List<string> records) in uniqueByLibrary)
                    AuditDecodedLibrary(Path.Combine(root, relative), relative, records);
            }

            void AuditDecodedLibrary(string path, string relative, IReadOnlyCollection<string> records)
            {
                log?.Report($"auditing native record coverage in {relative} for {records.Count} assets");
                UberModel model = UberModel.Load(File.ReadAllBytes(path));
                foreach (string record in records)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        UberModel.MeshSystem system = model.FetchMeshSystem(record)
                            ?? throw new InvalidDataException(model.RefuseReason ?? "mesh-system decode failed");
                        WriteCoverage(output, relative, record, model, system);
                        if (WriteAab(output, record, system)) aabRecords.Add(record);
                        else DeleteIfPresent(Path.Combine(output, record + ".aab.json"));
                        if (system.Coverage?.Complete != true)
                            throw new InvalidDataException("record byte ranges were not fully consumed");
                        complete++;
                    }
                    catch (Exception error)
                    {
                        failures.Add(new Failure(record, error.Message));
                    }
                    unresolved.Remove(record);
                }
            }

            AuditLibraries(canonical, requireUnique: false);
            AuditLibraries(installed.Where(relative => !canonical.Contains(relative, StringComparer.OrdinalIgnoreCase)),
                requireUnique: true);
            foreach (string record in unresolved)
                failures.Add(new Failure(record, "record was not found in an installed mesh UBR"));

            if (options.Records != null)
            {
                foreach (string existing in ReadExistingAabRecords(output))
                    if (File.Exists(Path.Combine(output, existing + ".aab.json"))) aabRecords.Add(existing);
            }
            WriteIfChanged(Path.Combine(output, "aab-manifest.json"), JsonSerializer.Serialize(new
            {
                format = "raxicore-native-aab-index",
                version = 1,
                records = aabRecords.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            }, JsonOptions));
            return new Result(requested.Length, complete, aabRecords.Count, failures);
        }

        private static void WriteCoverage(string output, string library, string record, UberModel model,
            UberModel.MeshSystem system)
        {
            UberModel.MeshSystemRecordCoverage coverage = system.Coverage
                ?? throw new InvalidDataException("record coverage was not recorded");
            var semantics = new List<object>
            {
                new { name = "mesh-headers-and-sections", status = "retained-and-exported" },
                new { name = "portal-system", status = system.PortalVisibility == null ? "absent" : "retained" },
                new { name = "auxiliary-vector-arrays", status = system.AuxiliaryVectorArrays.Count == 0 ? "absent" : "retained" },
                new { name = "user-data", status = system.UserData.Length == 0 ? "absent" : "retained-opaque" },
                new { name = "skeletons", status = system.Skeletons.Count == 0 ? "absent" : "retained" },
                new { name = "native-aab", status = system.NativeAab == null ? "absent" : "retained-debug-only" },
                new { name = "embedded-collision", status = !system.DeclaresCollision ? "absent" : system.Collisions == null ? "decode-failed" : "retained" },
            };
            WriteIfChanged(Path.Combine(output, record + ".source-coverage.json"), JsonSerializer.Serialize(new
            {
                format = "raxicore-source-byte-coverage",
                version = 1,
                record,
                library,
                complete = coverage.Complete,
                archive = new
                {
                    sourceBytes = model.SourceByteLength,
                    declaredBytes = model.DeclaredByteLength,
                    completelyPartitioned = model.SourceCompletelyPartitioned,
                },
                ranges = new
                {
                    modelData = new { consumed = coverage.ModelDataConsumed, total = coverage.ModelDataTotal },
                    meshData = new { consumed = coverage.MeshDataConsumed, total = coverage.MeshDataTotal },
                    lookupEntries = new { consumed = coverage.LookupEntriesConsumed, total = coverage.LookupEntriesTotal },
                    connectionEntries = new { consumed = coverage.ConnectionEntriesConsumed, total = coverage.ConnectionEntriesTotal },
                },
                semantics,
                unknownRecordBytes = coverage.ModelDataTotal - coverage.ModelDataConsumed
                    + coverage.MeshDataTotal - coverage.MeshDataConsumed
                    + (coverage.LookupEntriesTotal - coverage.LookupEntriesConsumed) * 4
                    + (coverage.ConnectionEntriesTotal - coverage.ConnectionEntriesConsumed) * 4,
                collisionClassification = system.DeclaresCollision
                    ? "embedded-collision"
                    : system.NativeAab?.Faces.Count > 0
                        ? "native-aab-render-fallback-candidate"
                        : "no-native-spatial-candidate",
            }, JsonOptions));
        }

        private static bool WriteAab(string output, string record, UberModel.MeshSystem system)
        {
            object? document = BuildAabDocument(record, system);
            if (document == null) return false;
            WriteIfChanged(Path.Combine(output, record + ".aab.json"), JsonSerializer.Serialize(document, JsonOptions));
            return true;
        }

        internal static object? BuildAabDocument(string record, UberModel.MeshSystem system)
        {
            NativeAabSelectionEvidence evidence = NativeAabSelectionEvidence.Inspect(system);
            if (!evidence.Available) return null;
            if (!evidence.Complete)
                throw new InvalidDataException(string.Join("; ", evidence.Failures));

            var owners = new Dictionary<uint, (int MeshIndex, UberModel.Mesh Mesh, UberModel.MeshSection Section)>();
            for (int meshIndex = 0; meshIndex < system.Meshes.Count; meshIndex++)
            {
                UberModel.Mesh mesh = system.Meshes[meshIndex];
                foreach (UberModel.MeshSection section in mesh.Sections)
                {
                    uint key = ((section.Id & 0xffffu) << 16) | (section.MeshId & 0xffffu);
                    owners[key] = (meshIndex, mesh, section);
                }
            }

            object[] sections = system.NativeAab!.Faces.GroupBy(face => face.PackedSectionKey)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    (int meshIndex, UberModel.Mesh mesh, UberModel.MeshSection section) = owners[group.Key];
                    var remap = new Dictionary<ushort, uint>();
                    var positions = new List<Vector3>();
                    var indices = new List<uint>();
                    uint Vertex(ushort nativeIndex)
                    {
                        if (remap.TryGetValue(nativeIndex, out uint existing)) return existing;
                        uint compact = checked((uint)positions.Count);
                        remap.Add(nativeIndex, compact);
                        Vector3 value = section.Verts[nativeIndex].Position;
                        positions.Add(new Vector3(value.X, value.Z, -value.Y));
                        return compact;
                    }
                    foreach (UberModel.NativeAabFace face in group)
                    {
                        indices.Add(Vertex(face.VertexA));
                        indices.Add(Vertex(face.VertexB));
                        indices.Add(Vertex(face.VertexC));
                    }
                    return (object)new
                    {
                        meshIndex,
                        meshName = mesh.Name,
                        meshId = section.MeshId,
                        sectionId = section.Id,
                        materialName = section.MaterialName,
                        vertexCount = positions.Count,
                        indexCount = indices.Count,
                        positions = EncodePositions(positions),
                        indices = EncodeIndices(indices),
                    };
                }).ToArray();

            return new
            {
                format = "raxicore-native-aab-debug",
                version = 1,
                coordinateSystem = "right-handed-y-up-local",
                record,
                collisionEnabled = false,
                faceCount = system.NativeAab.Faces.Count,
                nodeCount = system.NativeAab.Nodes.Count + 1,
                mapCount = system.NativeAab.Map.Length,
                positionsEncoding = "base64-little-endian-float32x3",
                indicesEncoding = "base64-little-endian-uint32",
                sections,
            };
        }

        private static string EncodePositions(IReadOnlyList<Vector3> positions)
        {
            using var stream = new MemoryStream(positions.Count * 12);
            using var writer = new BinaryWriter(stream);
            foreach (Vector3 value in positions)
            {
                writer.Write(value.X);
                writer.Write(value.Y);
                writer.Write(value.Z);
            }
            return Convert.ToBase64String(stream.ToArray());
        }

        private static string EncodeIndices(IReadOnlyList<uint> indices)
        {
            using var stream = new MemoryStream(indices.Count * 4);
            using var writer = new BinaryWriter(stream);
            foreach (uint value in indices) writer.Write(value);
            return Convert.ToBase64String(stream.ToArray());
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static IEnumerable<string> ReadExistingAabRecords(string output)
        {
            string path = Path.Combine(output, "aab-manifest.json");
            if (!File.Exists(path)) yield break;
            JsonDocument document;
            try { document = JsonDocument.Parse(File.ReadAllText(path)); }
            catch (JsonException) { yield break; }
            using (document)
            {
                if (!document.RootElement.TryGetProperty("records", out JsonElement records)
                    || records.ValueKind != JsonValueKind.Array) yield break;
                foreach (JsonElement record in records.EnumerateArray())
                    if (record.ValueKind == JsonValueKind.String && record.GetString() is string value)
                        yield return value;
            }
        }

        private static void WriteIfChanged(string path, string contents)
        {
            if (File.Exists(path) && File.ReadAllText(path).Equals(contents, StringComparison.Ordinal)) return;
            File.WriteAllText(path, contents);
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }
}
