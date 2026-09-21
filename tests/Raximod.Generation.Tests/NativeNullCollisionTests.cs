using System.Text.Json;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeNullCollisionTests
{
    [Fact]
    public void GrateMaterialNamesDoNotRemoveMovementBlocking()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR") ?? "/home/brynn/Downloads/PlanetSide";
        if (!File.Exists(Path.Combine(client, "uber.ubr"))) return;
        string output = Path.Combine("/var/tmp", "raximod-solid-grates-" + Guid.NewGuid());
        Directory.CreateDirectory(output);
        try
        {
            string[] records = ["amp_pipe", "spawn_grates"];
            foreach (string record in records) File.WriteAllBytes(Path.Combine(output, record + ".glb"), []);
            var result = CollisionManifestTool.Run(new(client, output, SelectedRecords: records));
            Assert.True(result.Complete, string.Join("; ", result.Failures));
            foreach (string record in records)
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, record + ".collision.json")));
                var shapes = manifest.RootElement.GetProperty("shapes").EnumerateArray().ToArray();
                Assert.NotEmpty(shapes);
                Assert.All(shapes, shape => Assert.Equal("static", shape.GetProperty("group").GetString()));
                Assert.All(shapes, shape => Assert.NotEmpty(shape.GetProperty("indices").EnumerateArray()));
            }
        }
        finally { Directory.Delete(output, true); }
    }

    [Fact]
    public void ExplicitNullCollisionSuppressesAabAndRemovesStaleSolidCompanions()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR") ?? "/home/brynn/Downloads/PlanetSide";
        if (!File.Exists(Path.Combine(client, "uber.ubr"))) return;
        string output = Path.Combine("/var/tmp", "raximod-null-collision-" + Guid.NewGuid());
        Directory.CreateDirectory(output);
        try
        {
            string[] empty = ["vt_holo", "game_table_effect", "light_tube", "ob_redlight", "ob_greenlight"];
            string[] records = [.. empty, "vt_spawn", "spawn_pad"];
            var source = UberModel.Load(File.ReadAllBytes(Path.Combine(client, "uber.ubr")));
            foreach (string record in empty)
            {
                var system = source.FetchMeshSystem(record)!;
                Assert.True(system.DeclaresCollision);
                Assert.NotEmpty(system.NativeAab!.Faces);
                Assert.NotEmpty(system.Collisions!.Parts);
                Assert.All(system.Collisions.Parts, part => Assert.Equal(UberModel.CollisionPartType.None, part.Type));
                File.WriteAllText(Path.Combine(output, record + ".collision.json"), "stale solid companion");
            }
            // Geometry is read from native archives, never these discovery placeholders.
            foreach (string record in records) File.WriteAllBytes(Path.Combine(output, record + ".glb"), []);
            var result = CollisionManifestTool.Run(new(client, output, SelectedRecords: records));
            Assert.True(result.Complete, string.Join("; ", result.Failures));
            foreach (string record in empty) Assert.False(File.Exists(Path.Combine(output, record + ".collision.json")));
            using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "collision-manifest.json")));
            Assert.Equal(new[] { "spawn_pad", "vt_spawn" }, index.RootElement.GetProperty("records").EnumerateArray().Select(x => x.GetString()));
            foreach (var entry in index.RootElement.GetProperty("sources").EnumerateArray().Where(x => empty.Contains(x.GetProperty("record").GetString())))
            {
                Assert.True(entry.GetProperty("embeddedCollisionDeclared").GetBoolean());
                Assert.Equal("none", entry.GetProperty("mode").GetString());
            }
            foreach (var (record, mode) in new[] { ("vt_spawn", "aab"), ("spawn_pad", "explicit") })
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, record + ".collision.json")));
                Assert.Equal(mode, manifest.RootElement.GetProperty("mode").GetString());
                Assert.NotEmpty(manifest.RootElement.GetProperty("shapes").EnumerateArray());
            }
        }
        finally { Directory.Delete(output, true); }
    }
}
