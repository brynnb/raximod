using System.Text.Json;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;
using Raximod.Generation.Assets;

namespace Raximod.Generation.Continents
{
    /// <summary>Exports the original client's zone sky, time-cycle, and weather resources.</summary>
    public static class EnvironmentCatalog
    {
        private static readonly string[] WeatherTextures =
        {
            "weathersky_sun_one.dds", "weathersky_moon_one.dds", "weathersky_moon_two.dds",
            "ef_sun_one.dds", "ef_moon_one.dds", "ef_moon_two.dds",
            "partly_cloudy.dds", "partly.dds", "full.dds",
            "clear2partly_trans.dds", "partly2clear_trans.dds", "partly2full_trans.dds",
            "full2partly_trans.dds", "overcast_a.dds", "overcast_b.dds", "overcast_c.dds",
            "overcast_d.dds", "gradient.dds", "dust.dds", "duststorm.dds",
            "lightning01.dds", "lightning02.dds", "rain_64x64.dds", "snow_64x64.dds"
        };

        private static readonly string[] WeatherAudio =
        {
            "wind_01.wav", "snow_medium.wav", "snow_heavy.wav", "sandstorm_01.wav",
            "sandstorm_02.wav", "rain_light.wav", "rain_medium.wav", "rain_hardest.wav",
            "lightning_distance_01.wav", "lightning_distance_02.wav", "lightning_distance_03.wav",
            "lightning_close_01.wav", "lightning_close_02.wav", "lightning_close_03.wav"
        };

        public static void Export(string planetside, string outDir, IEnumerable<string> zones, IProgress<string> log)
        {
            var weather = NativeWeatherData.Read(Path.Combine(planetside, "planetside.exe"));
            SkyDatabase? sky = SkyDatabase.TryLoad(planetside);
            if (sky == null)
            {
                log.Report("environment: startup sky databases could not be decoded");
                return;
            }

            string assetDir = Path.Combine(outDir, "environment");
            string skyDir = Path.Combine(assetDir, "sky");
            string weatherDir = Path.Combine(assetDir, "weather");
            string audioDir = Path.Combine(weatherDir, "audio");
            Directory.CreateDirectory(skyDir);
            Directory.CreateDirectory(weatherDir);
            Directory.CreateDirectory(audioDir);

            var textures = new TextureProvider(planetside);
            PakArchive startup = PakArchive.Load(File.ReadAllBytes(Path.Combine(planetside, "startup.pak")));
            var packages = NativeEffectPackageCatalog.Parse(startup.Extract("epackage.adb"))
                .ToDictionary(package => package.Name, StringComparer.OrdinalIgnoreCase);
            var environments = zones.Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(zone => sky.EnvironmentForZone(zone)).Where(environment => environment != null)
                .Select(environment => environment!).ToArray();
            var bindings = environments.SelectMany(environment => environment.Layers)
                .Distinct().ToDictionary(layer => layer, layer => SkyMaterialBindings.Resolve(layer, packages));
            SkyEffectAssets.Export(planetside, skyDir, environments.SelectMany(environment => environment.Layers)
                .Where(layer => layer.Kind.Equals("effect", StringComparison.OrdinalIgnoreCase))
                .Select(layer => layer.Name), textures);
            var skyModels = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var sourceMaterialNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string mesh in environments.SelectMany(environment => environment.Layers)
                .Where(layer => layer.Kind.Equals("mesh", StringComparison.OrdinalIgnoreCase))
                .Select(layer => layer.Name).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase))
            {
                // Preserve geometry, UVs and any authored animation; the layer's swap
                // package changes materials at runtime without duplicating this mesh.
                string modelPath = Path.Combine(skyDir, mesh.ToLowerInvariant() + ".glb");
                GlbExportTool.Result model = GlbExportTool.Run(new(planetside, mesh, modelPath,
                    IncludeTextures: false), log);
                using (JsonDocument sourceMaterials = JsonDocument.Parse(
                    File.ReadAllText(Path.ChangeExtension(modelPath, ".materials.json"))))
                    foreach (JsonElement material in sourceMaterials.RootElement.GetProperty("materials").EnumerateArray())
                        sourceMaterialNames.Add(material.GetProperty("name").GetString()!);
                skyModels[mesh] = new
                {
                    model = $"environment/sky/{Path.GetFileName(modelPath)}",
                    materials = $"environment/sky/{Path.GetFileNameWithoutExtension(modelPath)}.materials.json",
                    sourceLibrary = Path.GetRelativePath(planetside, model.LibraryPath).Replace('\\', '/'),
                    sourceRecord = model.RecordName,
                    triangles = model.Triangles,
                    animations = model.Animations
                };
            }
            sourceMaterialNames.UnionWith(bindings.Values.OfType<SkyMaterialBindings.Binding>()
                .SelectMany(binding => binding.MaterialSwaps).Select(swap => swap.Replacement)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            // The browser deliberately targets retail's generic four-stage profile, not
            // the user's GPU vendor. Preserve exact source names and export full selected
            // definitions; no material fields are merged from unlike hardware variants.
            var profileBindings = sourceMaterialNames.Order(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(name => name, name => NativeMaterialProfiles.Resolve(name,
                    NativeMaterialProfiles.GenericFourStageProfile, candidate => textures.MaterialCommands?.Lookup(candidate) != null),
                    StringComparer.OrdinalIgnoreCase);
            string[] replacementMaterials = profileBindings.Values.Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string replacement in replacementMaterials)
                if (textures.MaterialCommands?.Lookup(replacement) == null)
                    throw new InvalidDataException($"Sky swap references missing material '{replacement}'");
            // Share the same stage/sampler/alpha exporter as world geometry. A single
            // panorama URI cannot express the authored BLENDTEXTUREALPHA + TFACTOR stage.
            NativeMaterialManifestTool.Write(Path.Combine(skyDir, "sky-layers.glb"), "sky-layers",
                replacementMaterials.Select(name => new NativeMaterialManifestTool.Usage(name, 0, true, true)).ToArray(),
                textures);
            string[] unshippedSkyTextures;
            using (JsonDocument materials = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(skyDir, "sky-layers.materials.json"))))
            {
                string[] missing = materials.RootElement.GetProperty("missingTextures")
                    .EnumerateArray().Select(value => value.GetString()!).ToArray();
                // These two textures are absent in every installed archive and in the
                // original engine3d.txt failure log. Preserve that failure explicitly;
                // it is not permission to substitute another continent's panorama.
                string[] unexpected = missing.Except(["sky3des_layera", "skydes_layer2"],
                    StringComparer.OrdinalIgnoreCase).ToArray();
                if (unexpected.Length > 0)
                    throw new InvalidDataException($"Sky materials reference missing textures: {string.Join(", ", unexpected)}");
                unshippedSkyTextures = missing;
                foreach (string name in missing) log.Report($"environment: unshipped native sky texture '{name}'");
            }
            string weatherPakPath = Path.Combine(planetside, "pack", "weathersky.pak");
            var weatherAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var audioAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(weatherPakPath))
            {
                PakArchive weatherPak = PakArchive.Load(File.ReadAllBytes(weatherPakPath));
                foreach (string name in WeatherTextures)
                {
                    int index = weatherPak.IndexOf(name);
                    if (index < 0) continue;
                    try
                    {
                        DdsImage image = DdsImage.Decode(weatherPak.Extract(index));
                        string filename = Path.GetFileNameWithoutExtension(name).ToLowerInvariant() + ".png";
                        File.WriteAllBytes(Path.Combine(weatherDir, filename),
                            PngEncoder.EncodeBgra(image.Bgra, image.Width, image.Height));
                        weatherAssets[Path.GetFileNameWithoutExtension(name)] = $"environment/weather/{filename}";
                    }
                    catch (Exception exception)
                    {
                        log.Report($"  weather texture {name}: {exception.Message}");
                    }
                }
                foreach (string name in WeatherAudio)
                {
                    int index = weatherPak.IndexOf(name);
                    if (index < 0) continue;
                    string filename = name.ToLowerInvariant();
                    File.WriteAllBytes(Path.Combine(audioDir, filename), weatherPak.Extract(index));
                    audioAssets[Path.GetFileNameWithoutExtension(name)] = $"environment/weather/audio/{filename}";
                }
            }

            var zoneDocuments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (SkyDatabase.SkyEnvironment environment in environments)
            {
                zoneDocuments[environment.Zone] = new
                {
                    cycle = environment.Cycle,
                    skyDome = environment.SkyDome,
                    zoneProvenance = environment.ZoneProvenance,
                    skyDomeProvenance = environment.SkyDomeProvenance,
                    radius = environment.Radius,
                    sphereOffset = Vec(environment.SphereOffset),
                    dustCloudAngle = environment.DustCloudAngle is { } dust ? new[] { dust.X, dust.Y } : null,
                    layers = environment.Layers.Select(layer => new
                    {
                        index = layer.Index,
                        kind = layer.Kind,
                        name = layer.Name,
                        argument = layer.Argument,
                        swapBinding = bindings[layer] is { } binding ? new
                        {
                            package = binding.Package,
                            scope = binding.Scope,
                            materialSwaps = binding.MaterialSwaps.Select(swap => new
                            {
                                source = swap.Source, replacement = swap.Replacement,
                                streamOffset = swap.StreamOffset
                            }),
                            lightingSwaps = binding.LightingSwaps.Select(swap => new
                            {
                                source = swap.Source, replacement = swap.Replacement,
                                streamOffset = swap.StreamOffset
                            }),
                            hiddenParts = binding.HiddenParts.Select(part => new
                            {
                                part = part.Part, streamOffset = part.StreamOffset
                            }),
                            provenance = new
                            {
                                section = binding.Provenance.Section,
                                streamStart = binding.Provenance.StreamStart,
                                streamEnd = binding.Provenance.StreamEnd,
                                isIndexed = binding.Provenance.IsIndexed,
                                nameIndex = binding.Provenance.NameIndex
                            }
                        } : null
                    }),
                    keyframes = environment.Keyframes.Select(frame => new
                    {
                        hour = frame.Hour,
                        blend = frame.Blend,
                        light = frame.Light.Name,
                        fogColor = Vec(frame.Light.Horizon),
                        ambientColor = Vec(frame.Light.Sky),
                        diffuseColor = Vec(frame.Light.Sun),
                        directionDegrees = Vec(frame.Light.SunDir),
                        yon = frame.Light.Yon,
                        fogStart = frame.Light.FogStart,
                        fogEnd = frame.Light.FogEnd
                    })
                };
            }

            var document = new
            {
                format = "raxicore-planetside-environment",
                version = 3,
                skyMaterials = "environment/sky/sky-layers.materials.json",
                // Verified native texture failure path binds NULL at 0x9fd3a1,
                // not a replacement panorama. Distinguish source absence from
                // an incomplete export or failed browser download.
                unshippedSkyTextures,
                skyMaterialProfile = new
                {
                    nativeProfile = NativeMaterialProfiles.GenericFourStageProfile,
                    suffixOrder = NativeMaterialProfiles.Suffixes(NativeMaterialProfiles.GenericFourStageProfile),
                    bindings = profileBindings,
                    provenance = new
                    {
                        source = "planetside.exe",
                        sha256 = NativeMaterialProfiles.ExecutableSha256,
                        selection = "0x9c0230-0x9c03e0",
                        lookup = "0x9c0410-0x9c0597",
                        policy = "Browser skies use the native generic four-stage capability profile."
                    }
                },
                skyEffects = "environment/sky/sky-effects.json",
                skyEffectMaterials = "environment/sky/sky-effects.materials.json",
                skyModels,
                gameObjects = sky.GameObjectDiagnostics,
                zones = zoneDocuments,
                weather = WeatherDocument(weather, weatherAssets, audioAssets,
                    WeatherTerrainMapExport.Export(planetside, outDir, zoneDocuments.Keys))
            };
            WriteWeatherGeometry(outDir);
            File.WriteAllText(Path.Combine(outDir, "environment.json"),
                JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = false }));
            log.Report($"environment: {zoneDocuments.Count} zones, {skyModels.Count} sky meshes, " +
                $"{replacementMaterials.Length} selected sky materials, " +
                $"{weatherAssets.Count} weather textures, {audioAssets.Count} sounds");
        }

        private static float[] Vec(System.Numerics.Vector3 value) => new[] { value.X, value.Y, value.Z };

        private static object WeatherDocument(NativeWeatherData weather, object weatherAssets, object audioAssets, object terrainMaps) => new
        {
            textures = weatherAssets,
            audio = audioAssets,
            terrainMaps,
            // Retail constructs dedicated RainStorm/SnowStorm/SandStorm objects.
            // The former env_* effect-name map was handwritten and is not the
            // native weather path. Keep resources separate from state semantics.
            native = new
            {
                version = 1,
                source = "planetside.exe",
                sha256 = NativeWeatherData.ExecutableSha256,
                lightning = new
                {
                    textures = weather.Lightning.Textures, minimumStrength = weather.Lightning.MinimumStrength,
                    checkIntervalMs = weather.Lightning.CheckIntervalMs, eventThreshold = weather.Lightning.EventThreshold,
                    closeThreshold = weather.Lightning.CloseThreshold,
                    eventModulus = weather.Lightning.EventModulus, closeModulus = weather.Lightning.CloseModulus,
                    closeCategory = weather.Lightning.CloseCategory, distantCategory = weather.Lightning.DistantCategory,
                    durationSeconds = weather.Lightning.DurationSeconds, boltLifetimeScale = weather.Lightning.BoltLifetimeScale,
                    size = weather.Lightning.Size, halfSizeScale = weather.Lightning.HalfSizeScale,
                    diagonalScale = weather.Lightning.DiagonalScale,
                    horizontalRange = weather.Lightning.HorizontalRange, verticalRange = weather.Lightning.VerticalRange,
                    distanceRange = weather.Lightning.DistanceRange,
                    randomMultiplier = weather.Lightning.RandomMultiplier, randomIncrement = weather.Lightning.RandomIncrement,
                    randomShift = weather.Lightning.RandomShift, randomMask = weather.Lightning.RandomMask,
                    directionMultiplier = weather.Lightning.DirectionMultiplier, directionIncrement = weather.Lightning.DirectionIncrement,
                    directionScale = weather.Lightning.DirectionScale, flashDepth = weather.Lightning.FlashDepth,
                    evidence = new[] { "0x86456a-0x8645eb", "0x86320d-0x86350b", "0x874580", "0x874700",
                        "0x879a70", "0x879ac0", "0x879d60", "0x87a300", "0x87a370", "0xa6c81c" }
                },
                clouds = new
                {
                    geometry = "environment/weather/clouds.json",
                    fullTexture = weather.Clouds.FullTexture, partlyTexture = weather.Clouds.PartlyTexture,
                    radiusBase = weather.Clouds.RadiusBase, radiusByteScale = weather.Clouds.RadiusByteScale,
                    falloff = weather.Clouds.Falloff, vertexPositionScale = weather.Clouds.VertexPositionScale,
                    scale = weather.Clouds.Scale, verticalOffset = weather.Clouds.VerticalOffset,
                    driftRate = weather.Clouds.DriftRate, defaultWind = weather.Clouds.DefaultWind,
                    defaultWindScale = weather.Clouds.DefaultWindScale,
                    windTransitionSeconds = weather.Clouds.WindTransitionSeconds,
                    windMotionScale = weather.Clouds.WindMotionScale,
                    observerMotionScale = weather.Clouds.ObserverMotionScale,
                    alphaOperations = weather.Clouds.AlphaOperations,
                    evidence = new[] { "0x87e6e0", "0x87eb40", "0x87e930", "0x87f5b0", "0x87f720", "0x875280" }
                },
                audio = new
                {
                    thresholds = weather.Audio.Thresholds,
                    crossfadeSeconds = weather.Audio.CrossfadeSeconds,
                    categories = new { rain = weather.Audio.RainCategory,
                        snow = weather.Audio.SnowCategory, sand = weather.Audio.SandCategory },
                    shelteredGain = weather.Audio.ShelteredGain,
                    shelterFadeSeconds = weather.Audio.ShelterFadeSeconds,
                    exteriorFadeSeconds = weather.Audio.ExteriorFadeSeconds,
                    cues = weather.Audio.Cues.Select(c => new { category = c.Category, band = c.Band,
                        file = c.File, instruction = c.Instruction }),
                    evidence = new[] { "0x8627cd-0x862c81", "0x878110", "0x877f60",
                        "0x8784a0", "0x864457-0x864561", "0x8631c9-0x863206" }
                },
                particles = new
                {
                    coordinateSpace = "native-z-up",
                    rangeSampling = "first-plus-absolute-difference",
                    activeCount = "truncate-capacity-times-strength",
                    definitions = weather.Particles.Select(p => new
                    {
                        kind = p.Kind, texture = p.Texture, capacity = p.Capacity,
                        cameraRelative = p.CameraRelative, position = p.Position, velocity = p.Velocity,
                        size = p.Size, lifetime = p.Lifetime, quadHalfSizeScale = p.QuadHalfSizeScale,
                        streakLength = p.StreakLength, sourceBlend = p.SourceBlend,
                        destinationBlend = p.DestinationBlend, tint = p.Tint,
                        evidence = new { constructor = p.Constructor, allocation = p.Allocation,
                            draw = p.Draw, renderState = p.RenderState }
                    }),
                    evidence = new[] { "0x87de30", "0x87e040", "0x87d3c0", "0x87b9c0", "0x87c5d0" }
                },
                continents = weather.Continents.Select(continent => new
                {
                    id = continent.Id, zone = continent.Zone, map = continent.Map,
                    name = continent.Name, climate = continent.Climate,
                    precipitation = continent.Precipitation,
                    zoneType = continent.ZoneType, nativeMapIndex = continent.NativeMapIndex,
                    acceptsGlobalStorms = continent.AcceptsGlobalStorms,
                    climateInstruction = $"0x{continent.ClimateInstruction:x}"
                }),
                atmosphere = new
                {
                    version = 3,
                    localLiquidQuery = new
                    {
                        startOffset = weather.LocalLiquidQuery.StartOffset,
                        endOffset = weather.LocalLiquidQuery.EndOffset,
                        mask = weather.LocalLiquidQuery.Mask,
                        cavernMask = weather.LocalLiquidQuery.CavernMask,
                        cavernPrefix = weather.LocalLiquidQuery.CavernPrefix
                    },
                    profiles = weather.Underwater.Select(p => new
                    {
                        kind = p.Kind, tint = p.Tint, farPlane = p.FarPlane, transitionDepth = p.TransitionDepth
                    }),
                    deepFogStart = 0,
                    deepFogEnd = weather.UnderwaterFogEnd,
                    evidence = new[] { "0x8605dc-0x860625", "0x863910-0x863d3c", "0x863599", "0x863700" }
                },
                localStorm = new
                {
                    coordinateSpace = "zone-normalized-top-left",
                    coordinateExtent = weather.CoordinateExtent,
                    byteScale = weather.ByteScale,
                    radiusScale = weather.RadiusScale,
                    innerRadiusFraction = weather.InnerRadiusFraction,
                    strengthThreshold = weather.StrengthThreshold,
                    strengthScale = weather.StrengthScale,
                    strengthOffset = weather.StrengthOffset,
                    logarithmBase = weather.LogarithmBase,
                    transitionSeconds = weather.TransitionSeconds,
                    transitionCurve = "linear-current-to-target",
                    refreshSeconds = weather.RefreshSeconds,
                    teleportDistanceSquared = weather.TeleportDistanceSquared,
                    clearTransmission = weather.ClearTransmission,
                    evidence = new[] { "0x8641e0-0x86435b", "0x864407-0x864412", "0x8630ac-0x8630c4",
                        "0x862f57-0x862faa", "0x875ff0", "0x875530", "0x875780", "0x8760f0", "0x876550" }
                },
                provenance = new
                {
                    continentInitializer = "0x883430-0x884fc9",
                    continentTable = "0xd326e0", continentStride = 60, continentCount = 33,
                    climateLookup = "0x885270", precipitationSelection = "0x86435b-0x864415",
                    globalStormFilter = "0x863f83 -> 0x8853a0",
                    stormConstructors = new[] { "0x8625c8", "0x86261b", "0x86266b" }
                }
            },
            serverControl = new
            {
                packet = "WeatherMessage",
                // Wire storms are projected through the original continent
                // rectangles before local evaluation. Do not label raw global
                // coordinates as local positions or invent a retail scheduler.
                localStormsRequireContinentProjection = true,
                scheduling = "server-owned; retail schedule not recovered"
            }
        };

        private static void WriteWeatherGeometry(string outDir)
        {
            string path = Path.Combine(outDir, "environment", "weather", "clouds.json");
            var geometry = new {
                format = "raxicore-planetside-weather-geometry", version = 1,
                source = "planetside.exe", sha256 = NativeWeatherData.ExecutableSha256,
                geometrySha256 = NativeWeatherClouds.GeometrySha256, coordinateSpace = "native-z-up",
                builders = new[] { "0x87e6e0", "0x87eb40" },
                meshes = NativeWeatherClouds.Geometry().Select(m => new {
                    name = m.Name, positions = m.Positions, uvs = m.Uvs, alpha = m.Alpha, indices = m.Indices
                })
            };
            string output = JsonSerializer.Serialize(geometry);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || File.ReadAllText(path) != output) File.WriteAllText(path, output);
        }

        /// <summary>Regenerate weather metadata and procedural geometry after source research; preserve
        /// the already packaged sky/model/texture outputs and their atlas keys.</summary>
        public static void RefreshWeather(string planetside, string outDir)
        {
            var weather = NativeWeatherData.Read(Path.Combine(planetside, "planetside.exe"));
            string path = Path.Combine(outDir, "environment.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var source = document.RootElement;
            if (source.GetProperty("format").GetString() != "raxicore-planetside-environment"
                || source.GetProperty("version").GetInt32() != 3)
                throw new InvalidDataException("Weather refresh requires a schema-3 environment export");
            var updated = source.EnumerateObject().ToDictionary(field => field.Name,
                field => (object)field.Value.Clone(), StringComparer.Ordinal);
            var oldWeather = source.GetProperty("weather");
            updated["weather"] = WeatherDocument(weather,
                oldWeather.GetProperty("textures").Clone(), oldWeather.GetProperty("audio").Clone(),
                WeatherTerrainMapExport.Export(planetside, outDir, source.GetProperty("zones").EnumerateObject().Select(z => z.Name)));
            string output = JsonSerializer.Serialize(updated);
            WriteWeatherGeometry(outDir);
            if (File.ReadAllText(path) != output) File.WriteAllText(path, output);
        }

    }
}
