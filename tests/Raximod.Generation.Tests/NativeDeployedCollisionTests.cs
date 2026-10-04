using System.Text.Json;
using System.Numerics;
using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeDeployedCollisionTests
{
    [Fact]
    public void DeclaredDeployedHullsSurviveTheNativePublicationBoundary()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR") ?? "/home/brynn/Downloads/PlanetSide";
        if (!File.Exists(Path.Combine(client, "uber.ubr"))) return;
        string output = Path.Combine("/var/tmp", "raximod-deployed-collision-" + Guid.NewGuid());
        Directory.CreateDirectory(output);
        try
        {
            string[] records = ["ant", "ams", "router", "flail"];
            var physics = PhysicsListDatabase.ParseDirectory(Path.Combine(client, "startup.pak-out"));
            var objects = GameObjectDb.Parse(File.ReadAllBytes(Path.Combine(client, "startup.pak-out", "game_objects.adb")))
                .ResolvedObjects.ToDictionary(value => value.Name);
            foreach (string record in records) File.WriteAllBytes(Path.Combine(output, record + ".glb"), []);
            var result = CollisionManifestTool.Run(new(client, output, SelectedRecords: records));
            Assert.True(result.Complete, string.Join("; ", result.Failures));
            foreach (string record in records)
            {
                string name = GameObjectPropertyReader.Scalar(objects[record], "physics_deployed")!;
                var model = Assert.IsType<PhysicsListDatabase.Model>(physics.FindModel(name));
                Assert.Equal(record is "ams" or "router" ? "physics_fixed_misc.lst" : "physics.lst", model.SourceFile);
                using var sidecar = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, record + ".collision.json")));
                var deployed = sidecar.RootElement.GetProperty("shapes").EnumerateArray()
                    .Where(shape => shape.GetProperty("group").GetString() == "deployed").ToArray();
                Assert.NotEmpty(deployed);
                Assert.All(deployed, shape => Assert.False(shape.GetProperty("enabled").GetBoolean()));
                foreach (var emitted in deployed)
                {
                    var primitive = model.Shapes.Single(shape =>
                        emitted.GetProperty("id").GetString() == $"physics:{model.Name}:{shape.Name}");
                    if (primitive.Kind != PhysicsListDatabase.ShapeKind.Box) continue;
                    Assert.Equal(new[] { primitive.Size.X, primitive.Size.Z, primitive.Size.Y },
                        emitted.GetProperty("size").EnumerateArray().Select(value => value.GetSingle()));
                    var sourceRotation = Quaternion.CreateFromYawPitchRoll(
                        primitive.Orientation.Y, primitive.Orientation.X, primitive.Orientation.Z);
                    var expectedRotation = new Quaternion(sourceRotation.X, sourceRotation.Z, -sourceRotation.Y, sourceRotation.W);
                    float[] components = emitted.GetProperty("rotation").EnumerateArray().Select(value => value.GetSingle()).ToArray();
                    Assert.True(MathF.Abs(Quaternion.Dot(expectedRotation,
                        new Quaternion(components[0], components[1], components[2], components[3]))) > 0.99999f);
                }
                VehicleManifestContract.RequireMatchingActiveCollisionSidecar(record,
                    VehicleManifestContract.ActiveObjectCollisionShapes(model), deployed.Select(shape =>
                        new VehicleManifestContract.EmittedCollisionShape(shape.GetProperty("id").GetString()!,
                            shape.GetProperty("center").EnumerateArray().Select(value => value.GetSingle()).ToArray())).ToArray());
            }

            // An explicitly declared variant cannot silently disappear from the output.
            string incomplete = Path.Combine(output, "incomplete-client");
            string startup = Path.Combine(incomplete, "startup.pak-out");
            Directory.CreateDirectory(startup);
            File.CreateSymbolicLink(Path.Combine(incomplete, "uber.ubr"), Path.Combine(client, "uber.ubr"));
            File.CreateSymbolicLink(Path.Combine(startup, "game_objects.adb"), Path.Combine(client, "startup.pak-out", "game_objects.adb"));
            File.CreateSymbolicLink(Path.Combine(startup, "physics.lst"), Path.Combine(client, "startup.pak-out", "physics.lst"));
            string previous = File.ReadAllText(Path.Combine(output, "ams.collision.json"));
            var failure = Assert.Throws<InvalidDataException>(() =>
                CollisionManifestTool.Run(new(incomplete, output, SelectedRecords: ["ams"])));
            Assert.Contains("ams_fixed", failure.Message);
            Assert.Equal(previous, File.ReadAllText(Path.Combine(output, "ams.collision.json")));
        }
        finally { Directory.Delete(output, true); }
    }
}
