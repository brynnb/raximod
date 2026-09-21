using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class TerrainMaterialExportTests
{
    [Fact]
    public void DetailUsesNativeIdentityAndRateWithoutSurfaceClassGuessing()
    {
        var detail = TerrainMaterialExportTool.ReadDetail("map10",
            [new("mat_detail", ["det_map10"]), new("mat_tilerate", ["20"])]);
        Assert.Equal("det_map10", detail.Texture);
        Assert.Equal(20f, detail.TileRate);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("20 30")]
    public void InvalidScaleIsNotHiddenByADefault(string value) => Assert.Throws<InvalidDataException>(() =>
        TerrainMaterialExportTool.ReadDetail("map10", [new("mat_detail", ["det_map10"]), new("mat_tilerate", [value])]));

    [Fact]
    public void MissingRepeatedAndMultiValuedScalarsFail()
    {
        AsciiCommandDatabase.Command[] valid = [new("mat_detail", ["det_map10"]), new("mat_tilerate", ["20"])];
        Assert.Throws<InvalidDataException>(() => TerrainMaterialExportTool.ReadDetail("map10", []));
        Assert.Throws<InvalidDataException>(() => TerrainMaterialExportTool.ReadDetail("map10", [..valid, valid[0]]));
        Assert.Throws<InvalidDataException>(() => TerrainMaterialExportTool.ReadDetail("map10",
            [valid[0], new("mat_tilerate", ["20", "30"])]));
    }
}
