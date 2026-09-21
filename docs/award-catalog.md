# PlanetSide award catalog

`awards.adb` is the authoritative source for commendation order, ribbon colors, categories,
levels, faction restrictions, display policy, prerequisites, thresholds, and requirement filters.
The palette record named `award_colors` is embedded between commendation records and is **not** a
commendation ID. `AwardCatalogCompiler` assigns IDs in source order while skipping only that record;
the installed retail catalog therefore produces IDs `0..428` for 429 commendations.

The lossless `NativeAwardCatalog` remains the inspection/provenance layer. The compiler in
`Raximod.Generation.Awards` builds a separate typed runtime contract and must never replace
the ordered raw properties. Known scalar properties fail if repeated or multi-valued. Unknown
properties remain in `unsupportedProperties` and generate diagnostics rather than disappearing.

## Generated contract

`raximod export native-catalogs` writes `award-catalog.json` version 3 with:

- the 1,024-entry native ribbon palette;
- 429 ID-bearing typed definitions;
- localized display name, title, description, and requirement keys plus English values when the
  exact authored key exists;
- ordered typed requirements and per-property ADB stream offsets;
- `game_objects.adb`-derived object-group membership and `xp_event` reverse indexes;
- the complete raw award records;
- audit totals and explicit source diagnostics.

It also writes `award-runtime-catalog.json` version 1. This is the generated server boundary: the
same definitions, diagnostics, object groups, and first-time-event indexes, without the 1,024-color
presentation palette or the raw lossless ADB records. An optional third exporter argument writes
that exact payload directly to a server resource path so the checked-in server contract cannot
silently drift from the browser catalog.

It also writes `localization-en.json`, an ordered ISO-8859-1 decoding of `english.str`. Duplicate
keys stay as separate line-bearing entries. The installed table currently contains duplicate keys
and one malformed non-comment line, so consumers must not flatten it by silently taking the first
value.

Some requirement-text keys authored in `awards.adb` are absent from the installed English table.
The compiler preserves the requested key, emits `missing-localization-key`, and leaves its English
value null. It does not invent an alias or prose. Runtime UIs can render an accurate generic
description from the typed requirement fields while retaining the source discrepancy for audit.

## Reference validation

Award prerequisites must resolve to another compiled award. `objectgroup` filters must resolve to a
non-empty `award_requirement_groups` membership from resolved `game_objects.adb` records, and
`gameobjectclassname` filters must resolve to a game object. Missing references fail export.

Object-group and first-time-event indexes retain class ID, resolved object provenance, and exact
property-source offsets. They are classification data for server-authored gameplay facts; a client
reporting an `xp_event` is not proof that its award requirement occurred.

`awards.adb` has no general sex property. The two manual Valentine's awards encode recipient sex
only in their canonical native record names (`valentine_female` and `valentine_male`). The compiler
translates that narrow source convention into the typed `sex` field; runtime code must not infer sex
from localized labels or invent similar rules for other awards.

## Focused verification

```sh
dotnet test tests/Raximod.Generation.Tests/Raximod.Generation.Tests.csproj \
  -c Release \
  --filter 'FullyQualifiedName~NativeStringTableTests|FullyQualifiedName~AwardCatalogCompilerTests'
```

The installed-catalog test is conditional on `PLANETSIDE_DIR` (or the standard local install) and
checks palette shape, exact commendation count and ID anchors, localization, representative typed
requirements, object-group resolution, and preserved unsupported source properties.
