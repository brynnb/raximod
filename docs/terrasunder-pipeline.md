# TerraSunder extraction pipeline

Runtime material invariant: explicitly unlit multi-stage world materials must
retain per-stage saturation, not collapse successive `MODULATE2X` operations
into one brightness multiplier. `frame_maindoor`'s `frame_common+null` explicitly
binds the installed nearly-white `null.dds` followed by `frame_common`; its inner
material uses a baked lightmap instead. Both UV streams and native vertex color
must survive. This is a runtime translation correction, not permission to
replace the authored texture or change material definitions. TerraSunder's
`docs/planetside-unlit-material-stages.md` records the source and GPU checks.

This is the canonical operational guide for turning an installed PlanetSide client into the
transport-neutral assets consumed by TerraSunder. It describes the boundary between the two
repositories, the production order, the output contracts, and native conventions that should not
need to be reverse-engineered again.

## Repositories and responsibilities

The usual local layout is:

```text
/home/brynn/Downloads/PlanetSide/
├── startup.pak-out/             decoded PACK contents, including the ADB databases
├── uber.ubr, anims.ubr, ...     installed client archives
└── raximod/                     this repository

/home/brynn/Code/terrasunder/
└── public/planetside/           published browser assets
```

Use environment variables in commands so another checkout is not tied to those absolute paths:

```bash
export PLANETSIDE_DIR=/path/to/PlanetSide
export RAXIMOD_DIR=/path/to/raximod
export TERRASUNDER_DIR=/path/to/terrasunder
export PS_OUT="$TERRASUNDER_DIR/public/planetside"
```

Raximod owns source decoding, lossless/native catalogs, deterministic selection, coordinate
conversion, and transport-neutral export. TerraSunder owns Babylon loading, shaders, scene streaming,
portal traversal, animation/effect scheduling, and gameplay presentation. PSForever owns networked
gameplay state. A visual bug is not automatically an exporter bug: locate the first layer where the
source meaning changes.

## Architecture

```text
original files
  -> lossless parser (raw order, offsets, repeated values, unknown data)
  -> semantic view (typed fields, inheritance, links, provenance)
  -> family exporter (GLB + JSON + textures/audio)
  -> strict family and aggregate audits
  -> TerraSunder Babylon runtime
```

The source libraries are deliberately split:

- `src/Raximod.EngineAssets`: UI-free format readers/writers and lossless structures.
- `src/Raximod.Generation`: shared headless generation, selection, conversion, and audits.
- `src/Raximod.Cli`: supported headless command-line interface.
- `src/Raximod.Modules`: cohesive family extraction operations called directly by the CLI.
- `tools/*`: narrowly scoped source-recovery utilities, not exporter command hosts.

Never flatten away the raw representation merely because a current exporter needs one resolved
value. Lossless parsing and semantic resolution must remain independently inspectable.

## Input families

| Source | What it contributes | Completeness contract |
|---|---|---|
| PACK (`.pak`, extracted as `startup.pak-out`) | ADBs, physics/composition lists, effects, UI and other named records | archive structure round-trips; payload semantics are handled by their own readers |
| FLAT (`.fat`/`.fdx`) | textures, terrain tiles, audio and indexed stores | archive structure round-trips and record names remain stable |
| UBR mesh archives | render geometry, mesh sections, skeletons, portals/regions, embedded placements, AAB/spatial trees | every declared record/range is accounted for; unknown fields stay retained/reported |
| ANIM UBR archives | skeletal and rigid animation tracks | authored tracks and package membership survive; aliases are explicit |
| ADB | ordered commands, inheritance, definitions and cross-family metadata | raw + resolved views, all parents, offsets/name index, cycle safety, byte-identical structural encoding |
| MPO/LST/SRF | continent placement, recursive composition, surface/groundcover selection | transforms, ancestry, cycles, missing links and active dependencies are audited |
| DDS | source textures and mip chains | name resolution is explicit; missing active references fail/report |

“Byte complete” means all owned source ranges are accounted for. It does not mean every retained flag
has a known retail meaning. Keep unknown values neutral and provenance-bearing until evidence exists.

## Production order

The supported production entry point preserves the dependency graph in one versioned command. Copy
`raximod.example.json`, set its PlanetSide, PSForever, and output paths, inspect the plan, then run it:

```bash
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  export game --config /path/to/raximod.json --plan
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  export game --config /path/to/raximod.json --fail-on-warning
```

The phases below document that command's order and remain available as focused Raximod subcommands
for development. They are not separately published executables.

### 1. Verify the ADB foundation

```bash
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  audit adb --source "$PLANETSIDE_DIR" --out "$PS_OUT/native-adb/adb-audit.json"

dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  export native-catalogs --source "$PLANETSIDE_DIR" --out "$PS_OUT/native-adb"
```

The audit verifies parse/encode round trips, parent resolution, cycles, reference integrity, semantic
coverage, and the per-database support matrix. See `adb-decoder.md`.

### 2. Export continent composition

```bash
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  export continent --source "$PLANETSIDE_DIR" \
  --out "$PS_OUT/continents" \
  --terrain-out "$PS_OUT/terrain-native" \
  --native-terrain-out "$PS_OUT/terrain-native"

dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  export groundcover --source "$PLANETSIDE_DIR" --out "$PS_OUT/continents"

dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  export environment --source "$PLANETSIDE_DIR" --out "$PS_OUT/continents"
```

This discovers direct placements, every portal child, recursive composite members, terrain/surface
state, portal visibility definitions, groundcover, and environment references. Discovery must finish
before the reusable world-asset/collision publication step.

Procedural foliage is separate from authored MPO/LST tree/object placements. `raximod export groundcover`
publishes version-4 `groundcover.json` plus lossless two-metre named SRF grids in
`groundcover-surfaces/*.bin` (GCS1 header, uint32 side, uint16 count / uint8 type runs, including
zero). Run it after continent manifests exist; the full continent exporter does this internally.
Battle-island SRFs come from `patchmap/mapNN/`, matching the continent archive discovery roots.
The old 512-square `surfaceTypes` grid is majority-downsampled, not suitable for exact foliage edges.

The original installed `planetside.exe`, SHA-256
`7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`, establishes ten-metre cells
(`0xc9acbc`, `0x86b3f0`), sampled per-cell density times 85/170/256 divided by 256
(`0x86b8e3–0x86b968`, `0x86c380–0x86c3de`), separate detail-density counts (`0x86d043`), and
height as sampled width times sampled aspect ratio (`0x86b505–0x86b52e`). Ordinary range deviation
uses polar Box-Muller; `!` uses centred uniform deviation (`0xadce60–0xadcf7d`). Preserve range
tokens, atlas weights/cross flags and mesh tuples, not just their means. The high-quality flora
loop stops at 1600 minus 12 output vertices, counting six per plane; the browser translates this
capacity to indexed-quad placement costs instead of recreating legacy buffers. Never parse a
colon inside `<min:max>` as a range deviation or reweight missing/nonvisual mesh members.

Recipe selection is **not** independent per-cell random choice. The native constructor
(`0x86ee76–0x86eed8`) creates six-octave gradient noise with frequency 12/seed 0 for flora and
frequency 8/seed 43 for clutter. Selection (`0x8700e0–0x87021c`) scales native ten-metre cell
coordinates by float32 `1/800`, samples the noise (`0xadd290–0xadd6b4`), then takes
`trunc((noise + 0.5) * 256)`. Distribution intervals (`0x8704e0–0x8705bd`) cumulatively add
`floor(weight * 256 / total)`; the final interval ends at 256. Out-of-range noise has **no recipe**.
Do not normalize/clamp it or substitute a plant-position RNG: that erases authored empty patches.
The original initializer generates 1-D and 3-D gradients too; their RNG draws must be consumed even
when the browser only retains the 2-D table. Main startup sets `_RC_CHOP` (`0x4026bf–0x4026c9`),
so float32 stores and integer conversions use round-toward-zero, not nearest. TerraSunder's
focused noise tests use reference outputs from bounded emulation of these original instructions;
its optional `scripts/check-planetside-groundcover-noise.py` reproduces those scalar references.

The native `detailflora` configuration fallback is **500**, not the installed machine.ini's 1000
(`0x8538aa–0x853903`). The setting becomes `floor(value * 256 / 1000)` then integer `/64`:
zero tier has no groundcover, tiers 1/2 use low/medium, tiers 3+ use high
(`0x86c2b9–0x86c3de`). Schema 3 exports all four tiers and `defaultDetailFlora`; the browser
defaults to medium and exposes the tiers in the existing graphics settings. Native choices change
procedural flora/clutter only, not authored continent tree placements.

These are disassembly-backed semantics, not original source code. Exact per-plant spatial RNG seeds,
mixed-surface/SRF-blend policy, per-vertex ground conformation/lighting, detail-mesh buffer admission
and draw distance remain partly unrecovered. TerraSunder documents its bounded-window/fade and
surface-boundary approximations in `docs/planetside-groundcover.md`. It grounds foliage in the
actual terrain triangles, retaining foundation holes rather than using coarse elevation samples.

Native terrain chunks are a placement-specific derivative. During continent export, exact HQ
triangles from direct placements, native portal children, and recursive composite members are
transformed into continent space and subtracted from intersecting native terrain triangles.
Sloped tower/HART foundation skirts must participate; the former 0.04-m vertical-span filter
left nearly coplanar border strips uncovered. Patch records use the GLB exporter's archive lookup.
Original `pse_link` ordinals index the leading MPO parents, before non-composite links are filtered.
Preserve that ordinal and each child's native matrix; verify parent record/definition agreement and
reject missing parents rather than inferring ownership from proximity. The installed-corpus regression
checks the source ordering across maps. Boundary vertices remain on the original
terrain plane with interpolated normals and UVs. This removes true structure/terrain overlap without
duplicating reusable structure GLBs, guessing footprint radii, or relying on depth/stencil bias at
runtime. The adjacent `<map>.foundation-cutouts.json` records the method and affected placement
indices; the continent manifest's `terrainFoundationCutoutsUri` is the consumer's signal that the
terrain chunks already contain these cuts.

Method `exact-placed-hq-foundation-triangle-subtraction-v2` clips in double precision with local-origin
area calculations, keeping intermediate intersections out of float vectors. Millimetre contact strips
at continent coordinates otherwise disappear through cancellation/rounding. Terrain uses the exact
axis permutation `(x,y,z) -> (x,z,-y)`: `CreateRotationX(-float.PI/2)` leaks world northing into height.
The existing 0.8-m upper height window and 0.001-m contact tolerance remain explicit TerraSunder
policies, not retail foundation flags. They are applied to the actual overlapping planes, never the
8-bit contour map. `RecordsWithoutGeometry` must be audited before installing a generated set.
Use `TerrainTriangleClipperTests` and `NativeFoundationCutoutTests`, then rendered boundary checks
without the old stencil/depth offset. TerraSunder documents local regeneration and validation in
`docs/planetside-terrain-foundations.md`.

Capitol maps place `force_dome_*_physics` records in groundcover for gameplay ownership and barrier
physics. Those meshes use the textureless, opaque `force_dome_phy_tex` collision material and are not
presentation geometry, so continent export excludes them. The owning facility's `forcedomename`
contract instead publishes the matching `force_dome_*` translucent visual and authoritative
forcefield collision through its collision manifest.

Warpgate force domes are an explicit exception to ordinary `meshsequence` discovery. The installed
ADB gives `warpgate`, `warpgate_small`, and `warpgate_cavern` a `wrp_barrier_physics`, radius, and
local offset, while their separately-authored `dome3`, `warpgate_small_dome`, and
`warpgate_cavern_dome` meshes carry the matching dimensions and `warpgate_dome` alpha material.
`raximod export continent` therefore publishes these as `warpgateBarriers` with their ADB provenance; it must
not add them to the physical arm composite or infer a render mesh from collision geometry.

### 3. Materialize all referenced world assets and collision

```bash
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  export world-assets --source "$PLANETSIDE_DIR" --continents "$PS_OUT/continents" \
  --out "$PS_OUT/assets" --overwrite
```

This is the production boundary for reusable world GLBs. It searches all installed UBRs, resolves
direct and embedded records, writes GLB/material/selection/source companions, audits native spatial
coverage, then publishes explicit/AAB collision and indexes only after all assets exist.
`raximod export collision` is a diagnostic command; never ship an index made by its single-record mode.

Run the catalog-wide HQ audit before publishing selection changes:

```bash
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  audit mesh-selection --source "$PLANETSIDE_DIR" \
  --out "$PS_OUT/assets/mesh-selection-audit.json" --fail-on-ambiguity
```

### 4. Export runtime families

```bash
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  export weapons --source "$PLANETSIDE_DIR" --out "$PS_OUT/weapons"

dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  export players --source "$PLANETSIDE_DIR" --out "$PS_OUT/players"

dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  export vehicles --source "$PLANETSIDE_DIR" --psforever /path/to/PSForever \
  --out "$PS_OUT/vehicles"

dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  export effects --source "$PLANETSIDE_DIR" --out "$PS_OUT/effects" --assets-out "$PS_OUT/assets"

dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  export audio --source "$PLANETSIDE_DIR" --out "$PS_OUT/audio"
```

`--reuse-assets` on weapon/vehicle exporters is for an intentional metadata-only refresh against an
already validated asset set. Explicit first-person alias exports also honor this flag after
checking every requested clip; metadata-only refreshes must not silently recreate their
family textures and undo shared-pool packaging. Do not use it to conceal missing or stale GLBs.
`raximod export players --first-person-only` is useful for a focused viewmodel refresh. Export weapons first:
arm banks consume the sibling `weapons/manifest.json` for shared source rigs and
explicit animation bindings. Staged arm-only exports require that manifest too.

Weapon mode discovery uses only the owning resolved ADB weapon's `firemodeN_*`
properties, through `ClientWeaponMetadataResolver.FireModeIndices`. A reused mesh
can supply presentation defaults but cannot introduce additional modes. For
example, `vulture_bomb_bay` has only mode 0 (projectile index 2); its Liberator mesh
must not add Liberator's cluster-bomb mode 1. This is distinct from actual ADB
inheritance, which is already resolved before semantic export. Mounted mode
availability is projected by PSForever's native `NextFireModeIndex` cycle, just
as infantry mode availability is; do not infer player-selectable modes from
presentation metadata alone.

Weapon modes retain `burst.additionalShots` / `burst.intervalMs` from native
`firemodeN_autofirecount` / `autofiretime`, separately from `shotsPerRound`,
`ammunitionPerShot`, `fireDelayMs`, and `refireTimeMs`. The native count is additional
rounds after the initial shot: Jackhammer 2 gives three rounds, Rocklet 5 gives six.
The [PSForever Rocklet description](https://www.psforever.net/weapons/Rocklet_Rifle/)
corroborates magazine discharge; it is reconstruction documentation, not original code.
The source of the values remains the original `startup.pak/game_objects.adb`.
The browser uses a bounded sequence, continues it on trigger release, cancels on
equipment/mode/reload interruption, and retains recovery after the last round.
That recovery policy is a browser implementation choice, not executable-proven retail timing.

Weapon-level `turncofpenalty` / `turncofpenalty_max` are exported by
`ClientWeaponMetadataResolver.TurningAccuracy` as `turningAccuracy` with
`penaltyPerAngleUnit` / `maximumPenalty`. Read only the owning resolved weapon,
not its reused mesh. The five authored records are Bolt Driver, Heavy Scout Rifle,
Lancer, Striker and the disabled Long Rifle (`winchester`); absent pairs mean no
turn penalty, while partial/repeated/nonfinite pairs fail extraction.
In original executable SHA-256
`7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`, parser
`0x924cd0` stores this pair at WeaponDefinition `+0x30`/`+0x34`; getters
`0x904830`/`0x904840` feed accuracy update `0x8faf80`. Input update `0x748920`
stores `hypot(deltaYaw, deltaPitch)` in angle14 units (16384 per turn), and the
accuracy update adds `min(input * coefficient, maximum)` to the cone's minimum.
It then clamps `previousCone - elapsedSeconds * 1000 / COFrecovery` to that
minimum and the selected mode maximum. These fields are not angular velocity
coefficients or full-recovery durations. TerraSunder normalizes input to its
existing simulation interval for frame-rate consistency; that normalization is
an explicit browser adaptation. See TerraSunder `docs/planetside-weapon-accuracy.md`
for the complete evidence chain, numerical examples and tests.

`ClientCombatMetadata` additionally exports native mode `recoil`,
`burstRefireSlopMs`, `stamina: { required, drain }`, and weapon `optics` (ordered
`zoomlevelN`, `showscopeticks`) for both handheld and mounted weapon families.
`raximod export native-catalogs` emits `infantry-gameplay.json`, preserving resolved armor
recovery, damage, movement penalties and MAX look parameters with provenance.
Do not reinstate a second hand-maintained armor accuracy table in the client.

Original shot update `0x908820` resets only after refire plus authored burst slop,
then applies the mode's recoil when count reaches the threshold. Accuracy
`0x8faf80` subtracts armor recovery bonuses (+cc/e0/e4), and consumes integer
received damage through +210 with armor coefficient/cap (+e8/ec). Damage producers
`0x4a64a0`/`0x4a62f0` truncate the damage-message magnitude, not a percentage.
MAX input `0x748920` uses cubic interpolation between native walk/run factors,
then elapsed-time yaw/pitch caps. The absent pitch cap has an explicit recovered
default: original constructor `0x94d9b0`, write `0x94da9d`, sets armor +b8 to 5000
angle units/s; parser `0x94e791` identifies `heavy_armor_pitch_rate_cap` and getter
`0x912de0` reads it. Keep executable-default provenance distinct from ADB values.
Weapon zoom action `0x5652f0` cycles the authored vector (parser `0x9264ba`, getters
`0x926a30`/`0x926ad0`). The browser perspective conversion and continuous MAX
angles are documented adaptations, not recovered retail projection/quantization.
Native stamina update `0x906fc0` exempts already scheduled burst rounds from the
initial requirement. The browser server charges the exported drain once per
accepted burst through the existing stamina-owning AvatarActor.

The optics catalog also retains the explicit `is_oicw` rangefinder flag. Do not
infer it from a weapon name, one-shot magazine, or multistage ammunition. The
2009 Scorpion instructions describe a 50m-inclusive, 300m-exclusive distance lock
while zoomed, separate from the ADB projectile's 45m arming distance. The browser's
scope aperture/tick geometry is documented reconstruction: `ui_hud_sniper.inc`
references `sniper`, but that named texture is absent from this installed corpus.
Do not invent an exported original bitmap for it.

Launcher cartridges can author terrain `bounce` without `grenade_projectile`.
Thumper's `_b` variants have lifespan 2 and bounce_count 100. Environment walls and
floors are terrain contacts for this action; a material surface tag is not a
mutable gameplay object. Preserve avatar/vehicle/object overrides independently.

Run `ClientCombatMetadataTests` and TerraSunder's accuracy/infantry/optics/MAX tests
after changing these contracts. Regenerate native catalogs, weapon/vehicle
manifests with `--reuse-assets`, the PSForever native timing contract if applicable,
and the aggregate report last. See TerraSunder
`docs/planetside-native-combat-mechanics.md` for formulas and fidelity boundaries.

Do not infer held-fire support from `clientfiremodeN_looping_fire_sound`.
The original 3.15.84.0 executable's generic Weapon update `0x906fc0` and shot
scheduler `0x9075a0` use firing state, refire time, delay, burst and charge fields,
independently of audio. Parser `0x924cd0` assigns `refiretime` to mode offset
`0x50`, `fire_delay` to `0xa4`, and burst count/interval to `0x8c`/`0x90`.
Reaver rockets, for example, repeat a discrete sound at the authored 250 ms
cadence. A looping sound also does not authorize repeatedly starting a retained
hacking action. Keep release/charge and timed-use semantics distinct.

HUD equipment icon resolver `0x6d7020` tries the original UI style
`/ItemIcons1GridHigh.<definition>_fm<mode + 1>` before the base definition
(after ammunition-specific styles). `ui.pak/ui_root.ui` contains six `_fmN`
styles across Reaver, Wasp, Lightning, Vanguard and Skyguard. TerraSunder's
existing equipment icon export script uses Raximod textures and discovers
these styles, emitting both PNGs and the runtime mode mapping. Do not substitute
an ammo-box image or a hand-authored rocket-pod exception for that native art.

Vehicle weapon-system properties override their component mesh defaults, just as
the merged muzzle/presentation metadata already does. In particular, Wasp owns
`wasp_gun_ammo` and 50 ms refire; Lightning owns 750 ms; Vulture's primary bomb bay
owns 900 ms. Taking the first mesh's defaults instead changes gameplay and audio.
After metadata export, regenerate PSForever's `BrowserNativeWeaponTimings.scala`
with `tools/generate_browser_native_weapon_timings.py`, passing the ADB listing
and both `--weapon-manifest` and `--vehicle-manifest`. The TerraSunder
`PlanetSideAlternateFire.test.js` audit compares the full exported mode corpus
against the lossless resolved ADB catalog; keep it in focused validation.

`raximod export weapons` also exports a boolean `deleteAfterUse` from resolved native
`delete_after_use`, including false for ordinary weapons. Do not infer this from
magazine size: internal `phoenix` is the disposable Decimator; `hunterseeker` is
the reloadable guided Phoenix. Browser timing generation now requires the weapon
manifest and validates that every entry has this field before emitting its
consumable set. Preserve construction tools' existing placement lifecycle.

First-person action semantics (executable 3.15.84.0, SHA-256
`7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`): grenade dispatcher
`0x45f8f9` maps prime/hold/release to `fire1_start`/`fire1_idle`/`fire1_end`,
independent of fuse mode. Retain all three weapon clips and their matching arms
tracks. Knife branch `0x460200` alternates ordinary `fire1`/`fire2` and uses
`fire3` when powered; `flipswitch` is the mode-change clip for all three factions.
Chainblade additionally has an explicit native first-person exception: setup
`0x460f19` resolves `vehiclegentread_3`, and callback `0x45f1d0` overrides its UV
scroll speed to U=0/10, V=0 through `SetSpeedRollUV` (`0x96c880` -> `0x99f820`).
Keep this per instance, not in the shared material's `sc_texgen` program.
Force-Blade's `cell_ef_vsfb_goo` instead uses its authored 16-cell/30 FPS animation;
Mag-Cutter has no continuous texture animation. Do not synthesize bone motion.
TerraSunder documents and tests these contracts in
`docs/planetside-native-combat-mechanics.md` and `PlanetSideWeaponLifecycle.test.js`.

Also regenerate PSForever's `BrowserNativeProjectileProfiles.scala` with
`tools/generate_browser_native_projectile_profiles.py`, passing both manifests
and `--bindings-output server/src/test/resources/browser-native-weapon-bindings.json`.
`--check` detects stale profiles/fixtures without rewriting them. The focused
`BrowserNativeWeaponContractTest` exercises actual server mode/ammo selection
against this fixture, including ammo identity, projectile identity, range and timing.
Do not copy server balance changes into native metadata: PSForever deliberately
adds one metre to katana mode 0 and each faction knife's mode 1 (upstream commit
`58238df1`); its inventory `projectileRange` field supplies the browser's current
range. Both range calculations limit acceleration duration to projectile lifetime.

`raximod export players --animations-only` refreshes the five shared animation banks and their manifest
entries, retaining the existing body/head/cosmetic/viewmodel outputs. It requires an existing
player manifest. Keep animation exports sequential and memory-bounded.

`raximod export vehicles` owns the vehicle-model collision publication too. After model materialization it
regenerates `vehicles/models/*.collision.json` and `collision-manifest.json`; do not run the
diagnostic `raximod export collision` command as a second vehicle publish step.

### 5. Export terrain and loading textures when their sources change

Use `raximod export terrain-atlas` for continent macro maps **and** their versioned native-material sidecars
and raw repeating detail images (`terrain-macro/*.material.json` → `terrain-textures/*.png`).
The browser requires these resources before terrain loading completes; publish them together.
The terrain runtime saturates combined ambient/diffuse illumination to `[0,1]` before
texture modulation, matching the documented legacy Direct3D lighting boundary
([Microsoft](https://learn.microsoft.com/en-us/windows/win32/direct3d9/diffuse-lighting)).
Previously its unconstrained sum amplified sun-facing terrain (up to roughly 1.39 in
TR sanctuary's daytime red channel) before framebuffer clipping. Do not replace this
boundary with global exposure reduction, which also darkens shaded terrain and other
materials. This is a documented LDR adapter correction, not evidence that the complete
retail terrain shader or medium-distance pass has been recovered. The existing neutral-grey
`macro * (2 * detail)` close-detail approximation and colour encoding are unchanged;
`PlanetSideTerrainMaterial.test.js` covers illumination saturation and shade preservation.
See [terrain macro export](browser-export.md#export-terrain-macro-textures) for the native UV/rate
contract and the remaining medium-distance pass fidelity boundary. Do not use the coarse `.srf`
class grid as a visual texture selector or replace missing per-continent detail with `map01`.
`raximod export texture --babylon-detail` is an optional packed-detail output for other material consumers;
the terrain adapter uses raw colour detail, not those packed channels.
Use `raximod export texture --loading-screens` for the native loading-screen mosaics. Exact DDS
names should use `raximod export texture --exact`; material-name lookup is a different operation.

### 6. Externalize and validate shared GLB images

#### Shared lossless PNG encoding

`PngEncoder` is the common producer for native material, GLB, terrain, environment,
groundcover, loading-screen and texture-tool output. It converts BGRA to RGBA without
altering alpha or RGB beneath transparent pixels, evaluates PNG's five reversible row
filters using the specified signed-residual score, and uses zlib `SmallestSize` compression.
It also compares unfiltered strong compression and the former unfiltered `Fastest` output;
the smallest payload wins deterministically. Compression effort is spent during generation,
not in the game or on an HTTP request. This is lossless encoding, not texture quantization,
resizing, palette reduction or a native-material interpretation change.

To upgrade existing exports without decoding all native archives again, use the staged
Raximod migration. It updates both standalone PNGs and embedded GLB PNG copies. Updating
only the shared companions violates their byte-identity contract and lets reuse exports
restore the old encoding. `GlbImagePackaging` is the same buffer-relocation primitive used
by shared-image externalization; every non-image buffer view remains byte-identical.

```bash
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  package reencode-png --root "$PS_OUT" --stage /var/tmp/terrasunder-png-stage --workers 4

# From TerraSunder, while the originals are still at their recorded paths:
node scripts/audit-planetside-png-recompression.mjs /var/tmp/terrasunder-png-stage

# Retain a backup outside the served tree, then apply the validated stage:
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  package reencode-png --apply --stage /var/tmp/terrasunder-png-stage

# Refresh dependent model byte hashes through their canonical compilers:
npm run build:planetside-player-canonical-rigs
systemd-run --user --scope --quiet --property=MemoryMax=16G --property=MemorySwapMax=0 \
  node --max-old-space-size=4096 scripts/build-planetside-player-vat.mjs
npm run audit:planetside-shared-glb-textures
npm run audit:planetside-player-canonical-rigs
```

Use a new disk-backed stage directory outside the input tree. Worker count is bounded
to six; four is the default. The receipt records all original/output byte hashes. Apply
preflights the entire receipt and refuses changed sources or staged outputs before replacing
files atomically one at a time. A failed stage never changes served assets. Generation failures
are errors, not reasons to substitute a texture. Run the aggregate report after dependencies.

Migration supports the native non-interlaced RGBA8 format. It preserves IHDR and **every
non-IDAT chunk byte-for-byte**, including `gAMA`, `cHRM`, profiles and text; there is no
metadata-stripping shortcut. Palette, grayscale, 16-bit and animated PNGs remain unchanged.
Malformed CRCs, invalid sizes/filter bytes and unknown critical chunks fail. Decoded images
are bounded to 256 MiB. Already smaller encodings stay smaller; equal-size rewrites normalize
the current zlib header so future native exports still match their shared companions.

The independent client audit uses libpng/libvips to compare decoded RGBA and retained PNG
metadata, plus GLB mesh/rig/animation payloads and metadata. See TerraSunder's
`docs/planetside-png-encoding.md` for the measured corpus and browser decode results.

#### Shared-image externalization

`raximod export world-assets`, `raximod export players` (including partial modes), `raximod export weapons`
and `raximod export vehicles` finalize browser GLBs with `SharedGlbImageExport` before
publishing their family manifest. Geometry, skin and animation views are copied
byte-for-byte, with storage offsets and references relocated. Shared PNGs must
already match embedded images exactly; missing/conflicting companions fail preflight.
Standalone diagnostic/editor exports retain embedded images and complete rigs.

Weapon and vehicle models use sibling `material-textures/<name>` references.
Nested player models, heads, cosmetics and arms share `players/material-textures`;
their GLBs contain real relative URIs such as `../../../material-textures/<name>`.
Derived player rigs rebase these URIs against their output directory and continue
referencing the same native pool. Never copy another texture pool into derivatives.

To repackage existing exports without decoding the archives again, keep original
GLBs outside the served tree, then run:

```bash
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- package externalize-images --models "$PS_OUT/assets"
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- package externalize-images \
  --models "$PS_OUT/players" --recursive --textures "$PS_OUT/players/material-textures"
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- package externalize-images --models "$PS_OUT/weapons" --recursive
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- package externalize-images --models "$PS_OUT/vehicles" --recursive
```

Each invocation preflights the complete family and atomically replaces individual
models. `--check` verifies references and rejects remaining embedded images without
writing; repeating conversion is byte-idempotent. Reuse exporters validate existing
external references too. A missing PNG cannot pass because a GLB happens to exist.

From TerraSunder, rebuild canonical player rigs and shared VAT manifests in the order
shown above, then run `npm run audit:planetside-shared-glb-textures`. This read-only
audit covers all native GLBs **and** derived player rigs, rejects every embedded image,
and validates the expected URI and file. The old client companion-extraction command
and image-name redirector have been retired; Raximod owns companion generation.

`audit-planetside-world-glb-equivalence.mjs <originals> <outputs>` now recurses and
independently verifies all retained binary views, texture bytes, and mesh/rig/animation
metadata for any family. The output must retain all original glTF meaning, not merely
look similar when loaded.

Babylon 9.18 concatenates its root and URI and rejects `..` in raw glTF URIs. The shared
browser asset loader resolves the actual external image/buffer URIs against the model
URL, then supplies paths relative to that origin to Babylon. This is per-load standard
URL resolution, not a guess from an image name or an embedded-image replacement. It
keeps one normalized cache URL across source/derived models, permits only the model's
origin, and leaves embedded images unchanged so stale packaging cannot be hidden.
Original material sidecars, faction swaps and native render state remain independent.
Native material export also retains non-empire effect-package `stateBindings`
and the complete material definitions/textures they reference. In particular,
`epackage.adb`'s `ob_redlight`/`ob_greenlight` scopes switch both the HART lens
and frame to `hart_red1/2` or `hart_green1/2`; `materials.adb` supplies their
UV2 `_ob_light_red/green` lightmaps, UV0 lens/frame textures and unlit state.
Do not approximate these with emissive colors or export only faction targets.
Apply each scope from the original complete material set so switching off also
restores the frame. `NativeMaterialFactionBindingsTests` covers both lamps and
their references. TerraSunder binds the state to the server HART boarding phase;
see its `docs/planetside-hart.md` for lifecycle and local verification.
See TerraSunder `docs/planetside-shared-world-images.md` for corpus and rendered checks.

#### Cross-family byte sharing

After all family exports and PNG encoding, run `raximod package share-textures` over the complete
native tree. This is a required final packaging step before rebuilding derivatives
and publishing the extraction report. Family exporters may recreate their original
companions during partial exports; rerun this step instead of maintaining runtime
filename redirects.

```bash
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  package share-textures --root "$PS_OUT" --stage /var/tmp/terrasunder-texture-pool-stage --workers 4
# Run from TerraSunder before applying, while the original inputs still exist:
node scripts/audit-planetside-texture-pool.mjs /var/tmp/terrasunder-texture-pool-stage
# Retain a backup outside the served tree, then publish the verified stage:
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  package share-textures --apply --stage /var/tmp/terrasunder-texture-pool-stage
npm run build:planetside-player-canonical-rigs
systemd-run --user --scope --quiet --property=MemoryMax=16G --property=MemorySwapMax=0 \
  node --max-old-space-size=4096 scripts/build-planetside-player-vat.mjs
npm run audit:planetside-shared-glb-textures
npm run build:planetside-native-animation-atlases
npm run audit:planetside-native-animation-atlases
```

The pool groups explicitly referenced PNG/DDS files by SHA-256, confirms complete
byte equality, and writes `textures/<sha256>.<extension>`. It changes actual URI fields
in GLBs, native material stages, faction variants, animation frames and other catalogs.
It never merges material definitions or changes source names, stage order, render state,
UVs, sampler settings, rigs, animations or geometry. Even two differently named images
can share bytes while their material records stay independent. The full original GLB
BIN chunk is retained. Compact material JSON stays compact.

`textures/shared-textures.json` records each original family path and its image hash;
it is packaging provenance, not a runtime redirect table. Already-pooled reused GLBs
validate their addressed image rather than recreating a companion from `image.name`.
A partial export with changed image bytes cannot alias the old version. Groundcover
catalog version 4 replaces `textureBase` plus filename construction with an explicit
`textureUris` map keyed by native texture name; placement and surface-map data are unchanged.

Staging refuses missing references and keeps the source tree untouched. Applying verifies
all input/output hashes and the complete input inventory, publishes image bytes before
consumers, and removes explicitly referenced duplicate companions last. Each replacement
is atomic, but the directory is not a transaction: retain a backup and publish production
assets using the release workflow. Unreferenced diagnostics and files addressed only by
legacy runtime names are preserved; their apparent duplication is not proof they are unused.
A second stage without intervening exports must change zero files. The independent
client audit compares image bytes, complete BIN chunks and all JSON meaning after URI
resolution. Publish the aggregate report only after this step and derivative rebuilds.

Native animation atlases are TerraSunder derivatives of the **final** material sidecars
and PNG pages. Rebuild after URI pooling and PNG encoding, never before: retaining
pre-pool animation keys left 53 world-material stages unable to resolve their atlases.
The compiler validates frame/page counts and layout, copies exact RGBA with wrapped
gutters, publishes images before the manifest, and names images by their output hash.
TerraSunder release preparation and production builds run the deterministic atlas audit.

For native animated stages, page selection and `sc_transform`/`sc_texgen`/`sc_rotate`
must have distinct ownership of UV state. Whole-page animations select an image while
the native transform clock alone writes its UV properties; in-page cell animations
with identity native transforms own their cell UVs. The exported corpus currently has
no cell animation with a nonidentity native transform; future such records require
explicit shader composition and are rejected instead of silently overwriting UVs.
Texture wrappers must distinguish native transform and sampler state even when their
pixel files are shared. `warpgate_small_dome` (patch3/patch3.ubr, materials.adb) has
three active stages: base modulation, scrolling noise, then a second independently
scrolling noise stage with `modulate4x`. It must not be truncated to two stages.
See TerraSunder `docs/planetside-animated-materials.md` and its corpus/runtime tests.

### 7. Publish the aggregate report last

```bash
dotnet run --project "$RAXIMOD_DIR/src/Raximod.Cli" -c Release -- \
  audit extraction --continents "$PS_OUT/continents" \
  --assets "$PS_OUT/assets" \
  --effects "$PS_OUT/effects" \
  --out "$PS_OUT/extraction-report.json" \
  --fail-on-warning
```

During investigation, omit `--fail-on-warning` only when the remaining warnings are being examined.
A release should not silently replace the previous known-good tree after a strict failure.

## Focused inspection and development tools

Use the smallest tool that answers the question before starting a full export:

| Tool | Purpose |
|---|---|
| `raximod inspect database --file <database.adb> [--filter <text>]` | inspect raw ordered ADB commands without flattening |
| `raximod inspect record --source <PlanetSideDir> --record <record>` | inspect a decoded model record and its nested structures |
| `raximod inspect mesh-records --source ... [--filter ...]` | locate mesh-record ownership across installed libraries |
| `raximod inspect animations --source ... [--filter ...]` | inspect the installed animation catalog |
| `raximod export model --source ... --record ... --out ...` | export one model while developing geometry/skin/animation conversion |
| `raximod audit spatial --source ... --models ... [--record ...]` | inspect source coverage and native AAB/spatial ownership |
| `raximod export collision --source ... --models ... [--record ...]` | diagnose collision for one/all already exported records; not a production publish step |
| `raximod audit mesh-selection --source ... --out ... --fail-on-ambiguity` | validate HQ selection across the installed catalog |
| `raximod audit extraction ... --fail-on-warning` | aggregate output/reference audit after family generation |

For a narrow test, filter the one test project rather than starting unrelated export processes:

```bash
dotnet test "$RAXIMOD_DIR/tests/Raximod.Generation.Tests/Raximod.Generation.Tests.csproj" \
  -c Release --filter 'FullyQualifiedName~RelevantTestClass'
```

External glTF viewers are useful for visual inspection, while production generators remain callable
and testable headlessly.

### Vehicle water metadata

`VehicleWaterBindings` exports `handling.water` from the resolved gameplay
definition, not its model alias. All 24 exported ground vehicles retain their
native depth/speed endpoints, flotation and hover flags, hover transition time,
underwater lifetime/recovery and per-field defining record/command/offset.
Deliverer has `water_maxspeedpercentage=0.70`; its three variants override to
0.64 despite sharing model data. Preserve `water_underwaterlifespan=-1` and
absent fields as null. The five hover definitions do not author the floating
transport switch. Malformed/repeated scalars and missing provenance fail export.
TerraSunder's `docs/planetside-vehicle-water.md` tracks native update research;
exporting these fields does not establish the complete flotation/drag formula.

## Output contracts

| Output | Contract |
|---|---|
| `continents/*.json` | placement, terrain, water/biome/roads, portal children, composites and source transforms |
| `continents/portal-visibility/` | native region bounds, directional portals, ownership and diagnostics |
| `assets/*.glb` | reusable right-handed Y-up geometry with full retained rigs/animations where authored |
| `assets/*.materials.json` | native fixed-function stages and render state that glTF cannot express losslessly |
| `assets/*.mesh-selection.json` | why every candidate mesh was retained or rejected |
| `assets/*.source-coverage.json` | source byte/range accounting and semantic retention status |
| `assets/*.collision.json` | explicit native or native-AAB collision, never render triangles |
| `players/`, `weapons/`, `vehicles/` | family models, attachments, animation roles and semantic manifests |
| `effects/` | complete graph/layer/action inventory, links, provenance and runtime-support classification |
| `native-adb/` | transport-neutral semantic catalogs derived from the lossless ADB model |
| `extraction-report.json` | aggregate counts, unresolved references and filesystem integrity |

## Native conventions already established

### Properties and inheritance

- ADB command order and repeated commands are meaningful. Preserve both.
- Use `GameObjectPropertyReader.Scalar`, `Tuple`, or `List` deliberately. Scalar reads fail if a
  supposedly scalar property has multiple authored values; never restore “take the first value.”
- Resolved inheritance is cycle-safe and separate from the raw record. Every authored parent link
  remains represented, including missing/cyclic diagnostics, and all 298 `game_objects.adb` property
  names remain available even when some are intentionally transport-neutral.
- Name-index entries and stream offsets are provenance, not disposable parser internals.

### Coordinates

PlanetSide is right-handed Z-up; browser exports are right-handed Y-up. The base vector conversion is:

```text
(x, y, z) -> (x, z, -y)
```

Convert positions, rotations, animation tracks, collision, portals and effects at the same boundary,
exactly once. Do not infer corrective 90-degree rotations per asset when a hierarchy contains a
synthetic exporter/loader basis node.

### Mesh selection and LOD

- Detailed selection uses native LOD fields, complete AAB section ownership, `lodcurve.adb`, and a
  narrowly audited whole-model naming relation where native ownership is unavailable.
- Native LOD 1000+ geometry is a billboard/distance substitute and is removed only when detailed
  geometry exists. Continuous-LOD `clod_` player shells are duplicate low-detail bodies, not
  animation data.
- Bounds, 35-percent spatial similarity, proximity and vertex-count similarity are forbidden as
  deletion evidence. Similar architectural pieces are often independent assets.
- Multiple independent rigs are valid. A mesh claimed by multiple skeletons is ambiguous and must
  fail rather than selecting the first.

### Animation and first-person assets

- Preserve native skeletons and clips even for assets that are usually static. TerraSunder may make
  a dormant static instance and promote it using the same parsed container when animation is needed.
- `apackage.adb` is authoritative animation package membership; do not rediscover it only by prefix.
- Package playback modes `refposeN` and `blended_refpose` name virtual reference-pose families whose
  concrete archive records are `<animation>_refNN`. Resolve those records explicitly. When an
  unclassified playback value has no exact archive record but that concrete family exists, prefer
  the source records and preserve the resolution in the player manifest instead of special-casing
  the value. Player manifest schema 5 exposes this as each animation set's `clipBindings`.
- Retail head cosmetics are separate Patch 5 records, not body meshes or animation-bank variants.
  `PlayerCosmeticExport` discovers the complete `beret`, `hat`, `shades`, and `earpiece` families
  (both genders, all three factions) plus `female_headhat_a..e` in `patch5/patch5.ubr` and fails
  missing/ambiguous variants. `raximod export players --source <PlanetSideDir> --out <players-output> --cosmetics-only`
  regenerates `cosmetics.json`, its 29 GLBs/sidecars and shared textures without rebaking bodies or
  animations; full `raximod export players` includes the same step. Preserve original record capitalization
  and library provenance even though output IDs are lowercase. Every accessory retains its native
  two-joint head rig; skeletal and VAT presentation cancel the exported head-anchor frame and use
  the avatar's animated head socket. Do not add per-accessory offsets or another basis conversion.
  Installed `ui.pak-out/ui_hud_editflair.inc` defines helmet/beret/hat/bare-head radio choices and
  independent shades/earpiece controls; `english.str` specifies BR24, no accessories on MAX or
  infiltration armor, and helmet hiding for agile/reinforced armor. Selecting the authored female
  head-hat geometry for female hat/beret wearers is the runtime policy inferred from that explicit
  source family, not a claim that the retail executable's selection logic was recovered.
  PSForever's object-create cosmetic mask and attribute 106 encoding differ, including an inverted
  helmet bit. Browser appearance consumes only the object-create encoding.
- Ordinary infantry body GLBs remain full-fidelity source exports with their native skin joint order
  and inverse bind matrices. TerraSunder's `build:planetside-player-canonical-rigs` command produces
  a separate deterministic GPU-animation derivative: `nc-male-light` and `nc-female-light` define
  the canonical logical joint slots, while every body's `JOINTS_n`, skin joint array, and inverse-bind
  accessor are permuted together. Body-specific inverse binds are intentionally retained; replacing
  them with the canonical body's matrices deforms armor. A source-only joint name may occupy a
  missing canonical placeholder slot only when neither slot has any positive vertex weight. The
  original player GLBs must never be overwritten by this optimization derivative.
- Shared-bank skeletal playback has the same auxiliary-joint boundary. The original
  `ncmlite` bank targets `dummy04`, absent from `vs-male-standard`; `ncflite` also
  targets nine dummy joints absent from `vs-female-standard`. The full 24-body
  exported-skin audit confirms these have no positive vertex weights. Runtime may
  omit an absent channel only after proving its source joint and descendants are
  unweighted; missing deforming joints or their ancestors remain errors. Keep the
  source clips intact and retain omitted target names in retargeting diagnostics.
  Babylon's animation-group clone does not discard a null converted target: such
  channels must be removed before playback. TerraSunder's focused retargeting test
  audits both bank JSON chunks without expanding their full animation tracks and
  starts representative bindings against the real VS Standard bodies.
- TerraSunder's `build:planetside-player-keyframes` consumes all five original player animation
  GLBs into lossless, deduplicated sparse-keyframe banks. All 3,769 exported clips are resident
  before gameplay; body/weapon/action changes never request an animation bank. The original GLBs
  remain intact. The derivative reports authored bindings absent from their source GLB (currently
  `ncmlite_jumpidle_forward_rifle_ref00`) rather than inventing a replacement.
- Runtime evaluates native local TRS, blends the `bone_templates.adb` masks there, then composes
  global poses into a small shared GPU texture. `full_torso` retains pelvis 0, spine 0.01, spine1 1
  and head 0.6. Never blend global crouch/reload positions: that stretches the torso. Ordinary
  infantry shares the normalized gender rigs; MAX uses its three native rigs. Each body retains
  its own inverse binds, combined as `bodyInverseBind * sharedGlobalPose` in Babylon's Matrix API.
  The old full pose recordings and separate skeletal body playback path are retired.
- Every socket uses the same evaluated pose with the body's exact GLB ancestor transform. Keep
  its affine matrix intact: decomposing/recomposing nearly unit native quaternion transforms can
  introduce drift. No per-body guessed offsets or second basis conversion are permitted.
- Pose texture-size uniforms belong to TerraSunder's material plugin in both its UBO layout and
  ordinary vertex declarations. Babylon 9.18.0 disables UBOs on Mac Chrome even under WebGL2.
  Cloak/outfit variants must share both the live pose palette and the body's inverse binds.
  See TerraSunder's `docs/planetside-player-keyframes.md` and focused keyframe/source tests.
- `ap_audio_callback` entries do not provide equipment attachment events for infantry equip/holster
  clips. TerraSunder therefore derives each clip's attachment-transfer time from the authored pose:
  it is the 60 Hz sampled time where `hp_hand_right` is closest to the relevant `hp_hip_*` or
  `hp_shoulder_*` hardpoint. The derivative records all four hardpoint results, and runtime selects
  the one corresponding to the authoritative inventory slot. Held and holstered models must exchange
  visibility at that time, never at action start or from a hand-authored percentage.
- The three native MAX body rigs have no `hp_hip_*` or `hp_shoulder_*` joints:
  NC/VS retain `hp_hand`, while TR retains `hp_hand_left` and `hp_hand_right`.
  All 24 ordinary infantry bodies retain the four hip/shoulder hardpoints. Do not
  manufacture a body holster for a MAX's inventory slot 0 or treat that source
  absence as failed extraction; the MAX weapon uses the existing hand attachment
  path. TerraSunder's `PlanetSidePlayerAppearance.test.js` checks the actual three
  exported MAX bodies and weapons together, including world-entry holster loading.
- Retail `Engine3d::AttachSequence` uses the selected child hardpoint's model-space **position** as
  the source offset; it does not invert the child hardpoint's rotation (`planetside.exe`
  `0x99e50e-0x99e573`). If `arule.adb` resolves a rule, its fixed-turn quaternion is multiplied before
  the target hardpoint's authored base quaternion (`0x99a56a-0x99a592`). Runtime attachment must
  therefore cancel the exported source basis once, subtract the source hardpoint position, apply the
  rule as a local delta, and retain the target's existing bind rotation. Because `arule.adb` is native
  Z-up data, the rule quaternion's vector components require the standard `(x, y, z) -> (x, z, -y)`
  basis conversion before the delta is applied to exported Y-up bones. This is obscured by attachment
  rules dominated by X rotation but becomes obvious for Maelstrom's native-Y half-turn. Aligning the
  complete source hardpoint matrix, skipping that basis conversion, or replacing the target bind
  rotation makes shoulder weapons stand vertically and rotates hip equipment incorrectly.
- Infantry weapon rigs also require `addendum.lst`: plasma and jammer grenade `hp_hand`
  sockets are added at source lines 262/269; advanced ACE hand/shoulders at 337–339;
  Flamethrower shoulders at 24–25. ExportWeapons retains these as ordered per-model
  `thirdPersonBoneAdditions`, plus `firstPersonBoneAdditions` and equipment
  `worldModelBoneAdditions`, with source lines and native parent-local transforms.
  The Katana's `ff_sword_ef` also comes from this file in both first/third person.
  Apply amendments before attachment/effect lookup through the shared native socket
  primitive; do not create renamed bones or guessed offsets in the browser.
- The native missing-child rule at `0x99e350..0x99e3a2` applies to infantry too:
  Router telepad has no `hp_hand`; frag grenade and Boomer trigger have no hip
  sockets in either UBR or addendum. Resolve the requested child to the preserved
  native skin joint zero (`router_telepad` or `hp_hand` respectively), as retail
  does, rather than failing world entry or inventing replacement hardpoints.
  Rule lookup precedes this child resolution: the caller at `0x966836` passes
  the requested child name to `AttachmentRuleFactory::Get`, then `0x96684d`
  calls `AttachSequence`. Keep the requested name for the rule key even when
  joint zero supplies the source position; substituting its resolved name can
  incorrectly apply a different rule.
  A missing player/parent socket is still malformed data; this child rule never
  authorizes guessing a player mount. `PlanetSideNativeSockets.test.js` audits
  all 64 weapon metadata entries and loads the affected GLBs with Babylon.
- Ordinary first-person retail hierarchy is: shared camera presentation root -> animated arms ->
  `bip01_r_hand` -> weapon native joint 0. Arms and weapon play the same named clip concurrently.
- glTF adds a fixed Z-up/Y-up armature above native joint 0. Runtime may cancel that synthetic ancestor
  exactly once, but must not invert or reset native joint 0, its bind pose, or its clip transform.
- `fp_offset` and `fp_rot` move the shared arms+weapon viewmodel, not the weapon relative to the hand.
  `fp_rot` is signed fixed-turn yaw with 16,384 units per revolution. Retail's standard viewmodel
  pivot is 1.6 native units. First-person MAX exports are self-contained full rigs.
- The HART building shell is not the animated shuttle mechanism. Resolved `obbasemesh` owns four
  separate authored bindings: `openb` opens the four roof doors, `closea` closes them, `open` raises
  `hp_shuttle` while retracting all eight gangways, and `closeb` lowers it while extending them.
  Preserve the corresponding ADB duration fields; the GLB clips share an exporter timeline and do
  not by themselves retain the retail six-, eight-, and twelve-second playback durations. After
  refreshing the resolved game-object catalog, run TerraSunder's
  `node scripts/export-planetside-hart-presentation.mjs` to rebuild the focused runtime contract.

### Vehicle mounting

#### HART boarding and orbital landing data

`raximod export hart --source <PlanetSideDir> --out <output>/hart` is the focused, transport-neutral
HART export. It writes `manifest.json` and `drop-locations/mapNN.bin`, validating
the corpus before publication and leaving identical files untouched. It does not
re-export vehicle GLBs. Refresh `raximod export vehicles --reuse-assets` separately when
entrance bindings change. The repaired PSForever mount-declaration reader accepts
both `MountInfo(seat)` and `MountInfo(seat, Vector3(...))`; the optional vector is
a **server dismount position**, not an original native entrance. Unsupported
declarations fail instead of silently dropping an entrance.

Native `orbital_shuttle` has eight detached entrances and one passenger station
template duplicated into stations 2..300. Entrance 7 explicitly authors range
1..100, unlike the other seven 1..300 ranges. Preserve that asymmetry. Vehicle
manifest entries now retain the eight entrances, hidden-passenger policy and
camera template, without manufacturing infantry mount clips. The focused HART
manifest retains full ranges, native camera/clip/effect timing, and all three
Sanctuary buildings' eight entrances, eight paths, barrier boxes and command
provenance. Building paths and barriers are source data, not yet proof that a
particular barrier callback or path space has been reconstructed. `hasattachedmountzones=false`
and `hasdetachedmountzones=true` must not be replaced by moving vehicle-centre
proximity tests. Source vectors remain **right-handed Z-up**.

TerraSunder's walk-in policy joins the exported barrier centres to native portal
room bounds and transforms those rooms using the continent's placement matrix.
The eight barriers occupy four distinct corridor planes (local horizontal ±48,
versus outer room edges ±56); each corridor serves two entrances. Do not infer
barrier half-extents to enlarge them, or equate these source entrance indices
with PSForever's reconstructed gantry indices. The server resolves its own index
from Player position. This is an explicitly documented browser boarding policy,
not a claim to have recovered the retail automatic-trigger callback. Camera
offsets applied to the animated GLB shuttle node stay native-local, like other
vehicle articulation offsets: do not apply basis conversion twice.

HART camera source/runtime boundary: `chasecameraviewpoint=(-28,0,29)` is not
by itself a complete exterior-shot contract. The exported shuttle hull spans
about 85×40×29 native units; placing a camera at that point and looking into
joint zero produces a close-up of the tail. TerraSunder retains the native
viewing side but frames the hull using a cached bone-local bounding sphere,
current camera FOV/aspect and 10% composition margin. This is an explicit
browser framing policy, not an exporter offset correction; do not change
the source vectors or infer new ADB values from the browser's camera distance.
The test uses the actual loaded/skinned GLB and both flight clips, not a
synthetic camera-bone fixture alone.

Further evidence in the installed December 2009 executable (SHA-256
`7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`):
`0x006585f1–0x00658621` reads `flightosheightforholdingviewpoint` into the HART
state's `+0x14`. Viewpoint wrapper `0x00661d20` calls `0x004e71d0`, compares its
Z against the configured threshold (`0x00661da6`), then saves/holds that
viewpoint (`0x00661df7` onward). The original also loads the distinct
`flightostimebeforestasis` at `0x00658634–0x00658664`; its timer comparison is
at `0x006598eb–0x00659906`. Preserve both values (275 and 2 seconds) and the
passenger orientation: the browser's exterior-framing repair does not yet
implement the full native holding/stasis camera sequence.

The HART export retains `animationAttachBone` from `animattachbonename`.
`flight_os_*_effect_name` values are effect-package event names, not graph names:
resolve every `efp_effect` binding, including its authored socket. For the shuttle,
`root` is its configured animated attachment (`orbital_shuttle`), while skid
events explicitly use `osfrontskid` and `skids`. Do not attach these effects to
the synthetic GLB root. TerraSunder audits all 16 timed entries against the
package, graphs and shuttle joints. Their windows span compound arrival/lowering
or raising/takeoff phases; GLB clip duration is not the effect lifetime.
The browser scopes negative off-times to the compound phase as a documented
lifetime policy; the exact retail stop callback remains unproven.

HART `soundkey_*` properties are filename/volume/range tuples, not scalar
filenames. The three shuttle entries reference warmup (range 80), arrival and
launch (range 400). `sound_landing_delay` and `sound_liftoff_delay` each author
0.1 seconds; the latter accompanies `soundkey_takeoff`. The `obbasemesh`
mechanism uses scalar `sound_*` filenames with separate `_volume`, `_range`
and optional `_delay` properties (gantry movement: 0.5 seconds, range 200).
`HartBindings.ResolveSounds` preserves both representations, rejects malformed
tuples and exports the mechanism alongside the shuttle with property provenance.
Do not substitute a guessed `orbital_shuttle_landing.wav` or synthetic docking
cue. The browser's bounded one-shot lateness/cancellation policy remains an
integration choice, not a recovered retail scheduler.

The installed `maps/map_resources.pak` contains `map01.droppod` through
`map16.droppod`. Each is 524296 bytes: little-endian int32 extent 8192, int32 cell
size 32, then 256×256 float32 XY pairs. These are destination coordinates, not
height samples or a Boolean mask. `DropPodLocationTable` accounts for every byte
and round-trips unchanged. `raximod export hart` includes source identities and byte hashes;
all destinations must also map to themselves on a second lookup.

Original executable SHA-256
`7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`:
`0x8c0f83..0x8c0fe1` tries `%s.server.droppod` then `%s.droppod`;
`0x8c1600` reads the header and pairs; `0x8c1040` clamps XY to 0..8191,
converts each to integer, divides by cell size and indexes `y * side + x`.
Startup's `_RC_CHOP` at `0x4026bf` makes conversion truncate rather than round.
The observed archive has no server-variant table; do not invent one. The native
`english.str` drop-map help lists steep mountains, thick trees, water and facility
SOIs as unsafe choices that are relocated, followed by a separate Launch button.
Do not relabel these immutable suggestions as full server authorization: current
continent/faction restrictions, player state, and ownership-sensitive checks are
separate. Full neighborhood search/camera semantics are not established by this
lookup disassembly.

PSForever's native `DroppodLaunchInfo` packet codec carries only XY and decodes
Z as zero. TerraSunder's HART map keeps these native coordinates until its
existing terrain-coordinate adapter. Do not interpret the table as elevation,
or use request altitude in facility influence-area exclusion: that exclusion
must compare horizontal map footprints. The browser preview's coarse terrain
height is not a recovered native landing-height field or authoritative surface
validation.

Focused checks: `HartDataTests`, `VehicleEntryBindingsTests`,
`VehicleManifestContractTests`, and TerraSunder `PlanetSideHartData.test.js`.
The browser loader fetches selected tables on demand, retries failed requests
and cancels stale-session loads. These checks prove extraction and lookup, not
walk-in boarding or the complete rendered trip. See TerraSunder
`docs/planetside-hart.md` for implementation status and the acceptance procedure.

Drop-pod deployment is not ordinary dismount. Original `apackage.adb` has
`passenger_deploy` and `nchev_deploy` / `trhev_deploy` / `vshev_deploy` aliases.
The ordinary 0.4-second ANIM contains only `dpdoora`, `dpdoorb`, `dpdoorc`;
the 1.333-second MAX ANIMs additionally contain their biped tracks. Do not
invent an ordinary soldier deployment animation or substitute the normal
`poddoor_left` / `poddoor_right` mount/dismount clips. `raximod export players` includes
station deployment aliases when the same package declares the corresponding
mount alias; this recovers the MAX tracks previously excluded by its clip
selection. TerraSunder's `PlanetSideDropPodPresentation.test.js` checks all
factions, the five exported rigs, exact alias binding and shared sample timing.
Its camera consumes native radius 20 and final pitch 15 degrees through the
existing collision-aware camera; that fixed orbit is a browser approximation.

There are three distinct identities: native entrance (`mountzoneN`), native occupant station
(`mountpointM`), and PSForever seat index. PSForever `MountPoints` keys are entrance numbers, not
station numbers. Join `mountzoneN_mountpointindexes` to the station before reading station metadata.
For example, Skyguard entrance 2 (`DriverB`) also feeds station 1; entrance 3 (`gunnera`) feeds
station 2. APC's alternate driver entrance similarly shifts subsequent station numbers. Assuming
N=M caused wrong cameras, missing seat clips and shifted occupant policies.

`VehicleEntryBindings` reads resolved `game_objects.adb` records and `apackage.adb` aliases from
the original `startup.pak` extraction, with durations/tracks from the original ANIM catalog. Resolve
the definition's `animationpackage` (then the source model's package) and the **entrance name** plus
`_mount` / `_dismount`. Do not construct concrete filenames from the vehicle or station name.
Battlewagon and Aurora use `mediumtransport` aliases and Deliverer body bones; Magrider's entry
alias is `Driver`, not its station's `driver_mount` name. MAX passenger entries preserve `ncheavy`,
`trheavy`, `vsheavy` aliases; drop pods use `nchev`, `trhev`, `vshev`. A common infantry clip remains
available where no dedicated MAX variant is authored; native server seat restrictions still apply.

Armor eligibility is separate from animation availability. Original
`mountpointN_validarmortypes` lists use `std/stl/lit/med/hev` for Standard,
Infiltration, Agile, Reinforced and MAX with explicit `+`/`-` entries. Resolve N
as the occupant station, not the entrance number. Every ordinary aircraft pilot
station authors `std+ stl+ lit+ med- hev-` (Wasp shares Mosquito's station data).
Galaxy/APC stations 10 and 11 explicitly allow only `hev`; their server seat
indices are 9 and 10. Do not infer that an authored MAX animation makes the
driver seat eligible. PSForever's shared seat predicates own runtime permission
and browser acquisition uses the driver predicate. Missing ADB lists retain
PSForever's existing seat policy; the executable's default-list initialization
has not been recovered. See TerraSunder `docs/planetside-vehicle-entry.md` and
server `BrowserVehicleArmorTest` for the checked matrix and lifecycle contract.

Schema 10 exports each entrance's native station, retained-native `entryLocation`, and occupant
variants with paired clip names, package, playback mode, duration, bound body tracks, animated body
tracks, and unmatched source tracks. Radius, eight-way sector, sounds, camera, `hideavatar` and
`renderifcurrentchildin1stperson` come from the **station**. Missing `hideavatar` does not mean hidden.
Select an entrance by approach position; preserve its ID through the server's accepted mount and
subsequent dismount. Do not collapse alternate entrances by seat.

The original executable's `0x570b14–0x570b71` transition-view branch copies the platform
pose and calls `0x499950` with retained sector/radius. The helper clamps sector to 0–7,
offsets horizontally by radius at platform yaw plus `sector * 2048` native angle units,
removes pitch/roll and turns facing back by 8192 units. The caller adds 1.6 metres world Z.
These fields describe a radial transition camera, not soldier exit placement or an authored
camera spline. TerraSunder currently retains them but still uses ordinary chase framing
during entry; see its `docs/planetside-vehicle-camera.md` for the evidence and integration gap.

Each variant also exports an exact `seated` clip/mode/package/alias. Resolve a biped reference
or idle alias on the entrance first, then the occupant station: ANT DriverB shares Driver, but
Liberator gunnerb has an explicit pose despite its station being labelled gunnera. If neither
declares a biped pose, require the mount's authored `play_once_hold` and export `mount-end`.
An idle alias alone is insufficient: `flail_driver_idle` has only vehicle tracks (including wings).
Native `refposeN` can store one exact ANIM record instead of `_refNN` records. The old player
export selected only names containing `_ref`, discarding Liberator and other exact references.
Use `NativeAnimationClipSelection.NeutralClip` for both family and vehicle binding selection:
exact record or explicit `_ref00`, never an arbitrary first numbered pose. Player interaction
package discovery includes native mountzones, not only records declaring animattachbonename.
The current browser holds reference endpoints without native directional reference blending;
that adapter is not proof of visual fit. Source/GLB coverage tests and rendered acceptance differ.

Exact package-requested clips may legitimately overlap only one hatch bone in a large skeleton.
The exporter accepts that explicit overlap instead of applying its heuristic quarter-skeleton
threshold. `--reuse-assets` repairs bodies missing required entry clips; it does not merely rewrite
the manifest. Clips may also name collision/LOD/attachment targets absent from the retained body.
Those names remain explicit `unboundTracks`; they are not guessed into replacement bone aliases.
This is a binding audit, not proof that every auxiliary source track has a visible counterpart.

Current non-BFR corpus: 35 definitions, 130 ordinary entrances, 149 occupant/entrance variants and
298 directed clips. HART has a separate station/shuttle boarding lifecycle and an authored range of
passenger stations, not ordinary paired infantry clips. Galaxy's additional PSForever cargo entrance
is likewise not an infantry entry alias. Neither receives a fabricated animation.

The client samples vehicle channels and the full soldier clip from one elapsed-time clock. Only
animated body channels reserve shared doors; unrelated entrances can run concurrently. This queue,
bounded readiness waits, and camera/control locks are TerraSunder coordination policy, not claimed
retail callback reconstruction. Source playback metadata remains available for further fidelity work.

### Vehicle weapon attachments

- A vehicle weapon-system record is often a logical container rather than a render record. Its
  ordered `meshsequence` names the actual gun/barrel component records; never interpret a failed
  export of the container itself as evidence that those components are body-integrated.
- Preserve `weaponattachbonenames` alongside the corresponding `meshsequence` entries. Mount each
  component through the same-position entry in the vehicle's `weaponpointN_AttachBones`; repeated
  mesh records are intentional for paired barrels. The attachment is a bone-to-bone frame match:
  invert the component's authored `weaponattachbonenames` transform beneath the platform attach
  bone rather than parenting the component model root directly. Rotate only the exported platform
  `YawBone`/`PitchBone` nodes for aim; names such as `*_barrel` inside the mounted component are not
  additional independent aim joints. Empty chassis-joint lists alone do **not**
  mean a fixed weapon: Galaxy's two side guns and Galaxy Gunship's four side guns
  author nonzero `weaponpointN_*AOF` arcs and swivel as whole mounted components.
  Export `aim` with all arc values, `BoneOrientation` and `UpDownUsesY`; the browser
  rotates a child pivot at the matched attachment frame for these jointless
  weapons. Zero-arc fighter mounts stay fixed. AOF fields are ordered lists:
  `0x9536d0..0x953747` appends their entries; Flail/Switchblade preserve both mobile
  and deployed values. Never flatten the list to its first entry. The browser's former `gun`
  substring fallback matched Reaver's `lightgunship_body_armature` and
  `lightgunship_body`, applying pitch twice more than the physical aircraft.
  A correct 55-degree controller limit consequently displayed a 165-degree
  half-loop. TerraSunder now removes that fallback entirely; its
  `PlanetSideVehicleAttitudePresentation.test.js` loads all nine aircraft rigs
  and compares the displayed hull direction with the physical flight direction
  through the actual mouse/controller/world-entity presentation path.
- Vehicle cockpit presentation is not the infantry first-person-viewmodel pipeline. For example,
  Lightning authors `cockpitview true`, hides the mounted avatar, locates the camera through
  `mountpoint1_viewpointbone hp_guna`, and explicitly keeps the current weapon child rendered with
  `mountpoint1_renderifcurrentchildin1stperson true`. Its weapon system supplies the ordinary
  `75mm_lightning` and `chaingun_lightning` components and no separate first-person mesh records;
  the `fp_*` values on that record are sound names, not model references.
- A mesh-bearing vehicle weapon may be classified as separately rendered only when every declared
  component exported successfully. Missing component models must fail the family export rather than
  publishing a plausible `body-integrated-or-effect` fallback.

### Vehicle manifest coordinates and physics coverage

`raximod export vehicles` also publishes `vehicles/creation-pads.json` via `VehicleCreationPadBindings`.
It compiles `vehicle_creation_pad` records with authored mechanism bindings, preserving the named
attachment, open/close actions and requested durations, plus optional active-animation/effect and
child-visibility fields. Type-only `pad_create` and volume-only `spawnpoint_vehicle` do not declare
animated mechanisms; partial mechanisms fail extraction. `pad_creation` uses `create/retract`,
`mb_pad_creation` uses `open/close`, and the cavern pad reverses `close/open`. The shared exported
mb mesh contains both ordinary action pairs, so the server definition selects the action instead
of guessing from the model filename. ADB action names and durations are authoritative; renderer
promotion, clock sampling and physics ownership remain client concerns. The focused file also
retains native creation viewpoints, optional alternate attachment names, and each vehicle's
`usealternateattachbonenameifavailable`. `raximod inspect record` lists every source rig's bone names
to distinguish extraction loss from missing original data. In the installed `uber.ubr`, none of
`dropship_pad_doors`' five rigs contains the ADB-declared `hp_vehicle_alt`; only `hp_vehicle` (or
its LOD-suffixed counterparts) exists. However, original `startup.pak-out/addendum.lst:209`
adds `hp_vehicle_alt` below `hp_vehicle` at native position `(-5.5367, 0, 0)` with zero rotation.
The UBR alone is therefore not proof that this socket is absent from retail. The weapon-socket
repair below parses these additions but does not yet apply them to creation-pad assets/runtime.
That remaining pad integration must use the recorded transform, not an invented offset.

TerraSunder samples cavern open → active → automatic close on the shared spawn clock, starts the
scoped active effect after opening, and fades child-instance visibility by the native rate per
second. It matches the vehicle's native attachment frame to the pad frame, rather than aligning
only the vehicle origin. Original `planetside.exe` SHA-256
`7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a` confirms fade integration at
`0x790cbe–0x790cfe`, active-stage/fade start at `0x791244–0x7912da`, server-close branching at
`0x7911b2`, child opacity restoration on detach at `0x7910c0`, and parent/child attachment-name
dispatch at `0x790e7d–0x790f5a`. These are narrow original-code findings, not rendered parity proof.

- Vehicle manifest schema 11 retains the split between converted model assets and native gameplay data,
  preserves tracked-tread presentation, and adds native camera seat roles and optional camera fields.
  `coordinateSystems.modelAssets` is `right-handed-y-up`; GLB geometry has already received the
  source conversion `(x, y, z) -> (x, z, -y)`. `coordinateSystems.retainedNativeData` is
  `right-handed-z-up`. Raw wheel/contact positions, camera viewpoints/orientations, aircraft skid
  positions, cargo dimensions/offsets, and physics-list vectors remain in that native basis. Their
  containing blocks repeat the basis where practical; `wheelsCoordinateSystem` covers the wheel
  array. Never infer the basis of a raw vector from the adjacent GLB model URI.
- A driver's normal `mountpointN_viewpoint` is a vehicle-root position in retained native
  coordinates; convert it once with the vehicle root. ANT proves the distinction geometrically:
  adding its `ant_body` translation places the camera at 2.83 m above a 2.33 m roof, while its
  authored 1.84 m driver viewpoint lies in the cab. Normal gunner viewpoints with an articulated
  hardpoint are local offsets beneath that node: Raider's `[0,0,0.35]` is below its render floor if
  interpreted at the vehicle root, while `hp_guna` places it at the weapon station. Explicit bone
  modes likewise remain source-local beneath the named articulation node, whose GLB ancestors
  perform the basis conversion. Converting those vectors before applying the named node mirrors
  axes twice (for Lightning, its authored offset becomes inverted). Preserve mount role and
  `mountpointN_viewpointtype`; do not collapse driver-root and gunner/hardpoint rules.
- Preserve `mountpointN_IsDriver` / `IsGunner` on each exported camera station, optional `maxcamdist`,
  and `freecamerabonename` / `freecameraviewpoint` / `freecamerainitialrotation`. These use the existing
  source-record metadata followed by definition overrides, not invented per-model camera aliases.
  Missing optional fields remain null; retained free-camera fields do not imply runtime support.
  In original `planetside.exe` SHA-256
  `7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`, `0x00499bd0`
  selects driver mode 5 before gunner mode 6 (flags read by `0x0090d040` / `0x0090d0d0`), otherwise
  passenger mode 4. Driver/gunner chase initializes yaw `0x2000` and elevation `0x2aa`, using 16384
  units/turn (180 and 14.9853515625 degrees). `0x00499760` applies direct mouse orbit to mode 4,
  not 5/6; `0x00498de0` builds chase from target yaw and these offsets with upright roll.
  `chasecameraviewpoint` is a positional vector in `0x006b58a0` / `0x004e71d0`; never derive camera
  elevation with `asin(z/length)` from it. The browser keeps its existing body/driver-height pivot
  and independent gunner orbit as explicit policies, not proof of every retail pivot/aim-target branch.
  `PlanetSideVehicleViewModes.test.js` audits the entire exported seat corpus against the resolved
  native catalogs and tests driver heading, orbit, zoom and saved cockpit aim through Babylon.
- `animattachbonename` identifies the native vehicle frame used by the authored mount/refpose
  package, but the browser must not parent the complete converted player GLB beneath the complete
  converted vehicle GLB node. Both assets already contain their own source-Z-up to Babylon-Y-up
  conversion ancestor, so composing those ancestors rotates Raider occupants onto their backs.
  Parent the player beneath the vehicle appearance root, which contributes the shared vehicle
  source-forward-to-gameplay yaw but not the native body armature's second Z-up/Y-up conversion.
  Resolve the named attachment as a strict source-data invariant, and let the common-rig seat clip
  supply placement. A vehicle package's
  virtual `refposeN` entry resolves to archive clips named `<animation>_refNN`; export at least its
  neutral `_ref00` clip with mount/dismount. Otherwise the one-shot mount ends in a T-pose.
- Every supported non-BFR vehicle must author a `physics` name, that name must resolve in
  `physics.lst`, and its model must contain at least one object-colliding `role=body` primitive.
  A sphere referenced by `phys_carwheel` is `role=wheel-contact` and cannot satisfy the body
  footprint invariant. Static turret presentation records are a separate path: several author a
  same-named identifier for which no `physics.lst` model exists. That does not weaken the vehicle
  audit; when a turret identifier does resolve (the Spitfire family does), its body is audited too.
- The installed ordinary-aircraft corpus is an explicit regression boundary: Mosquito/lightgunship
  have 22 object-colliding body primitives, Liberator has 36, the shared dropship/Galaxy physics
  model has 49, Lodestar has 30, and Phantasm has 28. Wasp and Vulture share the corresponding
  Mosquito and Liberator footprints. A count change must be reviewed as source/parser evidence,
  never accepted by weakening the audit.
- Vehicle collision sidecars bind the exported render record to its resolved game-object definition,
  not to a same-named lookup. This is required for aliases such as `battlewagon -> battlewagontr`,
  whose active physics is inherited from `mediumtransport`. Active sidecar physics contains exactly
  the manifest primitives with `collidesWithObjects=true` (both `body` and `wheel-contact` roles);
  non-colliding hover contact spheres are excluded, while the disabled `destroyed` group is retained.
  Export fails closed unless all 35 supported definitions match their serialized sidecar by shape ID
  and source-basis center. Definitions sharing one render GLB must first prove identical active and
  destroyed physics contracts.
- `phys_com_offset` is primitive-scoped in the list and is preserved on every primitive. The root
  primitive's authored value is also exported as the model `centerOfMassOffset`, in
  `right-handed-z-up`, with runtime fidelity `runtime-consumed-approximation`. This includes the
  shared dropship/Galaxy value `(-5, 0, 1)`. TerraSunder applies the vector once to the dynamic
  Havok chassis proxy while weighting compound inertia by each positive-mass body primitive. The
  complete Karma mass/inertia solver remains unrecovered; a native zero-mass primitive stays in the
  authoritative object/terrain collision compound but is omitted from Havok inertia because Havok
  treats zero density as its default density rather than as zero mass.
- The complete installed `physics*.lst` vocabulary contains 54 commands. Typed commands are parsed;
  the 24 currently unsupported cone/hinge/RPRO/material-property/geometry-sharing commands retain
  source order, file/line, arguments, and model/shape context. The parser rejects a command outside
  that audited vocabulary instead of silently dropping it, and vehicle manifests publish the
  encountered/retained command coverage summary.

### Vehicle wheel contacts

- Tracked belts are material animation, not wheel-bone animation. The installed client records
  `istreaded` on Lightning, Prowler, and Vanguard and assigns one `wheelN_rollmatname` per side;
  those materials author `sc_texturetransformflags count2` but the vehicle GLBs contain no driving
  tread clip. Export `direction`, `righttread`, `rollmatlen`, the high/low material names, and the
  independent `tread` constraint value under that wheel's tread presentation block. Also export
  `rightTread` and `constraintTread` directly on **every wheel**, independently of that optional
  presentation block: ten of sixteen tracked contacts have no `rollmatname` but still author
  both control fields. Tracked vehicle export rejects missing side/tread/radius metadata. Do not rotate
  or suspension-displace a node merely because its name contains `tread` or `track`.
- `ClientWheelProperties` parsing in the installed `planetside.exe` at
  `0x007b0f90-0x007b105e` stores the reciprocal of `tread` for the Karma constraint and separately
  stores the reciprocal of `rollmatlen` for presentation. The active vehicle path at
  `0x004e5e7a-0x004e5ed4` combines signed longitudinal movement, `direction`, and the left/right
  turn contribution, then calls the material texture-transform path with one component fixed at
  zero. Runtime render validation establishes that the moving native component maps to Babylon U,
  not Babylon V; the API argument/storage order is not the Babylon component order. TerraSunder
  therefore scrolls a per-vehicle clone of the native tread texture in U. It must not share the
  mutable texture transform between vehicle instances.

- The commands `phys_model_collides_with_objects` and
  `phys_model_collides_with_terrain` are misleadingly named but primitive-scoped. In the installed
  `physics.lst`, all 34 object-collision occurrences and all 17 terrain-collision occurrences are
  inside an active box/sphere block; none are model-scoped. Preserve both booleans on each exported
  primitive. In particular, hovercraft body boxes still collide with objects while their four
  wheel/contact spheres explicitly do not—promoting one sphere's flag to the model disables the
  Magrider, Router, Switchblade, Flail, and Thresher body footprints.
- A `phys_carwheel` is an ordered constraint referencing a physics primitive. Preserve the
  primitive position/radius, steer/drive/brake flags, suspension chassis height, travel, damping,
  softness and `zToS`, plus the referenced primitive's `phys_offset_lowlimit` and
  `phys_offset_highlimit`. Wheel contacts without render nodes remain gameplay-significant.
- Suspension `travel` and primitive offset limits are independent native inputs. PlanetSide's
  `Physics_C::Create` in `planetside.exe` (`0x61104c`-`0x6110a7`) passes Karma a suspension
  reference of `wheelBoundingSphereRadius - chassisHeight` and physical low/high limits of
  `reference -/+ travel`; `travel` is therefore the symmetric half-range, not the total span.
  The matching declarations are in the contemporary extracted MathEngine toolkit at
  `ue25_warfare/metoolkit/include/MdtCarWheel.h` (dated 2003-05-27, revision 1.2) and the
  `MdtCarWheel` field documentation in adjacent `MdtTypes.h`. The authored primitive position
  remains the constraint attachment/nominal wheel centre; do not replace primitive Z with chassis
  height. For example, Router authors primitive Z `1.28` but chassis height `1.3`; Switchblade
  authors primitive Z `0.7`, radius `0.56`, and chassis height `0.7`, producing reference `-0.14`.
- The same executable block divides chassis-body mass by authored wheel-constraint count, then
  computes `Kp = 2 * sprungMass * 9.81 / (zToS * travel)` and
  `Kd = damping * sqrt(Kp * sprungMass)`. Karma receives `softness` specifically as suspension
  *limit* softness, not as free-travel spring damping. The mass comes from the named chassis body
  (`agg1` in ordinary vehicle records), so derive it from exported `role=body` primitives rather
  than the game-object total: Prowler totals `510.5` including wheel spheres but its chassis is
  `504.5`; Fury totals `32.1` but its chassis is `30.1`.
- `phys_offset_lowlimit` / `phys_offset_highlimit` remain presentation-primitive limits. Prowler
  happens to author travel `0.5` with primitive limits `-0.25/+0.25`, but Enforcer rear contacts
  author travel `1.25` with primitive limits `-0.2/+0.2`, and one Lightning contact authors travel
  `0.5` with primitive limits `-0.5/+0.1`. Keep those primitive limits for contact/bone artwork;
  never use them as the physical suspension stops.
- TerraSunder now uses one dynamic Havok chassis plus one native-collision support ray per authored
  wheel. Travel follows the rotated suspension axis; ground reactions follow the hit normal, with
  suspension-coordinate velocity derived through their dot product. Using chassis-up as the ground
  reaction incorrectly turns surface-parallel velocity into spring compression on a tilted chassis.
  Recovered Kp/Kd remain unchanged; the runtime uses the authored pose as a loaded rest pose with
  static sprung-weight preload (the PhysX convention), rather than forcing static sag beyond the
  separate artwork limits. This is an explicit adapter choice, not recovered Karma equilibrium.
  Authored steer/drive/brake flags gate independent contact commands; rollback braking must not
  remove drive power. See TerraSunder's `docs/planetside-ground-vehicle-physics.md` for the primary
  references and real-Havok regressions. This is a documented Babylon/Havok adapter, not a recovered
  Karma wheel solver. Limit softness remains transported and unsupported:
  it must not alter support or extend travel until the discrete Karma limit equation is recovered.
- The field/flag portion of native wheel power is partially recovered from the installed
  `planetside.exe` dated 2009-12-02 (SHA-256
  `7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`).
  `ClientVehicleProperties+0xc0/+0xc4/+0xc8` are throttle/idle/brake torque. In the active vehicle
  update at `0x004e59b0`, `0x004e5a79-0x004e5bff` computes
  `drivePower = idleTorque + (abs(accumulatedCommand) > 0.01 ? abs(throttleTorque) : 0)`.
  The per-wheel branch at `0x004e5d85-0x004e5e48` uses
  `drives ? drivePower : 0` normally; when the client's global brake condition selects a wheel
  authored with `brakes=true`, it uses `brakeTorque + (drives ? drivePower : 0)`. A wheel with
  `brakes=false` stays on the normal branch even while that condition is active; this includes the
  corpus's drive-only/non-braked front contacts.
- Tracked steering is now recovered separately: base parser `0x009474b0` stores degree, native-angle
  and radian forms of `maxwheelwhenstopped/fullspeed`. `0x004e5c03–0x004e5ca1` interpolates their
  wheel-motor angular speed using absolute forward speed divided by `maxforward`; these are not
  chassis yaw rates or physical steering locks on tracked vehicles. `0x004e5e10–0x004e5e48` applies
  opposite signs from `righttread` and adds `treadturntorque * (1 - speedFraction)` to motor power.
  Vanguard/Prowler author 400/250 degrees and torque 2500; Lightning authors 633/360 and 1500.
  TerraSunder's shared tracked adapter preserves this command and all contacts, using wheel radius
  for surface speed and a bounded motor under the existing grip limit. Torque/radius force units,
  smoothed-throttle power gating and resulting Havok motion remain explicit browser approximations.
- Keep the remaining longitudinal torque reconstruction diagnostic-only. The calculation of the `ClientVehicle+0x1b4`
  command accumulator and the global brake condition, and a later
  vehicle-disable gate are not yet identified. The call at `0x004e5f0b-0x004e5f2e` passes per-wheel
  desired velocity and power through the wrapper at `0x00619340`; the native store at `0x00a91e70`
  writes them to the Karma wheel at `+0x170/+0x174`. The solver callback beginning at `0x00a9c420`
  shows a bounded velocity-motor row at `0x00a9cf21-0x00a9d01e`, but does not establish the retail
  complete desired-velocity equation, torque units, timestep integration, or resulting chassis response.
  Report those values as unsupported/null and do not relabel TerraSunder's bounded raycast-vehicle
  speed controller as the retail torque equation or invent surface-specific traction from this
  evidence.

### Aircraft handling

- Pilot-controlled flight is a distinct three-axis control contract. The retail keymaps bind
  forward/back throttle, lateral strafe and elevate/lower separately, while client options expose
  independent pitch, yaw, strafe and throttle axes. Do not reuse ground steering input as aircraft
  yaw or reduce flight to horizontal movement plus cosmetic banking.
- Preserve both `flightmaxpitchatminspeed` and `flightmaxpitchatmaxspeed`, the `minyawspeed` /
  `maxyawspeed` endpoints, independent pitch/yaw/elevate/descent response times, acceleration,
  three-component `flightmomentumfactors`, vertical/strafe fractions, altitude and landing limits,
  and optional gravity/helicopter/throttle-down flags. Missing optional fields remain null; they are
  not permission to copy values from a different aircraft family.
- Afterburners retain their raw force, buildup, capacitor duration, recharge duration and delay.
  The original executable identified below confirms all four durations are milliseconds:
  `0x65f7e0` multiplies by `0.001`. It builds impulse linearly, captures the current impulse
  on the first released/exhausted step, fades it over **recharge delay**, then refills capacity
  over recharge duration. Reactivation resumes buildup from residual impulse. Do not infer
  seconds from small numbers or decay using buildup time.
- Despite the ADB field name, boost is submitted through physics vtable `0xb840ec +0x130`,
  `0x619fa0 -> 0xa90230`, to the impulse accumulator. Ordinary flight submits
  `(commandedVelocity - measuredVelocity) * mass / dt` through `+0x108`,
  `0x619c20 -> 0xa90140`, to force. `0xa93af0` adds impulse/dt to force;
  `0xa963c0` and `0xa95ec0` apply inverse mass and dt. The contact-free result is
  `commandedVelocity + forward * currentImpulse / mass`, not integrated `force/mass * dt`.
  Native `0x6589c9–0x6589ee` also calculates `flightmaxspeed * 0.27778 + force / mass`.
  Preserve these source values unchanged; don't bake a browser timestep, speed clamp or force
  conversion into export. TerraSunder's `scripts/check-planetside-afterburner.py` emulates
  bounded original instructions and its flight tests cover the complete ordinary-aircraft corpus.
- The installed December 2009 `planetside.exe` build (SHA-256
  `7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`) retains
  `ClientFlightVehicleProperties` type information and an embedded
  `ClientFlightVehicleProperties.cpp` diagnostic path. It expands TFDB in
  `@CouldNotLoadTFDBErr` as the terrain-following database. Its terrain path adds
  `flightminheight` to sampled terrain height and directly reads `flightrateofdescent`; this proves
  that `flightminheight` is not a universal world floor, but does not recover the mode transitions
  or complete ascent/descent equation.
- Aircraft `flightmaxspeed` uses km/h: its interned ID at `0x006b3760` is stored in
  `0x00d080d0`, and the base property loader `0x006b58a0` writes properties `+0xf0`
  (subobject `+0x414`, destination `-0x324`). `0x0065b559` scales it by `0.27778`.
  Preserve the raw number in `handling.maxForwardSpeed`; convert once in runtime.
  The native min/max-speed pitch fields are nose-elevation limits in degrees,
  shared by cockpit and chase control. They do not restrict total yaw heading.
- Original aircraft banking/yaw/strafe contract (same executable hash above): `flightmaxroll`
  loads at flight-properties `+0x41c` (`0x0076207f`, subobject pointer `+0x5bc`).
  `0x0065b559` divides forward speed by `flightmaxspeed * 0.27778`; do not subtract
  `flightminspeed` when constructing the interpolation fraction. `0x0065c570` interpolates
  `maxyawspeed` (properties `+0xe8`) toward `minyawspeed` (`+0xec`) as speed rises.
  `0x0065c93c` scales bank by speed fraction and normalized yaw response; maximum roll
  values are 10–80 degrees across ordinary aircraft, not a universal 15–25-degree cap.
  `flighthashelicopterfeel` sets flag bit 0 (`0x00760b00`, loader `0x00761d40`);
  `0x0065c6b2` levels roll at forward fractions <= `0.15` (constant `0x00b81a60`).
  This narrow flag behavior is recovered; the browser's transition smoothing remains an
  adaptation. `0x0065cb00` scales strafe by `1 - (1 - strafefractionatmaxspeed) * fraction`
  and rotates its vector by yaw alone (`0x0065cdbe`). All nine ordinary aircraft author
  fraction 1, so their authored strafe limit does not decrease with forward speed and bank
  must not turn strafe into lift. TerraSunder's `PlanetSideFlightBanking.test.js` checks the
  full exported corpus against resolved ADB fields and these runtime invariants.
- Original mouse yaw is a persistent normalized turn command, not a target heading.
  In the same executable, `0x0065a3d0` returns unchanged for zero mouse deltas and
  accumulates/clamps vehicle `+0x4a4` into [-1, 1]. `0x0065c990` slews response
  `+0x4a0` toward that target by `2 * dt / flightsecondstomaxyaw` (properties
  `+0x470`); `0x0065c570` uses it for per-tick yaw and speed-dependent bank.
  Keep the authored response value unchanged on export. TerraSunder's mouse-to-axis
  adapter retains browser sensitivity and does not claim native OS pixel scaling,
  diagonal-axis filtering or integer-angle rounding. Its `PlanetSideFlightTurns`
  tests prove persistence, counter-steering, saturation and handoff across the corpus.
- Preserve, but do not invent behavior for, `flightusestfdb`, the base and
  afterburner turbulence families, ascent/descent rates, or minimum height until their executable
  contracts are recovered. In the current non-BFR aircraft corpus every authored
  `flightgravityactsatlowspeed` is false and every authored `flightafterburnerturbulencestandard` is
  zero; downstream coverage must fail if low-speed gravity becomes active or the turbulence
  standard becomes nonzero so each behavior is re-audited before use.
- Momentum fields still use a documented TerraSunder approximation, not a recovered retail
  equation; afterburner free-flight integration is recovered above. Do not reinterpret their names as evidence for lift, drag, stalls, or
  sideslip, and keep the authored values reversible while executable research continues.
  The runtime's powered yaw response steers only commanded forward momentum through the
  collision-accepted yaw step, separately from independent world drift and the A/D response.
  Treating strafe acceleration as the whole turning budget made the Reaver nearly stop while W
  remained held. This is a browser integration correction, not a new extracted constant or native
  momentum formula. `PlanetSideFlightTurns.test.js` checks complete powered maneuvers for all nine
  aircraft without resetting velocity between steps; isolated attitude tests are insufficient.
- Keep aircraft-only engine, wing, skid-contact, contrail, dust, water and cargo presentation
  records separate from handling. Ordinary-aircraft export fails when a non-commented `flight*`
  property has no explicit manifest representation; new source fields must be classified rather
  than silently discarded.
- Aircraft engine-bone motion is recovered in original `0x0065d870` (same executable hash
  above). Resolve the explicit left/right bones at properties `+0x44c` / `+0x450`.
  Their native local-Y rotation is `(min(forwardSpeed / maximumSpeed, 1) - 1) * 4096`
  angle units: -90 degrees in hover, zero at full speed. Flag `+0x410 & 0x400`
  subtracts body roll on the left and adds it on the right; `& 0x200` subtracts
  body Y orientation (pitch) from both. `flightengineuseyorient` is **not compass yaw**;
  the retained manifest name `engineUsesYawOrientation` must be interpreted accordingly.
  Preserve the hinge's translation and bind transform. The native parked branch approaches
  -4096 at `flightlandingenginespeed` (properties `+0x4f0`, loader `0x00761d40`),
  not `flightlandingenginemaxrotation` as a bone endpoint. Native boost adds an internal
  force-state fraction before the speed cap; TerraSunder's shared local/remote presentation
  uses actual forward speed and its existing landing-mode signal as explicit adaptations.
  `PlanetSideFlightArticulation.test.js` verifies the eight applicable aircraft / 12 GLB
  engine joints and the shared runtime paths; do not replace explicit bones with regex names.
- Aircraft gear consumer `0x0065dca0` (same executable hash above) reads skid bones
  at properties `+0x458..+0x468`. Their exported bind pose is DEPLOYED: retraction adds
  positive native local Z up to `flightskidpositionmax` (`+0x474`). Do not negate this
  offset or lower the bind pose to "deploy" it; that pushes visible struts through
  otherwise-correct native collision supports. Phantasm's rotation bones `+0x50c/+0x510`
  additionally rotate in opposite local-X directions up to `flightskidrotationmax`
  (`+0x514`). Restore their bind rotations at zero too. Original update increments are
  0.02 metres and 1.5 degrees (`0x4ec590/0x4ec5f0` clamp at endpoints); the browser maps
  them to its 60 Hz clock. A binary 4 m clearance threshold is requested TerraSunder
  policy, separate from the native `flightlandingheight` physics gate, not a recovered
  retail height or progressive extension equation. `PlanetSideFlightArticulation.test.js`
  checks all ordinary aircraft at 10/30/60 FPS; `scripts/test-vehicle-landing-gear.mjs`
  renders their native gear after landing on the real collision controller's flat support.
- `droppod` and server-controlled `orbital_shuttle` have specialized flight state machines. Their
  presence must not weaken the shared-field invariant for ordinary pilot-controlled aircraft.
- `canfly` is only a broad source capability and must not be treated as a pilot-controller flag.
  Vehicle manifest schema 4 exports `handling.flightControl` as `pilot`, `drop-pod`, or
  `server-scripted`, while retaining the original nullable
  `handling.flightIsAlwaysServerControlled` source flag. The installed non-BFR corpus is strict:
  Mosquito, lightgunship, Wasp, Liberator, Vulture, dropship, Galaxy gunship, Lodestar, and Phantasm
  are `pilot`; `droppod` is `drop-pod`; `orbital_shuttle`, which authors
  `flightisalwaysservercontrolled=true`, is `server-scripted`. An added, removed, or reclassified
  flyable definition must fail export until its controller contract is reviewed.

### Sky layer swap packages

- A `game_objects.adb` sky layer `mesh <record> <argument>` uses `<argument>` as a
  **swap scope in the mesh record's `epackage.adb` package**, not as a material name or
  a texture-name suffix. The installed December 2009 `planetside.exe` (SHA-256
  `7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`)
  proves this in the sky constructor at `0x8775cb-0x8775ea`, which calls
  `Engine3d::SetSwapPackage` at `0x96af30` (the diagnostic name is at `0xcafa94`).
- For Amerish, `skydome10 -> layer1 mesh skydome1 map10` selects the `skydome1`
  package's `map10` scope. Both `sky3des_layera+skydes_layer2` and `sky3des_layera`
  map to `skydome_bend_e`. Do not select `skydome10.dds` based on the zone name.
  `SkyMaterialBindings` preserves every selected mapping and stream offset; invalid
  layer tuple arity and missing packages/scopes fail instead of silently losing data.
- Environment schema 3 publishes `layers[].swapBinding` plus a shared `skyMaterials`
  sidecar generated through the normal native-material exporter. The installed corpus
  resolves 17 mesh-layer scopes to 15 distinct `skydome_bend_a..o` materials, with
  1024-by-512 source textures. All use `cs0_skydome_bend_a`: `blendtexturealpha`
  combines `texture` and `tfactor` using texture alpha, with authored passthrough UVs.
  RGB-only panorama sampling drops a meaningful source channel. The retail supplier is
  environment fog color: `lc_fogcolor` registers opcode 15 at `0x9ad64d`; the dispatch
  table at `0x9ae19c` routes it to `0x9ae0af`, writing light offset `0x70` at `0x9ae10c`.
  `0x9ae53f` returns that value through `0x8605bc` to the weather update `0x863910`;
  weather offset `0x0c` is passed to the sky collection at `0x8635b1`. The browser uses
  floating-point fog RGB, not the original integer `/256` weather-color rounding.
  Underwater/storm modifiers require their own integration; do not substitute ambient
  or sunlight color for this field.
- Time-of-day fog bounds are fractions of `yon`, not metres. Retail blends yon/start/end
  individually (`0x9ae72c-0x9ae75e`), then multiplies the normalized bounds by the blended
  yon in render-state application (`0x9c6cc2-0x9c6cd1`). Preserve all three fields, not
  precomputed distance keyframes: interpolation of their products is not equivalent.
  Babylon scene linear fog can consume the resulting distances without shortening the
  camera far plane or applying world fog to the camera-relative background sky.
- Environment `lc_direction` is authored in degrees, but retail `0x9ade39` quantizes
  to 16384 units/turn, rotates native +X through `0x9e7540/0x9e75c0`, and stores
  the resulting vector at light `+0x48`. `0x9ae6a5-0x9ae6cf` blends those vectors,
  not Euler angles. Preserve original degrees in the export; consumers must evaluate
  endpoint vectors before interpolation and convert `(x,y,z)` to `(x,z,-y)` once.
  The native X rotation does not affect +X; it is not missing input data. Do not
  mirror the browser azimuth sign or interpolate +170/-170 through zero degrees.
- Schema 3 also publishes `skyModels`, keyed by distinct mesh record, with canonical
  GLB/material-sidecar paths and `sourceLibrary`/`sourceRecord`. Export the mesh once,
  then apply each layer's material swaps; do not generate one mesh per continent.
  The installed records are `uber.ubr`'s `skydome1` (56 vertices/72 triangles),
  `skydome20` (148/216), and `vrskydomeb` (20/10), all without animation clips.
  Shared material sidecars carry textures; the environment GLBs omit embedded images.
- The browser environment exporter no longer emits the guessed legacy `texture` or
  `sourceTexture` fields or its fixed panorama list. The separate editor-preview
  `SkyDatabase` texture helper is not authoritative sky material selection.
- Check missing dependencies **after selecting the layer's swap**. The unswapped
  `skydome1` references unavailable `sky3des_layera` and `skydes_layer2` textures;
  ordinary continent swaps replace those references, but VR's unswapped layer does not.
  The unsuffixed `vrgridstatic+fadegridsides` has no commands, but retail capability
  selection resolves it to the shipped `vrgridstatic+fadegridsides_gf3` definition
  (see below). Do not report an empty `missingTextures` list as complete when the
  selected definition is absent, or replace unshipped textures with a guessed panorama.
- Retail material capability selection is a separate lookup from package swaps.
  The verified executable's `0x9c0230` dispatches profiles through `0x9c03e4`;
  `0x9c0410` appends each candidate suffix and stores the first existing whole
  definition at material `+0x80`. Explicitly suffixed names bypass selection.
  `NativeMaterialProfiles` implements all native profile IDs and lookup orders.
  Environment exports explicitly select generic four-stage profile 8:
  `_ot4`, `_gf4`, `_ot3`, `_gf3`, `_ot2`. This is the browser sky output policy,
  not a guess about the user's GPU. `skyMaterialProfile` preserves exact bindings,
  profile order and executable provenance; consumers apply it after package swaps.
  Never combine fields from the unsuffixed definition and its selected variant.
- Capability suffixes belong to material names, not inferred texture filenames.
  Preserve the full record for command lookup, but split the logical unsuffixed
  name for base/lightmap metadata. The native constructor strips `_gf3` before
  lightmap lookup at `0x9bd601-0x9bd62f`. Thus `+null_gf3` still means no lightmap
  and `+fadegridsides_gf3` must not request a nonexistent `fadegridsides_gf3` image.
- The VR grid's selected definition has three stages: `vrtgrid` (16 pages, 5 FPS,
  looping), `fadegridsides` on UV2, and `white`, with ADDSMOOTH/SELECTARG1/
  BLENDFACTORALPHA color operations. Its visible grid is encoded in alpha. The
  sky draw wrapper at `0x877990 -> 0x965df0` overrides material defaults: primary
  enable/disable masks are `0x4080/0x1350`; secondary masks `0x4084/0x1352`.
  Both disable lighting (`0x40`), and secondary meshes enable alpha (`4`) and
  disable Z writes (`2`). Do not infer sky blending from `mat_pipeline=opaque`.
  Native meshes use Fat vertices with normals/two UV sets but no stored color;
  keep the exported neutral vertex stream, without inventing a light model.
- Clear weather initializes transmission to 200 at `0x86210f`. Sky TFACTOR alpha
  is `255 - (((255 - underwaterAlpha) * transmission) >> 8)` at
  `0x863cf2-0x863d19`, yielding byte 56 above water. Keep this separate from fog
  RGB; dynamic cloud/underwater integration must provide its actual state.
- Explicit environment `unshippedSkyTextures` describes source absence, not
  network failure. `DirectXGetTextureHandle` returns zero when data is absent
  (`0xa25780`, diagnostic `0xa257e9`); resource binder `0x9fd3a1 -> 0xa25590`
  calls `IDirect3DDevice8::SetTexture(NULL)` (vtable `+0xf4`). Direct3D terminates
  the cascade when COLORARG1 is TEXTURE and that binding is NULL. Consumers may
  implement this only for recorded source absences, never failed downloads or
  incomplete exports. The literal named `null` remains a real texture. The
  absent panorama art is not recovered by this behavior; do not replace it.
- Sidecars can retain alternate package definitions with `sections=0`. Sky
  consumers instantiate only definitions used by actual mesh sections, then apply
  their selected swap/profile. Loading every retained definition downloads other
  continents' skies and can fail on irrelevant dependencies.
- Sky effects such as `theplanet` retain their authored effect-graph position,
  orientation and dimensions. A sky layer's material-swap contract does not replace
  those effect graphs, or justify treating every sky as a cubemap/photo dome.
- Environment `skyEffects` is a dependency-scoped ordinary effect catalog, built by
  the same `EffectGraphExportTool.ReadEffects` interpretation as the full library.
  `skyEffectMaterials` uses the shared material exporter. The installed 16 graphs
  contain 34 sprite layers using 10 materials; no combat catalog download or duplicate
  effect parser is required. Missing references fail the export. A future mesh effect
  requires explicit mesh dependency export; it must not silently refer to an absent GLB.
- `ef_offset_pos` moves the **whole graph origin**, whereas `ef_layer_offset` is local
  to a layer. The reference executable's command table at `0x9b88bc` dispatches opcode
  `0x1f` to `0x9b90d0` (effect position `+0x70/+0x74/+0x78`, transformed by its basis)
  and opcode `0x4e` to `0x9b9af0` (layer context `+0x48`). All 815 recovered uses of
  `ef_offset_pos` occur once before the first layer. Keep the lossless command order;
  the pending-command layer index is not evidence that the graph offset affects only
  that first layer. Runtime applies it before world-space detachment, once per graph.
- Native material sidecars include ordered `sectionRenderStates` and `baseRenderStates`
  references with their complete `renderstate.adb` commands and `resolved` status.
  Preserve repeated preset applications and keep base and section views separate.
  Preset names alone do not specify blending: `cs9_ef_sun` sets destination blend `one`,
  unlike `cs9_bluemoon_a`. Unresolved references remain explicit, not empty success.
  Exporting this data does not imply that every runtime consumer implements it yet.
- World-aligned effect sprites are **native XY planes**, not native XZ planes.
  `ef_align_world` opcode `0x50` sets render-context bit `0x80000` at `0x9b840b`;
  `0x9b6fe7-0x9b7064` constructs a width segment on X and `0x9c56c0-0x9c57a0`
  expands it along Y before rotation. Browser geometry therefore converts to X/-Z
  once. Camera-facing billboards are a separate camera-space primitive. Native
  orientation `0x9b6a31 -> 0x9e7660/0x9e75c0` is `Rz * Ry * Rx`; convert the
  composed quaternion rather than swapping Euler angles into Babylon's other order.
  Native quads have four vertices; disabling material culling already makes them
  two-sided, so duplicate reversed triangles would blend translucent layers twice.
- Sky animation uses the game-day clock, not wall-clock seconds. Retail getter
  `0x96cb50` reads time-of-day `+0xac`, updated modulo 86400 at `0x9afcb0`;
  keyframe hours are multiplied by 3600 at `0x9b01f7`. Secondary `skydome20`
  rotates with `(seconds + 18000) % 86400`, converted to 16384 units/turn
  (`0x8779fb-0x877a25`), plus its authored `dust_cloud_angle` X/Y tilt. Only
  exact effect `thesun` receives the separate six-hour alpha envelope at
  `0x877ad8-0x877b61`, clamped to byte 128. Global alpha multiplies packed
  diffuse alpha before texture stages (`0x9b66ac`); byte 255 disables it.
- Secondary sky meshes retain the authored material section, not the similarly named
  base definition. `skydome20+null` uses two `modulate2x` stages: named `null` on UV2
  with initial DIFFUSE, then the dust-band image on UV0 with CURRENT. DIFFUSE does
  not become CURRENT after each stage, and the preset TFACTOR is inactive unless
  a stage references it. Preserve both UV sets and vertex color. The native sky
  wrapper (`0x965df0 -> 0x9c20c0`) initializes alpha blend factors 5/6; this section
  enables alpha without overriding them. Runtime adapters can share one ordered
  stage translator for celestial sprites and dust meshes, with separate vertex inputs.

### Materials and textures

- Vehicle faction skins are whole `epackage.adb` material swaps, not texture-name
  substitutions. `NativeMaterialManifestTool` exports `factionBindings` with ordinary
  `nc`/`tr`/`vs` scopes, source/replacement identities, hidden material sections and
  command provenance, plus full replacement material definitions. Package-backed
  records disable guessed per-texture variants; `_low` and Black Ops scopes are
  separate authored states. Mosquito's `mosquitonc_1_alpha` uses `mosquitonc_1`, but
  its TR swap at stream offset 60144 uses material/texture `mosquitotr_1_alpha`.
  Lodestar's `metalncblue -> metaltrblack` likewise cannot be recovered by token swaps.
  Keep the original imported section identity across clones and repeated faction
  changes, and isolate each hull/attachment's material set before parenting children.
  Native packages contain dangling swaps (notably `dropshipvs_4_alpha` and several
  faction wreck skins). Export `resolved: false` with exact identities; retain the
  base presentation with explicit runtime diagnostics instead of inventing a skin.
  `raximod export model --materials-only` refreshes sidecars/textures from an existing GLB's
  material/UV usage through the canonical translator without rewriting its mesh or
  animation. TerraSunder's aircraft faction test audits all nine pilot definitions,
  attachments and wrecks against the lossless effect-package catalog.
- A native material's identity is its complete definition, texture stages, sampler/lightmap state,
  faction/animation state and source render state—not just its name.
- Preserve stage order, texture arguments/operations, TFactor/vertex color, UV generation and
  transforms, texture animation pages, alpha/blend, fog, lighting and culling in material sidecars.
- Missing stage programs/textures must resolve by evidence-backed aliases or remain explicit warnings;
  analogous names are research evidence, not permission for an undocumented fallback.
- Shared texture extraction is allowed only with referential-integrity validation.
- An explicit `mat_textureN null` is a named resource reference, distinct from
  absent texture commands and from the `+null` no-lightmap suffix convention.
  Preserve the installed 16x16 `null.dds` in native stage sidecars. The native
  texture command dispatch (`0x9bf188 -> 0x9c0f00 -> 0x9fe170 -> 0x9811e0 ->
  0x9fcce0`) resolves the name and creates a resource; suppressing its image
  loses authored data, notably `skydome20+null`'s first stage. Keep inferred
  lightmap metadata empty for the `+null` suffix. This does not change the
  separate editor/glTF albedo heuristic: source-stage export must preserve all
  named inputs, not only the texture that approximates a material's base color.

### Cloak profiles and Darklight

`raximod export cloak` compiles the installed `game_objects.adb` cloak families and
`darklight_vision` implant/light preset, plus the complete `cool_stealth`
material/stage/texture sidecar. Run a focused export with:

```sh
dotnet run --project src/Raximod.Cli -c Release -- export cloak \
  --source /path/to/PlanetSide --out /path/to/terrasunder/public/planetside/cloak
dotnet test tests/Raximod.Generation.Tests --filter FullyQualifiedName~CloakExportToolTests
```

The game-object input must round-trip byte for byte. Preserve the three cloak
channels in **NC, TR, VS** order; they are faction values, not RGB or the numeric
browser faction order. This ordering and the current-plus-20-samples movement
smoothing are recovered from original executable `CloakHandler` constructors
`0x6ab550`/`0x6aba00` and movement functions `0x6ac690`/`0x6acb10` (executable
SHA-256 `7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`).
Retain movement/jumping/damage/object-use penalties and their hold/recovery
channels, additive/strongest combination, in/out fade rates, range/zoom,
`cloak_useavatarcloaking`, and `cloak_isactive` initial state. Reject malformed or
repeated scalar/tuple data instead of silently taking the first value.

Darklight's `effect_data1` is the 40 m viewer reveal range. Its light preset
authors yon 40.96 and normalized fog 0–.5, giving 0–20.48 m. Keep `effect_data`
(5) neutral until its meaning is established. Viewer presentation never changes
PSForever's authoritative cloak flag or collision/projectile availability.

The default native render setup (`0x73f760`) names `cool_stealth`; its animated
bump/environment stages, additive blend, and no-fog state are source data.
The fourth material slot remains unbound in ADB. Its retail draw-time binding
and native signed-DuDv texture upload/filtering remain unresolved. TerraSunder
currently renders the authored first bump/environment pair with recovered cloak
opacity; this is explicitly a bounded colored-shimmer approximation, not proven
retail screen refraction. Preserve all exported stages for future recovery.
See TerraSunder `docs/planetside-cloak.md` for the runtime boundary, unit tests,
rigged/VAT rendered checks, and loading/cancellation validation.

Runtime cloak twins must share the body's live VAT manager as well as its pose
palette and instance settings. Babylon mesh cloning copies the manager at the
current clock value; TerraSunder advances only the original. A private copied
manager freezes the cloak body while head/equipment sockets continue moving.
This is a runtime ownership issue, not a reason to regenerate animation assets.

### Effects

- `ef_point_trail` is spatial: integer point capacity (clamped 2..256), native
  spacing range (floored at .03), a second range, and orientation. In reference
  `planetside.exe` SHA-256 `7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`,
  registration `0x9b12c7` assigns opcode 0x55; compiler `0x9b44a6` and handler
  `0x9ba4b0` feed `0xa1cc70`, which squares spacing for the displacement test in
  `0xa1cd40`. `0xa1cff0` retains points in a capacity-bounded ring. The second
  range multiplies elapsed time into a point attribute (`0xa1d022`); its complete
  renderer semantics remain unresolved. Separate `ef_point_trail_decay_time`
  is opcode 0x5c, not the spacing argument. Preserve ranges and all commands;
  never export spacing as seconds or Babylon frame history. TerraSunder's
  `docs/planetside-effect-trails.md` documents the distance-sampled browser
  translation, smoothing/catch-up differences, tests and remaining limitations.

- Celestial `ef_color_ramp1/2` use ordered channel-rate steps (`0x9ba1d0`), not
  conventional color lerps. Preserve commands across layer boundaries: elapsed time
  initializes the color clock; `ef_color_ramp_fixup` restores lifespan, whose native
  default is zero (`0x9b52bd`). The cursor does not advance between these ramps.
  Packed channels normalize by 255 and requantize with nearest/even times 256,
  clamped to 255. The loop compiler accumulates authored durations; ordinary loop
  mode adds a return-to-initial-color segment of the last duration. Its period-patch
  byte offset is inconsistent with its four-byte instruction format in the reference
  executable (`0x9b4138`). TerraSunder explicitly preserves the accumulated period
  in a typed field, not corrupted opcode bytes; this is documented interpretation,
  not a claim of exact legacy memory behavior. Installed sky graphs use only zero-
  variance ramp1/ramp2. Keep the raw commands/provenance rather than exporting
  guessed linear-gradient keyframes.
  The ordinary installed corpus also uses ramp3: registrations `0x9b122d`,
  `0x9b123b`, `0x9b1249` map all three to compiler `0x9b4451` and renderer
  `0x9ba1d0`. Duration sampling occurs once at `0x9b4460` through `0xa1c6e0`;
  constant ranges return their mean before bounds checks, whereas varying ranges
  use their native uniform/Gaussian sampler and bounds. Preserve those ranges.
  Native `ef_color` parsing `0x9b2e42..0x9b2ff8` expands one/two/three/four
  arguments into `AAAA`/`ABAB`/`ABCB`/`ABCD`; creation selects a four-slot palette
  member at `0x9b4538`. Never deduplicate palette entries or interpret three
  arguments as three equally weighted choices. TerraSunder's shared compiler
  now covers all 454 exported color programs, independently of geometry type;
  its ordinary renderer integration remains unfinished. See its
  `docs/planetside-effect-colors.md` for tests and explicit zero-period/duration
  adaptations. Seven native stream graphs contain empty loop/fixup pairs.
  Ordinary runtime sprites now bind packed color as DIFFUSE/initial CURRENT,
  while imported effect meshes bind it as TFACTOR; do not bake both into a final
  multiplicative tint. Trails outside their generator hierarchy need the same
  binding. Browser sprites now share unit geometry with native width/height in
  their instance transforms; length-change rates use the same units, using Z
  for native XY planes converted to browser X/-Z. Do not allocate geometry by
  each randomized dimension. The full ordered size interpreter remains
  separate unfinished fidelity work.
- Native `ef_startdelay` is graph-wide. Compiler `0x9b417b` writes instance
  +0x9c, regardless of which layer surrounds the command. Update `0x9b7960`
  resets age +0x98 after the delay; color evaluation reads that visible age at
  `0x9b7d4d`. Time scale applies before the delay test (`0x9b78c3`). Preserve
  ordered commands rather than interpreting layer indexes as separate clocks.
  TerraSunder preserves fractional time crossing the boundary, an explicit
  adaptation from the native updater's frame-dependent discarded fraction.
- Native `ef_emit_count` is a whole-graph creation count, not a particle-layer
  modifier. Definition handler `0x9b25dd` stores its range at +0x94; creation
  `0x9b5670` samples once, rounds nearest/even, suppresses nonpositive counts,
  and caps at 1024. Loop `0x9b57d0..0x9b585c` compiles every member independently.
  Repeat flag `0x80000000` at `0x9b580a` makes `0x9b3a51` skip initial audio.
  Preserve all layers and ranges: 517 installed graphs use the count, 111 of
  those have multiple layers. TerraSunder uses the shared graph renderer and a
  browser-owned cancellation handle instead of a separate particle shortcut.
- Effect ranges may contain negative deviations or reversed bounds. Parser
  `0xa1c88a` retains them; evaluator `0xa1c812..0xa1c832` applies lower then upper
  bounds only for varying values. Constants return before bounds. Never sort
  bounds, reject signed deviations or clamp a constant to its editor limits.
- Trail renderer `0x9c84c1..0x9c84fb` writes longitudinal U from point +0x0c and
  uses V=0/1 across ribbon width. This establishes the second `ef_point_trail`
  range as longitudinal texture timing, with additional rolling offsets from
  +0x1450/+0x1454. TerraSunder now uses the correct axes but still documents its
  normalized-U translation until the full time/roll contract is implemented.
- The semantic path is game-object event -> effect package -> effect graph -> ordered layers/actions.
- `ef_wave_package` names resolve through the ordered `waves.adb` sets exported
  in `native-adb/audio-catalog.json`, not WAV filename prefixes. Native
  `oicw_bomblet` (stream offsets 768..804, name index 18) contains
  `oicw_explosion_small_sharper1.wav` and `oicw_explosion_small_sharper2.wav`.
  Preserve all members and validate every referenced file. TerraSunder's cue API
  also accepts an exact single WAV for native vehicle landing cues, but does not
  synthesize wave-group membership from similarly named files.
- Native effect audio settings are effect-wide, not sequential playback modifiers.
  Original `planetside.exe` SHA-256
  `7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`:
  initialization `0x9b5260` defaults distance to 32 and integer volume to 63;
  compiler `0x9b39c0` cases `0x34..0x37` sample/overwrite distance and volume,
  convert volume with nearest/even rounding, select priority bits and set auto-update
  bit `0x40`. Integer 3D audio bridge `0x403e90` normalizes by 127; normalized
  projectile/weapon volume must not receive that conversion. Native `0x411320`
  has 16 spatial channels and priority 4 > 2 > 1 > 0; `0x413d60` adjusts
  non-critical scores at squared distances 400/10000/40000. Auto-update stores
  a world position (`0x40def0`) and refreshes listener-relative coordinates
  (`0x40ea60` -> `0x40e170`), not a moving effect-parent attachment. Browser
  channel budgeting, attenuation/occlusion and charge binning remain separately
  documented runtime policies in TerraSunder's `docs/planetside-combat-audio.md`.
- Projectile `sound`, `sound_loop`, `sound_min`, `sound_range`, `sound_volume`
  export as structured `flightAudio`, replacing the misleading `nearMissSounds`.
  Constructor `0x75a440` defaults min/range/volume to 1/20/.9. Preserve sound list
  order and missing numeric fields; do not guess WAV variants. The source
  `vanu_sentry_turret_projectile` references `spiker_nearmiss.wav`, absent from
  every installed audio index; this unresolved source reference must remain reported.
  `charge_effect_count` exports as `chargeEffectCount`; Spiker's count 4 corresponds
  to complete numbered projectile and impact graph families. Missing base references
  are not evidence for renaming or fabricating graphs. The original heavy-impact
  `impact_heavyarmor_bullet` reference is also absent; any generic surface policy
  belongs in runtime, not a generated-record edit.
- Equipped weapon audio is separate from shot audio. Original `game_objects.adb`
  `soundkey_looping` is a three-value `(file, volume, radius)` tuple; `raximod export weapons`
  preserves it as `loopingSound`, validated by `ClientWeaponMetadataResolver`.
  Chainblade, Mag-Cutter and Force-Blade use `knife_{tr,nc,vs}_secondary_loop.wav`,
  volume `0.75`, radius `100`; katana uses `fp_ff_energysword_idle_loop.wav`, `0.45`,
  `100`. Do not set `clientfiremodeN_looping_fire_sound` for these idle hums: that
  boolean controls repeated firing sounds. The runtime binds melee equipped loops
  to mode 1 based on the powered swing records; this is an explicit presentation
  interpretation, not a mode binding encoded in the sound tuple. Native knife
  on/off records contain only filenames; TerraSunder uses its ordinary one-shot
  volume and the matching loop radius for positional toggle playback. Mutable
  equipped mode comes from PSForever replication, never inferred from a swing.
- Extraction completeness and runtime implementation are separate statuses. Preserve commands that
  Babylon does not yet interpret and classify their support honestly.
- Effect meshes and textures search every installed archive/asset family. Do not assume effects own a
  private namespace when an authoritative source exists elsewhere.
- `ef_every_other N` is authored shot cadence, not a generic randomizer. The native
  `9mmbullet_projectile` value is three. Runtime visual throttling must not change
  this into every third *displayed* event: pass a real-shot ordinal before coalescing.
- An `empire_effect=true` projectile reference can name a family without an unsuffixed graph:
  native `35mm_projectile` has only `_tr`, `_nc`, `_vs`, and `_bo` graphs. Resolve an
  available faction variant before requiring a generic record. Preserve both the
  original game-object reference and actual graph identities; do not fabricate an asset.
- TerraSunder distinguishes a projectile-owned graph from independent emissions. Primary/slave
  projectile graphs follow their flight owner; `ef_emitter` children with `ef_world_space`
  retain their emission positions. This is a runtime lifetime/transform contract, not a reason
  to delete or rewrite native `ef_world_space` commands during extraction.
- Weapon wire identity and native source identity are separate. Join PSForever's explicit
  `ObjectClass` constants to the original game-object registry class IDs when names differ;
  reject conflicts, missing IDs and ambiguous/non-weapon matches. Class 14 is named
  `cannon_dropship_20mm` by PSForever and `20mm_cannon_dropship` in the client. Both Galaxy
  cannon slots must retain the server definition while exporting the native record's components,
  ammunition and muzzle bindings. Resolve repeated weapon components within their owning slot,
  not through a definition-wide first-name match. All 424 same-named registry/server constants
  examined in the 2026-09-07 audit agreed; this is identity evidence, not a spelling heuristic.
- First-person animation/effect selection and world firing effects use different native fields.
  In the reference executable (SHA-256
  `7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`), getter `0x5de1c0`
  resolves `fp_fire_anim_state` (negative/absent means mode index). Caller `0x4604ef` maps
  states 0..3 to fire1..4, otherwise fire1. The selected state drives both animation and
  `TriggerEffect` (`0x462129 -> 0x96bd30`). Thus Quasar's alternate first-person mode authors
  fire1 even though its third-person package also has fire2. Do not infer effect selection
  solely from which package keys happen to exist.
- World firing (`0x565ca6`) instead selects from `fire_effect_keys`, using
  `fire_effect_offset` (negative/absent means mode index) plus shot ordinal modulo muzzle count;
  an out-of-range index selects key zero. The executable initializes the default vector to
  fire1/fire2/fire3 (`0x5dbfa3..0x5dc0b2`). Preserve this contract independently in exported
  mode `presentation` metadata, alongside the authored alternate/common-mesh flags. Exported
  `numberOfMeshes` retains the authored component-group size. TerraSunder uses ordered
  meshsequence groups and that count to resolve repeated component instances; with one
  component, modes share it. This is a source-data-driven adapter, not a claim that all
  legacy mesh-counter code has been reproduced. Component/barrel runtime coverage
  remains separately tested from this metadata export.
- `clientfiremodeN_muzzle` and `..._muzzle0` assign the **same** slot zero. The native
  prefix parser (`0x5dd2e6..0x5dd376`, key initialization `0x5db520`) converts an empty
  suffix to zero and assigns the indexed vector entry; it does not append a separate muzzle.
  Resolve aliases in inheritance/command order. Concatenation creates phantom barrels on
  inherited weapons such as `quadassault_weapon_system` and `prowler_weapon_systemB`.
  Preserve repeated *values* in different slots: two mounted guns may both call their muzzle
  `ef_chaingun_muzzel`. Do not deduplicate those names across component instances.
- Original `startup.pak-out/addendum.lst` amends already-loaded native assets. It is a separate
  source from ADB/UBR, not reconstructed developer code. `AddendumListDatabase` retains ordered
  commands and source lines, validates typed `addendum_bone` records, and preserves uninterpreted
  command kinds. `raximod export vehicles` exports vehicle-body and weapon-component `boneAdditions`; this is not yet a
  complete implementation of all addendum edits or all asset families. Source lines 181, 195,
  and 202 add Vanu sentry `ef_muzzle_a_adjusted`, VS field-gun `ef_fire`, and NC field-gun
  `ef_fire` respectively. Missing UBR sockets must be checked against this file first.
  Added bone position/rotation are parent-local right-handed Z-up. The renderer adds them beneath
  native joints already under the exported basis ancestor, without another basis conversion.
  Preserve fixed-turn rotation words and provenance, rather than flattening additions into
  renamed source joints. The three currently consumed weapon additions have zero rotation.
  Mosquito/Wasp afterburner sockets are body additions at lines 1732–1733:
  `ef_exhaust180a/b` under `mosquito_engine_l/r`, position `(-0.64, ±1.26, 0)`,
  rotation words `0000 0000 2000` (hex 8192, half of a 16384-unit turn).
  Vehicle metadata must carry these additions; do not replace missing UBR nodes with
  guessed exhaust offsets. TerraSunder instantiates them through the shared native
  socket primitive, then cancels the Y-up effect graph's basis at the Z-up socket.
  Afterburner sounds come from resolved `soundkey_afterburner_fire/loop` and
  `sound_afterburner_fadeintime_ms/fadeouttime_ms`; visuals consume `afterburnN`
  events in the resolved vehicle `sourceRecord` effect package. The Liberator
  package has no afterburn event for Vulture, and `lodestar_afterburn` is an empty
  native graph: retain these gaps instead of synthesizing another aircraft's flare.
  This does not mean those vehicles lack visible engine thrust. The same original
  executable, SHA-256 `7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`,
  binds idle/thrust/afterburn separately at `0x6aea2f..0x6aee78`; the family lookup
  at `0x6b0f22..0x6b0f88` uses masks `0x8000`, `0x20000`, `0x40000`, `0x80000`,
  `0x100000`. The selector `0x65e91a..0x65eb4c` divides signed forward speed by
  powered top speed and uses thresholds -0.20, 0.31 and 0.51 for none/thrust1/
  thrust2/thrust3. Active afterburn above top speed chooses thrust2 plus afterburn;
  normal engine meshes must remain available during boost. TerraSunder starts
  these families at their native package sockets for all pilot aircraft. It uses
  pilot occupancy as its engine-on policy and changes thrust graphs immediately;
  native warmup/exhaust scheduling and band crossfades remain unsupported.
  Flare materials also require the complete native stages: `ef_reaver_afterburn`
  combines a scrolling second stage with `addsigned` (Direct3D saturates
  `Arg1 + Arg2 - 0.5`); `ef_phant_engine_thrust` uses UV2 masking, UV0 fire and
  the graph's `ef_color` as TFACTOR. Preserve `hasUv1`, both UV sets, stage order,
  alpha-one blending and the exact operations; a one-texture PBR approximation
  produces solid exhaust and black rectangles. The browser's shared fixed-function
  effect shader consumes these fields; no flare-specific export material is needed.
- Native `AttachSequence` child-name search (`0x99e350..0x99e3a2`, same executable hash above)
  selects joint zero when the child attachment name is absent (`xor eax,eax` at `0x99e391`).
  Original Spitfire AA requests `barrels_bone`, whereas its patch5 rig starts at
  `barrels_aa_bone`; this is not an exporter spelling error. Component metadata retains both
  requested and resolved names with `weaponAttachResolution.reason = native-joint-zero`.
  A unique native first joint is required; do not choose a similarly named bone. Corpus checks
  currently find exactly this one use among supported turret components. No AA-specific muzzle
  flash package exists; do not fabricate one merely because the ordinary Spitfire has one.

### Projectiles and special weapons

- `ProjectileBehaviorMetadata` is the single transport-neutral ammunition/projectile contract used
  by both weapon and vehicle exports. Add shared physics, impact, lock, proxy, aggravated, jammer,
  secondary, or linked-projectile fields there instead of duplicating anonymous exporter records.
- `requiresProjectileFlight` is an auditable derived classification. Its accompanying
  `projectileFlightReasons` comes from authored lifecycle state such as remote existence, gravity,
  guidance, locks, bounce, multi-stage/proxy/slave behavior, flak, acceleration, and detonation
  actions. Velocity alone never decides whether browser/server projectile state is required.
- `projectile`, `projectile1`, `projectile2`, and later selections on an ammunition record are
  sibling variants. They do not inherit sparse fields from the base selection. Only the selected
  projectile record's real ADB inheritance chain supplies omitted values; copying base fields can,
  for example, leak the Switchblade Scythe slave onto unrelated ancient-ammunition variants.
  The Thumper's `firemode1_projectile_index = 1` selects each cartridge's timed `_b` variant;
  mode 0 uses its base contact projectile. This applies to frag, plasma, and jammer cartridges.
  PSForever must preserve that selection in `Tool.Projectile`, not merely in presentation:
  its range validation and flight lifetime otherwise incorrectly use the timed variant for both
  modes. The exported 450 m / 60 m maximum ranges are velocity-times-lifetime bounds, not promises
  of horizontal travel under gravity. `BrowserThumperModesTest` covers both server selections.
- Preserve nested `damageProxy`, `secondaryProjectile`, and `slaveProjectile` records with their own
  provenance. These links drive reusable persistent fields, OICW/cluster children, and linked
  presentation; generated manifests must not require a per-weapon Babylon or Scala lookup table.
- Lock policy fields remain independent. In particular, `autolock_fire_after_lock` and
  `autolock_fire_and_forget_projectile` are booleans with different meanings; the latter is not a
  projectile-name reference even though its development-era property name suggests one.

### Collision, AAB and portals

- Movement collision policy is explicit native collision, else the record's native AAB faces, else none.
  Materials explicitly marked non-colliding/effect/leaves are excluded. Render geometry is never a
  movement collision fallback.
- Projectile intersection is separate. Retail `planetside.exe` SHA-256
  `7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a`, functions
  `0xa07c00/0xa0a070`, resolves AAB section/vertex references into triangle intersections and rejects
  `nocollide`/`nodraw_nocollide`; that surface switch does not reject `nodraw`, `effect`, or `leaves`.
  Water/lava and shield surfaces have additional query-mask gates (`0x8f68f0`). Preserve material
  identity in lossless AAB exports; do not reuse movement simplification as shot geometry. TerraSunder
  consumes those existing buffers as retained projectile queries, independent of portal visibility.
  Its `docs/planetside-projectile-queries.md` records the native call chain, current mask/state scope,
  and focused `PlanetSideProjectileQuery.test.js` coverage. Debug AAB visualization remains separate.
- AAB is strong source spatial/section evidence but is not intrinsically labelled “collision” by the
  format. TerraSunder's AAB fallback is an explicit project policy.
- Portal records describe directional connectivity between bounded regions. Export all records,
  bounds, children, raw flags/IDs and ownership evidence. Runtime camera clipping controls rendering;
  ambiguous ownership must fail visible rather than delete geometry.
- Sanctuary VR buildings (`vt_building_nc/tr/vs`, `basetype=virtual_training`) each declare
  six `physicsbarrierN=avatar_barrier_virtual_training` volumes, paired with `barrierN` and
  `anglebarrierN` in `game_objects.adb`. `physics_barrier.lst` supplies their 0.5 x 6 x 5.5
  source-Z-up box. Export them as `training-zone-trigger` queries, preserving every group,
  position, rotation and dimension; never classify these avatar interaction barriers as
  vehicle-only walls or let them suppress structural AAB fallback. TerraSunder consumes the
  placed boxes for its VR-area prompt. Do not derive training entrances from a render-door
  child index or guessed ramp offset. `TrainingZoneBarrierTests` audits all three faction
  records and retained structural collision. The exact retail dialog callback is not recovered;
  query-volume entry is the documented browser policy, not an asserted engine reconstruction.
- Resolved continent `portalChildren` are also authoritative collision placements when the child
  record publishes a collision sidecar. Facility stairs are the important example: the visible
  steps and parent AAB commonly expose only steep riser faces, while a separate child such as
  `stairs_standard` supplies the explicit walkable ramp collider. Navigation derivatives must retain
  those resolved child transforms and authored face winding; they must not infer a ramp from the
  render mesh or make downward ceiling/underside faces walkable.
- PSForever's `BuildOutdoorNavMesh --collision-export` consumes this same canonical terrain,
  collision and portal-child corpus without requiring a Recast navmesh. Its `.collision.json`
  publishes the native `worldSize`, `maskN`/`deepMask`, `lavaN`/`lava` beside a hash-bound exact
  static index. HART/Instant Action use it to resolve physical landing elevations, including
  roofs, rather than trusting a browser heightfield. Standing-footprint support and 50-degree
  slope rejection are explicit browser/server policy, not recovered retail pod physics.
  Preserve the native masks and resolved portal-child collision when changing continent export;
  every index rebuild must also update its matching mask publication.

## Validation and investigation checklist

For a parser or exporter change:

1. Add a focused fixture/invariant at the earliest changed layer.
2. Inspect at least one known record and one structurally different record.
3. Verify raw values/provenance before checking the semantic output.
4. Confirm output determinism with a clean diff or write-if-changed behavior.
5. Run the affected family command and strict audit before the aggregate report.
6. Verify emitted URIs exist, not merely that Babylon tolerates a missing file.
7. Check the TerraSunder focused runtime test for the output contract.
8. Remove any prior workaround while validating the root correction.

Full-install UBR scans and GLB generation are intentionally sequential to bound memory. Avoid broad
parallel test/export processes and large `/tmp` trees; `/tmp` may be RAM-backed. Use a disk-backed
output directory and preserve unrelated dirty work in both repositories.

## Documentation map

- `adb-decoder.md`: lossless ADB layers, round-trip rules, semantic resolution and catalog audit.
- `browser-export.md`: individual GLB, continent, collision, terrain and loading-screen commands.
- `formats.md`: supported container/content formats and exact read/write meaning.
- `reports/adb-structural-audit.md`: locally generated, untracked installed-client ADB completeness matrix.
- TerraSunder `docs/planetside-native-aab.md`: runtime collision policy and source-coverage meaning.
- TerraSunder `docs/planetside-extraction-fidelity-plan.md`: cross-repository fidelity status and
  remaining runtime work.

## Runtime provenance

Browser implementation details, UI behavior, multiplayer authority, and runtime presentation
research are maintained with TerraSunder in
`docs/planetside-native-runtime-provenance.md`. Keeping those notes with their consumer prevents this
extractor guide from becoming the changelog for a particular engine while preserving the source
contracts above.
