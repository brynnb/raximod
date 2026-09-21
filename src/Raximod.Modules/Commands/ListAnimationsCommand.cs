using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Modules;

public static class ListAnimationsCommand
{
    private static readonly string[] Libraries =
    {
        "anims.ubr",
        "patch1/anim_patch1.ubr",
        "patch2/anim_patch2.ubr",
        "patch3/anim_patch3.ubr",
        "patch4/anim_patch4.ubr",
        "patch5/anim_patch5.ubr",
    };

    public static int Run(string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("usage: ListAnimations <PlanetSide-directory> [filter]");
            return 1;
        }

        string root = Path.GetFullPath(args[0]);
        string filter = args.Length == 2 ? args[1] : "";
        var records = new Dictionary<string, AnimRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in Libraries)
        {
            string path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue;
            foreach (AnimRecord record in AnimDb.Load(File.ReadAllBytes(path)).Records)
                records[record.Name] = record;
        }

        IEnumerable<AnimRecord> matches = records.Values
            .Where(record => filter.Length == 0
                || record.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (AnimRecord record in matches)
        {
            Console.WriteLine($"{record.Name}\t{record.Duration:0.###}\t{record.Tracks.Count}");
            count++;
        }
        Console.Error.WriteLine($"Listed {count} of {records.Count} animations.");
        return 0;
    }
}
