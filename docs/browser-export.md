# Babylon.js / GLB export

For a browser-based game, use two related outputs:

- one binary glTF (`.glb`) per reusable model or terrain tile;
- one continent JSON manifest containing terrain-tile references, placed-object transforms and the
  existing elevation, water, biome, road, lava and cavern layers.

This avoids a multi-gigabyte continent GLB, lets Babylon.js cache or instance repeated assets, and
allows tiles to be streamed by distance. GLB is the runtime format; the original PlanetSide files
remain the authoritative source data.

## Export a model

From the repository root:

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  export model \
  --source /path/to/PlanetSide \
  --record trmmed \
  --out web/assets/trmmed.glb
```

The exporter searches the shared `uber.ubr`, patch and expansion libraries unless `--library` is
given. The default standalone profile emits a glTF 2.0 binary containing the most detailed
non-billboard mesh, vertex normals, UVs and colours, standard materials, embedded PNG base-colour
textures, skeleton/skin data, rigid mesh-on-bone attachments and every compatible clip from the
installed animation archives.

Useful switches:

```text
--library <file.ubr>       select a specific library (required for map tiles)
--no-animations           omit animation clips
--max-animations <n>      limit compatible clips; 0 also disables them
--bake-world-offset       bake a map record's source placement into its vertices
--profile shared          reference an explicit shared texture pool
--shared-textures <dir>   shared texture directory; required by the shared profile
--submission-only         refresh native geometry submission bindings without rewriting the GLB
--selection-receipts-only directory refresh: select models with existing mesh-selection receipts
```

Shared model export includes native `{meshId, sectionId, material}` submission
bindings in its material companion. They preserve source mesh-array order and
indexed section order; material-only refresh retains them. This is separate
from `mat_sortkey`, which applies to the native generic alpha queue rather than
its world material batches. Bulk-upgrade recorded geometry selections with:

```bash
raximod export model --source <PlanetSideDir> --out <asset-output-directory> \
  --submission-only --selection-receipts-only
```

The command reports excluded legacy companions. For a focused refresh use a
GLB output path and `--record`; a single-source-mesh record is unambiguous, but
multiple-mesh exports without a selection receipt must be fully regenerated.
Source-coverage companions resolve map-specific archive provenance. Refresh
does not alter geometry, textures, material commands or animation.

Export a texture by its literal DDS record name (without material-name resolution) with
`raximod export texture --materials <name,...> --exact --out <directory>`. This is required for runtime
variants such as faction logos and terminal gradients whose names also participate in materials.

GLBs are right-handed and Y-up. Source world offsets are local by default, which is important for
terrain streaming and instancing. For example:

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  export model --source /path/to/PlanetSide \
  --library map02.ubr \
  --record map020101 \
  --out web/terrain/map020101.glb \
  --no-animations
```

## Export collision manifests

Production collision export is owned by the command that materializes each asset family, not by a
separate publish step. For continents, run `raximod export continent` followed by
`raximod export world-assets`; that
command first materializes every direct and recursively discovered portal/composite asset, then
regenerates collision companions and their index. For the vehicle family, `raximod export vehicles`
regenerates `vehicles/models/*.collision.json` and that directory's collision index after all vehicle
models exist. Both paths exit non-zero without publishing an incomplete index. This ordering is
intentional: running collision discovery before family assets previously omitted late-discovered
facility stairs and other portal children.

`raximod export collision` remains available only for focused exporter development and diagnostics:

Keep collision separate from render geometry so Babylon can instance native blockers without
testing player movement against visual triangles. Every exported record has exactly one collision
policy: decoded explicit collision, native AAB geometry when explicit collision is absent, or no
collision. Render meshes are never marked or rebuilt as collision:

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  export collision --source /path/to/PlanetSide --models web/assets
```

The directory is scanned for GLBs. For every matching record, the tool writes
`<record>.collision.json` plus a `collision-manifest.json` index. Outputs combine fully decoded Uber
collision spheres, boxes, cylinders, oriented boxes and triangle meshes with compound colliders from
`startup.pak-out/physics*.lst`. `game_objects.adb` supplies active/destroyed physics selection,
facility barrier placement, force-dome triangle meshes and separately toggleable door blockers.
Vehicle sidecars resolve the explicit vehicle definition even when its render-record name differs;
their active group includes only object-colliding primitives while their disabled destroyed group
retains the authored destroyed model. All values are converted to the same right-handed Y-up basis
as the GLB.

Pass a final record name to inspect one asset during development. Never ship the index produced by
a single-record invocation; the production family exporter (`raximod export world-assets` for reusable
world assets or `raximod export vehicles` for vehicle models) always rebuilds the full directory index after
all referenced GLBs exist.

## Export continent manifests

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  export continent --source /path/to/PlanetSide \
  --out web/continents \
  --native-terrain-out web/terrain-native
```

Each `mapNN.json` has `format: "raxicore-continent-scene"`, version 1, and contains:

- `terrainTiles`: record/library/GLB URI plus local tile placement;
- `objects`: reusable asset URI plus position, quaternion and scale;
- coarse elevation, water/deep-water, biome and road grids;
- lava, cavern floor/pillar masks and bridge segments.

When native terrain output is enabled, the exporter subtracts the exact placed HQ foundation
triangles from each affected terrain triangle and emits a provenance report beside the terrain
chunks. Structures remain reusable shared GLBs; this produces one clipped terrain derivative per
continent, not one structure mesh per placement. Consumers should skip any runtime terrain-stencil
workaround when `terrainFoundationCutoutsUri` is present.

The separate `portalChildren` array preserves every instance embedded in a facility portal system. It
includes the complete native source matrix, browser-space position/quaternion/scale, raw flags and
region fields, parent MPO index, child index, asset and instance names. A consumer should reconcile
these client-authored transforms with server amenities before rendering: the client transform remains
authoritative while the matched server record supplies GUID, ownership and state. This prevents both
incorrect server-only yaw and duplicate doors or terminals.

Use `raximod export world-assets` to sequentially export every direct model referenced by `objects`,
`portalChildren`, expanded composites, and groundcover while decoding each shared UBR only once. It
then audits and exports native collision for the completed asset directory; successful completion is
the required publish boundary for both render and collision manifests.

The command searches the canonical shared libraries first, then audits every installed `.ubr` for
records that remain unresolved. `manifest.json` records both the installed and searched archive
counts. A record found uniquely in a previously unknown archive is exported; duplicate candidates
fail closed and require an explicit source. Empty groundcover variants and logical game-object records
without a mesh are listed under `nonVisual`, not falsely reported as broken GLBs. A genuinely absent
record remains under `missing` (for the current reference client, `rockpilea` is absent from all 40
installed UBRs).

For a focused refresh of the shared groundcover catalog without rewriting continent terrain, use:

```bash
raximod export groundcover --source <PlanetSideDir> --out <continent-output-directory>
```

Groundcover aliases are deliberately evidence-backed and emitted in diagnostics. Active missing
recipe/texture dependencies fail the focused command; malformed recipes unused by every surface are
reported separately and do not masquerade as visible-world loss.

The version-3 catalog includes recovered spatial-noise selection and all native density tiers,
and references `groundcover-surfaces/mapNN.bin`, a lossless run-length
encoding of native two-metre SRF types (including empty type zero). Continent manifests must exist
before this focused refresh; their world dimensions determine each map. This is separate from
the older majority-downsampled `surfaceTypes` field. See the pipeline guide for recovered retail
cell/density/range semantics and the still-explicit browser presentation approximations.

Before publishing model exports, run the installed-client HQ audit:

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  audit mesh-selection --source <PlanetSideDir> \
  --out <asset-output-directory>/mesh-selection-audit.json --fail-on-ambiguity
```

The audit decodes every record in every installed mesh archive sequentially, checks the conservative
native-LOD selection policy, and verifies exact retained-mesh ownership by skeleton. Export never uses
spatial similarity to discard geometry: similar walls, stairs, mirrored pieces, and facility components
remain distinct. Native billboard tiers and recognized whole-model distance variants are omitted when
detailed geometry exists. Records containing multiple independent native skeletons export one skin per
owning mesh/rig. A single mesh claimed by multiple skeletons remains an error; it is never resolved by
choosing the first skeleton.

The retail `startup.pak` also contains `lodcurve.adb` with 822 named, source-authored distance curves.
For records such as `tower_a`, its four thresholds correspond to four whole-building distance shells,
while the record's complete AAB section map references only the five detailed shell/interior meshes.
Treat complete AAB ownership and the named curve as deterministic evidence; exact filename relationships
are fallback evidence for records without a complete native spatial map, not a substitute for the source
data. The project intentionally exports only the detailed set, but must retain this evidence in audits so
a future runtime LOD mode can reproduce the retail thresholds without reverse-engineering them again.

All transforms in `terrainTiles` and `objects` use right-handed Y-up coordinates. Set Babylon.js to
the same basis before applying them:

```js
const scene = new BABYLON.Scene(engine);
scene.useRightHandedSystem = true;

const manifest = await (await fetch("/continents/map02.json")).json();

for (const tile of manifest.terrainTiles) {
  const result = await BABYLON.SceneLoader.ImportMeshAsync("", "/", tile.uri, scene);
  for (const root of result.meshes.filter(mesh => !mesh.parent)) {
    root.position.fromArray(tile.position);
  }
}
```

For production, load only nearby terrain tiles, cache imported asset containers, and instantiate
repeated object GLBs instead of importing each placement separately.

## Export terrain macro textures

PlanetSide stores the authored continent-scale terrain colour in tiled DXT1 FLAT archives. Export
and stitch those tiles into browser-ready PNG atlases with:

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  export terrain-atlas --source /path/to/PlanetSide \
  --maps map01,map02,map03 \
  --out web/terrain-macro
```

The exporter discovers each `mapXX_dxt1.fat`, validates that its DDS entries form a complete grid,
decodes their highest-resolution mip, and performs the native whole-atlas vertical flip. Standard
continents contain 32 by 32 tiles of 128 pixels and produce a 4096 by 4096 image; the smaller maps
retain their native grid dimensions.

It also writes `<map>.material.json` beside the atlas and refreshes the raw native detail image in
the sibling `terrain-textures/<map>.png` directory. The version-1 sidecar preserves the exact
`materials.adb` record, referenced `stages.adb` commands, `mat_detail` identity, `mat_tilerate`, and
atlas tile dimensions. Missing/repeated/multi-valued detail scalars fail rather than choosing a
texture from another continent. This is now the canonical TerraSunder terrain-material export;
publish the sidecar, atlas, and referenced detail image together.

Use this atlas once across the continent as the clamped base-colour texture. The separate material
detail texture should remain a repeating detail-map layer. The macro atlas supplies roads, facility
pads, shorelines, water and broad biome transitions; the repeating layer supplies close-up surface
frequency.

TerraSunder's close-detail adapter samples those two images unconditionally in one Babylon
`ShaderMaterial`, with trilinear mipmapping and supported anisotropic filtering. It does **not**
select ground textures using the coarse `.srf` gameplay class grid: that introduced visible
class-cell boundaries, sampled Solsar textures on every continent, and used implicit texture
derivatives in divergent fragment branches. `.srf` types remain available for groundcover/gameplay.

UV provenance: `map10.ubr` records such as `map100000` span 256 native units and carry local base UVs
approximately `0.5/129..128.5/129`; each successive native tile repeats that base domain. The HQ
terrain derivative instead exports continuous continent UVs `(x/worldSize, 1-y/worldSize)`.
The detail adapter uses `(u, 1-v) * (tilesWide, tilesHigh) * mat_tilerate`, keeping native detail Y
orientation and continuous derivatives across tile/chunk boundaries. Amerish's authored rate is
20, giving 640 repeats across 32 tiles, not 20 repeats across the entire continent. Base-atlas
half-texel insets are not restarted for the repeating detail layer.

Fidelity boundary: texture identity/rate and source UVs are verified native data. Applying the
detail as `macro * (2 * detail)` is the native material pipeline's neutral-grey close-detail
interpretation, **not** a recovered retail terrain-pass implementation. Retail `med_mapXX`
stage data remains preserved in the sidecar, but its medium-distance pass switching and exact
detail fade/phase are not yet reconstructed. Do not reintroduce hand-picked class brightness
divisors or call the resulting rendering retail-identical without visual/native-pass evidence.

For other consumers using Babylon's packed detail-map shader, export the continent materials with neutral normal and
roughness channels instead of passing the source colour texture directly:

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  export texture --source /path/to/PlanetSide \
  --materials map01,map02,map03 \
  --babylon-detail \
  --out web/terrain-detail-maps
```

This writes diffuse detail into red, neutral normal Y into green, neutral roughness into blue, and
neutral normal X into alpha, matching Babylon's detail-map channel contract.

### Original loading screens

PlanetSide stores each loading illustration as a grid of 256 by 256 DDS textures referenced by
`ui_loading.inc`. Reconstruct every available continent, sanctuary, training, battle-island, and
cavern screen with:

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  export texture --source /path/to/PlanetSide \
  --loading-screens \
  --out web/loading-screens
```

The exporter preserves the authored 1024 by 512 or 1024 by 768 dimensions. Retail contains no
`ui_loading_map08` artwork, so no replacement image is invented for that zone.

## Current conversion boundary

GLB carries reusable geometry, ordinary glTF material inputs, skeletons, skins and animation tracks.
Native behavior that glTF cannot express losslessly is intentionally carried by companion data:
fixed-function material stages, effect graphs and packages, portal-region visibility, collision/AAB,
environment layers, terrain materials and semantic ADB catalogs. Recursive composite expansion and
portal-child placement are represented in the continent manifests.

This split is not extraction loss. TerraSunder interprets the companion formats in focused Babylon
runtimes, while `extraction-report.json` distinguishes preserved source data from fully implemented,
partial and unsupported runtime behavior. See [the TerraSunder pipeline](terrasunder-pipeline.md) for
the production order and complete output contract.
