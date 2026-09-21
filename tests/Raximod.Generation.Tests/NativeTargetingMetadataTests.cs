using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeTargetingMetadataTests
{
    private static GameObjectDb.GameObject Target()
    {
        var fields = new Dictionary<string, List<string>> {
            ["radius"] = ["5.25"], ["sphere_offset"] = ["-0.75", "0.049", "1.3091"],
        };
        return new() { Name = "wasp", Properties = fields, PropertySources = fields.Keys.ToDictionary(
            key => key, key => new GameObjectDb.GameObjectPropertySource("mosquito", 30, 120)) };
    }

    [Fact]
    public void InheritedRadiusRemainsDistinctFromSphereAndUnauthoredCentroid()
    {
        var target = NativeTargetingMetadata.Resolve(Target());
        Assert.Equal(5.25f, target.Radius);
        Assert.Equal(new[] { -.75f, .049f, 1.3091f }, target.SphereOffset);
        Assert.Null(target.SteerTowardCentroid);
        Assert.Null(target.TargetBone);
        Assert.Null(target.TargetOffset);
        Assert.Equal("mosquito", target.PropertySources["radius"].DefinedBy);
        Assert.Equal("right-handed-z-up", target.CoordinateSystem);
    }

    [Fact]
    public void BoneAndOffsetArePreservedAndCheckedAgainstTheActualModel()
    {
        var source = Target();
        source.Properties["autolock_steer_toward_centroid"] = ["true"];
        source.Properties["targetpos_bone"] = ["deliverer_body"];
        source.Properties["targetpos_offset"] = ["0", "0", "2.1"];
        foreach (var key in source.Properties.Keys)
            ((Dictionary<string, GameObjectDb.GameObjectPropertySource>)source.PropertySources)[key] = new("mediumtransport", 40, 160);
        var target = NativeTargetingMetadata.Resolve(source, ["DELIVERER_BODY"]);
        Assert.True(target.SteerTowardCentroid);
        Assert.Equal(new[] { 0f, 0f, 2.1f }, target.TargetOffset);
        Assert.Throws<InvalidDataException>(() => NativeTargetingMetadata.Resolve(source, ["some_other_body"]));
    }

    [Fact]
    public void MalformedOrRepeatedValuesAndMissingProvenanceFailExport()
    {
        var source = Target();
        source.Properties["radius"].Add("7");
        Assert.Throws<InvalidDataException>(() => NativeTargetingMetadata.Resolve(source));
        source.Properties["radius"] = ["NaN"];
        Assert.Throws<InvalidDataException>(() => NativeTargetingMetadata.Resolve(source));
        source.Properties["radius"] = ["5.25"];
        source.Properties["sphere_offset"] = ["0", "0"];
        Assert.Throws<InvalidDataException>(() => NativeTargetingMetadata.Resolve(source));
        source = Target();
        ((Dictionary<string, GameObjectDb.GameObjectPropertySource>)source.PropertySources).Clear();
        Assert.Throws<InvalidDataException>(() => NativeTargetingMetadata.Resolve(source));
    }
}
