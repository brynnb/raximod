using System.Numerics;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Assets
{
    /// <summary>Why one native mesh is retained or rejected by the highest-quality export policy.</summary>
    public enum HighestQualityMeshDecision
    {
        KeptNativeDetailed,
        KeptNativeDetailedBillboard,
        KeptOnlyRepresentationBillboard,
        KeptHighestVertexCount,
        DroppedWholeModelDistanceVariant,
        DroppedContinuousLodRepresentation,
        DroppedBillboard,
        DroppedEmpty,
        DroppedSupersededByNativeDetailed,
        DroppedLowerGeometrySibling,
        KeptNativeAabMember,
        DroppedOutsideNativeAab,
    }

    /// <summary>
    /// One auditable decision made for a native CMeshSystem mesh. Native LOD values are exposed both
    /// as their original unsigned bits and as a signed value because PlanetSide stores a few valid
    /// negative LODs (notably Searhus lava sheets) in the unsigned field.
    /// </summary>
    public sealed record HighestQualityMeshCandidate(
        int MeshIndex,
        string MeshName,
        string ModelName,
        uint EncodedNativeLod,
        int NativeLod,
        int? ModelDetailedLod,
        int VertexCount,
        Vector3? BoundsMinimum,
        Vector3? BoundsMaximum,
        int? ExtentGroup,
        bool IsNativeDetailed,
        bool IsBillboard,
        bool IsNumberedModelSibling,
        bool IsWholeModelDistanceVariant,
        bool ReferencedByNativeAab,
        bool Keep,
        HighestQualityMeshDecision Decision,
        int? SelectedMeshIndex);

    /// <summary>An independently checked invariant violation in a highest-quality selection report.</summary>
    public sealed record HighestQualityMeshInvariantViolation(
        string Code,
        string Message,
        IReadOnlyList<int> MeshIndices);

    /// <summary>Complete, immutable audit output for one CMeshSystem's highest-quality selection.</summary>
    public sealed class HighestQualityMeshSelectionReport
    {
        internal HighestQualityMeshSelectionReport(HighestQualityMeshCandidate[] candidates)
        {
            Candidates = Array.AsReadOnly(candidates);
            InvariantViolations = Array.AsReadOnly(Validate(candidates).ToArray());
        }

        public IReadOnlyList<HighestQualityMeshCandidate> Candidates { get; }
        public IReadOnlyList<HighestQualityMeshInvariantViolation> InvariantViolations { get; }
        public int KeptCount => Candidates.Count(candidate => candidate.Keep);
        public int DroppedCount => Candidates.Count - KeptCount;
        public bool UsesNativeAab => Candidates.Any(candidate => candidate.ReferencedByNativeAab);

        public bool[] CreateKeepMask()
        {
            var keep = new bool[Candidates.Count];
            foreach (HighestQualityMeshCandidate candidate in Candidates)
                keep[candidate.MeshIndex] = candidate.Keep;
            return keep;
        }

        public string Summary
        {
            get
            {
                string decisions = string.Join(", ", Candidates
                    .GroupBy(candidate => candidate.Decision)
                    .OrderBy(group => group.Key)
                    .Select(group => $"{group.Key}={group.Count()}"));
                return $"highest-quality mesh selection: kept {KeptCount}/{Candidates.Count}" +
                       (decisions.Length > 0 ? $" ({decisions})" : string.Empty);
            }
        }

        /// <summary>
        /// Fails closed if a future selector change accidentally retains a recognized whole-model
        /// distance variant, retains two members of an exact native LOD family, or bypasses its HQ mesh.
        /// </summary>
        public void ThrowIfInvalid()
        {
            if (InvariantViolations.Count == 0) return;
            string details = string.Join("; ", InvariantViolations.Select(violation => violation.Message));
            throw new InvalidOperationException($"Highest-quality mesh selection violated its invariants: {details}");
        }

        private static IEnumerable<HighestQualityMeshInvariantViolation> Validate(
            IReadOnlyList<HighestQualityMeshCandidate> candidates)
        {
            foreach (HighestQualityMeshCandidate candidate in candidates)
            {
                if (candidate.Keep && candidate.IsWholeModelDistanceVariant)
                {
                    yield return new HighestQualityMeshInvariantViolation(
                        "distance-variant-kept",
                        $"mesh {candidate.MeshIndex} '{candidate.MeshName}' retained a whole-model distance variant",
                        new[] { candidate.MeshIndex });
                }
            }

            foreach (IGrouping<int, HighestQualityMeshCandidate> group in candidates
                         .Where(candidate => candidate.ExtentGroup.HasValue)
                         .GroupBy(candidate => candidate.ExtentGroup!.Value))
            {
                // Preserve a native unsuffixed mesh when it is the record's only representation,
                // even when its native LOD tag is in the billboard range (apc_door2 is shipped this
                // way). A billboard is never allowed to add a second kept sibling to an extent group.
                HighestQualityMeshCandidate[] eligible = group
                    .Where(candidate => candidate.VertexCount > 0 &&
                                        (!candidate.IsBillboard || candidate.IsNativeDetailed))
                    .ToArray();
                HighestQualityMeshCandidate[] kept = group.Where(candidate => candidate.Keep).ToArray();
                int nonEmpty = group.Count(candidate => candidate.VertexCount > 0);
                int expected = eligible.Length > 0 || nonEmpty == 1 ? 1 : 0;
                if (kept.Length != expected)
                {
                    int[] indices = group.Select(candidate => candidate.MeshIndex).ToArray();
                    yield return new HighestQualityMeshInvariantViolation(
                        "extent-group-cardinality",
                        $"extent group {group.Key} retained {kept.Length} meshes but expected {expected} " +
                        $"(members: {string.Join(", ", indices)})",
                        indices);
                }

                HighestQualityMeshCandidate? preferredNative = eligible.LastOrDefault(candidate =>
                    candidate.IsNativeDetailed);
                if (preferredNative != null && !preferredNative.Keep)
                {
                    int[] indices = group.Select(candidate => candidate.MeshIndex).ToArray();
                    yield return new HighestQualityMeshInvariantViolation(
                        "native-detail-not-kept",
                        $"extent group {group.Key} did not retain native detailed mesh " +
                        $"{preferredNative.MeshIndex} '{preferredNative.MeshName}'",
                        indices);
                }
            }
        }
    }

    /// <summary>
    /// Pure PlanetSide native-mesh selector used by GLB export. It preserves every non-billboard
    /// component unless native naming/LOD evidence identifies it as a whole-model distance variant.
    /// It never uses bounds, proximity, or vertex-count similarity to delete geometry. Native
    /// billboard-tier representations are removed only when the same record contains detailed geometry.
    /// </summary>
    public static class HighestQualityMeshSelector
    {
        // PlanetSide reserves the 1000+ native LOD tier for very cheap distant
        // billboard representations. TerraSunder deliberately renders the highest-quality
        // geometry at every distance and does not run the retail LOD switcher. Keeping one
        // of these billboards beside a detailed sibling would therefore draw two versions
        // of the same object (wasting work and potentially causing doubled silhouettes,
        // seams, or z-fighting). A billboard is still retained when it is the record's only
        // usable representation; this threshold alone is never permission to erase an asset.
        private const uint BillboardLod = 1000;

        public static HighestQualityMeshSelectionReport Select(UberModel.MeshSystem system)
        {
            ArgumentNullException.ThrowIfNull(system);
            int count = system.Meshes.Count;
            var facts = new Facts[count];
            NativeAabSelectionEvidence aab = NativeAabSelectionEvidence.Inspect(system);

            var detailedLodByModel = system.Meshes
                .Where(IsNativeDetailedMesh)
                .GroupBy(mesh => mesh.ModelName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Lod, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < count; i++)
            {
                UberModel.Mesh mesh = system.Meshes[i];
                detailedLodByModel.TryGetValue(mesh.ModelName, out uint modelDetailedLod);
                bool hasModelDetailedLod = detailedLodByModel.ContainsKey(mesh.ModelName);
                facts[i] = Inspect(mesh, hasModelDetailedLod ? modelDetailedLod : null);
            }
            string?[] explicitFamilies = ExplicitZeroPaddedLodFamilies(system);

            if (aab.Available && !aab.Complete)
            {
                throw new InvalidOperationException(
                    $"Native AAB selection for '{system.Name}' is incomplete: {string.Join("; ", aab.Failures)}");
            }

            var decisions = new MutableDecision[count];
            var used = new bool[count];
            bool hasDetailedGeometry = facts.Any(fact => IsEligible(fact));
            int extentGroup = 0;
            for (int i = 0; i < count; i++)
            {
                if (used[i]) continue;
                if (facts[i].WholeModelDistanceVariant)
                {
                    used[i] = true;
                    decisions[i] = MutableDecision.Reject(
                        HighestQualityMeshDecision.DroppedWholeModelDistanceVariant);
                    continue;
                }
                if (facts[i].ContinuousLodRepresentation &&
                    facts.Any(fact => !fact.ContinuousLodRepresentation && IsEligible(fact)))
                {
                    // An all-clod_ mesh is PlanetSide's duplicate continuous-LOD player
                    // shell, not a skeleton or an animation clip. TerraSunder never selects
                    // the low-detail shell, so retain the normal detailed body and omit this
                    // duplicate. As with billboards, never drop a clod_ shell when it is the
                    // record's only usable geometry.
                    used[i] = true;
                    decisions[i] = MutableDecision.Reject(
                        HighestQualityMeshDecision.DroppedContinuousLodRepresentation);
                    continue;
                }
                if (facts[i].Billboard && hasDetailedGeometry)
                {
                    // The browser has no distance-based LOD switch that would make this
                    // mutually exclusive with the detailed mesh. Shipping both would make
                    // the billboard an always-present duplicate rather than a useful LOD.
                    used[i] = true;
                    decisions[i] = MutableDecision.Reject(HighestQualityMeshDecision.DroppedBillboard);
                    continue;
                }

                int group = extentGroup++;
                if (explicitFamilies[i] == null)
                {
                    used[i] = true;
                    decisions[i] = facts[i].VertexCount == 0
                        ? MutableDecision.Reject(HighestQualityMeshDecision.DroppedEmpty, group)
                        : MutableDecision.Retain(
                            facts[i].NativeDetailed
                                ? facts[i].Billboard
                                    ? HighestQualityMeshDecision.KeptNativeDetailedBillboard
                                    : HighestQualityMeshDecision.KeptNativeDetailed
                                : facts[i].Billboard
                                    ? HighestQualityMeshDecision.KeptOnlyRepresentationBillboard
                                    : HighestQualityMeshDecision.KeptHighestVertexCount,
                            group,
                            i);
                    continue;
                }

                int[] members = Enumerable.Range(0, count)
                    .Where(index => !used[index] &&
                                    explicitFamilies[index] != null &&
                                    explicitFamilies[index]!.Equals(
                                        explicitFamilies[i], StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (int member in members) used[member] = true;
                int[] eligible = members.Where(member => IsEligible(facts[member])).ToArray();
                int selected = eligible
                    .OrderByDescending(member => facts[member].NativeDetailed)
                    .ThenByDescending(member => facts[member].VertexCount)
                    .FirstOrDefault(-1);
                foreach (int member in members)
                {
                    Facts fact = facts[member];
                    if (member == selected)
                    {
                        decisions[member] = MutableDecision.Retain(
                            fact.NativeDetailed
                                ? fact.Billboard
                                    ? HighestQualityMeshDecision.KeptNativeDetailedBillboard
                                    : HighestQualityMeshDecision.KeptNativeDetailed
                                : fact.Billboard
                                    ? HighestQualityMeshDecision.KeptOnlyRepresentationBillboard
                                    : HighestQualityMeshDecision.KeptHighestVertexCount,
                            group,
                            selected);
                    }
                    else
                    {
                        HighestQualityMeshDecision reason = fact.VertexCount == 0
                            ? HighestQualityMeshDecision.DroppedEmpty
                            : fact.Billboard
                                ? HighestQualityMeshDecision.DroppedBillboard
                                : selected >= 0 && facts[selected].NativeDetailed
                                    ? HighestQualityMeshDecision.DroppedSupersededByNativeDetailed
                                    : HighestQualityMeshDecision.DroppedLowerGeometrySibling;
                        decisions[member] = MutableDecision.Reject(reason, group, selected >= 0 ? selected : null);
                    }
                }
            }

            var candidates = new HighestQualityMeshCandidate[count];
            for (int i = 0; i < count; i++)
            {
                UberModel.Mesh mesh = system.Meshes[i];
                Facts fact = facts[i];
                MutableDecision decision = decisions[i];
                bool referencedByNativeAab = aab.Complete && aab.ReferencedMeshIndices.Contains(i);
                candidates[i] = new HighestQualityMeshCandidate(
                    i,
                    mesh.Name,
                    mesh.ModelName,
                    mesh.Lod,
                    unchecked((int)mesh.Lod),
                    fact.ModelDetailedLod.HasValue ? unchecked((int)fact.ModelDetailedLod.Value) : null,
                    fact.VertexCount,
                    fact.HasBounds ? fact.Minimum : null,
                    fact.HasBounds ? fact.Maximum : null,
                    decision.ExtentGroup,
                    fact.NativeDetailed,
                    fact.Billboard,
                    fact.NumberedModelSibling,
                    fact.WholeModelDistanceVariant,
                    referencedByNativeAab,
                    decision.Keep,
                    decision.Keep && referencedByNativeAab
                        ? HighestQualityMeshDecision.KeptNativeAabMember
                        : decision.Decision,
                    decision.SelectedMeshIndex);
            }
            return new HighestQualityMeshSelectionReport(candidates);
        }

        private static Facts Inspect(UberModel.Mesh mesh, uint? modelDetailedLod)
        {
            int vertices = 0;
            int boundedVertices = 0;
            var minimum = new Vector3(float.MaxValue);
            var maximum = new Vector3(float.MinValue);
            foreach (UberModel.MeshSection section in mesh.Sections)
            {
                vertices += (int)section.VertexCount;
                foreach (UberModel.UberVert vertex in section.Verts)
                {
                    boundedVertices++;
                    minimum = Vector3.Min(minimum, vertex.Position);
                    maximum = Vector3.Max(maximum, vertex.Position);
                }
            }

            bool nativeDetailed = IsNativeDetailedMesh(mesh);
            bool numberedModelSibling = IsNumberedModelSibling(mesh);
            bool distanceVariant = modelDetailedLod.HasValue && numberedModelSibling &&
                                   IsWholeModelDistanceVariant(mesh, modelDetailedLod.Value);
            // Player CLOD shells may carry ordinary visor/decal sections in
            // addition to their clod_ body material (notably TR MAX). They are
            // still complete numbered duplicate bodies, not independent
            // accessories. The original all-clod_ rule remains valid; mixed
            // shells additionally require the explicit zero-padded sibling
            // name so the unsuffixed HQ body is retained without mistaking a
            // genuine accessory with one clod_ section for another body.
            bool hasContinuousLodMaterial = mesh.Sections.Any(section =>
                section.MaterialName.StartsWith("clod_", StringComparison.OrdinalIgnoreCase));
            bool continuousLodRepresentation = !IsBillboard(mesh) && hasContinuousLodMaterial && (
                mesh.Sections.All(section =>
                    section.MaterialName.StartsWith("clod_", StringComparison.OrdinalIgnoreCase)) ||
                IsZeroPaddedContinuousLodSibling(mesh));
            return new Facts(
                vertices,
                minimum,
                maximum,
                boundedVertices > 0,
                nativeDetailed,
                IsBillboard(mesh),
                numberedModelSibling,
                distanceVariant,
                continuousLodRepresentation,
                modelDetailedLod);
        }

        private static bool IsEligible(Facts facts) => !facts.Billboard && facts.VertexCount > 0;

        private static bool IsNativeDetailedMesh(UberModel.Mesh mesh) =>
            mesh.Name.Equals(mesh.ModelName, StringComparison.OrdinalIgnoreCase);

        private static bool IsWholeModelDistanceVariant(UberModel.Mesh mesh, uint detailedLod)
        {
            if (mesh.Lod <= detailedLod) return false;
            return IsNumberedModelSibling(mesh);
        }

        private static bool IsNumberedModelSibling(UberModel.Mesh mesh)
        {
            string prefix = mesh.ModelName + "_";
            if (!mesh.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            ReadOnlySpan<char> suffix = mesh.Name.AsSpan(prefix.Length);
            return suffix.Length == 1 && suffix[0] is >= '1' and <= '9';
        }

        private static bool IsZeroPaddedContinuousLodSibling(UberModel.Mesh mesh)
        {
            if (!mesh.Name.StartsWith(mesh.ModelName, StringComparison.OrdinalIgnoreCase)) return false;
            ReadOnlySpan<char> suffix = mesh.Name.AsSpan(mesh.ModelName.Length);
            return suffix.Length == 2 && suffix[0] == '0' && suffix[1] is >= '1' and <= '9';
        }

        // Some first-person tools store their billboard meshes as `name01`/`name02` beside an
        // unsuffixed detailed mesh, while all three retain a different shared MeshSystem model name.
        // Preserve that exact family in the audit output; no bounds comparison participates.
        private static string?[] ExplicitZeroPaddedLodFamilies(UberModel.MeshSystem system)
        {
            var explicitBases = system.Meshes
                .Where(IsZeroPaddedBillboardSibling)
                .Select(mesh => (mesh.ModelName, Base: mesh.Name[..^2]))
                .Where(entry => system.Meshes.Any(mesh =>
                    mesh.ModelName.Equals(entry.ModelName, StringComparison.OrdinalIgnoreCase) &&
                    mesh.Name.Equals(entry.Base, StringComparison.OrdinalIgnoreCase)))
                .Select(entry => entry.ModelName + "\0" + entry.Base)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return system.Meshes.Select(mesh =>
            {
                string baseName = IsZeroPaddedBillboardSibling(mesh) ? mesh.Name[..^2] : mesh.Name;
                string key = mesh.ModelName + "\0" + baseName;
                return explicitBases.Contains(key) ? key : null;
            }).ToArray();
        }

        private static bool IsZeroPaddedBillboardSibling(UberModel.Mesh mesh)
        {
            string name = mesh.Name;
            if (!IsBillboard(mesh) || name.Length < 3) return false;
            ReadOnlySpan<char> suffix = name.AsSpan(name.Length - 2);
            return suffix[0] == '0' && suffix[1] is >= '1' and <= '9';
        }

        // Signed comparison is intentional: a handful of native meshes encode negative detailed
        // LODs in this unsigned field and must not be mistaken for values above the billboard tier.
        private static bool IsBillboard(UberModel.Mesh mesh) => unchecked((int)mesh.Lod) >= (int)BillboardLod;

        private sealed record Facts(
            int VertexCount,
            Vector3 Minimum,
            Vector3 Maximum,
            bool HasBounds,
            bool NativeDetailed,
            bool Billboard,
            bool NumberedModelSibling,
            bool WholeModelDistanceVariant,
            bool ContinuousLodRepresentation,
            uint? ModelDetailedLod);

        private readonly record struct MutableDecision(
            bool Keep,
            HighestQualityMeshDecision Decision,
            int? ExtentGroup,
            int? SelectedMeshIndex)
        {
            public static MutableDecision Retain(
                HighestQualityMeshDecision decision,
                int extentGroup,
                int selectedMeshIndex) => new(true, decision, extentGroup, selectedMeshIndex);

            public static MutableDecision Reject(
                HighestQualityMeshDecision decision,
                int? extentGroup = null,
                int? selectedMeshIndex = null) => new(false, decision, extentGroup, selectedMeshIndex);
        }
    }
}
