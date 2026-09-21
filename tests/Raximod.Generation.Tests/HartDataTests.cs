using System.Buffers.Binary;
using System.Numerics;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Maps;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class HartDataTests
{
    [Fact]
    public void SoundKeysRetainTuplesAndNativeAnimationDelaysAndRejectMalformedValues()
    {
        var record = new GameObjectDb.GameObject { Name = "orbital_shuttle", Properties = new() {
            ["soundkey_takeoff"] = ["orbital_shuttle_launch.wav", "1.0", "400"],
            ["sound_liftoff_delay"] = ["0.1"],
            ["sound_gantry_moving"] = ["gantry_moving.wav"], ["sound_gantry_moving_volume"] = ["0.5"],
            ["sound_gantry_moving_range"] = ["200"], ["sound_gantry_moving_delay"] = ["0.5"],
        } };
        var sounds = HartBindings.ResolveSounds(record);
        Assert.Equal(new HartBindings.Sound("orbital_shuttle_launch.wav", 1, 400, 0.1f), sounds["soundkey_takeoff"]);
        Assert.Equal(new HartBindings.Sound("gantry_moving.wav", 0.5f, 200, 0.5f), sounds["sound_gantry_moving"]);
        record.Properties["soundkey_takeoff"] = ["orbital_shuttle_launch.wav", "1"];
        Assert.Throws<InvalidDataException>(() => HartBindings.ResolveSounds(record));
        record.Properties["soundkey_takeoff"] = ["orbital_shuttle_launch.wav", "NaN", "400"];
        Assert.Throws<InvalidDataException>(() => HartBindings.ResolveSounds(record));
    }
    private static byte[] Table()
    {
        var bytes = new byte[8 + 4 * 8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 64);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 32);
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8 + i * 8), 10 + i);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12 + i * 8), 20 + i);
        }
        return bytes;
    }

    [Fact]
    public void LookupUsesNativeRowMajorCoordinatesAndTruncationAtBoundaries()
    {
        byte[] bytes = Table();
        var table = DropPodLocationTable.Parse(bytes);
        Assert.Equal(40, table.AccountedBytes);
        Assert.Equal(4, table.CellCount);
        Assert.Equal(new Vector2(10, 20), table.Lookup(new Vector2(31.99f, 31.99f)));
        Assert.Equal(new Vector2(11, 21), table.Lookup(new Vector2(32, 31.99f)));
        Assert.Equal(new Vector2(12, 22), table.Lookup(new Vector2(31.99f, 32)));
        Assert.Equal(new Vector2(13, 23), table.Lookup(new Vector2(32, 32)));
        Assert.Equal(new Vector2(10, 20), table.Lookup(new Vector2(-1, -1)));
        Assert.Equal(new Vector2(13, 23), table.Lookup(new Vector2(9000, 9000)));
        Assert.Equal(bytes, table.Encode());
        bytes[8] ^= 255;
        Assert.Equal(new Vector2(10, 20), table.Cell(0, 0)); // owns immutable source copy
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Lookup(new Vector2(float.NaN, 0)));
    }

    [Fact]
    public void TruncatedTrailingOrInvalidCoordinateDataCannotPublishAsValid()
    {
        Assert.Throws<InvalidDataException>(() => DropPodLocationTable.Parse(new byte[7]));
        Assert.Throws<InvalidDataException>(() => DropPodLocationTable.Parse(Table()[..^1]));
        Assert.Throws<InvalidDataException>(() => DropPodLocationTable.Parse([.. Table(), 0]));
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, -1, 64 })
        {
            byte[] bytes = Table();
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), value);
            Assert.Contains("byte 8", Assert.Throws<InvalidDataException>(() => DropPodLocationTable.Parse(bytes)).Message);
        }
        byte[] invalid = Table();
        BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(4), 0);
        Assert.Throws<InvalidDataException>(() => DropPodLocationTable.Parse(invalid));
        BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(4), 3);
        Assert.Throws<InvalidDataException>(() => DropPodLocationTable.Parse(invalid));
    }

    [Theory]
    [InlineData("ant.MountPoints += 2 -> MountInfo(0)", "ant", "2", "0")]
    [InlineData("orbital_shuttle.MountPoints += 7 -> MountInfo(0, Vector3(62, 4, -18.2f))", "orbital_shuttle", "7", "0")]
    public void ServerMountBindingsRetainOptionalDismountPositionDeclarations(string line, string owner, string entrance, string seat)
    {
        var match = VehicleManifestContract.ServerMountDeclaration(line);
        Assert.True(match.Success);
        Assert.Equal(owner, match.Groups[1].Value);
        Assert.Equal(entrance, match.Groups[2].Value);
        Assert.Equal(seat, match.Groups[3].Value);
        Assert.False(VehicleManifestContract.ServerMountDeclaration("// ant.MountPoints += test").Success);
        Assert.Throws<InvalidDataException>(() => VehicleManifestContract.ServerMountDeclaration("ant.MountPoints += unsupported()"));
    }

    [Fact]
    public void HARTEntrancesKeepTheAuthoredRangeAndNativeLocationNotServerExitCoordinates()
    {
        var record = new GameObjectDb.GameObject { Name = "orbital_shuttle", Properties = new() {
            ["mountzone7_name"] = ["PassengerG"], ["mountzone7_mountpointindexes"] = ["range", "1", "100"],
            ["mountzone7_location"] = ["-60", "4", "-18.2"], ["mountzone7_zorientation"] = ["270"],
        } };
        var entry = Assert.Single(HartBindings.Entrances(record));
        Assert.Equal(new HartBindings.StationRange(1, 100), entry.Stations);
        Assert.Equal(new float[] { -60, 4, -18.2f }, entry.Location);
        record.Properties["mountzone7_mountpointindexes"] = ["range", "100", "1"];
        Assert.Throws<InvalidDataException>(() => HartBindings.Entrances(record));
        record.Properties["mountzone7_mountpointindexes"] = ["range", "1", "300"];
        record.Properties["mountzone7_zorientation"] = ["90", "270"];
        Assert.Throws<InvalidDataException>(() => HartBindings.Entrances(record));
    }
}
