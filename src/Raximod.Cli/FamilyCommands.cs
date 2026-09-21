using Raximod.EngineAssets.Textures;
using Raximod.Generation;
using Raximod.Generation.Assets;
using Raximod.Generation.Continents;
using Raximod.Modules;

namespace Raximod;

internal static class FamilyCommands
{
    public static int Players(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var forwarded = new List<string> { args.Required("--source"), args.Required("--out") };
        string[] modes = ["--first-person-only", "--cosmetics-only", "--animations-only"];
        string? mode = modes.SingleOrDefault(args.Has);
        if (mode is not null) forwarded.Add(mode);
        return ExportPlayersCommand.Run(forwarded.ToArray());
    }

    public static int Weapons(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var forwarded = new List<string> { args.Required("--source"), args.Required("--out") };
        if (args.Has("--reuse-assets")) forwarded.Add("--reuse-assets");
        return ExportWeaponsCommand.Run(forwarded.ToArray());
    }

    public static int Vehicles(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var forwarded = new List<string>
        {
            args.Required("--source"), args.Required("--psforever"), args.Required("--out")
        };
        if (args.Has("--reuse-assets")) forwarded.Add("--reuse-assets");
        return ExportVehiclesCommand.Run(forwarded.ToArray());
    }

    public static int Audio(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        return ExportAudioCommand.Run([args.Required("--source"), args.Required("--out")]);
    }

    public static int NativeCatalogs(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        string source = args.Value("--adb-source")
            ?? Path.Combine(args.Required("--source"), "startup.pak-out");
        var forwarded = new List<string> { source, args.Required("--out") };
        string? serverAwards = args.Value("--server-awards-out");
        if (serverAwards is not null) forwarded.Add(serverAwards);
        return ExportNativeAdbCatalogsCommand.Run(forwarded.ToArray());
    }

    public static int WorldAssets(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var forwarded = new List<string>
        {
            args.Required("--source"), args.Required("--continents"), args.Required("--out")
        };
        if (args.Has("--overwrite")) forwarded.Add("--overwrite");
        if (args.Has("--liquids-only")) forwarded.Add("--liquids-only");
        return ExportManifestAssetsCommand.Run(forwarded.ToArray());
    }

    public static int Environment(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        string source = Path.GetFullPath(args.Required("--source"));
        string output = Path.GetFullPath(args.Required("--out"));
        if (args.Has("--weather-only"))
        {
            EnvironmentCatalog.RefreshWeather(source, output);
            Console.WriteLine("refreshed native weather metadata and geometry");
            return 0;
        }

        IEnumerable<string> zones = Directory.EnumerateFiles(source, "map*.ubr")
            .Concat(Directory.Exists(Path.Combine(source, "patchmap"))
                ? Directory.EnumerateFiles(Path.Combine(source, "patchmap"), "map*.ubr", SearchOption.AllDirectories)
                : [])
            .Concat(Directory.Exists(Path.Combine(source, "expansion1"))
                ? Directory.EnumerateFiles(Path.Combine(source, "expansion1"), "ugd*.ubr")
                : [])
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!);
        EnvironmentCatalog.Export(source, output, zones, new SynchronousProgress<string>(Console.WriteLine));
        return 0;
    }

    public static int Groundcover(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        string source = Path.GetFullPath(args.Required("--source"));
        string output = Path.GetFullPath(args.Required("--out"));
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"PlanetSide directory not found: {source}");
        Directory.CreateDirectory(output);
        GroundcoverCatalog.Result result = GroundcoverCatalog.Export(
            source, output, new SynchronousProgress<string>(Console.WriteLine));
        return result.MissingRecipes.Length == 0 && result.ActiveMissingTextures.Length == 0 ? 0 : 3;
    }

    public static int OutfitDecals(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        OutfitDecalExport.Run(
            Path.GetFullPath(args.Required("--source")),
            Path.GetFullPath(args.Required("--out")));
        return 0;
    }

    public static int TerrainAtlas(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        string source = args.Required("--source");
        string output = args.Required("--out");
        var textures = new TextureProvider(source);
        foreach (string requestedMap in args.Required("--maps").Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string map = requestedMap.ToLowerInvariant();
            string archive = TerrainAtlasExportTool.FindArchive(source, map);
            TerrainAtlasExportTool.Result result = TerrainAtlasExportTool.Export(
                archive, Path.Combine(output, map + ".png"));
            string material = TerrainMaterialExportTool.Export(result, textures);
            Console.WriteLine(
                $"{result.Map}: {result.TilesWide}x{result.TilesHigh} tiles, " +
                $"{result.TilesWide * result.TileWidth}x{result.TilesHigh * result.TileHeight}, {material}");
        }
        return 0;
    }

    public static int Texture(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var forwarded = new List<string> { "--planetside", args.Required("--source") };
        foreach (string option in new[] { "--materials", "--surface-pak", "--base", "--out", "--search" })
        {
            if (args.Value(option) is string value)
            {
                forwarded.Add(option);
                forwarded.Add(value);
            }
        }
        foreach (string option in new[] { "--babylon-detail", "--named", "--exact", "--loading-screens", "--opaque" })
            if (args.Has(option)) forwarded.Add(option);
        return ExportTextureCommand.Run(forwarded.ToArray());
    }

    public static int AuditAdb(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        string source = args.Value("--adb-source")
            ?? Path.Combine(args.Required("--source"), "startup.pak-out");
        string? report = args.Value("--out");
        return report is null
            ? AuditAdbCatalogCommand.Run([source])
            : AuditAdbCatalogCommand.Run([source, report]);
    }
}
