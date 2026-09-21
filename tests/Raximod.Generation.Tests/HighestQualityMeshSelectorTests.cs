using System.Numerics;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class HighestQualityMeshSelectorTests
    {
        [Fact]
        public void FacilityDistanceVariantsAreNeverCoExportedWithNativeDetailedMesh()
        {
            var system = new UberModel.MeshSystem();
            // Native files do not guarantee detailed-first ordering. Creation pads and facilities can
            // put larger whole-building distance meshes before the smaller unsuffixed close shell.
            system.Meshes.Add(Mesh("facility_1", "facility", 36, 200, Vector3.Zero, new Vector3(10f)));
            system.Meshes.Add(Mesh("facility_2", "facility", 64, 80, Vector3.Zero, new Vector3(9f)));
            system.Meshes.Add(Mesh("facility", "facility", 14, 20, Vector3.Zero, new Vector3(10f)));
            system.Meshes.Add(Mesh("facility_door", "facility", 14, 12,
                new Vector3(20f, 0f, 0f), new Vector3(22f, 2f, 5f)));

            HighestQualityMeshSelectionReport report = HighestQualityMeshSelector.Select(system);

            Assert.Equal(new[] { false, false, true, true }, report.CreateKeepMask());
            Assert.Equal(14, report.Candidates[0].ModelDetailedLod);
            Assert.Equal(HighestQualityMeshDecision.DroppedWholeModelDistanceVariant,
                report.Candidates[0].Decision);
            Assert.Equal(HighestQualityMeshDecision.DroppedWholeModelDistanceVariant,
                report.Candidates[1].Decision);
            Assert.Equal(HighestQualityMeshDecision.KeptNativeDetailed, report.Candidates[2].Decision);
            Assert.Empty(report.InvariantViolations);
            report.ThrowIfInvalid();
        }

        [Fact]
        public void SpatialSimilarityAloneNeverDeletesUnprovenGeometry()
        {
            var system = new UberModel.MeshSystem();
            system.Meshes.Add(Mesh("vehicle_body_high", "vehicle", 14, 100,
                Vector3.Zero, new Vector3(10f, 5f, 3f)));
            system.Meshes.Add(Mesh("vehicle_body_low", "vehicle", 60, 30,
                new Vector3(0.2f), new Vector3(9.8f, 4.8f, 2.8f)));
            system.Meshes.Add(Mesh("vehicle_body_billboard", "vehicle", 1001, 6,
                Vector3.Zero, new Vector3(10f, 5f, 3f)));
            system.Meshes.Add(Mesh("vehicle_turret_high", "vehicle", 14, 60,
                new Vector3(0f, 20f, 0f), new Vector3(4f, 24f, 3f)));
            system.Meshes.Add(Mesh("vehicle_turret_low", "vehicle", 50, 20,
                new Vector3(0.1f, 20.1f, 0.1f), new Vector3(3.9f, 23.9f, 2.9f)));

            HighestQualityMeshSelectionReport report = HighestQualityMeshSelector.Select(system);

            Assert.Equal(new[] { true, true, false, true, true }, report.CreateKeepMask());
            Assert.NotEqual(report.Candidates[0].ExtentGroup, report.Candidates[1].ExtentGroup);
            Assert.Null(report.Candidates[2].ExtentGroup);
            Assert.NotEqual(report.Candidates[0].ExtentGroup, report.Candidates[3].ExtentGroup);
            Assert.Equal(HighestQualityMeshDecision.KeptHighestVertexCount,
                report.Candidates[1].Decision);
            Assert.Equal(HighestQualityMeshDecision.DroppedBillboard,
                report.Candidates[2].Decision);
            Assert.Equal(1, report.Candidates[1].SelectedMeshIndex);
            Assert.Equal(4, report.Candidates[4].SelectedMeshIndex);
            Assert.Empty(report.InvariantViolations);
        }

        [Fact]
        public void UnsuffixedNativeMeshDoesNotDeleteUnprovenDenseNeighbor()
        {
            var system = new UberModel.MeshSystem();
            system.Meshes.Add(Mesh("facility_shell_dense", "facility", 20, 120,
                Vector3.Zero, new Vector3(10f)));
            system.Meshes.Add(Mesh("facility", "facility", 14, 24,
                Vector3.Zero, new Vector3(10f)));

            HighestQualityMeshSelectionReport report = HighestQualityMeshSelector.Select(system);

            Assert.Equal(new[] { true, true }, report.CreateKeepMask());
            Assert.Equal(HighestQualityMeshDecision.KeptHighestVertexCount,
                report.Candidates[0].Decision);
            Assert.Equal(HighestQualityMeshDecision.KeptNativeDetailed, report.Candidates[1].Decision);
            Assert.Equal(0, report.Candidates[0].SelectedMeshIndex);
            Assert.Empty(report.InvariantViolations);
        }

        [Fact]
        public void NegativeNativeLodIsDetailedRatherThanBillboard()
        {
            var system = new UberModel.MeshSystem();
            system.Meshes.Add(Mesh("lava_sheet", "lava", -49, 12,
                Vector3.Zero, new Vector3(8f, 8f, 0.1f)));

            HighestQualityMeshSelectionReport report = HighestQualityMeshSelector.Select(system);
            HighestQualityMeshCandidate candidate = Assert.Single(report.Candidates);

            Assert.Equal(-49, candidate.NativeLod);
            Assert.False(candidate.IsBillboard);
            Assert.True(candidate.Keep);
            Assert.Equal(HighestQualityMeshDecision.KeptHighestVertexCount, candidate.Decision);
            Assert.Empty(report.InvariantViolations);
        }

        [Fact]
        public void SoleBillboardTaggedMeshIsKeptAsTheRecordsOnlyRepresentation()
        {
            var system = new UberModel.MeshSystem();
            system.Meshes.Add(Mesh("articclutterc_billboard", "articclutterc", 1001, 24,
                new Vector3(-0.15f), new Vector3(0.15f)));

            HighestQualityMeshSelectionReport report = HighestQualityMeshSelector.Select(system);
            HighestQualityMeshCandidate candidate = Assert.Single(report.Candidates);

            Assert.True(candidate.Keep);
            Assert.True(candidate.IsBillboard);
            Assert.Equal(HighestQualityMeshDecision.KeptOnlyRepresentationBillboard, candidate.Decision);
            Assert.Empty(report.InvariantViolations);
        }

        [Fact]
        public void ThreeDigitPortalConstituentIsNotMistakenForWholeModelDistanceVariant()
        {
            var system = new UberModel.MeshSystem();
            system.Meshes.Add(Mesh("facility", "facility", 14, 40,
                Vector3.Zero, new Vector3(20f)));
            system.Meshes.Add(Mesh("facility_1", "facility", 36, 20,
                Vector3.Zero, new Vector3(20f)));
            system.Meshes.Add(Mesh("facility_001", "facility", 36, 24,
                new Vector3(30f, 0f, 0f), new Vector3(35f, 5f, 5f)));

            HighestQualityMeshSelectionReport report = HighestQualityMeshSelector.Select(system);

            Assert.Equal(new[] { true, false, true }, report.CreateKeepMask());
            Assert.True(report.Candidates[1].IsNumberedModelSibling);
            Assert.True(report.Candidates[1].IsWholeModelDistanceVariant);
            Assert.False(report.Candidates[2].IsNumberedModelSibling);
            Assert.False(report.Candidates[2].IsWholeModelDistanceVariant);
            Assert.Equal(HighestQualityMeshDecision.KeptHighestVertexCount, report.Candidates[2].Decision);
            Assert.Empty(report.InvariantViolations);
        }

        [Fact]
        public void ZeroPaddedBillboardSiblingsCollapseOntoUnsuffixedDetailedToolMesh()
        {
            var system = new UberModel.MeshSystem();
            system.Meshes.Add(Mesh("remote_electronics_kit02", "fp_t_remote_electronics_kit", 1002, 55,
                new Vector3(-0.134f, -0.021f, -0.111f), new Vector3(0.184f, 0.020f, 0.111f)));
            system.Meshes.Add(Mesh("remote_electronics_kit01", "fp_t_remote_electronics_kit", 1001, 342,
                new Vector3(-0.141f, -0.035f, -0.111f), new Vector3(0.247f, 0.035f, 0.111f)));
            system.Meshes.Add(Mesh("remote_electronics_kit", "fp_t_remote_electronics_kit", 14, 747,
                new Vector3(-0.141f, -0.035f, -0.111f), new Vector3(0.247f, 0.035f, 0.111f)));

            HighestQualityMeshSelectionReport report = HighestQualityMeshSelector.Select(system);

            Assert.Equal(new[] { false, false, true }, report.CreateKeepMask());
            Assert.Null(report.Candidates[0].ExtentGroup);
            Assert.Null(report.Candidates[1].ExtentGroup);
            Assert.Equal(HighestQualityMeshDecision.DroppedBillboard, report.Candidates[0].Decision);
            Assert.Equal(HighestQualityMeshDecision.DroppedBillboard, report.Candidates[1].Decision);
            Assert.Equal(HighestQualityMeshDecision.KeptHighestVertexCount, report.Candidates[2].Decision);
            Assert.Empty(report.InvariantViolations);
        }

        [Fact]
        public void ExplicitLodFamiliesCannotBeStolenBySimilarNeighboringParts()
        {
            var system = new UberModel.MeshSystem();
            system.Meshes.Add(Mesh("leg_shin_left02", "peregrine", 1002, 50,
                Vector3.Zero, new Vector3(1f, 4f, 1f)));
            system.Meshes.Add(Mesh("leg_shin_right02", "peregrine", 1002, 50,
                new Vector3(0.1f, 0f, 0f), new Vector3(1.1f, 4f, 1f)));
            system.Meshes.Add(Mesh("leg_shin_right", "peregrine", 14, 200,
                new Vector3(0.1f, 0f, 0f), new Vector3(1.1f, 4f, 1f)));
            system.Meshes.Add(Mesh("leg_shin_left", "peregrine", 14, 200,
                Vector3.Zero, new Vector3(1f, 4f, 1f)));

            HighestQualityMeshSelectionReport report = HighestQualityMeshSelector.Select(system);

            Assert.Equal(new[] { false, false, true, true }, report.CreateKeepMask());
            Assert.Null(report.Candidates[0].ExtentGroup);
            Assert.Null(report.Candidates[1].ExtentGroup);
            Assert.NotEqual(report.Candidates[2].ExtentGroup, report.Candidates[3].ExtentGroup);
            Assert.Empty(report.InvariantViolations);
        }

        [Fact]
        public void ContinuousLodPlayerShellsDoNotStackOnDetailedBody()
        {
            var system = new UberModel.MeshSystem();
            system.Meshes.Add(Mesh("trmsi01", "trmsi", 14, 120,
                Vector3.Zero, new Vector3(1f), "clod_soldiera003"));
            system.Meshes.Add(Mesh("trmsi02", "trmsi", 14, 80,
                Vector3.Zero, new Vector3(1f), "clod_soldiera_lod001"));
            system.Meshes.Add(Mesh("trmsi", "trmsi", 14, 400,
                Vector3.Zero, new Vector3(1f), "tr1msi"));

            HighestQualityMeshSelectionReport report = HighestQualityMeshSelector.Select(system);

            Assert.Equal(new[] { false, false, true }, report.CreateKeepMask());
            Assert.Equal(HighestQualityMeshDecision.DroppedContinuousLodRepresentation,
                report.Candidates[0].Decision);
            Assert.Equal(HighestQualityMeshDecision.DroppedContinuousLodRepresentation,
                report.Candidates[1].Decision);
            Assert.Empty(report.InvariantViolations);
        }

        [Fact]
        public void ContinuousLodPlayerShellWithAuxiliaryMaterialDoesNotStackOnDetailedBody()
        {
            var system = new UberModel.MeshSystem();
            UberModel.Mesh lod = Mesh("trhev01", "trhev", 14, 120,
                Vector3.Zero, new Vector3(1f), "clod_soldiera003");
            lod.Sections.Add(new UberModel.MeshSection
            {
                VertexCount = 3,
                Verts = new UberModel.UberVert[3],
                MaterialName = "trhev_visor",
            });
            system.Meshes.Add(lod);
            system.Meshes.Add(Mesh("trhev", "trhev", 14, 400,
                Vector3.Zero, new Vector3(1f), "tr1hev1"));

            HighestQualityMeshSelectionReport report = HighestQualityMeshSelector.Select(system);

            Assert.Equal(new[] { false, true }, report.CreateKeepMask());
            Assert.Equal(HighestQualityMeshDecision.DroppedContinuousLodRepresentation,
                report.Candidates[0].Decision);
            Assert.Empty(report.InvariantViolations);
        }

        [Fact]
        public void NativeAabMembershipOverridesNameAndLodHeuristics()
        {
            var system = new UberModel.MeshSystem { Name = "tower_a" };
            system.Meshes.Add(MeshWithNativeIds("tower_a_1", "tower_a", 36, 12, meshId: 8, sectionId: 0));
            system.Meshes.Add(MeshWithNativeIds("tower_a_001", "tower_a", 14, 12, meshId: 2, sectionId: 0));
            system.Meshes.Add(MeshWithNativeIds("tower_a", "tower_a", 0, 12, meshId: 1, sectionId: 0));
            system.NativeAab = new UberModel.NativeAabData();
            system.NativeAab.Faces.Add(new UberModel.NativeAabFace(2, 0, 1, 2));
            system.NativeAab.Faces.Add(new UberModel.NativeAabFace(1, 0, 1, 2));
            system.NativeAab.Map = new uint[] { 0, 1 };

            HighestQualityMeshSelectionReport report = HighestQualityMeshSelector.Select(system);

            Assert.Equal(new[] { false, true, true }, report.CreateKeepMask());
            Assert.True(report.UsesNativeAab);
            Assert.False(report.Candidates[0].ReferencedByNativeAab);
            Assert.All(report.Candidates.Skip(1), candidate => Assert.True(candidate.ReferencedByNativeAab));
            Assert.Equal(HighestQualityMeshDecision.DroppedWholeModelDistanceVariant,
                report.Candidates[0].Decision);
            Assert.Empty(report.InvariantViolations);
        }

        [Fact]
        public void NativeAabDoesNotDeleteAnIndependentVisualOnlyMesh()
        {
            var system = new UberModel.MeshSystem { Name = "facility" };
            system.Meshes.Add(MeshWithNativeIds("facility", "facility", 0, 12, meshId: 1, sectionId: 0));
            system.Meshes.Add(MeshWithNativeIds("facility_glow", "facility_glow", 0, 6, meshId: 2, sectionId: 0));
            system.NativeAab = new UberModel.NativeAabData();
            system.NativeAab.Faces.Add(new UberModel.NativeAabFace(1, 0, 1, 2));
            system.NativeAab.Map = new uint[] { 0 };

            HighestQualityMeshSelectionReport report = HighestQualityMeshSelector.Select(system);

            Assert.Equal(new[] { true, true }, report.CreateKeepMask());
            Assert.True(report.Candidates[0].ReferencedByNativeAab);
            Assert.False(report.Candidates[1].ReferencedByNativeAab);
            Assert.Empty(report.InvariantViolations);
        }

        [Fact]
        public void NativeAabWithUnknownSectionFailsClosed()
        {
            var system = new UberModel.MeshSystem { Name = "broken" };
            system.Meshes.Add(MeshWithNativeIds("broken", "broken", 0, 3, meshId: 1, sectionId: 0));
            system.NativeAab = new UberModel.NativeAabData();
            system.NativeAab.Faces.Add(new UberModel.NativeAabFace(9, 0, 1, 2));

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => HighestQualityMeshSelector.Select(system));

            Assert.Contains("section key", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        private static UberModel.Mesh Mesh(
            string name,
            string modelName,
            int nativeLod,
            int vertexCount,
            Vector3 minimum,
            Vector3 maximum,
            string materialName = "material")
        {
            var vertices = new UberModel.UberVert[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                float amount = vertexCount == 1 ? 0f : i / (float)(vertexCount - 1);
                vertices[i].Position = Vector3.Lerp(minimum, maximum, amount);
            }

            var mesh = new UberModel.Mesh
            {
                Name = name,
                ModelName = modelName,
                Lod = unchecked((uint)nativeLod),
            };
            mesh.Sections.Add(new UberModel.MeshSection
            {
                VertexCount = (uint)vertexCount,
                Verts = vertices,
                MaterialName = materialName,
            });
            return mesh;
        }

        private static UberModel.Mesh MeshWithNativeIds(
            string name,
            string modelName,
            int nativeLod,
            int vertexCount,
            uint meshId,
            uint sectionId)
        {
            UberModel.Mesh mesh = Mesh(
                name, modelName, nativeLod, vertexCount, Vector3.Zero, Vector3.One);
            mesh.Sections[0].MeshId = meshId;
            mesh.Sections[0].Id = sectionId;
            mesh.Sections[0].IndexCount = 3;
            mesh.Sections[0].Indices = new ushort[] { 0, 1, 2 };
            return mesh;
        }
    }
}
