using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class CloakExportToolTests
{
    private static GameObjectDb.GameObject Record()
    {
        var record = new GameObjectDb.GameObject { Name = "fixture" };
        foreach (string key in new[] { "cloak_transition_fadeinrate", "cloak_transition_fadeoutrate", "cloak_movement_speedformaxpenalty" })
            record.Properties[key] = ["1", "2", "3"];
        record.Properties["cloak_penalitesareadditive"] = ["false", "true", "false"];
        record.Properties["cloak_alphafade_minrange"] = ["45"];
        record.Properties["cloak_alphafade_maxrange"] = ["175"];
        record.Properties["cloak_alphafade_zoom"] = ["0.25"];
        foreach (string key in new[] { "penaltypct", "postpenaltyduration", "recoverytime" })
            record.Properties[$"cloak_movement_{key}"] = ["0.2", "0.14", "0.27"];
        return record;
    }

    [Fact]
    public void PreservesFactionTuplesAndAbsentEventChannels()
    {
        var json = JsonSerializer.SerializeToElement(CloakExportTool.Profile(Record()));
        Assert.Equal(new[] { "nc", "tr", "vs" }, json.GetProperty("factionOrder").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { .2f, .14f, .27f }, json.GetProperty("movement").GetProperty("penalty").EnumerateArray().Select(value => value.GetSingle()));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("jumping").ValueKind);
    }

    [Fact]
    public void RejectsRepeatedScalarsIncompleteTuplesAndMalformedValues()
    {
        var record = Record();
        record.Properties["cloak_alphafade_zoom"] = ["0.25", "0.5"];
        Assert.Throws<InvalidDataException>(() => CloakExportTool.Profile(record));
        record = Record();
        record.Properties["cloak_movement_penaltypct"] = ["0.2"];
        Assert.Throws<InvalidDataException>(() => CloakExportTool.Profile(record));
        record = Record();
        record.Properties["cloak_transition_fadeinrate"] = ["NaN", "2", "3"];
        Assert.Throws<InvalidDataException>(() => CloakExportTool.Profile(record));
    }
}
