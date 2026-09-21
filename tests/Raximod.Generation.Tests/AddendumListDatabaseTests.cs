using Raximod.EngineAssets.Databases;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class AddendumListDatabaseTests
{
    [Fact]
    public void PreservesOrderProvenanceAndNonBoneCommands()
    {
        var data = AddendumListDatabase.Parse("""
            # Native parent-local additions, not a renamed original joint.
            addendum_package energy_gun_nc
            addendum_bone ef_fire ef_muzzlea 0.5600 0 -0.0400 0000 0000 1000
            addendum_material before after
            addendum_bone ef_other ef_fire 1 0 0 0000 0000 0000
            """);
        Assert.Equal(4, data.Commands.Count);
        var bones = data.Bones("ENERGY_GUN_NC");
        Assert.Equal(2, bones.Length);
        Assert.Equal("ef_muzzlea", bones[0].Parent);
        Assert.Equal([0.56f, 0f, -0.04f], bones[0].Position);
        Assert.Equal([0, 0, 4096], bones[0].RotationFixedTurns);
        Assert.Equal(3, bones[0].Line);
        Assert.Equal("addendum.lst", bones[0].Source);
        Assert.Empty(data.Bones("absent"));
    }

    [Theory]
    [InlineData("addendum_bone x parent 0 0 0 0 0 0")]
    [InlineData("addendum_package test\naddendum_bone x parent 0 0 0 0 0")]
    [InlineData("addendum_package test\naddendum_bone x parent NaN 0 0 0 0 0")]
    [InlineData("addendum_package test\naddendum_bone x parent 0 0 0 0 0 xyz")]
    public void MalformedBoneCommandsFailWithSourceLine(string source)
    {
        var error = Assert.Throws<InvalidDataException>(() => AddendumListDatabase.Parse(source));
        Assert.Contains("addendum.lst:", error.Message);
    }
}
