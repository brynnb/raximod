using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Assets
{
    /// <summary>
    /// Resolves native SAABFace section keys back to decoded meshes. A complete mapping is exact
    /// source evidence for which mesh representations participated in the original spatial tree.
    /// </summary>
    public sealed record NativeAabSelectionEvidence(
        bool Available,
        bool Complete,
        IReadOnlySet<int> ReferencedMeshIndices,
        IReadOnlyList<string> Failures)
    {
        public static NativeAabSelectionEvidence Inspect(UberModel.MeshSystem system)
        {
            UberModel.NativeAabData? aab = system.NativeAab;
            if (aab == null || aab.Faces.Count == 0)
                return new NativeAabSelectionEvidence(false, false, new HashSet<int>(), Array.Empty<string>());

            var sectionOwners = new Dictionary<uint, List<(int MeshIndex, UberModel.MeshSection Section)>>();
            for (int meshIndex = 0; meshIndex < system.Meshes.Count; meshIndex++)
            {
                foreach (UberModel.MeshSection section in system.Meshes[meshIndex].Sections)
                {
                    uint key = ((section.Id & 0xffffu) << 16) | (section.MeshId & 0xffffu);
                    if (!sectionOwners.TryGetValue(key, out var owners))
                    {
                        owners = new List<(int, UberModel.MeshSection)>();
                        sectionOwners.Add(key, owners);
                    }
                    owners.Add((meshIndex, section));
                }
            }

            var referenced = new HashSet<int>();
            var failures = new List<string>();
            foreach (IGrouping<uint, UberModel.NativeAabFace> group in aab.Faces.GroupBy(face => face.PackedSectionKey))
            {
                if (!sectionOwners.TryGetValue(group.Key, out var owners) || owners.Count == 0)
                {
                    failures.Add($"AAB section key 0x{group.Key:X8} has no decoded mesh section");
                    continue;
                }
                if (owners.Count != 1)
                {
                    failures.Add($"AAB section key 0x{group.Key:X8} resolves to {owners.Count} mesh sections");
                    continue;
                }
                (int meshIndex, UberModel.MeshSection section) = owners[0];
                referenced.Add(meshIndex);
                foreach (UberModel.NativeAabFace face in group)
                {
                    if (face.VertexA >= section.VertexCount
                        || face.VertexB >= section.VertexCount
                        || face.VertexC >= section.VertexCount)
                    {
                        failures.Add($"AAB face for key 0x{group.Key:X8} exceeds {section.VertexCount} vertices");
                        break;
                    }
                }
            }
            for (int i = 0; i < aab.Map.Length; i++)
            {
                if (aab.Map[i] >= aab.Faces.Count)
                {
                    failures.Add($"AAB map entry {i} references face {aab.Map[i]} of {aab.Faces.Count}");
                    break;
                }
            }
            return new NativeAabSelectionEvidence(
                true,
                failures.Count == 0 && referenced.Count > 0,
                referenced,
                failures);
        }
    }
}
