# Generated output layout

Raximod produces two different kinds of output. A standalone export is a portable asset selected by
the person running the command. A shared game bundle is a versioned directory contract consumed by a
game runtime such as TerraSunder. Generated output is useful development data; it is not inherently
temporary. Keep it when its path, provenance, regeneration command, and tracking policy are clear.

## Standalone assets

`raximod export model --out <path>.glb` writes:

```text
<path>.glb                 portable model, materials, rig, and animations
<path>.export.json         deterministic source and fidelity receipt
```

The default `standalone` profile embeds images. Callers choose the enclosing directory because these
files are ordinary portable deliverables rather than part of a fixed game-bundle schema.

## Shared game bundles

`raximod export game --out <bundle>` owns the following layout:

```text
<bundle>/
├── manifest.json          logical paths, byte lengths, and SHA-256 hashes
├── extraction-report.json
├── assets/                reusable world GLBs and native companions
├── audio/                 audio files and catalogs
├── cloak/                 cloak presentation metadata
├── continents/            placements, portals, water, and environment manifests
├── effects/               native effect graphs and catalogs
├── hart/                  HART presentation metadata
├── native-adb/            typed native catalogs and ADB audit
├── players/               player models, rigs, animations, and manifests
├── terrain-macro/         continent terrain atlases
├── terrain-native/        native terrain chunks and provenance
├── textures/              content-addressed cross-family texture pool
├── ui/loading/            extracted loading-screen images
├── vehicles/              vehicle models and manifests
└── weapons/               weapon models and manifests
```

Family exporters may add documented files beneath their owned directory. They must not write into a
sibling family. Relative references in manifests resolve from the containing manifest unless that
schema explicitly states another base.

Some JSON `format` values retain their historical `raxicore-*` prefix. These strings are stable,
versioned wire identifiers already consumed by generated bundles and runtimes; they do not name the
current executable or repository. Renaming one requires a schema-version migration across generators,
published assets and every consumer, so ordinary branding cleanup must leave them unchanged.

Before final cross-family packaging, a family may contain its own `material-textures/` directory.
Packaging rewrites duplicate references to the root `textures/` pool and removes only copies whose
consumers were successfully rewritten. Unique family-local textures may remain in place.

The bundle root is suitable for a consuming repository's generated-assets directory, including
TerraSunder's `public/planetside/`. Raximod does not require that output to live inside either source
repository.

## Staging and caches

Packaging changes that replace files use a sibling staging directory, defaulting to
`<bundle>.texture-stage`. A completed stage carries its receipt and can be verified before it is
applied. Stage directories are replaceable working data and should not be committed.

Build products (`bin/`, `obj/`, `artifacts/`, `publish/`), package caches, scratch directories, and
local logs are also replaceable. They are ignored by this repository and may be deleted without
changing an authored or published export.

## Reports and source control

Reports that describe a shared bundle belong inside that bundle so the manifest hashes the exact
report used to validate it. Repository-level audit output belongs under `reports/` only when it is a
deliberate, sanitized reference fixture with stable inputs. Reports containing absolute machine
paths, installed-client samples, or transient corpus counts should be regenerated locally and remain
untracked.

Source code, schemas, small deterministic test fixtures, and recovery provenance belong in Git.
Installed PlanetSide archives, ordinary extraction output, caches, and game bundles do not. The one
tracked derived weather resource and its publication boundary are documented in
[Recovered resources](recovered-resources.md). A consuming game repository may choose to track or
publish its generated bundle independently.

## Reproduction

Keep the full-game configuration with the consuming project, then inspect and execute the same plan:

```bash
raximod export game --config /path/to/raximod.json --plan
raximod export game --config /path/to/raximod.json --fail-on-warning
raximod package verify --root /path/to/bundle
```

Receipts and manifests must use logical or source-relative identities. They must not depend on the
checkout location of Raximod itself.
