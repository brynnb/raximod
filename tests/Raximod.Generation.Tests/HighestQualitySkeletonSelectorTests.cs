using System.Numerics;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class HighestQualitySkeletonSelectorTests
    {
        [Fact]
        public void DetailedRetainedMeshOutweighsTinyBillboardSkeleton()
        {
            var system = new UberModel.MeshSystem();
            system.Meshes.Add(Mesh("tool02", 55));
            system.Meshes.Add(Mesh("tool", 747));
            system.Skeletons.Add(Skeleton("tool02"));
            system.Skeletons.Add(Skeleton("tool"));

            HighestQualitySkeletonSelectionReport report =
                HighestQualitySkeletonSelector.Select(system, new[] { true, true });

            Assert.Equal(1, report.SelectedSkeletonIndex);
            Assert.False(report.Ambiguous);
            Assert.Equal(747, report.Candidates[1].MatchedRetainedVertexWeight);
        }

        [Fact]
        public void ExactOwnershipTieRemainsVisible()
        {
            var system = new UberModel.MeshSystem();
            system.Meshes.Add(Mesh("shared", 20));
            system.Skeletons.Add(Skeleton("shared"));
            system.Skeletons.Add(Skeleton("shared"));

            HighestQualitySkeletonSelectionReport report =
                HighestQualitySkeletonSelector.Select(system, new[] { true });

            Assert.True(report.Ambiguous);
            Assert.Contains("tie", report.Reason);
        }

        [Fact]
        public void IndependentRetainedMeshesBindToIndependentSkeletons()
        {
            var system = new UberModel.MeshSystem();
            system.Meshes.Add(Mesh("door_left", 20));
            system.Meshes.Add(Mesh("door_right", 20));
            system.Skeletons.Add(Skeleton("door_left"));
            system.Skeletons.Add(Skeleton("door_right"));

            HighestQualitySkeletonBindingReport report =
                HighestQualitySkeletonSelector.Bind(system, new[] { true, true });

            Assert.Empty(report.InvalidBindings);
            Assert.Equal(new[] { 0, 1 }, report.UsedSkeletonIndices);
            Assert.Equal(0, report.SkeletonForMesh(0));
            Assert.Equal(1, report.SkeletonForMesh(1));
        }

        [Fact]
        public void OneMeshClaimedByTwoSkeletonsFailsClosed()
        {
            var system = new UberModel.MeshSystem();
            system.Meshes.Add(Mesh("shared", 20));
            system.Skeletons.Add(Skeleton("shared"));
            system.Skeletons.Add(Skeleton("shared"));

            HighestQualitySkeletonBindingReport report =
                HighestQualitySkeletonSelector.Bind(system, new[] { true });

            HighestQualityMeshSkeletonBinding failure = Assert.Single(report.InvalidBindings);
            Assert.Equal(new[] { 0, 1 }, failure.CandidateSkeletonIndices);
        }

        private static UberModel.Mesh Mesh(string name, int vertices)
        {
            var mesh = new UberModel.Mesh { Name = name, ModelName = "record", Lod = 14 };
            mesh.Sections.Add(new UberModel.MeshSection
            {
                VertexCount = (uint)vertices,
                Verts = Enumerable.Range(0, vertices).Select(_ => new UberModel.UberVert
                {
                    Position = Vector3.Zero,
                }).ToArray(),
            });
            return mesh;
        }

        private static UberModel.Skeleton Skeleton(string bone)
        {
            var skeleton = new UberModel.Skeleton { Name = bone };
            skeleton.Bones.Add(new UberModel.Bone { Name = bone });
            return skeleton;
        }
    }
}
