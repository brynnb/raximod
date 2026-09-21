using Raximod.Generation;
using Raximod.Generation.Assets;
using Raximod.Generation.Continents;
using Raximod.Generation.Effects;

namespace Raximod;

internal static class ExportCommands
{
    public static int Continent(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var options = new ContinentExportTool.Options(
            args.Required("--source"),
            args.Required("--out"),
            args.Value("--terrain-out"),
            args.Value("--native-terrain-out"),
            args.Value("--map"),
            args.Has("--environment-only"),
            args.Has("--liquids-only"),
            args.Has("--ocean-only"),
            args.Has("--placements-only"));
        ContinentExportTool.Result result = ContinentExportTool.Run(
            options, new SynchronousProgress<string>(Console.WriteLine));
        Console.WriteLine(
            $"continent export: continents={result.ContinentsExported} facilityTypes={result.FacilityFootprintTypes}");
        return 0;
    }

    public static int Collision(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        CollisionManifestTool.Result result = CollisionManifestTool.Run(
            new CollisionManifestTool.Options(
                args.Required("--source"),
                args.Required("--models"),
                args.Value("--record")),
            new SynchronousProgress<string>(Console.WriteLine));
        foreach (CollisionManifestTool.Failure failure in result.Failures)
            Console.Error.WriteLine($"{failure.Record}: {failure.Reason}");
        Console.WriteLine(
            $"collision export: {result.Written}/{result.Examined} records, {result.Shapes} shapes, " +
            $"{result.ForcefieldVisuals} forcefield visuals, {result.Failures.Count} failures");
        return result.Complete ? 0 : 3;
    }

    public static int Effects(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        EffectGraphExportTool.Result result = EffectGraphExportTool.Run(
            new EffectGraphExportTool.Options(
                args.Required("--source"),
                args.Required("--out"),
                args.Value("--assets-out")),
            new SynchronousProgress<string>(Console.WriteLine));
        Console.WriteLine(
            $"effects export: effects={result.Effects} layers={result.Layers} links={result.Links} " +
            $"materials={result.Materials} meshes={result.Meshes} cycles={result.Cycles}");
        return result.MissingLinks.Count == 0 && result.MissingMeshes.Count == 0 ? 0 : 3;
    }

    public static int Cloak(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        CloakExportTool.Run(Path.GetFullPath(args.Required("--source")), Path.GetFullPath(args.Required("--out")));
        Console.WriteLine("cloak metadata exported");
        return 0;
    }

    public static int Hart(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        HartExportTool.Run(Path.GetFullPath(args.Required("--source")), Path.GetFullPath(args.Required("--out")));
        Console.WriteLine("HART entrances, passenger presentation, and native landing tables exported");
        return 0;
    }
}
