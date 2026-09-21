using System.Numerics;
using System.Text.Json;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Assets
{
    public sealed record HighestQualityMeshCatalogIssue(
        string Severity,
        string Code,
        string Library,
        string Record,
        string Message,
        IReadOnlyList<int> MeshIndices,
        IReadOnlyList<int> SkeletonIndices);

    public sealed record HighestQualityMeshCatalogMetrics(
        int InstalledLibraries,
        int AuditedMeshLibraries,
        int SkippedAnimationLibraries,
        int Records,
        int DecodedRecords,
        int DecodeFailures,
        int Meshes,
        int RetainedMeshes,
        int Skeletons,
        int Errors,
        int Warnings);

    public sealed record HighestQualityMeshCatalogAuditDocument(
        string Format,
        int Version,
        string GeneratedAt,
        string Policy,
        HighestQualityMeshCatalogMetrics Metrics,
        IReadOnlyList<HighestQualityMeshCatalogIssue> Issues);

    /// <summary>
    /// Audits every installed UBR sequentially. It never exports geometry and retains only one decoded
    /// archive at a time, so a full-client check cannot accidentally become a parallel memory spike.
    /// </summary>
    public static class HighestQualityMeshCatalogAudit
    {
        public sealed record Options(string PlanetSideDirectory, string OutputPath);

        public static HighestQualityMeshCatalogAuditDocument Run(
            Options options,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string root = Path.GetFullPath(options.PlanetSideDirectory);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            string[] libraries = Directory.EnumerateFiles(root, "*.ubr", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] meshLibraries = libraries.Where(path => !IsAnimationLibrary(path)).ToArray();
            var issues = new List<HighestQualityMeshCatalogIssue>();
            int records = 0, decoded = 0, failures = 0, meshes = 0, retained = 0, skeletons = 0;

            foreach (string libraryPath in meshLibraries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string library = Path.GetRelativePath(root, libraryPath).Replace('\\', '/');
                IReadOnlyList<string> names;
                UberModel archive;
                try
                {
                    names = GlbExportTool.ListRecords(libraryPath);
                    archive = UberModel.Load(File.ReadAllBytes(libraryPath));
                }
                catch (Exception exception)
                {
                    failures++;
                    issues.Add(new HighestQualityMeshCatalogIssue(
                        "error", "library-decode-failed", library, "*", exception.Message, [], []));
                    continue;
                }
                progress?.Report($"auditing {library}: {names.Count} records");
                records += names.Count;
                for (int recordIndex = 0; recordIndex < names.Count; recordIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string record = names[recordIndex];
                    UberModel.MeshSystem? system;
                    try
                    {
                        system = archive.FetchMeshSystemAt(recordIndex);
                    }
                    catch (Exception exception)
                    {
                        system = null;
                        issues.Add(new HighestQualityMeshCatalogIssue(
                            "warning", "record-decode-threw", library, record, exception.Message, [], []));
                    }
                    if (system == null)
                    {
                        failures++;
                        issues.Add(new HighestQualityMeshCatalogIssue(
                            "warning", "record-decode-failed", library, record,
                            archive.RefuseReason ?? "native mesh system was not decoded", [], []));
                        continue;
                    }
                    decoded++;
                    meshes += system.Meshes.Count;
                    skeletons += system.Skeletons.Count;
                    HighestQualityMeshSelectionReport meshReport = HighestQualityMeshSelector.Select(system);
                    bool[] keep = meshReport.CreateKeepMask();
                    retained += meshReport.KeptCount;
                    foreach (HighestQualityMeshInvariantViolation violation in meshReport.InvariantViolations)
                        issues.Add(new HighestQualityMeshCatalogIssue(
                            "error", violation.Code, library, record, violation.Message,
                            violation.MeshIndices, []));

                    HighestQualitySkeletonBindingReport skeletonReport =
                        HighestQualitySkeletonSelector.Bind(system, keep);
                    foreach (HighestQualityMeshSkeletonBinding binding in skeletonReport.InvalidBindings)
                    {
                        issues.Add(new HighestQualityMeshCatalogIssue(
                            "error", "mesh-skeleton-ownership-unresolved", library, record,
                            $"retained mesh {binding.MeshIndex} '{binding.MeshName}': {binding.Reason}",
                            new[] { binding.MeshIndex }, binding.CandidateSkeletonIndices));
                    }

                    foreach ((HighestQualityMeshCandidate A, HighestQualityMeshCandidate B) pair in
                             ProbableDuplicateRepresentations(system, meshReport))
                    {
                        issues.Add(new HighestQualityMeshCatalogIssue(
                            "warning", "retained-lod-representation-overlap", library, record,
                            $"retained meshes {pair.A.MeshIndex} '{pair.A.MeshName}' (LOD {pair.A.NativeLod}) and " +
                            $"{pair.B.MeshIndex} '{pair.B.MeshName}' (LOD {pair.B.NativeLod}) have overlapping native " +
                            "bounds/materials and may be one unresolved LOD family",
                            new[] { pair.A.MeshIndex, pair.B.MeshIndex }, []));
                    }
                }
            }

            HighestQualityMeshCatalogIssue[] ordered = issues
                .OrderBy(issue => issue.Severity == "error" ? 0 : 1)
                .ThenBy(issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(issue => issue.Library, StringComparer.OrdinalIgnoreCase)
                .ThenBy(issue => issue.Record, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var metrics = new HighestQualityMeshCatalogMetrics(
                libraries.Length, meshLibraries.Length, libraries.Length - meshLibraries.Length,
                records, decoded, failures, meshes, retained, skeletons,
                ordered.Count(issue => issue.Severity == "error"),
                ordered.Count(issue => issue.Severity == "warning"));
            var document = new HighestQualityMeshCatalogAuditDocument(
                "raxicore-highest-quality-mesh-catalog-audit", 1,
                DateTimeOffset.UtcNow.ToString("O"),
                "native LOD + exact ownership evidence; ambiguous relationships remain explicit",
                metrics, ordered);
            string output = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(document,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }));
            return document;
        }

        private static bool IsAnimationLibrary(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return name.Equals("anims", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("anim_patch", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<(HighestQualityMeshCandidate A, HighestQualityMeshCandidate B)>
            ProbableDuplicateRepresentations(
                UberModel.MeshSystem system,
                HighestQualityMeshSelectionReport report)
        {
            HighestQualityMeshCandidate[] kept = report.Candidates.Where(candidate => candidate.Keep).ToArray();
            for (int i = 0; i < kept.Length; i++)
            for (int j = i + 1; j < kept.Length; j++)
            {
                HighestQualityMeshCandidate a = kept[i], b = kept[j];
                if (a.IsBillboard == b.IsBillboard || !a.BoundsMinimum.HasValue || !b.BoundsMinimum.HasValue)
                    continue;
                if (!a.ModelName.Equals(b.ModelName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ShareMaterial(system.Meshes[a.MeshIndex], system.Meshes[b.MeshIndex])) continue;
                if (!DominantBoundsOverlap(a.BoundsMinimum.Value, a.BoundsMaximum!.Value,
                        b.BoundsMinimum.Value, b.BoundsMaximum!.Value)) continue;
                yield return (a, b);
            }
        }

        private static bool ShareMaterial(UberModel.Mesh a, UberModel.Mesh b)
        {
            var materials = a.Sections.Select(section => section.MaterialName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return b.Sections.Any(section => materials.Contains(section.MaterialName));
        }

        private static bool DominantBoundsOverlap(Vector3 amin, Vector3 amax, Vector3 bmin, Vector3 bmax)
        {
            Vector3 asize = amax - amin, bsize = bmax - bmin;
            float[] dominant = { MathF.Max(asize.X, bsize.X), MathF.Max(asize.Y, bsize.Y), MathF.Max(asize.Z, bsize.Z) };
            int[] axes = Enumerable.Range(0, 3).OrderByDescending(axis => dominant[axis]).Take(2).ToArray();
            foreach (int axis in axes)
            {
                float a0 = Axis(amin, axis), a1 = Axis(amax, axis);
                float b0 = Axis(bmin, axis), b1 = Axis(bmax, axis);
                float overlap = MathF.Max(0, MathF.Min(a1, b1) - MathF.Max(a0, b0));
                float smaller = MathF.Min(a1 - a0, b1 - b0);
                if (smaller <= 1e-4f || overlap / smaller < 0.7f) return false;
            }
            return true;
        }

        private static float Axis(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
    }
}
