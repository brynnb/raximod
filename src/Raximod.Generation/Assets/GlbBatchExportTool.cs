using Raximod.EngineAssets.Meshes;
using Raximod.EngineAssets.Textures;
using System.Text.Json;

namespace Raximod.Generation.Assets
{
    /// <summary>
    /// Sequentially exports a set of shared-library records while decoding each UBR only once. This is
    /// intentionally bounded: no worker pool and only one decoded source library is retained at a time.
    /// </summary>
    public static class GlbBatchExportTool
    {
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

        public sealed record Options(
            string PlanetSideDir,
            string OutputDirectory,
            IReadOnlyCollection<string> RecordNames,
            bool Overwrite = false,
            bool IncludeTextures = true,
            bool IncludeAnimations = false,
            IReadOnlyCollection<string>? AnimatedRecordNames = null,
            string? SharedTextureDirectory = null,
            bool SearchAllInstalledLibraries = false,
            IReadOnlyDictionary<string, string>? EmbeddedMeshSources = null,
            bool PreserveRigidBoneAttachments = false,
            bool AutoDetectNativeAnimations = false,
            IReadOnlyDictionary<string, string>? MaterialReplacements = null);

        public sealed record Failure(
            string Record,
            string Reason,
            int? NativeMeshCount = null,
            int? NativeVertexCount = null,
            IReadOnlyList<string>? SourceLibraries = null);

        public sealed record Result(
            int Requested,
            int Existing,
            int Exported,
            IReadOnlyList<string> Missing,
            IReadOnlyList<Failure> Failed,
            int InstalledLibraryCount = 0,
            int SearchedLibraryCount = 0);

        public static Result Run(Options options, IProgress<string>? log = null, CancellationToken ct = default)
        {
            string root = Path.GetFullPath(options.PlanetSideDir);
            string output = Path.GetFullPath(options.OutputDirectory);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            Directory.CreateDirectory(output);

            string[] requested = options.RecordNames
                .Where(record => !string.IsNullOrWhiteSpace(record))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(record => record, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var unresolved = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
            int existing = 0;
            if (!options.Overwrite && !options.AutoDetectNativeAnimations)
            {
                foreach (string record in requested)
                {
                    string outputPath = Path.Combine(output, record + ".glb");
                    if (!File.Exists(outputPath)) continue;
                    SynchronizeExistingTextures(outputPath, options.SharedTextureDirectory);
                    unresolved.Remove(record);
                    existing++;
                }
            }

            var failures = new List<Failure>();
            int exported = 0;
            var textures = new TextureProvider(root);
            var animatedRecords = options.AnimatedRecordNames?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<AnimRecord>? animationCatalog = options.AutoDetectNativeAnimations
                ? GlbExportTool.LoadAnimationCatalog(root)
                : null;
            string[] installedLibraries = Directory.EnumerateFiles(root, "*.ubr", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var searchedLibraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] sharedLibraries = SharedLibraries
                .Where(relative => File.Exists(Path.Combine(root, relative)))
                .ToArray();

            void ExportFrom(IEnumerable<string> libraries,
                IReadOnlyDictionary<string, List<string>>? uniqueSources = null)
            {
                foreach (string relativeLibrary in libraries)
                {
                    ct.ThrowIfCancellationRequested();
                    string libraryPath = Path.Combine(root, relativeLibrary);
                    if (!File.Exists(libraryPath) || unresolved.Count == 0) continue;
                    searchedLibraries.Add(relativeLibrary);
                    var available = new HashSet<string>(GlbExportTool.ListRecords(libraryPath),
                        StringComparer.OrdinalIgnoreCase);
                    string[] records = unresolved.Where(available.Contains).ToArray();
                    if (uniqueSources != null)
                        records = records.Where(record => uniqueSources[record].Count == 1).ToArray();
                    if (records.Length == 0) continue;

                    log?.Report($"loading {relativeLibrary} for {records.Length} assets");
                    UberModel model = UberModel.Load(File.ReadAllBytes(libraryPath));
                    foreach (string record in records)
                    {
                        try
                        {
                            bool explicitlyAnimated = options.IncludeAnimations || animatedRecords.Contains(record);
                            IReadOnlyList<string>? detectedAnimationNames = null;
                            if (animationCatalog is not null)
                            {
                                UberModel.MeshSystem? system = model.FetchMeshSystem(record);
                                if (system is not null)
                                    detectedAnimationNames = GlbExportTool.CompatibleAnimationNames(
                                        system, record, animationCatalog,
                                        explicitlyAnimated ? [record] : null);
                            }
                            bool includeAnimations = explicitlyAnimated || detectedAnimationNames is { Count: > 0 };
                            string outputPath = Path.Combine(output, record + ".glb");
                            if (!options.Overwrite && File.Exists(outputPath)
                                && ExistingAssetPreservesAnimations(outputPath, detectedAnimationNames))
                            {
                                SynchronizeExistingTextures(outputPath, options.SharedTextureDirectory);
                                existing++;
                                unresolved.Remove(record);
                                continue;
                            }
                            var exportOptions = new GlbExportTool.Options(
                                root, record, outputPath, libraryPath,
                                options.IncludeTextures,
                                IncludeAnimations: includeAnimations,
                                MaxAnimations: includeAnimations ? int.MaxValue : 0,
                                AnimationPrefixes: explicitlyAnimated ? [record] : null,
                                SharedTextureDirectory: options.SharedTextureDirectory,
                                AnimationNames: detectedAnimationNames,
                                PreserveRigidBoneAttachments: options.PreserveRigidBoneAttachments,
                                AnimationCatalog: animationCatalog,
                                MaterialReplacements: options.MaterialReplacements);
                            GlbExportTool.RunDecoded(exportOptions, libraryPath, model, textures, null, ct);
                            exported++;
                        }
                        catch (Exception exception)
                        {
                            int? meshCount = null;
                            int? vertexCount = null;
                            try
                            {
                                UberModel.MeshSystem? system = model.FetchMeshSystem(record);
                                if (system == null) throw new InvalidDataException("mesh system is null");
                                meshCount = system.Meshes.Count;
                                vertexCount = system.Meshes.Sum(mesh => mesh.Sections.Sum(section =>
                                    checked((int)section.VertexCount)));
                            }
                            catch { }
                            failures.Add(new Failure(record, exception.Message, meshCount, vertexCount,
                                new[] { relativeLibrary }));
                        }
                        unresolved.Remove(record);
                    }
                }
            }

            static void SynchronizeExistingTextures(string outputPath, string? sharedTextureDirectory)
            {
                string directory = string.IsNullOrWhiteSpace(sharedTextureDirectory)
                    ? Path.Combine(Path.GetDirectoryName(outputPath)!, "material-textures")
                    : Path.GetFullPath(sharedTextureDirectory);
                SharedTextureOutput.SynchronizeEmbeddedPngs(outputPath, directory);
            }

            // The canonical shared archive precedence stays unchanged for known assets.
            ExportFrom(sharedLibraries);

            if (options.SearchAllInstalledLibraries)
            {
                // Only records unresolved by canonical shared sources are searched globally. This
                // catches forgotten archive roles without letting duplicate map-local names override
                // the established shared-library precedence.
                string[] fallbackLibraries = installedLibraries.Where(relative =>
                    !sharedLibraries.Contains(relative, StringComparer.OrdinalIgnoreCase)).ToArray();
                var sourcesByRecord = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string relativeLibrary in fallbackLibraries)
                {
                    searchedLibraries.Add(relativeLibrary);
                    HashSet<string> available;
                    try
                    {
                        available = new HashSet<string>(
                            GlbExportTool.ListRecords(Path.Combine(root, relativeLibrary)),
                            StringComparer.OrdinalIgnoreCase);
                    }
                    catch (InvalidOperationException)
                    {
                        // Animation-only and other specialized UBR containers do not carry an Uber
                        // mesh directory. They were still inspected and count toward the complete
                        // installed archive audit, but cannot contain a render record.
                        continue;
                    }
                    foreach (string record in unresolved.Where(available.Contains))
                    {
                        if (!sourcesByRecord.TryGetValue(record, out List<string>? sources))
                            sourcesByRecord[record] = sources = new List<string>();
                        sources.Add(relativeLibrary);
                    }
                }
                ExportFrom(sourcesByRecord.Values.SelectMany(value => value)
                    .Distinct(StringComparer.OrdinalIgnoreCase), sourcesByRecord);
                foreach ((string record, List<string> sources) in sourcesByRecord
                             .Where(pair => pair.Value.Count > 1))
                {
                    failures.Add(new Failure(record,
                        "record exists in multiple installed UBRs; explicit source selection is required",
                        SourceLibraries: sources.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
                    unresolved.Remove(record);
                }

                ExportEmbeddedMeshes();
            }

            void ExportEmbeddedMeshes()
            {
                if (unresolved.Count == 0) return;
                var owners = new Dictionary<string, List<(string Library, string Record)>>(StringComparer.OrdinalIgnoreCase);
                foreach (string relativeLibrary in installedLibraries)
                {
                    ct.ThrowIfCancellationRequested();
                    string path = Path.Combine(root, relativeLibrary);
                    if (!ContainsAnyName(path, unresolved)) continue;
                    UberModel model;
                    try { model = UberModel.Load(File.ReadAllBytes(path)); }
                    catch { continue; }
                    for (int index = 0; index < model.Records.Count; index++)
                    {
                        UberModel.MeshSystem? system = model.FetchMeshSystemAt(index);
                        if (system == null) continue;
                        foreach (string target in unresolved.Where(target => system.Meshes.Any(mesh =>
                                     mesh.Name.Equals(target, StringComparison.OrdinalIgnoreCase))))
                        {
                            if (!owners.TryGetValue(target, out List<(string, string)>? matches))
                                owners[target] = matches = new List<(string, string)>();
                            matches.Add((relativeLibrary, system.Name));
                        }
                    }
                }

                foreach ((string target, List<(string Library, string Record)> matches) in owners)
                {
                    List<(string Library, string Record)> selected = matches;
                    if (matches.Count > 1
                        && options.EmbeddedMeshSources?.TryGetValue(target, out string? requestedSource) == true)
                    {
                        selected = matches.Where(match =>
                            $"{match.Library}:{match.Record}".Equals(
                                requestedSource, StringComparison.OrdinalIgnoreCase)).ToList();
                    }
                    if (selected.Count != 1)
                    {
                        failures.Add(new Failure(target,
                            "embedded mesh exists in multiple parent records; explicit source selection is required",
                            SourceLibraries: matches.Select(match => $"{match.Library}:{match.Record}").ToArray()));
                        unresolved.Remove(target);
                        continue;
                    }
                    (string relativeLibrary, string parentRecord) = selected[0];
                    try
                    {
                        string libraryPath = Path.Combine(root, relativeLibrary);
                        UberModel model = UberModel.Load(File.ReadAllBytes(libraryPath));
                        var exportOptions = new GlbExportTool.Options(
                            root,
                            parentRecord,
                            Path.Combine(output, target + ".glb"),
                            libraryPath,
                            options.IncludeTextures,
                            IncludeAnimations: false,
                            MaxAnimations: 0,
                            SharedTextureDirectory: options.SharedTextureDirectory,
                            MeshNames: new[] { target },
                            MaterialReplacements: options.MaterialReplacements);
                        GlbExportTool.RunDecoded(exportOptions, libraryPath, model, textures, log, ct);
                        exported++;
                        unresolved.Remove(target);
                        log?.Report($"exported embedded effect mesh {target} from {relativeLibrary}:{parentRecord}");
                    }
                    catch (Exception exception)
                    {
                        failures.Add(new Failure(target, exception.Message,
                            SourceLibraries: new[] { $"{relativeLibrary}:{parentRecord}" }));
                        unresolved.Remove(target);
                    }
                }
            }

            string[] missing = unresolved.OrderBy(record => record, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (Failure failure in failures)
            {
                string sources = failure.SourceLibraries?.Count > 0
                    ? $" [{string.Join(", ", failure.SourceLibraries)}]"
                    : "";
                log?.Report($"failed {failure.Record}: {failure.Reason}{sources}");
            }
            foreach (string record in missing)
            {
                log?.Report($"missing {record}: not present in " +
                    (options.SearchAllInstalledLibraries
                        ? $"any of {installedLibraries.Length} installed UBRs"
                        : "shared mesh libraries"));
            }
            return new Result(requested.Length, existing, exported, missing, failures,
                installedLibraries.Length, searchedLibraries.Count);
        }

        /// <summary>Shared guard for batch and family-specific reuse paths.
        /// File presence alone does not establish that authored clips survived.</summary>
        public static bool ExistingAssetPreservesAnimations(
            string glbPath,
            IReadOnlyList<string>? expectedAnimations)
        {
            if (expectedAnimations is not { Count: > 0 }) return true;
            try
            {
                using FileStream stream = File.OpenRead(glbPath);
                using var reader = new BinaryReader(stream);
                if (reader.ReadUInt32() != 0x46546c67u) return false;
                reader.ReadUInt32(); // glTF version
                reader.ReadUInt32(); // total length
                uint jsonLength = reader.ReadUInt32();
                if (reader.ReadUInt32() != 0x4e4f534au || jsonLength > int.MaxValue) return false;
                using JsonDocument document = JsonDocument.Parse(reader.ReadBytes((int)jsonLength));
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("skins", out JsonElement skins)
                    || skins.ValueKind != JsonValueKind.Array
                    || skins.GetArrayLength() == 0
                    || !root.TryGetProperty("animations", out JsonElement animations)
                    || animations.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }
                var present = animations.EnumerateArray()
                    .Select(animation => animation.TryGetProperty("name", out JsonElement name)
                        ? name.GetString() : null)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return expectedAnimations.All(present.Contains);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException
                                              or EndOfStreamException)
            {
                return false;
            }
        }

        private static bool ContainsAnyName(string path, IEnumerable<string> names)
        {
            byte[][] needles = names.Select(name => System.Text.Encoding.Latin1.GetBytes(name.ToLowerInvariant()))
                .Where(bytes => bytes.Length > 0).ToArray();
            if (needles.Length == 0) return false;
            int overlap = needles.Max(bytes => bytes.Length) - 1;
            byte[] buffer = new byte[65536 + overlap];
            int retained = 0;
            using FileStream stream = File.OpenRead(path);
            while (true)
            {
                int read = stream.Read(buffer, retained, 65536);
                int length = retained + read;
                for (int i = 0; i < length; i++)
                    if (buffer[i] is >= (byte)'A' and <= (byte)'Z') buffer[i] += 32;
                foreach (byte[] needle in needles)
                    if (buffer.AsSpan(0, length).IndexOf(needle) >= 0) return true;
                if (read == 0) return false;
                retained = Math.Min(overlap, length);
                buffer.AsSpan(length - retained, retained).CopyTo(buffer);
            }
        }
    }
}
