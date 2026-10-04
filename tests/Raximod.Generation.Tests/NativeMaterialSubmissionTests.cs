using System.Text.Json.Nodes;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeMaterialSubmissionTests
{
    private static string ScratchRoot => OperatingSystem.IsLinux() ? "/var/tmp" : Path.GetTempPath();
    [Fact]
    public void CompanionRefreshRecoversSectionIdsWithoutReplacingMaterialDefinitions()
    {
        string path = Path.Combine(ScratchRoot, "native-submission-" + Guid.NewGuid() + ".materials.json");
        string selectionPath = path.Replace(".materials.json", ".mesh-selection.json", StringComparison.Ordinal);
        var source = new UberModel.MeshSystem();
        var mesh = new UberModel.Mesh { Id = 1 };
        // The installed terminal archive reads LED before circuitry and glass.
        // Retail indexes them by these IDs before submitting its material batches.
        mesh.Sections.Add(new() { Id = 7, MaterialName = "controls", VertexCount = 3, IndexCount = 3 });
        mesh.Sections.Add(new() { Id = 5, MaterialName = "circuitry", VertexCount = 3, IndexCount = 3 });
        mesh.Sections.Add(new() { Id = 6, MaterialName = "glass", VertexCount = 3, IndexCount = 3 });
        source.Meshes.Add(mesh);
        var original = JsonNode.Parse("""
            {"format":"raxicore-native-materials","record":"fixture","unknown":42,"materials":[
             {"name":"controls","sections":1,"sectionCommands":[{"name":"mat_sortkey","arguments":["3"]}]},
             {"name":"circuitry","sections":1,"extra":"keep"},{"name":"glass","sections":1},
             {"name":"faction_variant","sections":0}]}
            """)!;
        try
        {
            File.WriteAllText(path, original.ToJsonString());
            Assert.True(NativeMaterialMetadataRefresh.SubmissionOrder(path, source));
            var result = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(new[] { "circuitry", "glass", "controls" },
                result["submissionOrder"]!.AsArray().Select(section => section!["material"]!.GetValue<string>()));
            result.AsObject().Remove("submissionOrder");
            Assert.True(JsonNode.DeepEquals(original, result));
            string accepted = File.ReadAllText(path);
            Assert.False(NativeMaterialMetadataRefresh.SubmissionOrder(path, source));
            Assert.Equal(accepted, File.ReadAllText(path));
            // A discarded LOD uses these same materials at lower section IDs.
            // It cannot change submission order for the existing retained GLB.
            var omitted = new UberModel.Mesh { Id = 0 };
            omitted.Sections.Add(new() { Id = 0, MaterialName = "controls", VertexCount = 3, IndexCount = 3 });
            source.Meshes.Add(omitted);
            File.WriteAllText(selectionPath, """
                {"format":"raxicore-highest-quality-mesh-selection","candidates":[
                 {"meshIndex":0,"keep":true},{"meshIndex":1,"keep":false}]}
                """);
            Assert.False(NativeMaterialMetadataRefresh.SubmissionOrder(path, source));
            Assert.Equal(accepted, File.ReadAllText(path));
            // Without geometry provenance multiple source meshes fail closed.
            File.Delete(selectionPath);
            Assert.Throws<InvalidDataException>(() => NativeMaterialMetadataRefresh.SubmissionOrder(path, source));
            source.Meshes.RemoveAt(1);
            mesh.Sections.RemoveAt(0);
            Assert.Throws<InvalidDataException>(() => NativeMaterialMetadataRefresh.SubmissionOrder(path, source));
            Assert.Equal(accepted, File.ReadAllText(path));
        }
        finally { File.Delete(path); File.Delete(selectionPath); }
    }

    [Fact]
    public void RecordedMeshOrderSurvivesMergedMaterialPrimitives()
    {
        string path = Path.Combine(ScratchRoot, "native-submission-" + Guid.NewGuid() + ".materials.json");
        string selectionPath = path.Replace(".materials.json", ".mesh-selection.json", StringComparison.Ordinal);
        var source = new UberModel.MeshSystem();
        var first = new UberModel.Mesh { Id = 9 };
        first.Sections.Add(new() { Id = 7, MaterialName = "controls", VertexCount = 3, IndexCount = 3 });
        first.Sections.Add(new() { Id = 4, MaterialName = "glass", VertexCount = 3, IndexCount = 3 });
        var second = new UberModel.Mesh { Id = 1 };
        second.Sections.Add(new() { Id = 0, MaterialName = "glass", VertexCount = 3, IndexCount = 3 });
        source.Meshes.Add(first); source.Meshes.Add(second);
        try
        {
            // A material-only refresh counts merged GLB primitives, not sections.
            File.WriteAllText(path, """
                {"format":"raxicore-native-materials","record":"fixture","materials":[
                 {"name":"controls","sections":1},{"name":"glass","sections":1}]}
                """);
            File.WriteAllText(selectionPath, """
                {"format":"raxicore-highest-quality-mesh-selection","candidates":[
                 {"meshIndex":0,"keep":true},{"meshIndex":1,"keep":true}]}
                """);
            Assert.True(NativeMaterialMetadataRefresh.SubmissionOrder(path, source));
            var sections = JsonNode.Parse(File.ReadAllText(path))!["submissionOrder"]!.AsArray();
            Assert.Equal(new uint[] { 9, 9, 1 }, sections.Select(section => section!["meshId"]!.GetValue<uint>()));
            Assert.Equal(new[] { "glass", "controls", "glass" }, sections.Select(section => section!["material"]!.GetValue<string>()));
        }
        finally { File.Delete(path); File.Delete(selectionPath); }
    }
}
