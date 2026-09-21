# Raximod documentation

Raximod decodes the original PlanetSide archives, meshes, animations, textures, surfaces, maps, and
ASCII databases, then packages them for ordinary 3D tools or game runtimes.

## What it does

- **Standalone exports:** produce a portable GLB with a textured mesh, native skeleton, and
  compatible animations for standard glTF software.
- **Shared game bundles:** remove duplicated textures and animation data while preserving explicit
  manifests and bindings for runtimes such as TerraSunder.
- **Source fidelity:** retain provenance, record identity, rigs, authored transforms, material
  stages, collision, and unsupported fields instead of hiding them behind guessed defaults.
- **Deterministic commands:** run extraction, inspection, auditing, and packaging headlessly in local
  scripts or CI.
- **World extraction:** export continent terrain, placements, portals, native materials, collision,
  effects, audio, and source-backed gameplay catalogs.
- **Inspection and audits:** inspect decoded records and run strict coverage checks without a
  desktop UI or graphics driver.

The parser and TerraSunder production exporters are mature. Extraction, inspection, auditing, and
packaging are exposed through the single `raximod` executable. See [Supported formats](formats.md)
for exact per-format read/write status.

You need your own installed PlanetSide client. No original client archives or general extracted
asset library are included. One small derived weather-geometry resource is documented under
[Recovered resources](recovered-resources.md).

## Guides and references

- [Building and running](building.md)
- [Supported formats](formats.md)
- [Babylon.js and GLB export](browser-export.md)
- [Generated output layout](output-layout.md)
- [PlanetSide ADB decoding](adb-decoder.md)
- [PlanetSide award catalog](award-catalog.md)
- [Using the data in Godot](godot.md)
- [Recovered resources](recovered-resources.md)
- [TerraSunder extraction pipeline](terrasunder-pipeline.md)

If Raximod cannot open a source file, create an issue in the public repository once its location is
established and include the format name and observed error.
