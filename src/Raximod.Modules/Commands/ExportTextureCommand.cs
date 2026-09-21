using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raximod.EngineAssets.Textures;
using Raximod.Generation.Assets;
using Raximod.Generation.Continents;

namespace Raximod.Modules;

public static class ExportTextureCommand
{
    public static int Run(string[] args)
    {
        string? planetside = Arg(args, "--planetside");
        string? materialsArg = Arg(args, "--materials");
        string? outDir = Arg(args, "--out");
        string? surfacePak = Arg(args, "--surface-pak");
        string? baseName = Arg(args, "--base");
        string? search = Arg(args, "--search");
        bool babylonDetail = args.Contains("--babylon-detail", StringComparer.OrdinalIgnoreCase);
        bool namedTextures = args.Contains("--named", StringComparer.OrdinalIgnoreCase);
        bool exactTextures = args.Contains("--exact", StringComparer.OrdinalIgnoreCase);
        bool loadingScreens = args.Contains("--loading-screens", StringComparer.OrdinalIgnoreCase);
        bool forceOpaque = args.Contains("--opaque", StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(planetside))
        {
            Console.Error.WriteLine("usage: --planetside <dir> ((--materials <name,...> | --surface-pak <path> --base <mapXX> | --loading-screens) --out <dir> | --search <term,...>) [--named | --exact] [--opaque] [--babylon-detail]");
            return 1;
        }

        var textures = new TextureProvider(planetside);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string[] terms = search.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string name in textures.TextureNames.Order(StringComparer.OrdinalIgnoreCase)
                .Where(name => terms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase))))
            {
                Console.WriteLine(name);
            }
            return 0;
        }
        if (string.IsNullOrWhiteSpace(outDir)
            || (string.IsNullOrWhiteSpace(materialsArg) && string.IsNullOrWhiteSpace(surfacePak) && !loadingScreens))
        {
            Console.Error.WriteLine("usage: --planetside <dir> ((--materials <name,...> | --surface-pak <path> --base <mapXX> | --loading-screens) --out <dir> | --search <term,...>) [--named | --exact] [--opaque] [--babylon-detail]");
            return 1;
        }
        Directory.CreateDirectory(outDir!);
        if (loadingScreens) return ExportLoadingScreens(textures, outDir!);
        if (!string.IsNullOrWhiteSpace(surfacePak))
        {
            if (string.IsNullOrWhiteSpace(baseName))
            {
                Console.Error.WriteLine("--base is required with --surface-pak");
                return 1;
            }
            return ExportSurfaceMaterials(textures, surfacePak, baseName, outDir!);
        }
        int exported = 0;
        foreach (string material in materialsArg!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (exactTextures)
            {
                DdsImage? image = textures.Get(material);
                if (image == null)
                {
                    Console.WriteLine($"skip {material}: no exact texture");
                    continue;
                }
                string exactPath = Path.Combine(outDir, material.ToLowerInvariant() + ".png");
                byte[] exactPixels = forceOpaque ? ForceOpaque(image.Bgra) : image.Bgra;
                File.WriteAllBytes(exactPath, PngEncoder.EncodeBgra(exactPixels, image.Width, image.Height));
                Console.WriteLine($"{material}: {image.Width}x{image.Height}, exact texture, {exactPath}");
                exported++;
                continue;
            }
            if (namedTextures)
            {
                (DdsImage? image, string? key) = textures.ResolveNamed(material);
                if (image == null)
                {
                    Console.WriteLine($"skip {material}: no named texture");
                    continue;
                }
                string namedPath = Path.Combine(outDir, material.ToLowerInvariant() + ".png");
                byte[] namedPixels = forceOpaque ? ForceOpaque(image.Bgra) : image.Bgra;
                File.WriteAllBytes(namedPath, PngEncoder.EncodeBgra(namedPixels, image.Width, image.Height));
                Console.WriteLine($"{material}: {image.Width}x{image.Height}, source={key}, texture, {namedPath}");
                exported++;
                continue;
            }
            var detail = textures.ResolveDetail(material);
            if (detail == null)
            {
                Console.WriteLine($"skip {material}: no detail texture");
                continue;
            }
            string path = Path.Combine(outDir, material.ToLowerInvariant() + ".png");
            byte[] pixels = babylonDetail ? PackBabylonDetail(detail.Value.Bgra) : detail.Value.Bgra;
            File.WriteAllBytes(path, PngEncoder.EncodeBgra(pixels, detail.Value.Width, detail.Value.Height));
            string kind = babylonDetail ? "Babylon detail map" : "texture";
            Console.WriteLine($"{material}: {detail.Value.Width}x{detail.Value.Height}, tileRate={detail.Value.TileRate:g}, {kind}, {path}");
            exported++;
        }
        return exported > 0 ? 0 : 2;
    }

    private static int ExportLoadingScreens(TextureProvider textures, string outDir)
    {
        var screens = new List<(string Output, string Source, int Rows)>();
        for (int map = 1; map <= 16; map++)
        {
            // Retail's loading UI has no map08 artwork in ui_loading.inc or the
            // texture archives. Preserve that absence rather than inventing it.
            if (map == 8) continue;
            screens.Add(($"map{map:00}", $"ui_loading_map{map:00}", map >= 14 ? 3 : 2));
        }
        for (int map = 96; map <= 99; map++)
            screens.Add(($"map{map}", $"ui_loading_map{map}", 2));
        for (int cavern = 1; cavern <= 6; cavern++)
            screens.Add(($"ugd{cavern:00}", $"ui_loading_ugd{cavern}", 3));

        int exported = 0;
        foreach ((string output, string source, int rows) in screens)
        {
            const int columns = 4;
            const int tileSize = 256;
            byte[] pixels = new byte[columns * tileSize * rows * tileSize * 4];
            bool complete = true;
            for (int tile = 0; tile < columns * rows; tile++)
            {
                DdsImage? image = textures.Get($"{source}_{tile:00}");
                if (image == null || image.Width != tileSize || image.Height != tileSize)
                {
                    Console.WriteLine($"skip {output}: missing or invalid {source}_{tile:00}");
                    complete = false;
                    break;
                }
                int tileX = tile % columns;
                int tileY = tile / columns;
                for (int row = 0; row < tileSize; row++)
                {
                    int sourceOffset = row * tileSize * 4;
                    int destinationOffset = ((tileY * tileSize + row) * columns * tileSize + tileX * tileSize) * 4;
                    Buffer.BlockCopy(image.Bgra, sourceOffset, pixels, destinationOffset, tileSize * 4);
                }
            }
            if (!complete) continue;
            string path = Path.Combine(outDir, output + ".png");
            File.WriteAllBytes(path, PngEncoder.EncodeBgra(pixels, columns * tileSize, rows * tileSize));
            Console.WriteLine($"{output}: {columns * tileSize}x{rows * tileSize}, original loading screen, {path}");
            exported++;
        }
        return exported > 0 ? 0 : 2;
    }

    private static int ExportSurfaceMaterials(TextureProvider textures, string pakPath, string baseName,
        string outDir)
    {
        var exported = new HashSet<ContinentBiome.Class>();
        foreach (string surfaceName in ContinentBiome.ReadSurfaceNames(pakPath, baseName))
        {
            ContinentBiome.Class surfaceClass = ContinentBiome.ClassifyName(surfaceName);
            if (surfaceClass is ContinentBiome.Class.None or ContinentBiome.Class.Default
                || exported.Contains(surfaceClass)) continue;

            (DdsImage? image, string? key) = textures.ResolveNamed(surfaceName);
            byte[]? pixels = image?.Bgra;
            int width = image?.Width ?? 0;
            int height = image?.Height ?? 0;
            if (image == null)
            {
                var detail = textures.ResolveDetail(surfaceName);
                if (detail != null)
                {
                    pixels = detail.Value.Bgra;
                    width = detail.Value.Width;
                    height = detail.Value.Height;
                }
            }
            if (pixels == null)
            {
                Console.WriteLine($"skip {surfaceClass.ToString().ToLowerInvariant()} ({surfaceName}): no texture");
                continue;
            }

            string className = surfaceClass.ToString().ToLowerInvariant();
            string path = Path.Combine(outDir, className + ".png");
            File.WriteAllBytes(path, PngEncoder.EncodeBgra(pixels, width, height));
            Console.WriteLine($"{className}: {surfaceName} -> {key ?? surfaceName}, {width}x{height}, {path}");
            exported.Add(surfaceClass);
        }
        return exported.Count > 0 ? 0 : 2;
    }

    private static string? Arg(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static byte[] PackBabylonDetail(byte[] source)
    {
        var packed = new byte[source.Length];
        for (int offset = 0; offset < source.Length; offset += 4)
        {
            int blue = source[offset];
            int green = source[offset + 1];
            int red = source[offset + 2];
            byte luminance = (byte)((red * 54 + green * 183 + blue * 19 + 128) >> 8);
            packed[offset] = 128;     // B: neutral roughness
            packed[offset + 1] = 128; // G: neutral normal Y
            packed[offset + 2] = luminance; // R: diffuse detail
            packed[offset + 3] = 128; // A: neutral normal X
        }
        return packed;
    }

    private static byte[] ForceOpaque(byte[] source)
    {
        byte[] opaque = (byte[])source.Clone();
        for (int offset = 3; offset < opaque.Length; offset += 4) opaque[offset] = 255;
        return opaque;
    }
}
