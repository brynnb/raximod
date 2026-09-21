using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raximod.EngineAssets.Databases;

namespace Raximod.Modules;

public static class InspectAsciiDatabaseCommand
{
    public static int Run(string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("usage: <database.adb> [record-name-or-substring]");
            return 1;
        }

        AsciiCommandDatabase? database = AsciiCommandDatabase.Parse(File.ReadAllBytes(args[0]));
        if (database == null)
        {
            Console.Error.WriteLine("database could not be decoded");
            return 2;
        }

        string filter = args.Length == 2 ? args[1] : "";
        Console.WriteLine($"records={database.Count}");
        foreach ((string name, IReadOnlyList<AsciiCommandDatabase.Command> commands) in database.Records
                     .Where(pair => pair.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(name);
            foreach (AsciiCommandDatabase.Command command in commands)
            {
                Console.WriteLine($"  {command.Name} {string.Join(" ", command.Arguments)}".TrimEnd());
            }
        }
        return 0;
    }
}
