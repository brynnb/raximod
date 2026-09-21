# PlanetSide ADB decoding

Raximod treats every installed `*.adb` as a source artifact, not as a convenient dictionary.
The pipeline order is mandatory:

1. `LosslessAsciiDatabase.Parse` decodes the container, exact string-pool bytes, complete name
   index, record offsets, ordered command words, separators, repeats, and opaque header fields.
2. `Encode()` must reproduce the source byte-for-byte. A typed reader is not allowed to run around
   or weaken this invariant.
3. `AdbSemanticDatabase` adds lexical scalar types and source provenance without changing the raw
   view.
4. A family-specific catalog validates its complete command vocabulary and arities. Families
   executed by replacement systems may use `NativeAuthoredCatalog`, which still retains every
   command, operand word, lexical interpretation, and stream offset.
5. Resolvers build separate inherited/reference views. They never overwrite the authored raw view,
   and an evidence-backed repair retains both the authored and resolved target plus its reason.
6. Exporters consume the typed/resolved view and retain provenance in generated manifests.

Do not flatten first and attempt to reconstruct repeated properties later. Do not accept an offset
inside another string-pool entry as a valid symbol. Do not silently default an unknown command,
unknown arity, broken inheritance cycle, or dangling reference.

## Verification

Run the bounded whole-install audit against an extracted retail install:

```sh
dotnet run --project src/Raximod.Cli -c Release -- \
  audit adb --source /path/to/PlanetSide --out reports/adb-structural-audit.json
```

It must report all 23 installed databases, `roundtrip=YES` for each, and zero literal words when the
retail set contains only exact string-pool symbols. The command writes a Markdown completeness
matrix beside the requested JSON report. These generated reports distinguish structural decoding,
typed coverage, runtime use, intentional replacement-system boundaries, and genuinely unknown
semantics. Byte identity proves structural preservation; it does not by itself claim behavioral
reimplementation. Repository-local reports remain untracked because they contain installed-client
samples and machine-specific source provenance.

Generate browser-facing catalogs with:

```sh
dotnet run --project src/Raximod.Cli -c Release -- \
  export native-catalogs --source /path/to/PlanetSide \
  --out /path/to/terrasunder/public/planetside/native-adb
```

The award export is a stricter cross-database contract: it combines the ordered `awards.adb`
records, `game_objects.adb` requirement groups/events, and ordered `english.str` localization.
See [PlanetSide award catalog](award-catalog.md) for its ID, provenance, missing-localization, and
reference-validation rules.

## Intentional boundaries

Every byte remains decoded and round-trippable, but TerraSunder deliberately translates rather
than recreates the retail Direct3D state machine, physics solver/broadphase, lighting engine, audio
mixer, editor UI/workflow, and timed-help presentation. Obsolete, test-only, and BFR-only records
may remain preserved-only. UI layout data is evidence for the independently implemented browser UI,
not a requirement to recreate the retail UI framework.

These boundaries never permit discarding authored gameplay, animation, attachment, collision,
material, effect, environment, audio-routing, or object-inheritance intent.
