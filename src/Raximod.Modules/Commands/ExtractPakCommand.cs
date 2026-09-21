using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raximod.EngineAssets.Archives;

namespace Raximod.Modules;

public static class ExtractPakCommand
{
    public static int Run(string[] args)
    {
        bool listOnly = args.Length == 2 && args[0] == "--list";
        bool oneEntry = args.Length == 4 && args[0] == "--extract";
        if (!listOnly && !oneEntry && args.Length != 2)
        {
            Console.Error.WriteLine("usage: ExtractPak <archive.pak> <output-directory>");
            Console.Error.WriteLine("       ExtractPak --list <archive.pak>");
            Console.Error.WriteLine("       ExtractPak --extract <archive.pak> <entry> <output-file>");
            return 1;
        }

        string archivePath = Path.GetFullPath(listOnly ? args[1] : oneEntry ? args[1] : args[0]);
        PakArchive archive = PakArchive.Load(File.ReadAllBytes(archivePath));

        if (listOnly)
        {
            foreach (PakEntry entry in archive.Entries)
            {
                Console.WriteLine($"{entry.Name}\t{entry.UncompressedSize}");
            }
            return 0;
        }

        if (oneEntry)
        {
            string outputPath = Path.GetFullPath(args[3]);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, archive.Extract(args[2]));
            return 0;
        }

        string outputDirectory = Path.GetFullPath(args[1]);

        Directory.CreateDirectory(outputDirectory);
        int failed = 0;
        foreach (PakEntry entry in archive.Entries)
        {
            string outputPath = Path.GetFullPath(Path.Combine(outputDirectory, entry.Name));
            string outputPrefix = outputDirectory.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!outputPath.StartsWith(outputPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Archive entry escapes output directory: {entry.Name}");
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllBytes(outputPath, archive.Extract(entry.Name));
                Console.WriteLine($"{entry.Name}\t{entry.UncompressedSize}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"Failed {entry.Name}: {ex.Message}");
            }
        }

        Console.Error.WriteLine($"Extracted {archive.Entries.Count - failed}/{archive.Entries.Count} entries to {outputDirectory}");
        return failed == 0 ? 0 : 2;
    }
}
