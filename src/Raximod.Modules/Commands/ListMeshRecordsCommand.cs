using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raximod.Generation.Assets;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Modules;

public static class ListMeshRecordsCommand
{
    public static int Run(string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("usage: <PlanetSideDir> [name-filter]");
            return 1;
        }

        string root = Path.GetFullPath(args[0]);
        string filter = args.Length == 2 ? args[1] : "";
        bool collisionsOnly = filter.Equals("--collisions", StringComparison.OrdinalIgnoreCase);
        string nameFilter = collisionsOnly ? "" : filter;
        string[] libraries =
        [
            "uber.ubr",
            "patch1/patch1.ubr",
            "patch2/patch2.ubr",
            "patch3/patch3.ubr",
            "patch4/patch4.ubr",
            "patch5/patch5.ubr",
            "expansion1/expansion1.ubr",
        ];

        foreach (string relative in libraries)
        {
            string path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue;
            UberModel model = UberModel.Load(File.ReadAllBytes(path));
            foreach (string record in GlbExportTool.ListRecords(path)
                         .Where(name => name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                if (collisionsOnly)
                {
                    UberModel.MeshSystem? system = model.FetchMeshSystem(record);
                    if (system?.Collisions == null || system.Collisions.Parts.Count == 0) continue;
                    Console.WriteLine($"{relative}\t{record}\t{system.Collisions.Name}\t" +
                        string.Join(',', system.Collisions.Parts.Select(part => $"{part.Name}:{(uint)part.Type}:{part.Vertices.Length}")));
                    continue;
                }
                Console.WriteLine($"{relative}\t{record}");
            }
        }
        return 0;
    }
}
