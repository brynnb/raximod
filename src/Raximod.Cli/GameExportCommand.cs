using Raximod.Generation.Packaging;

namespace Raximod;

internal static class GameExportCommand
{
    private sealed record Step(string Name, string Command, Func<int> Run);

    public static int Run(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        GameBundleConfiguration? configuration = args.Value("--config") is string configurationPath
            ? GameBundleConfiguration.Load(configurationPath)
            : null;
        string source = Path.GetFullPath(args.Value("--source") ?? configuration?.Source
            ?? throw new ArgumentException("Missing required option --source (or source in --config)."));
        string output = Path.GetFullPath(args.Value("--out") ?? configuration?.Output
            ?? throw new ArgumentException("Missing required option --out (or output in --config)."));
        string psforever = Path.GetFullPath(args.Value("--psforever") ?? configuration?.PsForever
            ?? throw new ArgumentException("Missing required option --psforever (or psforever in --config)."));
        string startup = Path.Combine(source, "startup.pak-out");
        bool planOnly = args.Has("--plan");
        bool overwrite = args.Has("--overwrite") || configuration?.Overwrite == true;
        int workers = args.Integer("--workers", configuration?.Workers ?? 4);
        if (workers is < 1 or > 6)
            throw new ArgumentException("--workers must be between 1 and 6.");

        string continents = Path.Combine(output, "continents");
        string assets = Path.Combine(output, "assets");
        string nativeAdb = Path.Combine(output, "native-adb");
        string players = Path.Combine(output, "players");
        string weapons = Path.Combine(output, "weapons");
        string vehicles = Path.Combine(output, "vehicles");
        string effects = Path.Combine(output, "effects");
        var steps = new List<Step>
        {
            Direct("Audit native databases", "raximod audit adb", () => FamilyCommands.AuditAdb(
                ["--adb-source", startup, "--out", Path.Combine(nativeAdb, "adb-audit.json")])),
            Direct("Export native catalogs", "raximod export native-catalogs", () => FamilyCommands.NativeCatalogs(
                ["--adb-source", startup, "--out", nativeAdb])),
            Direct("Export continents", "raximod export continent", () => ExportCommands.Continent(
                ["--source", source, "--out", continents, "--native-terrain-out", Path.Combine(output, "terrain-native")])),
            Direct("Materialize world assets", "raximod export world-assets", () => FamilyCommands.WorldAssets(
                overwrite
                    ? ["--source", source, "--continents", continents, "--out", assets, "--overwrite"]
                    : ["--source", source, "--continents", continents, "--out", assets])),
            Direct("Export weapons", "raximod export weapons", () => FamilyCommands.Weapons(
                ["--source", source, "--out", weapons])),
            Direct("Export players", "raximod export players", () => FamilyCommands.Players(
                ["--source", source, "--out", players])),
            Direct("Export vehicles", "raximod export vehicles", () => FamilyCommands.Vehicles(
                ["--source", source, "--psforever", psforever, "--out", vehicles])),
            Direct("Export effects", "raximod export effects", () => ExportCommands.Effects(
                ["--source", source, "--out", effects, "--assets-out", assets])),
            Direct("Export audio", "raximod export audio", () => FamilyCommands.Audio(
                ["--source", source, "--out", Path.Combine(output, "audio")])),
            Direct("Export cloak", "raximod export cloak", () => ExportCommands.Cloak(
                ["--source", source, "--out", Path.Combine(output, "cloak")])),
            Direct("Export HART", "raximod export hart", () => ExportCommands.Hart(
                ["--source", source, "--out", Path.Combine(output, "hart")])),
            Direct("Export outfit decals", "raximod export outfit-decals", () => FamilyCommands.OutfitDecals(
                ["--source", source, "--out", players])),
            Direct("Export loading screens", "raximod export texture", () => FamilyCommands.Texture(
                ["--source", source, "--loading-screens", "--out", Path.Combine(output, "ui", "loading")])),
        };

        foreach (Step step in steps)
        {
            Console.WriteLine($"[{step.Name}] {step.Command}");
            if (planOnly) continue;
            int exitCode = step.Run();
            if (exitCode != 0) return exitCode;
        }

        if (planOnly)
        {
            Console.WriteLine("[Export terrain atlases] maps discovered from generated continent manifests");
            Console.WriteLine("[Share cross-family textures] staged and applied with a receipt");
            Console.WriteLine("[Audit extraction] aggregate report written last");
            Console.WriteLine("[Publish bundle manifest] logical paths, sizes, and SHA-256 hashes");
            return 0;
        }

        string[] maps = Directory.EnumerateFiles(continents, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && IsContinentName(name))
            .Cast<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (maps.Length > 0)
        {
            int terrainExit = FamilyCommands.TerrainAtlas([
                "--source", source,
                "--maps", string.Join(',', maps),
                "--out", Path.Combine(output, "terrain-macro")]);
            if (terrainExit != 0) return terrainExit;
        }

        bool deduplicateTextures = !args.Has("--skip-cross-family-dedup")
            && configuration?.CrossFamilyTextureDeduplication != false;
        if (deduplicateTextures)
        {
            string stage = Path.GetFullPath(
                args.Value("--texture-stage") ?? configuration?.TextureStage ?? output + ".texture-stage");
            Raximod.Generation.Assets.SharedTexturePool.Stage(
                output, stage, workers);
            Raximod.Generation.Assets.SharedTexturePool.Apply(stage);
        }

        var reportArguments = new List<string>
        {
            "--continents", continents,
            "--assets", assets,
            "--effects", effects,
            "--out", Path.Combine(output, "extraction-report.json"),
        };
        if (args.Has("--fail-on-warning") || configuration?.FailOnWarning == true)
            reportArguments.Add("--fail-on-warning");
        int reportExit = AuditCommands.Extraction(reportArguments.ToArray());
        if (reportExit != 0) return reportExit;

        BundleManifestTool.Document manifest = BundleManifestTool.Write(output);
        Console.WriteLine(
            $"shared game bundle complete: files={manifest.Files.Count} bytes={manifest.TotalBytes} root={output}");
        return 0;
    }

    private static Step Direct(string name, string command, Func<int> run) => new(name, command, run);

    private static bool IsContinentName(string name) =>
        name.Length == 5 &&
        (name.StartsWith("map", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("ugd", StringComparison.OrdinalIgnoreCase)) &&
        char.IsAsciiDigit(name[3]) && char.IsAsciiDigit(name[4]);
}
