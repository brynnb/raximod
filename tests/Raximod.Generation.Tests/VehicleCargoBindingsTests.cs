using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class VehicleCargoBindingsTests
{
    [Fact]
    public void ExportsSeparateNativeLoadingCompoundsWithoutEnablingClosedRamps()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR") ?? "/home/brynn/Downloads/PlanetSide";
        if (!File.Exists(Path.Combine(client, "startup.pak-out", "physics_fixed_misc.lst"))) return;
        string output = Path.Combine("/var/tmp", "raximod-cargo-" + Guid.NewGuid());
        Directory.CreateDirectory(output);
        try
        {
            string[] records = ["dropship", "lodestar"];
            foreach (string record in records) File.WriteAllBytes(Path.Combine(output, record + ".glb"), []);
            var result = CollisionManifestTool.Run(new(client, output, SelectedRecords: records));
            Assert.True(result.Complete, string.Join("; ", result.Failures));
            foreach (string record in records)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(output, record + ".collision.json")));
                var shapes = doc.RootElement.GetProperty("shapes").EnumerateArray()
                    .Where(x => x.GetProperty("group").GetString() == "cargo").ToArray();
                Assert.Equal(7, shapes.Length);
                Assert.All(shapes, x => Assert.False(x.GetProperty("enabled").GetBoolean()));
                Assert.Contains(shapes, x => x.GetProperty("node").GetString() == "c_ramp");
                Assert.Contains(shapes, x => x.GetProperty("node").GetString() == "c_plat");
                Assert.All(shapes, x => Assert.StartsWith("physics:" + record + "_fixed:", x.GetProperty("id").GetString()));
            }
        }
        finally { Directory.Delete(output, true); }
    }

    [Fact]
    public void ResolvesDoorApproachZonePromptEffectsAndAcceptedVehicles()
    {
        var properties = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["cargomountpoint1_RotateBone"] = ["dropship_cargodoor"],
            ["cargomountpoint1_StartAngle"] = ["0"],
            ["cargomountpoint1_EndAngle"] = ["-75"],
            ["cargomountpoint1_OpenRate"] = ["60"],
            ["cargomountpoint1_CloseRate"] = ["80"],
            ["cargomountpoint1_MountRejectDist"] = ["25"],
            ["cargomountpoint1_PhysicsBodyCookies"] = ["ramp", "platform", "stop"],
            ["cargomountpoint1_StopPointOffset"] = ["-6.5", "0", "1.5"],
            ["cargomountpoint1_dismountdist"] = ["15"],
            ["cargomountpoint1_internalid"] = ["1"],
            ["cargomountpoint1_mountradius"] = ["2"],
            ["cargomountpoint1_mountrejectangle"] = ["21"],
            ["cargomountpoint1_name"] = ["vehicle"],
            ["cargomountpoint1_renderchildwhenclosed"] = ["false"],
            ["cargomountzone1_acceptedvehicles"] = ["fury", "ant"],
            ["cargomountzone1_acceptedvehicles2"] = ["ant", "lightning"],
            ["cargomountzone1_cargomountpointindexes"] = ["1"],
            ["cargomountzone1_effect"] = ["cargoentry"],
            ["cargomountzone1_effect_na"] = ["cargonoentry"],
            ["cargomountzone1_effectbone"] = ["cargoa_real_mount"],
            ["cargomountzone1_location"] = ["-20.75", "0", "0"],
            ["cargomountzone1_mountstring"] = ["@mount_vehicle"],
            ["cargomountzone1_zorientation"] = ["113.25"],
            ["soundkey_cargo_bay_close"] = ["dropship_cargo_bay_close.wav"],
        };

        VehicleCargoBindings.Manifest cargo = VehicleCargoBindings.Resolve(
            properties, "dropship", key => properties.GetValueOrDefault(key)?.FirstOrDefault());

        VehicleCargoBindings.MountPoint point = Assert.Single(cargo.mountPoints);
        Assert.Equal("dropship_cargodoor", point.rotateBone);
        Assert.Equal(25, point.mountRejectDistance);
        Assert.Equal(["ramp", "platform", "stop"], point.physicsBodyCookies);
        Assert.Equal([-6.5f, 0, 1.5f], Assert.IsType<float[]>(point.stopPointOffset));
        Assert.Equal(2, point.mountRadius);
        Assert.Equal(21, point.mountRejectAngleDegrees);

        VehicleCargoBindings.MountZone zone = Assert.Single(cargo.mountZones);
        Assert.Equal(["fury", "ant", "lightning"], zone.acceptedVehicles);
        Assert.Equal([1], zone.cargoMountPointIndexes);
        Assert.Equal("cargoentry", zone.effect);
        Assert.Equal("cargonoentry", zone.rejectedEffect);
        Assert.Equal("cargoa_real_mount", zone.effectBone);
        Assert.Equal([-20.75f, 0, 0], Assert.IsType<float[]>(zone.location));
        Assert.Equal("@mount_vehicle", zone.mountString);
        Assert.Equal(113.25f, zone.orientationDegrees);
        Assert.Equal("dropship_cargo_bay_close.wav", cargo.sounds.close);
    }

    [Fact]
    public void RejectsMalformedAuthoredCargoVectorsInsteadOfGuessing()
    {
        var properties = new Dictionary<string, List<string>>
        {
            ["cargomountpoint1_StopPointOffset"] = ["-6.5", "1.5"],
        };

        Assert.Throws<InvalidDataException>(() => VehicleCargoBindings.Resolve(
            properties, "dropship", _ => null));
    }
}
