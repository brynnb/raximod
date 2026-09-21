using System.Text.Json;

namespace Raximod.Generation.Assets
{
    /// <summary>
    /// Persists native mesh-selection provenance that is necessarily lost when the GLB exporter
    /// combines retained sections by material. The sidecar makes it possible to prove which native
    /// HQ meshes were kept and which LOD siblings were rejected without reverse-engineering the GLB.
    /// </summary>
    internal static class HighestQualityMeshSelectionManifestTool
    {
        public static void Write(
            string glbPath,
            string record,
            HighestQualityMeshSelectionReport report,
            IReadOnlyList<bool>? effectiveKeep = null,
            string policy = "highest-native-detail-no-distance-lod")
        {
            if (effectiveKeep is not null && effectiveKeep.Count != report.Candidates.Count)
                throw new ArgumentException("Effective mesh-selection mask has the wrong length.", nameof(effectiveKeep));
            bool reportPolicy = effectiveKeep is null
                || policy is "highest-native-detail-no-distance-lod" or "native-aab-validated-highest-quality";
            object[] candidates = report.Candidates.Select(candidate => (object)new
            {
                meshIndex = candidate.MeshIndex,
                meshName = candidate.MeshName,
                modelName = candidate.ModelName,
                encodedNativeLod = candidate.EncodedNativeLod,
                nativeLod = candidate.NativeLod,
                modelDetailedLod = candidate.ModelDetailedLod,
                vertexCount = candidate.VertexCount,
                boundsMinimum = Vector(candidate.BoundsMinimum),
                boundsMaximum = Vector(candidate.BoundsMaximum),
                extentGroup = candidate.ExtentGroup,
                isNativeDetailed = candidate.IsNativeDetailed,
                isBillboard = candidate.IsBillboard,
                isNumberedModelSibling = candidate.IsNumberedModelSibling,
                isWholeModelDistanceVariant = candidate.IsWholeModelDistanceVariant,
                referencedByNativeAab = candidate.ReferencedByNativeAab,
                keep = effectiveKeep?[candidate.MeshIndex] ?? candidate.Keep,
                decision = reportPolicy
                    ? candidate.Decision.ToString()
                    : effectiveKeep![candidate.MeshIndex]
                        ? "KeptExplicitNativeMaterial"
                        : "DroppedExplicitNativeMaterial",
                selectedMeshIndex = reportPolicy
                    ? candidate.SelectedMeshIndex
                    : effectiveKeep![candidate.MeshIndex] ? candidate.MeshIndex : null,
            }).ToArray();
            object[] invariantViolations = (reportPolicy
                ? report.InvariantViolations
                : Array.Empty<HighestQualityMeshInvariantViolation>()).Select(violation => (object)new
            {
                code = violation.Code,
                message = violation.Message,
                meshIndices = violation.MeshIndices,
            }).ToArray();
            var document = new
            {
                format = "raxicore-highest-quality-mesh-selection",
                version = 1,
                record,
                policy,
                kept = effectiveKeep?.Count(value => value) ?? report.KeptCount,
                dropped = effectiveKeep?.Count(value => !value) ?? report.DroppedCount,
                candidates,
                invariantViolations,
            };
            File.WriteAllText(
                Path.ChangeExtension(glbPath, ".mesh-selection.json"),
                JsonSerializer.Serialize(document));
        }

        private static float[]? Vector(System.Numerics.Vector3? value) => value.HasValue
            ? new[] { value.Value.X, value.Value.Y, value.Value.Z }
            : null;
    }
}
