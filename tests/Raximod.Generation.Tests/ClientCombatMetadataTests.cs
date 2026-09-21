using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class ClientCombatMetadataTests
{
    private static GameObjectDb.GameObject Record() => new() { Name = "test", Properties = new() };
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void ModeDynamicsRetainZeroAndRejectMalformedScalars()
    {
        var record = Record();
        Assert.Null(ClientCombatMetadata.ModeNumber(record, 0, "recoil"));
        foreach (var raw in new[] { "0", "1.75", "700" })
        {
            record.Properties["firemode0_recoil"] = [raw];
            Assert.NotNull(ClientCombatMetadata.ModeNumber(record, 0, "recoil"));
        }
        foreach (var raw in new string[][] { ["NaN"], ["Infinity"], ["-1"], ["1", "2"], ["bad"] })
        {
            record.Properties["firemode0_recoil"] = raw.ToList();
            Assert.Throws<InvalidDataException>(() => ClientCombatMetadata.ModeNumber(record, 0, "recoil"));
        }
    }

    [Fact]
    public void OpticsOrdersNativeSlotsAndRejectsHoles()
    {
        var record = Record();
        record.Properties["zoomlevel3"] = ["8"];
        record.Properties["zoomlevel1"] = ["2"];
        Assert.Throws<InvalidDataException>(() => ClientCombatMetadata.Optics(record));
        record.Properties["zoomlevel2"] = ["4"];
        Assert.Equal([2f, 4f, 8f], Json(ClientCombatMetadata.Optics(record)).GetProperty("magnifications")
            .EnumerateArray().Select(value => value.GetSingle()).ToArray());
    }

    [Fact]
    public void OpticsRetainsExplicitRangefinderAndRejectsMalformedFlags()
    {
        var record = Record();
        Assert.False(Json(ClientCombatMetadata.Optics(record)).GetProperty("rangefinder").GetBoolean());
        record.Properties["is_oicw"] = ["true"];
        Assert.True(Json(ClientCombatMetadata.Optics(record)).GetProperty("rangefinder").GetBoolean());
        record.Properties["is_oicw"] = ["true", "false"];
        Assert.Throws<InvalidDataException>(() => ClientCombatMetadata.Optics(record));
    }

    [Fact]
    public void StaminaRequiresBothNativeIntegerFields()
    {
        var record = Record();
        Assert.Null(ClientCombatMetadata.Stamina(record, 1));
        record.Properties["firemode1_stamina_required"] = ["75"];
        Assert.Throws<InvalidDataException>(() => ClientCombatMetadata.Stamina(record, 1));
        record.Properties["firemode1_stamina_drain"] = ["100"];
        var value = Json(ClientCombatMetadata.Stamina(record, 1)!);
        Assert.Equal(75, value.GetProperty("required").GetSingle());
        Assert.Equal(100, value.GetProperty("drain").GetSingle());
        record.Properties["firemode1_stamina_drain"] = ["0.5"];
        Assert.Throws<InvalidDataException>(() => ClientCombatMetadata.Stamina(record, 1));
    }
    [Fact]
    public void MaxPitchCapRetainsRecoveredConstructorDefaultAndExplicitOverride()
    {
        var record = new GameObjectDb.GameObject { Name = "test", Type = "armor", Properties = new() };
        foreach (var field in new[] { "heavy_armor_turn_rate", "heavy_armor_turn_rate_cap",
            "min_turn_rate", "max_turn_rate", "min_pitch_rate", "max_pitch_rate",
            "walk_forward_speed", "run_forward_speed" }) record.Properties[field] = ["1"];
        JsonElement Look() => Json(ClientCombatMetadata.InfantryCatalog([record]))
            .GetProperty("armor").GetProperty("test").GetProperty("look");
        Assert.Equal(5000, Look().GetProperty("pitchCapAngleUnitsPerSecond").GetSingle());
        Assert.Contains("0x94da9d", Look().GetProperty("pitchCapProvenance").GetString());
        record.Properties["heavy_armor_pitch_rate_cap"] = ["6000"];
        Assert.Equal(6000, Look().GetProperty("pitchCapAngleUnitsPerSecond").GetSingle());
        record.Properties.Remove("min_turn_rate");
        Assert.Throws<InvalidDataException>(() => ClientCombatMetadata.InfantryCatalog([record]));
    }

}
