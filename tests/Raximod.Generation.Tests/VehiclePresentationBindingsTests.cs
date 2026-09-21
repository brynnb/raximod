using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class VehiclePresentationBindingsTests
{
    [Fact]
    public void BodySelectionUsesTheAuthoredSequenceWithoutChangingLogicalIdentity()
    {
        var source = new GameObjectDb.GameObject { Name = "apc", Properties = new() { ["meshsequence"] = ["apc_mk2"] } };
        Assert.Equal("apc_mk2", VehiclePresentationBindings.BodyRecord("apc", source));
        Assert.Equal("registered_model", VehiclePresentationBindings.BodyRecord("registered_model"));
        source.Properties["meshsequence"] = ["first", "second"];
        Assert.Throws<InvalidDataException>(() => VehiclePresentationBindings.BodyRecord("apc", source));
    }

    [Fact]
    public void MissingPlatformJointsCannotBecomeAnUnrelatedMount()
    {
        VehiclePresentationBindings.RequireMounts("vehicle", ["HP_GUN"], ["hp_gun"]);
        Assert.Throws<InvalidDataException>(() => VehiclePresentationBindings.RequireMounts(
            "apc", ["hp_ballgun_r"], ["hp_guna", "hp_gunb"]));
    }

    [Fact]
    public void MuzzleBindingsRetainExactNamesAndRejectUnresolvedSources()
    {
        var component = new VehiclePresentationBindings.MuzzleComponent(0, "gun", ["muzzle"], null);
        var binding = Assert.Single(VehiclePresentationBindings.Muzzles("gun", ["muzzle"], "fire1", [component]));
        Assert.Equal("native-muzzle", binding.Reason);
        Assert.Equal("muzzle", binding.Bone);
        Assert.Throws<InvalidDataException>(() => VehiclePresentationBindings.Muzzles("gun", ["missing"], "fire1", [component]));
    }
    [Fact]
    public void MuzzleRepairUsesOnlyOneUnambiguousNativeFiringSocket()
    {
        NativeEffectPackage Package(params NativeEffectBinding[] effects) => new("gun", effects, [], [], [], [], [], false,
            new("efp_begin", 10, 50, false, null));
        var component = new VehiclePresentationBindings.MuzzleComponent(2, "gun", ["actual", "second"],
            Package(new NativeEffectBinding(1, "fire1", "flash", "actual", null, 12)));
        var binding = Assert.Single(VehiclePresentationBindings.Muzzles("gun", ["stale"], "fire1", [component]));
        Assert.Equal("actual", binding.Bone);
        Assert.Equal("stale", binding.Requested);
        Assert.Equal("native-fire-event", binding.Reason);
        Assert.Equal([2], binding.ComponentIndices);
        Assert.Throws<InvalidDataException>(() => VehiclePresentationBindings.Muzzles("gun", ["stale"], "fire2", [component]));
        component = component with { Effects = Package(new NativeEffectBinding(1, "fire1", "flash", "actual", null, 12), new NativeEffectBinding(2, "fire1", "flash", "second", null, 13)) };
        Assert.Throws<InvalidDataException>(() => VehiclePresentationBindings.Muzzles("gun", ["stale"], "fire1", [component]));
    }

    [Fact]
    public void NativeBodyAssemblyRequiresEveryOrderedChildAndMount()
    {
        var source = new GameObjectDb.GameObject { Properties = new() {
            ["attach_sequence_mesh_name"] = ["body", "fin"],
            ["attach_sequence_my_bone_name"] = ["body_mount", "fin_mount"],
            ["attach_sequence_child_bone_name"] = ["body_root", "fin_root"],
        }};
        Assert.Equal([new("body", "body_mount", "body_root"), new("fin", "fin_mount", "fin_root")],
            VehiclePresentationBindings.BodyComponents(source));
        source.Properties["attach_sequence_my_bone_name"].RemoveAt(0);
        Assert.Throws<InvalidDataException>(() => VehiclePresentationBindings.BodyComponents(source));
    }

    [Fact]
    public void DeploymentKeepsNativeAxesAndChildIdentityAndRejectsIncompleteVectors()
    {
        var source = new GameObjectDb.GameObject { Name = "vehicle", Properties = new() {
            ["deploy5_childname"] = ["gun"], ["deploy5_rotbonename"] = ["muzzle"],
            ["deploy5_rotmaxangle"] = ["0", "0", "180"],
            ["deploy4_transbonename"] = ["mount"], ["deploy4_transmaxdist"] = ["0", "0", "1.5"],
        }};
        var transforms = VehiclePresentationBindings.Deployment(source);
        Assert.Equal([4, 5], transforms.Select(value => value.Index));
        Assert.Equal([0f, 0f, 1.5f], transforms[0].Translation!);
        Assert.Equal("gun", transforms[1].Child);
        Assert.Equal([0f, 0f, 180f], transforms[1].RotationDegrees!);
        source.Properties["deploy5_rotmaxangle"] = ["180"];
        Assert.Throws<InvalidDataException>(() => VehiclePresentationBindings.Deployment(source));
    }

}
