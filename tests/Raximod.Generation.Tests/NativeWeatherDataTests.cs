using Raximod.EngineAssets.Databases;
using Raximod.Generation.Continents;
using System.Text.Json;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeWeatherDataTests
{
    private static string Executable => Path.Combine(Environment.GetEnvironmentVariable("PLANETSIDE_DIR")
        ?? "/home/brynn/Downloads/PlanetSide", "planetside.exe");

    [InstalledClientFact("planetside.exe")]
    public void ReadsEveryInitializedContinentWithoutMapNameHeuristics()
    {
        var weather = NativeWeatherData.Read(Executable);
        Assert.Equal(Enumerable.Range(1, 33), weather.Continents.Select(c => c.Id));
        Assert.Equal(33, weather.Continents.Select(c => c.Zone).Distinct().Count());
        Assert.Equal(["map06", "map07", "ugd06", "map96"], weather.Continents
            .Where(c => c.Precipitation == "snow").Select(c => c.Map));
        Assert.Equal(["map01", "map04", "map08", "ugd01", "ugd04", "map97"], weather.Continents
            .Where(c => c.Precipitation == "sand").Select(c => c.Map));
        Assert.Equal(23, weather.Continents.Count(c => c.Precipitation == "rain"));
        Assert.Equal(1, weather.Continents.Single(c => c.Map == "map09").Climate);
        Assert.Equal("rain", weather.Continents.Single(c => c.Map == "map09").Precipitation);
        Assert.Equal([17, 18, 19], weather.Continents.Where(c => c.Map == "map14").Select(c => c.Id));
        Assert.DoesNotContain(weather.Continents, c => c.Map == "map16");
        Assert.Equal([14, 15, 16, 23, 24, 25, 26, 27, 28, 33], weather.Continents
            .Where(c => !c.AcceptsGlobalStorms).Select(c => c.Id));
        // This native filter alone does not exclude Sanctuary or VR. Browser
        // scheduling eligibility must be an explicit separate policy.
        Assert.All(weather.Continents.Where(c => c.ZoneType is 1 or 3 or 4 && c.NativeMapIndex != 0),
            c => Assert.True(c.AcceptsGlobalStorms));
        Assert.All(weather.Continents, c => Assert.InRange(c.ClimateInstruction,
            NativeWeatherData.InitializerStart, NativeWeatherData.InitializerEnd - 1));
    }

    [InstalledClientFact("planetside.exe")]
    public void ReadsOriginalScalarConstantsIncludingFloatPrecision()
    {
        var weather = NativeWeatherData.Read(Executable);
        Assert.Equal(8192, weather.CoordinateExtent);
        Assert.Equal((double)(1f / 255f), weather.ByteScale);
        Assert.Equal(2500, weather.RadiusScale);
        Assert.Equal(.25, weather.InnerRadiusFraction);
        Assert.Equal(.25, weather.StrengthThreshold);
        Assert.Equal((double)(4f / 3f), weather.StrengthScale);
        Assert.Equal((double)(1f / 30f), weather.StrengthOffset);
        Assert.Equal(30, weather.LogarithmBase);
        Assert.Equal(5, weather.TransitionSeconds);
        Assert.Equal(2.5, weather.RefreshSeconds);
        Assert.Equal(1000000, weather.TeleportDistanceSquared);
        Assert.Equal(200, weather.ClearTransmission);
    }

    [InstalledClientFact("planetside.exe")]
    public void ReadsCloudWindTransitionSeparatelyFromConstructorDefaults()
    {
        var clouds = NativeWeatherData.Read(Executable).Clouds;
        Assert.Equal([0d, 1d], clouds.DefaultWind);
        Assert.Equal(1d, clouds.DefaultWindScale);
        Assert.Equal(10d, clouds.WindTransitionSeconds);
        Assert.Equal((double).3f, clouds.WindMotionScale);
    }

    [InstalledClientFact("planetside.exe")]
    public void ReadsWaterAndLavaFogProfilesWithoutConfusingNormalizedFogWithClipDistance()
    {
        var weather = NativeWeatherData.Read(Executable);
        Assert.Equal(["water", "lava"], weather.Underwater.Select(p => p.Kind));
        Assert.Equal([0x081015, 0xff5000], weather.Underwater.Select(p => p.Tint));
        Assert.Equal([100d, 10d], weather.Underwater.Select(p => p.FarPlane));
        Assert.Equal([2d, (double).01f], weather.Underwater.Select(p => p.TransitionDepth));
        Assert.Equal((double).1f, weather.UnderwaterFogEnd);
        Assert.Equal(new NativeWeatherData.LiquidQuery(-1, 100, 8, 11, "ugd"), weather.LocalLiquidQuery);
    }

    [InstalledClientFact("planetside.exe")]
    public void ReadsDedicatedParticleConstructorsAndNativeDrawDifferences()
    {
        var all = NativeWeatherData.Read(Executable).Particles;
        Assert.Equal(["rain", "snow", "sand"], all.Select(p => p.Kind));
        Assert.Equal([12000, 12000, 16], all.Select(p => p.Capacity));
        var rain = all[0]; var snow = all[1]; var sand = all[2];
        Assert.Equal([-9.5, -11.5], rain.Velocity[2]); // Keep the reversed native range.
        Assert.Equal([.5, 1], rain.Lifetime);
        Assert.Equal([5, 8], snow.Lifetime);
        Assert.Equal([1, 4], sand.Lifetime);
        Assert.Equal(1, rain.StreakLength);
        Assert.Equal((double).707f, snow.QuadHalfSizeScale);
        Assert.Equal([5, 2, 5], all.Select(p => p.SourceBlend));
        Assert.Equal([6, 2, 6], all.Select(p => p.DestinationBlend));
        Assert.Equal([false, false, true], all.Select(p => p.CameraRelative));
        Assert.Equal(["rain_64x64", "snow_64x64", "duststorm"], all.Select(p => p.Texture));
        Assert.Equal((double)(140f / 255f), sand.Tint[0]);
        Assert.All(all, p => Assert.Equal(3, p.Position.Length));
    }

    [InstalledClientFact("planetside.exe")]
    public void RetainsEverySoundRegistrationAndOriginalBandAndFadeConstants()
    {
        var audio = NativeWeatherData.Read(Executable).Audio;
        Assert.Equal([.25, (double).45f, .75], audio.Thresholds);
        Assert.Equal(5, audio.CrossfadeSeconds);
        Assert.Equal((double).35f, audio.ShelteredGain);
        Assert.Equal(.5, audio.ShelterFadeSeconds);
        Assert.Equal(1, audio.ExteriorFadeSeconds);
        Assert.Equal(18, audio.Cues.Count);
        Assert.Equal(["rain_light.wav", "rain_medium.wav", "rain_hardest.wav"],
            audio.Cues.Where(c => c.Category == audio.RainCategory).Select(c => c.File));
        Assert.Equal(["wind_01.wav", "snow_medium.wav", "snow_heavy.wav"],
            audio.Cues.Where(c => c.Category == audio.SnowCategory).Select(c => c.File));
        foreach (int band in new[] { 1, 2, 3 })
            Assert.Equal(["sandstorm_01.wav", "sandstorm_02.wav"], audio.Cues
                .Where(c => c.Category == audio.SandCategory && c.Band == band).Select(c => c.File));
        Assert.Equal(6, audio.Cues.Count(c => c.Band == 0 && c.Category >= 4));
    }

    [InstalledClientFact("planetside.exe")]
    public void PreservesOriginalCloudGeometryAndDistinctCoverageConstants()
    {
        var clouds = NativeWeatherData.Read(Executable).Clouds;
        Assert.Equal("full", clouds.FullTexture); Assert.Equal("partly", clouds.PartlyTexture);
        Assert.Equal(200, clouds.RadiusBase); Assert.Equal((double)(2500f / 255f), clouds.RadiusByteScale);
        Assert.Equal(600, clouds.Falloff); Assert.Equal((double)2.8f, clouds.VertexPositionScale);
        Assert.Equal([2, 2, 1], clouds.Scale); Assert.Equal(-50, clouds.VerticalOffset);
        Assert.Equal((double).00625f, clouds.DriftRate);
        Assert.Equal(["modulate", "modulate2x"], clouds.AlphaOperations);
        var meshes = NativeWeatherClouds.Geometry();
        Assert.Equal([526, 385], meshes.Select(m => m.Positions.Length));
        Assert.Equal([3000, 1920], meshes.Select(m => m.Indices.Length));
        Assert.All(meshes, m => Assert.Equal([0, 0, 200], m.Positions[0]));
        Assert.Equal(0, meshes[0].Alpha.Max()); // Filled from storm coverage at runtime.
        Assert.Equal(1, meshes[1].Alpha.Max()); // Original horizon masking curve retained.
        Assert.True(meshes[1].Positions.Min(p => p[2]) < 0); // Original horizon edge adjustment.
    }

    [InstalledClientFact("planetside.exe")]
    public void ReadsLightningSchedulerTexturesAndFlashTimingFromOriginalOperands()
    {
        var d = NativeWeatherData.Read(Executable).Lightning;
        Assert.Equal(["lightning01", "lightning02"], d.Textures);
        Assert.Equal(.25, d.MinimumStrength);
        Assert.Equal([10000, 10000], d.CheckIntervalMs);
        Assert.Equal([1000, 1500], d.EventThreshold);
        Assert.Equal([350, 650], d.CloseThreshold);
        Assert.Equal(10000, d.EventModulus); Assert.Equal(5000, d.CloseModulus);
        Assert.Equal(4, d.CloseCategory); Assert.Equal(5, d.DistantCategory);
        Assert.Equal(1, d.DurationSeconds); Assert.Equal(.75, d.BoltLifetimeScale);
        Assert.Equal(1, d.Size); Assert.Equal(.5, d.HalfSizeScale); Assert.Equal((double).707f, d.DiagonalScale);
        Assert.Equal([-1, 2], d.HorizontalRange); Assert.Equal([(double).1f, (double).9f], d.VerticalRange);
        Assert.Equal([1, 2], d.DistanceRange); Assert.Equal((double).9f, d.FlashDepth);
        Assert.Equal(214013u, d.RandomMultiplier); Assert.Equal(2531011u, d.RandomIncrement);
        Assert.Equal(16, d.RandomShift); Assert.Equal(32767, d.RandomMask);
        Assert.Equal(1103515245u, d.DirectionMultiplier); Assert.Equal(12345u, d.DirectionIncrement);
        Assert.Equal(1d / 4294967296, d.DirectionScale);
    }

    [Fact]
    public void RejectsUnrecognizedExecutableInsteadOfApplyingAddressGuesses()
    {
        string path = Path.Combine("/var/tmp", "weather-executable-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, [77, 90, 0, 0]);
            var error = Assert.Throws<InvalidDataException>(() => NativeWeatherData.Read(path));
            Assert.Contains("SHA-256", error.Message);
        }
        finally { File.Delete(path); }
    }

    [InstalledClientFact("planetside.exe")]
    public void MetadataRefreshPreservesPackagedResourcesAndIsIdempotent()
    {
        string directory = Path.Combine("/var/tmp", "weather-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "environment.json");
        try
        {
            File.WriteAllText(path, """
                {"format":"raxicore-planetside-environment","version":3,
                 "zones":{"map12":{"cycle":"retained"}},"skyModels":{"model":"retained.glb"},
                 "weather":{"textures":{"rain":"../textures/unchanged.png"},
                   "audio":{"rain_light":"unchanged.wav"},"effects":{"rain":"env_rain"}}}
                """);
            EnvironmentCatalog.RefreshWeather(Path.GetDirectoryName(Executable)!, directory);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.Equal("retained", root.GetProperty("zones").GetProperty("map12").GetProperty("cycle").GetString());
            Assert.Equal("retained.glb", root.GetProperty("skyModels").GetProperty("model").GetString());
            var weather = root.GetProperty("weather");
            Assert.Equal("../textures/unchanged.png", weather.GetProperty("textures").GetProperty("rain").GetString());
            Assert.Equal("unchanged.wav", weather.GetProperty("audio").GetProperty("rain_light").GetString());
            Assert.False(weather.TryGetProperty("effects", out _));
            Assert.Equal(33, weather.GetProperty("native").GetProperty("continents").GetArrayLength());
            Assert.Single(Directory.GetFiles(directory));
            string cloudPath = Path.Combine(directory, "environment", "weather", "clouds.json");
            Assert.True(File.Exists(cloudPath));
            var sentinel = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, sentinel);
            EnvironmentCatalog.RefreshWeather(Path.GetDirectoryName(Executable)!, directory);
            Assert.Equal(sentinel, File.GetLastWriteTimeUtc(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
