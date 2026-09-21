# Generated audit reports

This directory is the conventional destination for repository-development audits. Report JSON and
Markdown are generated from the operator's installed PlanetSide client and remain untracked because
they may contain machine-specific source paths, record samples, and corpus counts.

For example:

```bash
dotnet run --project src/Raximod.Cli -c Release -- \
  audit adb --source /path/to/PlanetSide --out reports/adb-structural-audit.json
```

The command also writes `adb-structural-audit.md` beside the JSON report. Reports that validate a
shared game bundle belong inside that bundle and are included in its integrity manifest.
