# Recovered resources

Most Raximod output is generated from the operator's installed PlanetSide client and is never stored
in this repository. One current exception requires an explicit public-release decision:

```text
src/Raximod.EngineAssets/Resources/NativeWeatherGeometry.bin
```

The 31,732-byte file contains immutable vertex and index results from two identified retail weather
mesh builders. Raximod embeds it so ordinary extraction does not require Python, an x86 emulator, or
the original executable at build time. Its SHA-256 is
`df57ab5dc51bd7c9b5899d71375cced324817b1c717ad882ac16c9b3b404e3a5`.

`tools/RecoverWeatherGeometry/recover.py` deterministically regenerates or verifies the resource. It
accepts only the documented executable SHA-256, executes a bounded set of identified instructions in
Unicorn, enforces allocation and instruction limits, and never launches the game executable:

```bash
python -m venv .work/weather-recovery
.work/weather-recovery/bin/pip install -r tools/RecoverWeatherGeometry/requirements.txt
.work/weather-recovery/bin/python tools/RecoverWeatherGeometry/recover.py \
  /path/to/planetside.exe --check
```

The optional recovery environment uses MIT-licensed `pefile` and GPL-2.0-licensed Unicorn Engine.
It is not included in the published .NET command-line archives and is documented separately in
[`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).

The resource is reproducible and required, so it is not an accidental build artifact. It is also
derived from installed client instructions and should receive an explicit distribution/licensing
review before the new public repository is published. Until that decision, release documentation
must not imply that every byte in the repository was authored independently of the client.
