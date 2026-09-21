# Third-Party Notices

Raximod's own source is distributed under the **MIT License**. The dependencies used by the
published command-line application are separate works under the licenses listed below.

## Published command-line application

| Component | Purpose | License |
|---|---|---|
| [.NET 10 runtime and libraries](https://github.com/dotnet/runtime) | Base class library and self-contained runtime | MIT |
| [SharpGLTF Core, Runtime and Toolkit](https://github.com/vpenades/SharpGLTF) | glTF 2.0 and GLB authoring | MIT |

## Optional weather-geometry recovery tool

`tools/RecoverWeatherGeometry` is a development utility. Its Python environment is not included in
Raximod's published command-line archives and its dependencies are not linked into the .NET
application.

| Component | Purpose | License |
|---|---|---|
| [pefile](https://github.com/erocarrera/pefile) | Parse the installed PlanetSide executable | MIT |
| [Unicorn Engine](https://github.com/unicorn-engine/unicorn) | Bounded x86 emulation of identified weather-mesh builders | GPL-2.0 |

## Development and test dependencies

The test project uses Microsoft.NET.Test.Sdk and xUnit packages declared in
`tests/Raximod.Generation.Tests/Raximod.Generation.Tests.csproj`. These packages are not included in
published Raximod command-line archives.

## Notes

- The asset-format code in `Raximod.EngineAssets` is derived from the project's engine-format
  research and is part of this repository rather than a third-party package.
- Document every new runtime, test, build or recovery dependency in the appropriate section. A
  copyleft development-tool dependency must never be described as a permissive runtime dependency.
