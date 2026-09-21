# Building and running

## Requirements

- The **.NET 10 SDK**
- Your own installed PlanetSide client data

Raximod is headless and does not require Avalonia, Vulkan, a window server, or a GPU.

## Build and test

```bash
cd /path/to/raximod
dotnet build Raximod.slnx -c Release
dotnet test tests/Raximod.Generation.Tests -c Release
```

## Run

```bash
dotnet run --project src/Raximod.Cli -c Release -- --help
```

Export a self-contained model:

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  export model --source /path/to/PlanetSide --record trmmed --out exports/trmmed.glb
```

The default standalone profile includes standard glTF materials, embedded textures, a native rig,
and every compatible animation. Use `--profile shared --shared-textures <directory>` when building
an optimized asset family whose models intentionally share texture files.

## Project layout

```text
src/Raximod.EngineAssets/  lossless source-format parsing
src/Raximod.Generation/    semantic export, packaging, and audits
src/Raximod.Cli/           supported command-line interface
src/Raximod.Modules/       cohesive family extraction operations
tests/                     focused deterministic tests
tools/                     narrowly scoped source-recovery utilities
```

For the production browser pipeline, command ordering, and validation rules, see the
[TerraSunder extraction guide](terrasunder-pipeline.md).
