using System.Buffers.Binary;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Maps;
using Raximod.Generation.Continents;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class WeatherTerrainMapTests
{
    [Fact]
    public void PreservesEveryBitAndRejectsInvalidLength()
    {
        var bytes = new byte[TerrainMap.ByteLength];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
        Assert.Equal(bytes, TerrainMap.Parse(bytes).ToBytes());
        Assert.Throws<InvalidDataException>(() => TerrainMap.Parse(bytes.AsSpan(1)));
        Assert.Throws<InvalidDataException>(() => TerrainMap.Parse(new byte[TerrainMap.ByteLength + 1]));
    }

    [InstalledClientFact("maps/map_resources.pak")]
    public void SourceRoofsAndCellBoundariesMatchOriginalInstructionSamples()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR") ?? "/home/brynn/Downloads/PlanetSide";
        var pak = PakArchive.Load(File.ReadAllBytes(Path.Combine(client, "maps/map_resources.pak")));
        var map = TerrainMap.Parse(pak.Extract("map12.trn"));
        Assert.Equal(60, map.HeightAt(2620, 5418)); Assert.Equal(64, map.HeightAt(2636, 5418));
        Assert.Equal(36, map.HeightAt(2652, 5418)); Assert.Equal(84, map.HeightAt(4484, 3080));
        Assert.Equal(51, map.HeightAt(4516, 3080));
        Assert.Equal(map.HeightAt(0, 0), map.HeightAt(-.5, 0));
        Assert.Equal(map.HeightAt(0, 0), map.HeightAt(3.99, 0));
        Assert.Equal(0, map.HeightAt(-4, 0)); Assert.Equal(0, map.HeightAt(8192, 0));
        Assert.Throws<ArgumentException>(() => map.HeightAt(double.NaN, 0));
        var encoded = WeatherTerrainMapExport.Encode(map);
        Assert.Equal("TRN1"u8.ToArray(), encoded[..4]);
        int cursor = 0;
        for (int offset = 8; offset < encoded.Length; offset += 4)
        {
            int count = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(offset));
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(offset + 2));
            Assert.InRange(count, 1, ushort.MaxValue);
            for (int i = 0; i < count; i++) Assert.Equal(map.Cells[cursor++], value);
        }
        Assert.Equal(map.Cells.Length, cursor);
        Assert.True(encoded.Length < TerrainMap.ByteLength);
        Assert.Equal(encoded, WeatherTerrainMapExport.Encode(map));
    }

    [InstalledClientFact("maps/map_resources.pak")]
    public void InvalidSourceSetCannotOverwriteAnExistingPublication()
    {
        string client = Environment.GetEnvironmentVariable("PLANETSIDE_DIR") ?? "/home/brynn/Downloads/PlanetSide";
        string output = Path.Combine(Path.GetTempPath(), "weather-grid-" + Guid.NewGuid());
        string path = Path.Combine(output, "environment/weather/terrain/map12.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] existing = [1, 2, 3]; File.WriteAllBytes(path, existing);
        try
        {
            var error = Record.Exception(() => WeatherTerrainMapExport.Export(client, output, ["map12", "mapzz"]));
            Assert.NotNull(error); Assert.Contains("mapzz.trn", error.ToString());
            Assert.Equal(existing, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(output, true); }
    }
}
