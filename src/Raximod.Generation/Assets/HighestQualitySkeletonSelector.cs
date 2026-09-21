using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Assets
{
    public sealed record HighestQualitySkeletonCandidate(
        int SkeletonIndex,
        string SkeletonName,
        int BoneCount,
        int ExactRetainedMeshMatches,
        long MatchedRetainedVertexWeight,
        IReadOnlyList<string> MatchedMeshNames);

    public sealed record HighestQualityMeshSkeletonBinding(
        int MeshIndex,
        string MeshName,
        int? SkeletonIndex,
        bool RequiresSkeleton,
        string Reason,
        IReadOnlyList<int> CandidateSkeletonIndices);

    /// <summary>Lossless per-mesh rig ownership for every retained native mesh.</summary>
    public sealed class HighestQualitySkeletonBindingReport
    {
        internal HighestQualitySkeletonBindingReport(IReadOnlyList<HighestQualityMeshSkeletonBinding> bindings)
        {
            Bindings = bindings;
        }

        public IReadOnlyList<HighestQualityMeshSkeletonBinding> Bindings { get; }
        public IReadOnlyList<int> UsedSkeletonIndices => Bindings
            .Where(binding => binding.SkeletonIndex.HasValue)
            .Select(binding => binding.SkeletonIndex!.Value)
            .Distinct().Order().ToArray();
        public IReadOnlyList<HighestQualityMeshSkeletonBinding> InvalidBindings => Bindings
            .Where(binding => binding.RequiresSkeleton && !binding.SkeletonIndex.HasValue)
            .ToArray();

        public int? SkeletonForMesh(int meshIndex) =>
            Bindings.First(binding => binding.MeshIndex == meshIndex).SkeletonIndex;
    }

    /// <summary>Auditable skeleton ownership decision for the retained native meshes.</summary>
    public sealed class HighestQualitySkeletonSelectionReport
    {
        internal HighestQualitySkeletonSelectionReport(
            IReadOnlyList<HighestQualitySkeletonCandidate> candidates,
            int? selectedSkeletonIndex,
            bool ambiguous,
            string reason)
        {
            Candidates = candidates;
            SelectedSkeletonIndex = selectedSkeletonIndex;
            Ambiguous = ambiguous;
            Reason = reason;
        }

        public IReadOnlyList<HighestQualitySkeletonCandidate> Candidates { get; }
        public int? SelectedSkeletonIndex { get; }
        public bool Ambiguous { get; }
        public string Reason { get; }
    }

    /// <summary>
    /// Selects the skeleton that owns the retained native meshes. Exact bone/mesh relationships are
    /// weighted by retained vertex count so a tiny billboard rig cannot beat its detailed sibling.
    /// No fuzzy name normalization participates.
    /// </summary>
    public static class HighestQualitySkeletonSelector
    {
        /// <summary>
        /// Binds each retained mesh to its exact owning skeleton. Multiple independent rigs are valid;
        /// ambiguity exists only when one mesh itself could belong to more than one skeleton.
        /// </summary>
        public static HighestQualitySkeletonBindingReport Bind(
            UberModel.MeshSystem system,
            IReadOnlyList<bool> keep)
        {
            ArgumentNullException.ThrowIfNull(system);
            ArgumentNullException.ThrowIfNull(keep);
            if (keep.Count != system.Meshes.Count)
                throw new ArgumentException("Skeleton binding mask has the wrong length.", nameof(keep));

            var boneNames = system.Skeletons.Select(skeleton => skeleton.Bones
                .Select(bone => bone.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToArray();
            var bindings = new List<HighestQualityMeshSkeletonBinding>();
            for (int meshIndex = 0; meshIndex < system.Meshes.Count; meshIndex++)
            {
                if (!keep[meshIndex]) continue;
                UberModel.Mesh mesh = system.Meshes[meshIndex];
                int[] exactOwners = Enumerable.Range(0, system.Skeletons.Count)
                    .Where(index => boneNames[index].Contains(mesh.Name)).ToArray();
                if (exactOwners.Length == 1)
                {
                    bindings.Add(new HighestQualityMeshSkeletonBinding(
                        meshIndex, mesh.Name, exactOwners[0], true, "exact native bone/mesh ownership", exactOwners));
                    continue;
                }
                if (exactOwners.Length > 1)
                {
                    bindings.Add(new HighestQualityMeshSkeletonBinding(
                        meshIndex, mesh.Name, null, true, "multiple skeletons contain the exact mesh bone", exactOwners));
                    continue;
                }

                UberModel.MeshSection[] skinned = mesh.Sections.Where(section => section.HasSkin).ToArray();
                if (skinned.Length == 0)
                {
                    bindings.Add(new HighestQualityMeshSkeletonBinding(
                        meshIndex, mesh.Name, null, false, "static mesh has no native skeleton ownership", []));
                    continue;
                }
                int maximumBone = skinned.SelectMany(section => section.Verts)
                    .SelectMany(vertex => new[] { (int)vertex.BoneA, (int)vertex.BoneB })
                    .DefaultIfEmpty(-1).Max();
                int[] compatible = Enumerable.Range(0, system.Skeletons.Count)
                    .Where(index => system.Skeletons[index].Bones.Count > maximumBone).ToArray();
                bindings.Add(compatible.Length == 1
                    ? new HighestQualityMeshSkeletonBinding(
                        meshIndex, mesh.Name, compatible[0], true,
                        "only skeleton compatible with native skin indices", compatible)
                    : new HighestQualityMeshSkeletonBinding(
                        meshIndex, mesh.Name, null, true,
                        compatible.Length == 0
                            ? $"no skeleton contains native skin index {maximumBone}"
                            : $"{compatible.Length} skeletons accept native skin index {maximumBone} without ownership evidence",
                        compatible));
            }
            return new HighestQualitySkeletonBindingReport(bindings);
        }

        public static HighestQualitySkeletonSelectionReport Select(
            UberModel.MeshSystem system,
            IReadOnlyList<bool> keep)
        {
            ArgumentNullException.ThrowIfNull(system);
            ArgumentNullException.ThrowIfNull(keep);
            if (keep.Count != system.Meshes.Count)
                throw new ArgumentException("Skeleton selection mask has the wrong length.", nameof(keep));
            if (system.Skeletons.Count == 0)
                return new HighestQualitySkeletonSelectionReport([], null, false, "record has no skeletons");

            var retained = system.Meshes.Select((mesh, index) => new
                {
                    Mesh = mesh,
                    Index = index,
                    Vertices = mesh.Sections.Sum(section => checked((long)section.VertexCount)),
                })
                .Where(entry => keep[entry.Index])
                .ToArray();
            HighestQualitySkeletonCandidate[] candidates = system.Skeletons
                .Select((skeleton, index) =>
                {
                    var bones = skeleton.Bones.Select(bone => bone.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var matches = retained.Where(entry => bones.Contains(entry.Mesh.Name)).ToArray();
                    return new HighestQualitySkeletonCandidate(
                        index,
                        skeleton.Name,
                        skeleton.Bones.Count,
                        matches.Length,
                        matches.Sum(entry => entry.Vertices),
                        matches.Select(entry => entry.Mesh.Name).ToArray());
                })
                .ToArray();

            long bestWeight = candidates.Max(candidate => candidate.MatchedRetainedVertexWeight);
            int bestMatches = candidates
                .Where(candidate => candidate.MatchedRetainedVertexWeight == bestWeight)
                .Max(candidate => candidate.ExactRetainedMeshMatches);
            HighestQualitySkeletonCandidate[] winners = candidates.Where(candidate =>
                    candidate.MatchedRetainedVertexWeight == bestWeight &&
                    candidate.ExactRetainedMeshMatches == bestMatches)
                .ToArray();
            bool evidenceFree = bestWeight == 0 && bestMatches == 0;
            bool ambiguous = winners.Length > 1;
            string reason = evidenceFree
                ? candidates.Length == 1
                    ? "single skeleton; no exact retained-mesh ownership name"
                    : "multiple skeletons have no exact retained-mesh ownership name"
                : ambiguous
                    ? $"{winners.Length} skeletons tie on exact retained-mesh ownership"
                    : "highest exact retained-mesh vertex ownership";
            return new HighestQualitySkeletonSelectionReport(
                candidates,
                winners[0].SkeletonIndex,
                ambiguous,
                reason);
        }
    }
}
