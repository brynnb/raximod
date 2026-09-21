namespace Raximod
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                Usage(Console.Out);
                return 0;
            }

            try
            {
                return args switch
                {
                    ["export", "model", .. string[] rest] => ModelCommands.Export(rest),
                    ["export", "game", .. string[] rest] => GameExportCommand.Run(rest),
                    ["export", "players", .. string[] rest] => FamilyCommands.Players(rest),
                    ["export", "weapons", .. string[] rest] => FamilyCommands.Weapons(rest),
                    ["export", "vehicles", .. string[] rest] => FamilyCommands.Vehicles(rest),
                    ["export", "audio", .. string[] rest] => FamilyCommands.Audio(rest),
                    ["export", "native-catalogs", .. string[] rest] => FamilyCommands.NativeCatalogs(rest),
                    ["export", "world-assets", .. string[] rest] => FamilyCommands.WorldAssets(rest),
                    ["export", "environment", .. string[] rest] => FamilyCommands.Environment(rest),
                    ["export", "groundcover", .. string[] rest] => FamilyCommands.Groundcover(rest),
                    ["export", "outfit-decals", .. string[] rest] => FamilyCommands.OutfitDecals(rest),
                    ["export", "terrain-atlas", .. string[] rest] => FamilyCommands.TerrainAtlas(rest),
                    ["export", "texture", .. string[] rest] => FamilyCommands.Texture(rest),
                    ["export", "continent", .. string[] rest] => ExportCommands.Continent(rest),
                    ["export", "collision", .. string[] rest] => ExportCommands.Collision(rest),
                    ["export", "effects", .. string[] rest] => ExportCommands.Effects(rest),
                    ["export", "cloak", .. string[] rest] => ExportCommands.Cloak(rest),
                    ["export", "hart", .. string[] rest] => ExportCommands.Hart(rest),
                    ["inspect", "model", .. string[] rest] => ModelCommands.Inspect(rest),
                    ["inspect", "animations", .. string[] rest] => InspectCommands.Animations(rest),
                    ["inspect", "mesh-records", .. string[] rest] => InspectCommands.MeshRecords(rest),
                    ["inspect", "record", .. string[] rest] => InspectCommands.Record(rest),
                    ["inspect", "database", .. string[] rest] => InspectCommands.Database(rest),
                    ["inspect", "game-objects", .. string[] rest] => InspectCommands.GameObjects(rest),
                    ["archive", "extract", .. string[] rest] => InspectCommands.Archive(rest),
                    ["audit", "mesh-selection", .. string[] rest] => AuditCommands.MeshSelection(rest),
                    ["audit", "spatial", .. string[] rest] => AuditCommands.Spatial(rest),
                    ["audit", "extraction", .. string[] rest] => AuditCommands.Extraction(rest),
                    ["audit", "adb", .. string[] rest] => FamilyCommands.AuditAdb(rest),
                    ["package", "manifest", .. string[] rest] => PackageCommands.Manifest(rest),
                    ["package", "verify", .. string[] rest] => PackageCommands.Verify(rest),
                    ["package", "externalize-images", .. string[] rest] => PackageCommands.ExternalizeImages(rest),
                    ["package", "share-textures", .. string[] rest] => PackageCommands.ShareTextures(rest),
                    ["package", "reencode-png", .. string[] rest] => PackageCommands.ReencodePng(rest),
                    ["version"] => Version(),
                    _ => UnknownCommand(args),
                };
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("raximod: " + exception.Message);
                return 2;
            }
        }

        private static int Version()
        {
            string version = typeof(Program).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .SingleOrDefault()?.InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString()
                ?? "unknown";
            Console.WriteLine(version);
            return 0;
        }

        private static int UnknownCommand(string[] args)
        {
            Console.Error.WriteLine($"raximod: unknown command '{string.Join(' ', args.Take(2))}'");
            Usage(Console.Error);
            return 1;
        }

        private static void Usage(TextWriter output)
        {
            output.WriteLine("Raximod - PlanetSide asset extraction and packaging");
            output.WriteLine();
            output.WriteLine("Commands:");
            output.WriteLine("  raximod export model --source <PlanetSide> --record <name> --out <file.glb>");
            output.WriteLine("      [--profile standalone|shared] [--library <file.ubr>]");
            output.WriteLine("      [--shared-textures <directory>] [--no-textures] [--no-animations]");
            output.WriteLine("      [--max-animations <count>] [--receipt <file.json>]");
            output.WriteLine("      [--animation-prefix <prefix>]... [--mesh-material <material>]...");
            output.WriteLine("      [--materials-only | --render-states-only]");
            output.WriteLine("  raximod export game --source <PlanetSide> --psforever <repo> --out <bundle>");
            output.WriteLine("      [--config <raximod.json>] [--overwrite] [--plan] [--workers <count>]");
            output.WriteLine("      [--texture-stage <directory>] [--skip-cross-family-dedup] [--fail-on-warning]");
            output.WriteLine("  raximod export players|weapons|vehicles|audio|native-catalogs [options]");
            output.WriteLine("  raximod export world-assets|environment|groundcover|outfit-decals [options]");
            output.WriteLine("  raximod export terrain-atlas|texture [options]");
            output.WriteLine("  raximod inspect model --source <PlanetSide> --record <name>");
            output.WriteLine("  raximod inspect animations|mesh-records|record|database|game-objects [options]");
            output.WriteLine("  raximod archive extract --file <archive.pak> (--out <dir> | --list | --entry <name> --out <file>)");
            output.WriteLine("  raximod export continent --source <PlanetSide> --out <directory> [options]");
            output.WriteLine("  raximod export collision --source <PlanetSide> --models <directory> [--record <name>]");
            output.WriteLine("  raximod export effects --source <PlanetSide> --out <directory> [--assets-out <directory>]");
            output.WriteLine("  raximod export cloak|hart --source <PlanetSide> --out <directory>");
            output.WriteLine("  raximod audit adb|mesh-selection|spatial|extraction [options]");
            output.WriteLine("  raximod package manifest --root <bundle> [--out <bundle/manifest.json>]");
            output.WriteLine("  raximod package verify --root <bundle> [--manifest <manifest.json>]");
            output.WriteLine("  raximod package externalize-images|share-textures|reencode-png [options]");
            output.WriteLine("  raximod version");
            output.WriteLine();
            output.WriteLine("The standalone profile is the default and embeds textures plus every compatible animation.");
            output.WriteLine("The shared profile requires an explicit shared texture directory.");
        }
    }
}
