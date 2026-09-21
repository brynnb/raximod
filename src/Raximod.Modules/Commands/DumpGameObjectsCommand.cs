using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Raximod.EngineAssets.Databases;

namespace Raximod.Modules;

public static class DumpGameObjectsCommand
{
    private enum ViewMode { Direct, Resolved, Raw }

    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Usage();
            return 1;
        }

        ViewMode mode = ViewMode.Direct;
        var positional = new List<string>();
        foreach (string argument in args)
        {
            switch (argument)
            {
                case "--direct": mode = ViewMode.Direct; break;
                case "--resolved": mode = ViewMode.Resolved; break;
                case "--raw": mode = ViewMode.Raw; break;
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"unknown option: {argument}");
                        Usage();
                        return 1;
                    }
                    positional.Add(argument);
                    break;
            }
        }
        if (positional.Count is < 1 or > 3)
        {
            Usage();
            return 1;
        }

        string inputPath = Path.GetFullPath(positional[0]);
        string? outputPath = positional.Count >= 2 ? Path.GetFullPath(positional[1]) : null;
        string filter = positional.Count >= 3 ? positional[2] : "";
        byte[] input = File.ReadAllBytes(inputPath);
        GameObjectDb database = GameObjectDb.Parse(input);
        byte[] encoded = database.Encode();
        if (!input.AsSpan().SequenceEqual(encoded))
            throw new InvalidDataException("Lossless game_objects.adb round-trip verification failed");

        var output = new StringBuilder();
        int count = mode == ViewMode.Raw
            ? AppendRaw(database, output, filter)
            : AppendObjects(
                mode == ViewMode.Resolved ? database.ResolvedObjects : database.DirectObjects,
                output,
                filter,
                includeInheritance: mode == ViewMode.Resolved);

        if (outputPath is null) Console.Write(output.ToString());
        else File.WriteAllText(outputPath, output.ToString(), Encoding.UTF8);
        Console.Error.WriteLine(
            $"Dumped {count} {mode.ToString().ToLowerInvariant()} records; "
            + $"lossless round-trip verified {encoded.Length:N0} bytes, "
            + $"{database.Commands.Count:N0} command nodes, "
            + $"{database.ParentLinks.Count:N0} parent commands.");
        return 0;
    }

    private static int AppendObjects(
        IReadOnlyList<GameObjectDb.GameObject> objects,
        StringBuilder output,
        string filter,
        bool includeInheritance)
    {
        int count = 0;
        foreach (GameObjectDb.GameObject gameObject in objects)
        {
            bool matches = filter.Length == 0
                || gameObject.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || gameObject.Type.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || gameObject.Properties.Any(property =>
                    property.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || property.Value.Any(value => value.Contains(filter, StringComparison.OrdinalIgnoreCase)));
            if (!matches) continue;

            count++;
            output.Append("# class_id ").Append(gameObject.ClassId)
                .Append(" type ").Append(gameObject.Type);
            if (includeInheritance)
            {
                output.Append(" parent ").Append(gameObject.ParentName ?? "<none>")
                    .Append(" chain ").Append(string.Join(" -> ", gameObject.InheritanceChain));
            }
            output.AppendLine();
            foreach ((string key, List<string> values) in gameObject.Properties)
            {
                output.Append("add_property ").Append(gameObject.Name).Append(' ').Append(key);
                foreach (string value in values) output.Append(' ').Append(value);
                output.AppendLine();
            }
            output.AppendLine();
        }
        return count;
    }

    private static int AppendRaw(GameObjectDb database, StringBuilder output, string filter)
    {
        int count = 0;
        foreach (GameObjectDb.NameIndexEntry entry in database.NameIndex)
        {
            if (filter.Length > 0 && !entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            output.Append("# name_index ").Append(entry.Index)
                .Append(" name_offset 0x").Append(entry.NameOffset.ToString("x8"))
                .Append(" record_offset 0x").Append(entry.RecordOffset.ToString("x8"))
                .Append(' ').AppendLine(entry.Name);
        }
        if (filter.Length == 0 || database.NameIndex.Any(entry =>
            entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))) output.AppendLine();
        foreach (GameObjectDb.RawCommand command in database.Commands)
        {
            if (command.IsSeparator)
            {
                if (filter.Length == 0) output.AppendLine();
                continue;
            }
            if (filter.Length > 0 && !command.Fields.Any(field =>
                field.Contains(filter, StringComparison.OrdinalIgnoreCase))) continue;
            count++;
            output.Append("# command_offset 0x").Append(command.StreamOffset.ToString("x8"))
                .Append(" symbols");
            foreach (uint symbolOffset in command.SymbolOffsets)
                output.Append(" 0x").Append(symbolOffset.ToString("x8"));
            output.AppendLine();
            output.AppendLine(string.Join(' ', command.Fields));
        }
        return count;
    }

    private static void Usage() => Console.Error.WriteLine(
        "usage: DumpGameObjects <game_objects.adb> [output.lst] [filter] "
        + "[--direct|--resolved|--raw]");
}
