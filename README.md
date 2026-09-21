# Raximod

Raximod is a headless command-line toolkit for extracting and packaging assets from an installed
PlanetSide client. It reads the original archive, mesh, animation, texture, map, surface, and ASCII
database formats and produces portable GLB models or optimized game bundles. This project is the extraction side of TerraSunder, which is a recreation of the original Planetside client in Babylon.js so it's playable in a browser. That project is located and playable at https://terrasunder.net/.

The extraction pipeline is estimated to be approximately 90–95% complete and accurate. A handful
of areas may still differ slightly from the original client—for example, terminal displays and some
decorative elements. Other areas may require small additional extraction work, such as certain
nuanced aspects of vehicle collision models.

Raximod is derived from the awesome [Raxicore Editor](https://github.com/psforever/raxicore-editor),
originally created by [GeekOfWires](https://github.com/GeekOfWires) and published by the PSForever
project. Raximod transforms Raxicore Editor from a desktop viewer and editor into a headless toolkit
focused on asset extraction, inspection, validation, and packaging. Raximod remains distributed
under the MIT License.

Raximod supports both self-contained model exports—with geometry, textures, skeletons, and
animations—and structured game bundles that share textures, rigs, animation packages, and other
common data to avoid unnecessary duplication. It also substantially expands the original project's
export and validation capabilities:

- **Complete GLB export:** exports geometry, standard glTF materials, embedded or shared textures,
  skeletons, skinning, rigid bone attachments, and compatible skeletal animations. Standalone
  exports open without a Raximod-specific loader.

- **Automated asset-family export:** generates complete player, weapon, vehicle, world-object, audio,
  effect, environment, and native-data families instead of exporting one selected model at a time.

- **Game-ready continent export:** converts continent composition into structured manifests covering
  terrain chunks, placed objects, recursive composites, portal interiors, cavern terrain, water,
  lakes, oceans, roads, bridges, groundcover, skies, weather, loading screens, and terrain
  foundation cutouts.

- **Complete player-model pipelines:** exports faction, sex, and armor combinations, heads,
  cosmetics, first-person arms, shared rigs, animation packages, interaction transitions, collision
  profiles, and camera metadata.

- **Detailed vehicle extraction:** exports non-BFR (because BFRs are gross) vehicles with faction appearances, seats,
  mounted weapons, turrets, cameras, handling values, wheel and suspension data, native collision,
  destroyed models, cargo systems, water behavior, landing data, entry and exit positions, and
  presentation animations.

- **Detailed weapon extraction:** exports first-person and world models together with animation
  bindings, muzzle and attachment sockets, fire modes, projectile behavior, accuracy and recoil
  profiles, pellet spread, charge timing, automatic-fire behavior, melee behavior, grenades,
  support equipment, crosshairs, and associated audio metadata.

- **Native material reconstruction:** preserves complete material definitions, texture-stage order,
  UV transforms, animation stages, render states, faction swaps, lighting information, and
  externally shared texture references instead of reducing every material to a single selected
  texture.

- **Effects, environment, and audio pipelines:** exports native effect graphs, effect packages,
  weather systems, sky layers, clouds, lightning, underwater atmosphere, world lighting, sound
  catalogs, and combat and vehicle audio relationships.

- **Lossless native-database processing:** round-trips all installed ASCII databases byte-for-byte,
  resolves inheritance without discarding authored records, and produces typed catalogs with source
  provenance for gameplay, materials, animation, physics, awards, effects, and other systems.

- **Native collision and spatial export:** converts explicit collision, native bounding geometry,
  compound physics shapes, forcefields, doors, carrier ramps, facility interiors, and portal-region
  boundaries into dedicated runtime collision data rather than deriving collision from visible
  triangles.

- **Shared game-bundle packaging:** externalizes identical GLB images, shares textures across asset
  families, preserves common rigs and animation packages, improves PNG encoding, and avoids
  duplicating large resources while retaining distinct material definitions.

- **Deterministic validation and provenance:** records source archives and records, validates
  references and coverage, audits mesh selection and spatial data, reports unsupported native
  behavior, and publishes versioned manifests containing logical paths, sizes, and SHA-256 hashes.

- **Headless automation:** replaces the desktop workflow with a single cross-platform command-line
  interface for extraction, inspection, auditing, and packaging, including a versioned full-game
  configuration and reproducible export order.

No original PlanetSide archives or general extracted asset library are included. You need your own
installed client. The repository currently contains one small, reproducible derived
weather-geometry resource; its provenance and publication boundary are documented below.

One required development resource, the compact native weather-geometry table, is currently stored as
a reproducible derived binary. Its provenance and public-distribution review are documented in
[Recovered resources](docs/recovered-resources.md).

The remaining guides and references are indexed in [the documentation](docs/README.md).

## Export a standalone model

The default `standalone` profile produces one GLB containing the selected mesh, standard glTF
materials, embedded textures, its native skeleton, and every compatible skeletal animation:

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  export model \
  --source /path/to/PlanetSide \
  --record trmmed \
  --out exports/trmmed.glb
```

This output is intended to open in ordinary glTF-compatible software without a Raximod-specific
loader. A deterministic `trmmed.export.json` receipt records source provenance, content counts, the
GLB hash, and fidelity limits.

GLB can represent textured geometry, skeletons, and animation, but it cannot reproduce every native
material program. Standalone exports use a documented standard-material representation. Particle
effects, sounds, and gameplay behavior are outside this model profile.

## Export for a game bundle

The `shared` profile writes textures through an explicit shared pool instead of embedding duplicate
copies in every model:

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  export model \
  --profile shared \
  --source /path/to/PlanetSide \
  --record trmmed \
  --out bundle/players/models/trmmed.glb \
  --shared-textures bundle/material-textures
```

The full shared bundle pipeline also keeps common rigs and animation packages separate. TerraSunder
consumes those family manifests and bindings through the supported Raximod command-line interface.

After generating a shared bundle, publish its versioned integrity manifest and verify it before use:

```bash
dotnet run --project src/Raximod.Cli -c Release -- package manifest --root bundle
dotnet run --project src/Raximod.Cli -c Release -- package verify --root bundle
```

The root manifest inventories every output by logical path, byte length, and SHA-256. It complements
the existing family manifests rather than flattening their rig, animation, material, and collision
contracts into one generic file.

Generate the complete shared game bundle with one ordered command:

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  export game --config raximod.json
```

Copy `raximod.example.json`, adjust its three input/output paths, and keep it with the consuming game
project. Relative paths resolve from the configuration file. The versioned reader rejects unknown
fields, unsupported versions, missing paths, and unsafe worker counts. Use `--plan` to print the
complete phase order without writing anything.

See [Generated output layout](docs/output-layout.md) for the standalone, shared-bundle, staging,
report, and source-control contracts.

## Inspect a model

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  inspect model --source /path/to/PlanetSide --record dropship
```

## Build and test

Raximod requires the .NET 10 SDK. It has no desktop UI or GPU requirement.

```bash
dotnet build Raximod.slnx -c Release
dotnet test tests/Raximod.Generation.Tests -c Release
```

## Architecture

```text
original client files
  -> lossless parsers
  -> typed semantic views with provenance
  -> shared generation and validation
  -> standalone GLB or shared game bundle
```

- `src/Raximod.EngineAssets`: UI-free, lossless readers and writers.
- `src/Raximod.Generation`: shared selection, conversion, packaging, and audit logic.
- `src/Raximod.Cli`: the supported command-line interface.
- `src/Raximod.Modules`: cohesive family extraction operations called directly by the CLI.
- `tools`: narrowly scoped source-recovery utilities, not user-facing exporter commands.
- `tests/Raximod.Generation.Tests`: deterministic parser, semantic, export, and audit checks.

See [the TerraSunder extraction pipeline](docs/terrasunder-pipeline.md) for production ordering,
source provenance rules, output contracts, and native conventions.

## Supported source formats

| Format | Extension(s) | Purpose |
|---|---|---|
| PACK archive | `.pak` | LZO-compressed record container |
| FLAT archive | `.fat` / `.fdx` | Textures, terrain data, and audio stores |
| UberMesh | `.ubr` | Geometry, materials, skeletons, portals, and collision |
| UberAnim | `anims.ubr` and patches | Skeletal and rigid animation tracks |
| ASCII database | `.adb` | Ordered commands, inheritance, and gameplay metadata |
| Surface tile | `.srf` | Surface-type grids |
| Map manifest | `.mpo` and lists | Continent and object placement |
| DDS texture | `.dds` | Source texture and mip data |

## License

MIT. This project is not affiliated with or endorsed by Sony Online Entertainment, Daybreak Game
Company, or Rogue Planet Games. PlanetSide is a trademark of its respective owner.
