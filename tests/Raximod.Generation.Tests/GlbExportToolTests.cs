using Raximod.Generation.Assets;
using Raximod.EngineAssets.Meshes;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class GlbExportToolTests
    {
        [Theory]
        [InlineData(true, true, false, false, false, true)]
        [InlineData(true, false, true, true, false, true)]
        [InlineData(true, false, false, true, false, false)]
        [InlineData(true, false, false, true, true, true)]
        [InlineData(false, true, true, true, true, false)]
        public void StaticRigidGeometryIsNotNeedlesslySkinned(
            bool hasRig,
            bool sectionHasSkin,
            bool includeAnimations,
            bool hasRigidBone,
            bool preserveRigidBoneAttachments,
            bool expected)
        {
            Assert.Equal(expected, GlbExportTool.ShouldExportSectionAsSkinned(
                hasRig, sectionHasSkin, includeAnimations, hasRigidBone,
                preserveRigidBoneAttachments));
        }

        [Fact]
        public void AnimationCompatibilityComesFromNativeClipAndSkeletonNames()
        {
            var system = new UberModel.MeshSystem { Name = "repair_silo" };
            var skeleton = new UberModel.Skeleton { Name = "repair_silo" };
            skeleton.Bones.Add(new UberModel.Bone { Name = "repair_silo_root" });
            skeleton.Bones.Add(new UberModel.Bone { Name = "repair_silo_arm", Parent = 0 });
            system.Skeletons.Add(skeleton);
            var mesh = new UberModel.Mesh
            {
                Name = "repair_silo_arm",
                ModelName = "repair_silo",
                Lod = 14,
            };
            mesh.Sections.Add(new UberModel.MeshSection { VertexCount = 3, IndexCount = 3 });
            system.Meshes.Add(mesh);

            var own = new AnimRecord { Name = "repair_silo_activate", Duration = 1 };
            own.Tracks.Add(new AnimTrack { Name = "repair_silo_arm" });
            var unrelated = new AnimRecord { Name = "unrelated_activate", Duration = 1 };
            unrelated.Tracks.Add(new AnimTrack { Name = "repair_silo_arm" });

            Assert.Equal(new[] { "repair_silo_activate" },
                GlbExportTool.CompatibleAnimationNames(system, system.Name, [own, unrelated]));

            // Shared vehicle packages can animate one hatch in a large body skeleton. An
            // explicitly requested native binding must not fail the heuristic overlap threshold.
            for (int i = 0; i < 20; i++)
                skeleton.Bones.Add(new UberModel.Bone { Name = $"static_{i}", Parent = 0 });
            Assert.Equal(new[] { "unrelated_activate" }, GlbExportTool.CompatibleAnimationNames(
                system, system.Name, [own, unrelated], animationNames: ["unrelated_activate"]));
        }

    }
}
