using System.Text.Json;
using System.Text.RegularExpressions;
using System.Numerics;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Maps;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Continents
{
    /// <summary>
    /// Reads every continent's <c>mapNN.ubr</c> (the reference client's per-tile terrain mesh) via the
    /// faithful UberModel decoder and emits one JSON file per continent describing its terrain (a
    /// coarse water/lava/floor/pillar mask, a road network, broad ground classes, bridge decks, and a
    /// per-cell height grid), plus one shared file of per-facility-type top-down footprints.
    ///
    /// This data is generic: nothing here assumes a particular consumer or how the output gets used --
    /// a renderer, a map viewer, a game server, anything that wants continent geometry can read it. One
    /// example of what a consumer might do with the water/lava/floor masks is turn them into a
    /// coastline (ocean vs. inland water, land silhouette) via ocean-vs-lake classification and
    /// marching-squares contouring, entirely downstream of this tool.
    ///
    /// Why the mesh and not <c>contents_mapNN.mpo</c>: the MPO's <c>map_water</c> layer only flags
    /// tiles that carry an <c>_oc</c> water-plane record (coast, rivers and lakes alike -- ~74% of
    /// tiles), so it does NOT separate ocean from land. The real silhouette is in the per-tile terrain
    /// heights, thresholded at the <c>_oc</c> plane -- verified: the plane is a single flat Z per map
    /// (map03 = 29.5) and all 1024 tiles decode.
    ///
    /// Shared generation implementation used by the Raximod CLI and focused tests.
    /// </summary>
    public static class ContinentExportTool
    {
        /// <param name="PlanetSideDir">The read-only reference client folder. Nothing under it is written.</param>
        /// <param name="OutDir">Where the per-continent JSON and footprints.json go.</param>
        /// <param name="TerrainOutDir">
        /// Optional: writes the same per-continent height grid the main output's <c>elevation</c> field
        /// already carries, a second time, to a separate folder. The main output is aimed at rendering a
        /// map (contour lines, tinting); this is the raw grid itself, for anything that needs to look up
        /// real per-cell ground height directly -- ground-truth data instead of an unverified caller's
        /// claim about where the ground is, e.g. for placing an effect or a decal accurately. Any
        /// consumer that wants this can read from here; it has no dependency on the main output folder.
        /// </param>
        public sealed record Options(
            string PlanetSideDir,
            string OutDir,
            string? TerrainOutDir = null,
            string? NativeTerrainOutDir = null,
            string? OnlyBaseName = null,
            bool EnvironmentOnly = false,
            bool LiquidsOnly = false,
            bool OceanOnly = false,
            bool PlacementsOnly = false);

        public sealed record Result(int ContinentsExported, int FacilityFootprintTypes);

        // Resolution of the exported water masks, in cells per axis. 512 -> 16 world units per cell on a
        // full 8192 continent. The wadeable shelf is a narrow depth band (a couple of world units), so it
        // needs a finer grid than the plain coastline did to resolve at all.
        private const int MaskN = 512;

        // Lava pools are small (a few hundred world units across), so sample them finer than the
        // coastline. 512 -> 16 world units per cell.
        private const int LavaN = 512;

        // Roads are only a few world units wide, so they need a fine grid or the network breaks into
        // dashes.
        private const int RoadN = 512;

        // Biome class grid. This only drives a broad tint, so it stays coarse (64 -> 128 world units/cell).
        private const int BiomeN = 64;

        // Material selection grid from the same .srf source. 512 matches the road/water overlays and
        // resolves 16-world-unit cells on full continents without shipping the full 4096² source grid.
        private const int SurfaceN = 512;

        private static readonly Regex ZoneUbr = new(@"^(map\d{2}|ugd\d{2})\.ubr$", RegexOptions.IgnoreCase);

        // TerraSunder deliberately excludes BattleFrame Robotics. These two records are alternative
        // names for the BFR spawn shed and are authored at the same sanctuary positions, so exporting
        // either one leaves an unusable building (and exporting both produces overlapping geometry).
        private static readonly HashSet<string> ExcludedSceneRecords = new(StringComparer.OrdinalIgnoreCase)
        {
            "bfr_building",
            "vt_bfr_shed"
        };

        /// <summary>
        /// Runs the export, reporting one line per notable event to <paramref name="log"/> -- the same
        /// lines the CLI prints to stdout/stderr. Throws <see cref="ArgumentException"/> for a bad
        /// options value (missing directories) rather than a usage-error exit code so programmatic
        /// callers receive a structured failure; throws <see cref="OperationCanceledException"/> if
        /// <paramref name="ct"/> is cancelled between continents.
        /// </summary>
        public static Result Run(Options options, IProgress<string> log, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(options.PlanetSideDir))
            {
                throw new ArgumentException("a PlanetSide reference client folder is required", nameof(options));
            }
            if (string.IsNullOrWhiteSpace(options.OutDir))
            {
                throw new ArgumentException("an output folder is required", nameof(options));
            }

            string planetside = options.PlanetSideDir;
            string outDir = options.OutDir;
            string terrainOutDir = options.TerrainOutDir ?? "";

            string gameObjectsPath = Path.Combine(planetside, "startup.pak-out", "game_objects.adb");
            if (!File.Exists(gameObjectsPath))
                throw new ArgumentException("startup.pak-out/game_objects.adb is required", nameof(options));
            GameObjectDb gameObjects = GameObjectDb.Parse(File.ReadAllBytes(gameObjectsPath));
            IReadOnlyDictionary<string, WarpgateBarrierCatalog.Definition> warpgateBarrierDefinitions =
                WarpgateBarrierCatalog.Read(gameObjects);

            Directory.CreateDirectory(outDir);
            if (terrainOutDir.Length > 0)
            {
                Directory.CreateDirectory(terrainOutDir);
            }

            // The .srf surface grids and the .mpo object lists live alongside the .ubr in the reference
            // folder.
            string mapResources = Path.Combine(planetside, "maps", "map_resources.pak");
            ContinentNames? continentNames = ContinentNames.TryLoad(planetside);
            UberModel? sharedModels = null;
            string sharedModelPath = Path.Combine(planetside, "uber.ubr");
            if (File.Exists(sharedModelPath))
            {
                try { sharedModels = UberModel.Load(File.ReadAllBytes(sharedModelPath)); }
                catch (Exception exception) { log.Report($"portal children: {exception.Message}"); }
            }

            // Zone meshes live in three places: the overworld continents + sanctuaries as loose
            // mapNN.ubr, the battle islands under patchmap/mapNN/, and the Core Combat caverns as
            // expansion1/ugdNN.ubr.
            var ubrPaths = new List<string>();
            ubrPaths.AddRange(Directory.EnumerateFiles(planetside, "map*.ubr"));
            string patchmap = Path.Combine(planetside, "patchmap");
            if (Directory.Exists(patchmap))
            {
                foreach (string dir in Directory.EnumerateDirectories(patchmap))
                {
                    ubrPaths.AddRange(Directory.EnumerateFiles(dir, "map*.ubr"));
                }
            }
            string expansion1 = Path.Combine(planetside, "expansion1");
            if (Directory.Exists(expansion1))
            {
                ubrPaths.AddRange(Directory.EnumerateFiles(expansion1, "ugd*.ubr"));
            }
            if (options.PlacementsOnly)
            {
                if (options.EnvironmentOnly || options.LiquidsOnly || options.OceanOnly)
                    throw new ArgumentException("Choose one refresh mode");
                return new Result(PortalChildPlacements.Refresh(planetside, outDir, ubrPaths,
                    options.OnlyBaseName, sharedModels, log), 0);
            }
            if (options.OceanOnly)
            {
                if (options.EnvironmentOnly || options.LiquidsOnly)
                    throw new ArgumentException("Choose one refresh mode");
                return new Result(NativeOceanExport.Refresh(planetside, outDir, ubrPaths,
                    options.OnlyBaseName, log, ct), 0);
            }
            if (options.LiquidsOnly)
            {
                if (options.EnvironmentOnly) throw new ArgumentException("Choose one metadata refresh mode");
                return new Result(NativeContinentLiquids.Refresh(planetside, outDir, ubrPaths,
                    options.OnlyBaseName, log, ct), 0);
            }
            int exported = 0;

            // Export the shared native sky/time/weather catalog once. Per-zone manifests remain focused
            // on geometry, while consumers can resolve their zone key in environment.json.
            EnvironmentCatalog.Export(
                planetside,
                outDir,
                ubrPaths.Select(path => Path.GetFileNameWithoutExtension(path).ToLowerInvariant()),
                log);

            // Environment metadata is independently consumable and considerably cheaper to refresh
            // than every terrain and placement manifest. Keep this as an explicit mode so callers do
            // not need to bypass the canonical exporter or duplicate its archive-discovery rules.
            if (options.EnvironmentOnly)
            {
                return new Result(0, 0);
            }

            log.Report($"{"continent",-10} {"tiles",6} {"sea",7} {"terrain split",30}  overlays");
            log.Report(new string('-', 60));

            foreach (string ubrPath in ubrPaths.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();

                string file = Path.GetFileName(ubrPath);
                Match m = ZoneUbr.Match(file);
                if (!m.Success)
                {
                    continue;
                }
                string baseName = m.Groups[1].Value.ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(options.OnlyBaseName)
                    && !baseName.Equals(options.OnlyBaseName, StringComparison.OrdinalIgnoreCase)) continue;

                ContinentTerrain terrain;
                try
                {
                    terrain = ContinentTerrain.Build(ubrPath, MaskN, LavaN);
                }
                catch (Exception e)
                {
                    log.Report($"skip {file}: {e.Message}");
                    continue;
                }

                // Road network from the per-tile surface grids (surface maps only; the caverns ship no
                // surface pak). The pak sits beside its .ubr, which for the battle islands is
                // patchmap/mapNN/ rather than the reference folder root.
                ContinentRoads? roads = null;
                string srfPak = Path.Combine(Path.GetDirectoryName(ubrPath) ?? planetside, baseName + "_srf.pak");
                if (File.Exists(srfPak))
                {
                    try
                    {
                        roads = ContinentRoads.Build(srfPak, baseName, RoadN, terrain.WorldSize);
                    }
                    catch (Exception e)
                    {
                        log.Report($"  {baseName} roads: {e.Message}");
                    }
                }

                // Broad ground classes (grass / sand / rock / ...) for tinting, from the same surface pak.
                ContinentBiome? biome = null;
                ContinentBiome? surface = null;
                ContinentSurfaceTypes? surfaceTypes = null;
                if (File.Exists(srfPak))
                {
                    try
                    {
                        biome = ContinentBiome.Build(srfPak, baseName, BiomeN, terrain.WorldSize);
                        surface = ContinentBiome.Build(srfPak, baseName, SurfaceN, terrain.WorldSize,
                            includeStructural: true);
                        surfaceTypes = ContinentSurfaceTypes.Build(srfPak, baseName, SurfaceN, terrain.WorldSize);
                    }
                    catch (Exception e)
                    {
                        log.Report($"  {baseName} biome: {e.Message}");
                    }
                }

                // Bridge deck polylines from the object list. The overworld shares maps/map_resources.pak,
                // but each battle island ships its own mapNN_resources.pak beside its .ubr -- Extinction
                // and Desolation both have bridges that the shared pak knows nothing about.
                string ownResources = Path.Combine(Path.GetDirectoryName(ubrPath) ?? planetside, baseName + "_resources.pak");
                string bridgePak = File.Exists(ownResources) ? ownResources : mapResources;
                List<List<float[]>> bridges = File.Exists(bridgePak)
                    ? ContinentBridges.Build(bridgePak, baseName)
                    : new List<List<float[]>>();

                // A browser scene should instance reusable GLBs instead of baking thousands of repeated
                // buildings and all 1024 terrain cells into one monolith. Preserve the authoritative MPO
                // placement list and tile references in Babylon/glTF's right-handed Y-up basis:
                // PlanetSide (x,y,z) -> glTF (x,z,-y).
                MpoFile? mpo = File.Exists(bridgePak) ? LoadMpo(bridgePak, baseName, log) : null;
                CompositeObjectCatalog.Catalog? composites = File.Exists(bridgePak)
                    ? LoadComposites(bridgePak, mapResources, baseName, log)
                    : null;
                IReadOnlyList<ExactObject> groundcover = LoadGroundcover(planetside, baseName, log);
                string terrainLibrary = Path.GetRelativePath(planetside, ubrPath).Replace('\\', '/');
                object[] terrainTiles = mpo?.TerrainTileIds.Select(id =>
                    {
                        (int col, int row) = MpoFile.UnpackCell(id);
                        return (object)new
                        {
                            record = $"{baseName}{col:00}{row:00}",
                            library = terrainLibrary,
                            uri = $"terrain/{baseName}{col:00}{row:00}.glb",
                            position = new[] { col * 256f, 0f, row * -256f }
                        };
                        }).ToArray() ?? Array.Empty<object>();
                (MapObject Parent, int SourceIndex)[] placedMapObjects = mpo?.Objects
                    .Select((parent, sourceIndex) => (Parent: parent, SourceIndex: sourceIndex))
                    .Where(item => !string.IsNullOrWhiteSpace(item.Parent.Name))
                    .Where(item => IsRenderableSceneRecord(NormaliseObjectRecord(item.Parent.Name)))
                    .ToArray() ?? Array.Empty<(MapObject, int)>();
                TerrainFoundationCutouts terrainFoundations = TerrainFoundationCutouts.Build(
                    sharedModels,
                    placedMapObjects,
                    log,
                    planetside,
                    composites);
                NativeTerrainChunkExportTool.Export? nativeTerrainExport =
                    string.IsNullOrWhiteSpace(options.NativeTerrainOutDir)
                        ? null
                        : NativeTerrainChunkExportTool.Run(
                            ubrPath,
                            baseName,
                            terrain.WorldSize,
                            options.NativeTerrainOutDir,
                            terrainFoundations,
                            log);
                IReadOnlyList<NativeTerrainChunkExportTool.Chunk> nativeTerrainChunkRecords =
                    nativeTerrainExport?.Chunks ?? Array.Empty<NativeTerrainChunkExportTool.Chunk>();
                object[] nativeTerrainChunks = nativeTerrainChunkRecords.Select(chunk => new
                {
                    uri = chunk.Uri,
                    column = chunk.Column,
                    row = chunk.Row,
                    tiles = chunk.Tiles,
                    triangles = chunk.Triangles
                }).ToArray();
                string? terrainFoundationCutoutsUri = null;
                if (nativeTerrainExport != null)
                {
                    string cutoutDirectory = Path.Combine(options.NativeTerrainOutDir!, baseName);
                    Directory.CreateDirectory(cutoutDirectory);
                    string cutoutFilename = baseName + ".foundation-cutouts.json";
                    File.WriteAllText(
                        Path.Combine(cutoutDirectory, cutoutFilename),
                        JsonSerializer.Serialize(nativeTerrainExport.FoundationCutouts));
                    terrainFoundationCutoutsUri = $"terrain-native/{baseName}/{cutoutFilename}";
                }
                IEnumerable<object> mapObjects = placedMapObjects
                    .Select(item => BuildMapObjectPlacement(item.Parent));
                var portalParents = PortalChildPlacements.Parents(mpo, groundcover);
                PortalChildPlacements.Placement[] portalChildren = portalParents
                    .SelectMany(item => PortalChildPlacements.Build(
                        item.Parent,
                        item.SourceIndex,
                        sharedModels))
                    .ToArray();
                string portalVisibilityFile = PortalVisibilityManifest.Write(
                    outDir, baseName, portalParents, sharedModels);
                IEnumerable<object> groundObjects = groundcover
                    .Where(o => !string.IsNullOrWhiteSpace(o.Name))
                    .Where(o => IsRenderableSceneRecord(NormaliseObjectRecord(o.Name)))
                    .Select(o =>
                    {
                        string record = NormaliseObjectRecord(o.Name);
                        return (object)new
                        {
                            record,
                            uri = $"assets/{record}.glb",
                            position = new[] { o.Position.X, o.Position.Z, -o.Position.Y },
                            rotation = new[] { 0f, MathF.Sin(o.Yaw * 0.5f), 0f, MathF.Cos(o.Yaw * 0.5f) },
                            scale = new[] { o.Scale.X, o.Scale.Z, o.Scale.Y },
                            layer = "groundcover"
                        };
                    });
                object[] objects = mapObjects.Concat(groundObjects).ToArray();
                object[] warpgateBarriers = WarpgateBarrierCatalog.Build(
                    placedMapObjects.Select(item => item.Parent).Concat(
                        groundcover
                            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                            .Select(item => new MapObject(
                                -1,
                                NormaliseObjectRecord(item.Name),
                                item.Position,
                                item.Scale,
                                item.Yaw))),
                    warpgateBarrierDefinitions)
                    .Select(item => (object)new
                    {
                        record = item.Definition.Visual,
                        uri = $"assets/{item.Definition.Visual}.glb",
                        position = new[] { item.Position.X, item.Position.Y, item.Position.Z },
                        rotation = new[] { item.Rotation.X, item.Rotation.Y, item.Rotation.Z, item.Rotation.W },
                        scale = new[] { item.Scale.X, item.Scale.Y, item.Scale.Z },
                        layer = "warpgate-barrier",
                        sourceRecord = item.Definition.SourceRecord,
                        barrierPhysics = item.Definition.Physics,
                        barrierRadius = item.Definition.Radius,
                        sourceOffset = new[] {
                            item.Definition.SourceOffset.X,
                            item.Definition.SourceOffset.Y,
                            item.Definition.SourceOffset.Z,
                        },
                        sourceCoordinateSystem = "right-handed-z-up",
                        provenance = item.Definition.Provenance,
                    })
                    .ToArray();

                var doc = new
                {
                    @base = baseName,
                    name = continentNames?.Name(baseName) ?? baseName,
                    format = "raxicore-continent-scene",
                    version = 1,
                    coordinateSystem = "right-handed-y-up",
                    assetFormat = "glb",
                    worldSize = terrain.WorldSize,
                    sea = terrain.SeaLevel,
                    maskN = terrain.N,
                    // Row-major (j*N + i) bit-packed masks, base64. i indexes world +X (east), j world
                    // +Y (north).
                    //   mask     : bit set == below sea level (submerged at all)
                    //   deepMask : bit set == deeper than the wade threshold (not walkable -> open ocean)
                    // A cell submerged but NOT deep is the shallow, still-walkable shelf.
                    mask = Convert.ToBase64String(terrain.PackedMask),
                    deepMask = Convert.ToBase64String(terrain.DeepMask),
                    // Exact native terrain-triangle coverage, captured before the compatibility
                    // heightfield fills gaps. Deliberate facility/basement openings remain zero.
                    terrainCoverageN = terrain.N,
                    terrainCoverage = Convert.ToBase64String(terrain.CoverageMask),
                    wadeDepth = ContinentTerrain.WadeDepth,
                    // Lava-pool overlay: same bit-packing at lavaN resolution (empty on non-volcanic
                    // continents).
                    lavaN = terrain.LavaN,
                    lava = Convert.ToBase64String(terrain.LavaMask),
                    // Cavern-only layers: the walkable floor (navigable area of a vertical cave) and the
                    // pillar/crystal formations. Empty on surface maps.
                    floor = Convert.ToBase64String(terrain.FloorMask),
                    pillars = Convert.ToBase64String(terrain.PillarMask),
                    // Road network: bit-packed at roadN resolution (null where the continent ships no
                    // surface pak).
                    roadN = roads?.N,
                    roads = roads != null ? Convert.ToBase64String(roads.Mask) : null,
                    // Ground class per cell (see ContinentBiome.Class) -- a consumer picks the tint
                    // colours.
                    biomeN = biome?.N,
                    biome = biome != null ? Convert.ToBase64String(biome.Cells) : null,
                    biomeFamily = biome?.Family,
                    // Higher-resolution .srf material-control grid. Unlike the compatibility biome
                    // layer, this retains Base and Road classes for runtime splat selection.
                    surfaceN = surface?.N,
                    surface = surface != null ? Convert.ToBase64String(surface.Cells) : null,
                    surfaceFamily = surface?.Family,
                    // Exact native .srf type selection for distribution.lst. A type byte N indexes
                    // surfaceTypeNames[N-1]; unlike `surface`, this does not collapse authored names.
                    surfaceTypeN = surfaceTypes?.N,
                    surfaceTypeNames = surfaceTypes?.Names,
                    surfaceTypes = surfaceTypes != null ? Convert.ToBase64String(surfaceTypes.Cells) : null,
                    // Per-cell terrain height, normalised 0..255, base64 (same N as the water mask). For
                    // an optional elevation contour overlay, or anything else that wants coarse height.
                    elevationN = terrain.N,
                    elevation = Convert.ToBase64String(terrain.Elevation),
                    elevMin = terrain.ElevMin,
                    elevMax = terrain.ElevMax,
                    // Bridge deck runs as [[x,y],...] world-coord polylines.
                    bridges,
                    // Local-space terrain GLBs and placed reusable object GLBs. Set Babylon.js
                    // scene.useRightHandedSystem=true before applying these transforms.
                    terrainTiles,
                    nativeTerrainChunks,
                    terrainFoundationCutoutsUri,
                    objects,
                    liquids = NativeContinentLiquids.Read(planetside, ubrPath),
                    ocean = JsonSerializer.SerializeToNode(NativeOceanExport.Export(planetside, ubrPath, outDir, ct),
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                    nativeTerrainQueries = NativeCavernQueries.Export(planetside, ubrPath, outDir),
                    // Warpgate force domes are not composite children. Retail resolves their visual
                    // shell from the wrp_barrier_* contract even when meshsequence is `none`.
                    warpgateBarriers,
                    // Complete client-authored facility composition. Kept separate until the consumer
                    // reconciles server-controlled children, preventing duplicate doors and terminals.
                    portalChildren,
                    portalVisibilityUri = portalVisibilityFile,
                    composites
                };

                File.WriteAllText(
                    Path.Combine(outDir, baseName + ".json"),
                    JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));

                if (terrainOutDir.Length > 0)
                {
                    // Same grid, same encoding, same (worldSize, n) -> cell -> row-major m=j*n+i
                    // convention as `elevation` above -- this is that field again, just also written to
                    // its own folder for a consumer that only wants the raw heights. Nothing here is
                    // re-derived or re-sampled.
                    var heightDoc = new
                    {
                        @base = baseName,
                        worldSize = terrain.WorldSize,
                        n = terrain.N,
                        elevMin = terrain.ElevMin,
                        elevMax = terrain.ElevMax,
                        elevation = Convert.ToBase64String(terrain.Elevation)
                    };
                    File.WriteAllText(
                        Path.Combine(terrainOutDir, baseName + ".json"),
                        JsonSerializer.Serialize(heightDoc, new JsonSerializerOptions { WriteIndented = false }));
                }

                exported++;

                int cells = terrain.N * terrain.N;
                log.Report(
                    $"{baseName,-10} {terrain.TileCount,6} {terrain.SeaLevel,7:F1} " +
                    $"deep={100.0 * terrain.DeepCells / cells,5:F1}% " +
                    $"shallow={100.0 * (terrain.WaterCells - terrain.DeepCells) / cells,5:F1}% " +
                    $"land={100.0 * (cells - terrain.WaterCells) / cells,5:F1}%  " +
                    $"lava={terrain.LavaCells,5} floor={terrain.FloorCells,5} road={roads?.Cells ?? 0,6} br={bridges.Count,2}");
            }

            log.Report(new string('-', 52));
            log.Report($"exported {exported} continents to {outDir}");

            // Per-facility-type top-down footprints (shared across continents -> one file).
            ct.ThrowIfCancellationRequested();
            Dictionary<string, FacilityFootprints.Footprint> footprints = FacilityFootprints.Build(planetside);
            if (footprints.Count > 0)
            {
                Dictionary<string, object> fpDoc = footprints.ToDictionary(
                    kv => kv.Key,
                    kv => (object)new
                    {
                        ox = kv.Value.Ox,
                        oy = kv.Value.Oy,
                        cell = kv.Value.Cell,
                        n = kv.Value.N,
                        mask = Convert.ToBase64String(kv.Value.Mask)
                    });
                File.WriteAllText(
                    Path.Combine(outDir, "footprints.json"),
                    JsonSerializer.Serialize(fpDoc, new JsonSerializerOptions { WriteIndented = false }));
                log.Report($"footprints: {string.Join(", ", footprints.Select(k => $"{k.Key}({k.Value.Cells})"))}");
            }

            // Surface dimensions come from the just-published continent manifests,
            // including on the first export into an empty output directory.
            GroundcoverCatalog.Export(planetside, outDir, log);
            return new Result(exported, footprints.Count);
        }

        internal static MpoFile? LoadMpo(string resourcesPak, string baseName, IProgress<string> log)
        {
            try
            {
                var pak = PakArchive.Load(File.ReadAllBytes(resourcesPak));
                int index = pak.IndexOf("contents_" + baseName + ".mpo");
                return index >= 0 ? MpoFile.Parse(pak.Extract(index)) : null;
            }
            catch (Exception exception)
            {
                log.Report($"  {baseName} placements: {exception.Message}");
                return null;
            }
        }

        private static CompositeObjectCatalog.Catalog? LoadComposites(
            string resourcesPak,
            string sharedResourcesPak,
            string baseName,
            IProgress<string> log)
        {
            try
            {
                PakArchive local = PakArchive.Load(File.ReadAllBytes(resourcesPak));
                var definitions = new List<PakArchive> { local };
                if (!Path.GetFullPath(resourcesPak).Equals(
                    Path.GetFullPath(sharedResourcesPak),
                    StringComparison.OrdinalIgnoreCase) && File.Exists(sharedResourcesPak))
                {
                    definitions.Add(PakArchive.Load(File.ReadAllBytes(sharedResourcesPak)));
                }
                CompositeObjectCatalog.Catalog catalog =
                    CompositeObjectCatalog.Build(definitions, local, baseName, ExcludedSceneRecords);
                return catalog.Links.Length > 0 ? catalog : null;
            }
            catch (Exception exception)
            {
                log.Report($"  {baseName} composites: {exception.Message}");
                return null;
            }
        }

        internal static IReadOnlyList<ExactObject> LoadGroundcover(
            string planetside, string baseName, IProgress<string> log)
        {
            string entry = "groundcover_" + baseName + ".lst";
            string[] candidates =
            {
                Path.Combine(planetside, "maps", "map_resources.pak"),
                Path.Combine(planetside, "patchmap", baseName, baseName + "_resources.pak"),
                Path.Combine(planetside, "expansion1", "expansion1.pak")
            };
            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    PakArchive pak = PakArchive.Load(File.ReadAllBytes(path));
                    int index = pak.IndexOf(entry);
                    if (index >= 0) return ExactObjectList.Parse(pak.Extract(index)).Objects;
                }
                catch (Exception exception)
                {
                    log.Report($"  {baseName} groundcover: {exception.Message}");
                }
            }
            return Array.Empty<ExactObject>();
        }

        internal static string NormaliseObjectRecord(string name) =>
            name.Length > 0 && (name[0] == '@' || name[0] == '!') ? name.Substring(1) : name;

        public static bool IsRenderableSceneRecord(string record)
        {
            if (ExcludedSceneRecords.Contains(record)) return false;

            // Capitol maps place the gameplay shield as a force_dome_*_physics
            // groundcover record. It is the collision hull, uses the textureless
            // opaque force_dome_phy_tex material, and is not the retail visual.
            // The owning facility's forcedomename contract separately exports
            // and drives force_dome_* as an authoritative translucent field.
            return !record.StartsWith("force_dome_", StringComparison.OrdinalIgnoreCase)
                || !record.EndsWith("_physics", StringComparison.OrdinalIgnoreCase);
        }

        private static object BuildMapObjectPlacement(MapObject parent)
        {
            return new
            {
                record = parent.Name,
                uri = $"assets/{parent.Name}.glb",
                position = new[] { parent.Position.X, parent.Position.Z, -parent.Position.Y },
                rotation = new[] { 0f, MathF.Sin(parent.Yaw * 0.5f), 0f, MathF.Cos(parent.Yaw * 0.5f) },
                scale = new[] { parent.Scale.X, parent.Scale.Z, parent.Scale.Y }
            };
        }
    }
}
