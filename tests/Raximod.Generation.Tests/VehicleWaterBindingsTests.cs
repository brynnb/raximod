using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class VehicleWaterBindingsTests
{
    private static GameObjectDb.GameObject Definition(string name, params (string Key, string Value, string Owner)[] values) => new()
    {
        Name = name,
        Properties = values.ToDictionary(v => v.Key, v => new List<string> { v.Value }),
        PropertySources = values.Select((v, i) => (v.Key, Source: new GameObjectDb.GameObjectPropertySource(v.Owner, i, i * 20)))
            .ToDictionary(v => v.Key, v => v.Source),
    };

    [Fact]
    public void VariantOverrideAndInheritedFlotationKeepTheirSeparateOrigins()
    {
        var source = Definition("thunderer", ("water_maxdragdepth", "1.2", "mediumtransport"),
            ("water_maxspeedpercentage", "0.64", "thunderer"),
            ("water_floatsatmaxdragdepth", "true", "mediumtransport"),
            ("water_underwaterlifespan", "-1", "mediumtransport"));
        var water = Assert.IsType<VehicleWaterBindings.Water>(VehicleWaterBindings.Resolve(source));
        Assert.Equal("thunderer", water.Definition);
        Assert.Equal(1.2f, water.MaxDragDepth);
        Assert.Equal(.64f, water.MaxSpeedFraction);
        Assert.True(water.FloatsAtMaxDragDepth);
        Assert.Equal(-1f, water.UnderwaterLifespanSeconds);
        Assert.Equal("thunderer", water.PropertySources["water_maxspeedpercentage"].DefinedBy);
        Assert.Equal("mediumtransport", water.PropertySources["water_maxdragdepth"].DefinedBy);
        Assert.Null(water.IsHoverVehicle);
    }

    [Fact]
    public void HoverTimingAndMissingFieldsAreNotConvertedToFlotationDefaults()
    {
        var source = Definition("hover", ("ishovervehicle", "true", "hover"),
            ("water_maxspeedpercentage_hover_secondstil", "3", "hover"));
        var water = VehicleWaterBindings.Resolve(source)!;
        Assert.True(water.IsHoverVehicle);
        Assert.Equal(3f, water.HoverSecondsToMaxDrag);
        Assert.Null(water.FloatsAtMaxDragDepth);
        Assert.Null(water.UnderwaterLifespanSeconds);
        Assert.Null(VehicleWaterBindings.Resolve(Definition("aircraft")));
    }

    [Theory]
    [InlineData("water_maxdragdepth", "not-a-number")]
    [InlineData("water_maxspeedpercentage", "NaN")]
    [InlineData("water_floatsatmaxdragdepth", "sometimes")]
    public void MalformedWaterValuesFailAtTheProducingLayer(string key, string value)
    {
        Assert.Throws<InvalidDataException>(() => VehicleWaterBindings.Resolve(Definition("bad", (key, value, "bad"))));
    }

    [Fact]
    public void RepeatedScalarsAndMissingProvenanceCannotSilentlyBecomeDefaults()
    {
        var source = Definition("bad", ("water_maxspeedpercentage", "0.7", "bad"));
        source.Properties["water_maxspeedpercentage"].Add("0.64");
        Assert.Throws<InvalidDataException>(() => VehicleWaterBindings.Resolve(source));
        source = new() { Name = "bad", Properties = new() { ["water_maxdragdepth"] = ["1.2"] } };
        Assert.Throws<InvalidDataException>(() => VehicleWaterBindings.Resolve(source));
    }
}
