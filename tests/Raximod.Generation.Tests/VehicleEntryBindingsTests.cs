using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class VehicleEntryBindingsTests
{
    private static (VehicleEntryBindings Compiler, GameObjectDb.GameObject Vehicle) Fixture()
    {
        string[] aliases = ["DriverB_mount", "DriverB_dismount", "DriverB_ncheavy_mount", "DriverB_ncheavy_dismount"];
        var clips = aliases.Select(alias => new AnimRecord { Name = "shared_" + alias, Duration = 2.5f }).ToArray();
        foreach (var clip in clips)
        {
            clip.Tracks.Add(new AnimTrack { Name = "bip01_spine" });
            clip.Tracks.Add(new AnimTrack { Name = "door" });
            clip.Tracks.Add(new AnimTrack { Name = "lod_door" });
        }
        var package = new NativeAnimationPackage("shared", aliases.Select(alias =>
            new NativeAnimationEntry("shared_" + alias, alias, null, null, "play_once_hold", null, [])).ToArray(),
            [], [], null, null!);
        return (new([package], clips), new GameObjectDb.GameObject {
            Name = "variant", Properties = new() {
                ["animationpackage"] = ["shared"], ["mountzone3_name"] = ["DriverB"],
                ["mountzone3_mountpointindexes"] = ["1"], ["mountzone3_location"] = ["0", "2", "1"],
                ["mountpoint1_name"] = ["Driver"],
            },
        });
    }

    [Fact]
    public void AlternateEntranceUsesZoneAliasStationAndOccupantVariantWithoutGuessing()
    {
        var (compiler, vehicle) = Fixture();
        vehicle.Properties["mountzone3_mirroryposfordismount"] = ["true"];
        var entry = Assert.Single(compiler.Resolve(vehicle, null));
        Assert.True(entry.MirrorYPositionForDismount);
        Assert.Equal(3, entry.EntryPoint);
        Assert.Equal(1, entry.NativeMountPoint);
        Assert.Equal("DriverB", entry.Name);
        Assert.Equal(new float[] { 0, 2, 1 }, entry.Location);
        Assert.Equal(new[] { "infantry", "max-nc" }, entry.Variants.Select(v => v.Occupant));
        Assert.Equal("shared_DriverB_mount", entry.Variants[0].Mount.Name);
        Assert.Equal(2.5f, entry.Variants[0].Mount.DurationSeconds);
        Assert.Equal("mount-end", entry.Variants[0].Seated.Mode);
        Assert.Equal(entry.Variants[0].Mount.Name, entry.Variants[0].Seated.Name);
        var bound = Assert.Single(VehicleEntryBindings.BindBody("variant", [entry], ["door"], compiler.PackageClips("shared")));
        Assert.Equal(new[] { "door" }, bound.Variants[0].Mount.VehicleTracks);
        Assert.Equal(new[] { "lod_door" }, bound.Variants[0].Mount.UnboundTracks);
        Assert.Throws<InvalidDataException>(() => VehicleEntryBindings.BindBody("variant", [entry], ["door"], []));
    }

    [Theory]
    [InlineData("shared_driver_ref00", "refpose3", "reference")]
    [InlineData("shared_driver", "refpose3", "reference")]
    [InlineData("shared_driver", "loop", "loop")]
    public void AlternateEntryUsesStationPoseAndPreservesExactReferenceClips(string name, string playback, string mode)
    {
        var (_, vehicle) = Fixture();
        var animations = new[] {
            new NativeAnimationEntry("shared_mount", "DriverB_mount", null, null, "play_once_hold", null, []),
            new NativeAnimationEntry("shared_exit", "DriverB_dismount", null, null, "play_once_hold", null, []),
            new NativeAnimationEntry("shared_driver", playback == "loop" ? "Driver_idle" : "Driver", null, null, playback, null, []),
        };
        var package = new NativeAnimationPackage("shared", animations, [], [], null, null!);
        var clips = new[] { "shared_mount", "shared_exit", name }.Select(n => new AnimRecord { Name = n, Duration = 1 }).ToArray();
        foreach (var clip in clips) clip.Tracks.Add(new AnimTrack { Name = "bip01_spine" });
        var compiler = new VehicleEntryBindings([package], clips);
        var pose = Assert.Single(Assert.Single(compiler.Resolve(vehicle, null)).Variants).Seated;
        Assert.Equal(name, pose.Name);
        Assert.Equal(mode, pose.Mode);
        Assert.Equal("shared", pose.Package);
    }

    [Fact]
    public void VehicleOnlyIdleDoesNotReplaceTheOccupantMountEnd()
    {
        var (_, vehicle) = Fixture();
        var entries = new[] { ("DriverB_mount", "play_once_hold"), ("DriverB_dismount", "play_once_hold"), ("Driver_idle", "loop") };
        var package = new NativeAnimationPackage("shared", entries.Select(e =>
            new NativeAnimationEntry(e.Item1, e.Item1, null, null, e.Item2, null, [])).ToArray(), [], [], null, null!);
        var clips = entries.Select(e => new AnimRecord { Name = e.Item1, Duration = 1 }).ToArray();
        clips[2].Tracks.Add(new AnimTrack { Name = "wing_l" });
        var pose = new VehicleEntryBindings([package], clips).Resolve(vehicle, null)[0].Variants[0].Seated;
        Assert.Equal("mount-end", pose.Mode);
        Assert.Equal("DriverB_mount", pose.Name);
    }

    [Fact]
    public void UnsupportedStationListsAndMissingAliasesFailAtTheSourceBoundary()
    {
        var (compiler, vehicle) = Fixture();
        vehicle.Properties["mountzone3_mountpointindexes"] = ["1", "2"];
        Assert.Throws<InvalidDataException>(() => compiler.Resolve(vehicle, null));
        vehicle.Properties["mountzone3_mountpointindexes"] = ["1"];
        vehicle.Properties["mountzone3_name"] = ["MissingRole"];
        Assert.Throws<InvalidDataException>(() => compiler.Resolve(vehicle, null));
    }

    [Fact]
    public void RepeatedOrMalformedExitMirrorFlagsFailInsteadOfSilentlySelectingAValue()
    {
        var (compiler, vehicle) = Fixture();
        vehicle.Properties["mountzone3_mirroryposfordismount"] = ["true", "false"];
        Assert.Throws<InvalidDataException>(() => compiler.Resolve(vehicle, null));
        vehicle.Properties["mountzone3_mirroryposfordismount"] = ["perhaps"];
        Assert.Throws<FormatException>(() => compiler.Resolve(vehicle, null));
    }
}
