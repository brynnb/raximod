using System.Text.Json;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class TrainingZoneBarrierTests
{
    [Fact]
    public void NativeTrainingBarriersRemainSixQueriesWithoutReplacingStructuralCollision()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR")
            ?? "/home/brynn/Downloads/PlanetSide";
        if (!File.Exists(Path.Combine(client, "uber.ubr"))) return;
        string output = Path.Combine("/var/tmp", "raximod-training-test-" + Guid.NewGuid());
        Directory.CreateDirectory(output);
        try
        {
            string[] records = ["VT_building_nc", "VT_building_tr", "VT_building_vs"];
            // The exporter discovers source collision from UBR/LST/ADB; it never
            // reads render triangles from these placeholder model files.
            foreach (string record in records) File.WriteAllBytes(Path.Combine(output, record + ".glb"), []);
            var result = CollisionManifestTool.Run(new(client, output, SelectedRecords: records));
            Assert.True(result.Complete, string.Join("; ", result.Failures));
            Assert.Equal(3, result.Written);
            foreach (string record in records)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, record + ".collision.json")));
                var manifest = document.RootElement;
                Assert.Equal("aab", manifest.GetProperty("mode").GetString());
                var shapes = manifest.GetProperty("shapes").EnumerateArray().ToArray();
                Assert.Contains(shapes, shape => shape.GetProperty("source").GetString() == "native_aab");
                var triggers = shapes.Where(shape => shape.GetProperty("behavior").GetString() == "training-zone-trigger").ToArray();
                Assert.Equal(6, triggers.Length);
                Assert.Equal(Enumerable.Range(1, 6).Select(i => "barrier:" + i),
                    triggers.Select(shape => shape.GetProperty("group").GetString()));
                foreach (var trigger in triggers)
                {
                    Assert.Equal("physics:avatar_barrier_virtual_training:root", trigger.GetProperty("id").GetString());
                    Assert.Equal(new[] { 0.5f, 5.5f, 6f }, trigger.GetProperty("size").EnumerateArray().Select(v => v.GetSingle()));
                }
                Assert.Equal(new[] { 22f, -1f, -29f }, triggers[3].GetProperty("center").EnumerateArray().Select(v => v.GetSingle()));
                Assert.Equal(new[] { 15f, 4f, 0f }, triggers[1].GetProperty("center").EnumerateArray().Select(v => v.GetSingle()));
            }
        }
        finally { Directory.Delete(output, true); }
    }
}
