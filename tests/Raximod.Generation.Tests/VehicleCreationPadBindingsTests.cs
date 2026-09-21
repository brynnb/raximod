using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public class VehicleCreationPadBindingsTests
{
    private static GameObjectDb.GameObject Pad() => new() {
        Name = "test_pad", Properties = new() {
            ["type"] = ["vehicle_creation_pad"], ["animattachbonename"] = ["hp_vehicle"],
            ["animname_open"] = ["create"], ["animname_close"] = ["retract"],
            ["desired_anim_open_duration"] = ["2.0"], ["desired_anim_close_duration"] = ["2.5"],
        },
    };

    [Fact]
    public void NativeActionsAndDurationsAreNotGuessedFromTheMeshName()
    {
        var binding = VehicleCreationPadBindings.Resolve(Pad());
        Assert.Equal("hp_vehicle", binding.AttachBone);
        Assert.Equal(new VehicleCreationPadBindings.Animation("create", 2000), binding.Open);
        Assert.Equal(new VehicleCreationPadBindings.Animation("retract", 2500), binding.Close);
        Assert.Null(binding.Active);
        Assert.Null(binding.ChildVisibleOnAdd);
        Assert.Equal("game_objects.adb resolved test_pad", binding.Source);
    }

    [Fact]
    public void TypeOnlyAndVolumeOnlyRecordsAreNotAnimatedMechanismsButPartialBindingsFail()
    {
        var typeOnly = new GameObjectDb.GameObject { Name = "pad_create", Properties = new() {
            ["type"] = ["vehicle_creation_pad"],
        } };
        Assert.Empty(VehicleCreationPadBindings.Resolve(new[] { typeOnly }));
        typeOnly.Properties["animname_open"] = ["create"];
        Assert.Throws<InvalidDataException>(() => VehicleCreationPadBindings.Resolve(new[] { typeOnly }));
    }

    [Fact]
    public void AircraftAttachmentAndCameraAndCavernFadeRemainSourceDriven()
    {
        var pad = Pad();
        pad.Properties["altanimattachbonename"] = ["hp_vehicle_alt"];
        pad.Properties["vehiclecreationviewpointpos"] = ["-10", "0", "15"];
        pad.Properties["child_fadein_rate"] = ["0.2"];
        pad.Properties["child_visible_onadd"] = ["false"];
        var binding = VehicleCreationPadBindings.Resolve(pad);
        Assert.Equal("hp_vehicle_alt", binding.AlternateAttachBone);
        Assert.Equal(new float[] { -10, 0, 15 }, binding.CreationViewpoint);
        Assert.Equal(0.2f, binding.ChildFadeInRate);
        Assert.False(binding.ChildVisibleOnAdd);
        pad.Properties["vehiclecreationviewpointpos"] = ["1", "2"];
        Assert.Throws<InvalidDataException>(() => VehicleCreationPadBindings.Resolve(pad));
    }

    [Fact]
    public void RepeatedScalarAndNonpositiveDurationFailRatherThanSilentlyChoosingAValue()
    {
        var pad = Pad();
        pad.Properties["animname_open"] = ["create", "other"];
        Assert.Throws<InvalidDataException>(() => VehicleCreationPadBindings.Resolve(pad));
        pad.Properties["animname_open"] = ["create"];
        pad.Properties["desired_anim_open_duration"] = ["0"];
        Assert.Throws<InvalidDataException>(() => VehicleCreationPadBindings.Resolve(pad));
    }
}
