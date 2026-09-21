using System.Numerics;
using Raximod.EngineAssets.Meshes;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class VehicleExitBindingsTests
{
    [Fact]
    public void EndpointUsesAuthoredLastKeysAndFullParentHierarchyIncludingForwardReferences()
    {
        var skeleton = new UberModel.Skeleton { Name = "native-biped" };
        skeleton.Bones.Add(new UberModel.Bone { Name = "Bip01_R_Toe0", Parent = 1, Position = new(0, 0, -1) });
        skeleton.Bones.Add(new UberModel.Bone { Name = "Bip01", Position = new(99, 99, 99) });
        var clip = new AnimRecord { Name = "native_exit", Duration = 2 };
        var root = new AnimTrack { Name = "Bip01", StaticRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) };
        root.PosKeys.Add(new() { Time = 0, Value = Vector3.Zero });
        root.PosKeys.Add(new() { Time = 2, Value = new(3, 4, 1) });
        clip.Tracks.Add(root);
        var result = VehicleExitBindings.SampleBone(skeleton, clip, "Bip01_R_Toe0");
        Assert.True(Vector3.Distance(new(3, 4, 0), result.Translation) < 1e-6f);
        skeleton.Bones[1].Parent = 0;
        Assert.Throws<InvalidDataException>(() => VehicleExitBindings.SampleBone(skeleton, clip, "Bip01_R_Toe0"));
    }
}
