using Raximod.Generation.Packaging;
using Raximod.Generation.Assets;
using System.Text.Json;

namespace Raximod;

internal static class PackageCommands
{
    public static int Manifest(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        string root = args.Required("--root");
        BundleManifestTool.Document result = BundleManifestTool.Write(root, args.Value("--out"));
        Console.WriteLine(
            $"bundle manifest: version={result.Version} files={result.Files.Count} bytes={result.TotalBytes}");
        return 0;
    }

    public static int Verify(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        BundleManifestTool.Verify(args.Required("--root"), args.Value("--manifest"));
        Console.WriteLine("bundle verified");
        return 0;
    }

    public static int ExternalizeImages(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        SharedGlbImageExport.Result result = SharedGlbImageExport.Run(
            Path.GetFullPath(args.Required("--models")),
            args.Has("--check"),
            args.Value("--textures"),
            args.Has("--recursive"));
        Console.WriteLine(JsonSerializer.Serialize(result));
        return 0;
    }

    public static int ShareTextures(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        if (args.Has("--apply"))
        {
            SharedTexturePool.Apply(args.Required("--stage"));
            Console.WriteLine("shared texture stage applied");
            return 0;
        }
        SharedTexturePool.Receipt result = SharedTexturePool.Stage(
            args.Required("--root"), args.Required("--stage"), args.Integer("--workers", 4));
        Console.WriteLine(
            $"shared textures: images={result.SharedImages} removedCopies={result.RemovedCopies} " +
            $"savedBytes={result.SavedImageBytes}");
        return 0;
    }

    public static int ReencodePng(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        if (args.Has("--apply"))
        {
            PngAssetRecompression.Apply(args.Required("--stage"));
            Console.WriteLine("PNG packaging stage applied");
            return 0;
        }
        PngAssetRecompression.Result result = PngAssetRecompression.Stage(
            args.Required("--root"), args.Required("--stage"), args.Integer("--workers", 4));
        Console.WriteLine(
            $"PNG stage: changed={result.ChangedFiles}/{result.Files} " +
            $"bytes={result.BeforeBytes}->{result.AfterBytes}");
        return 0;
    }
}
