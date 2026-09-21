using Raximod.Modules;

namespace Raximod;

internal static class InspectCommands
{
    public static int Animations(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var forwarded = new List<string> { args.Required("--source") };
        string? filter = args.Value("--filter");
        if (filter is not null) forwarded.Add(filter);
        return ListAnimationsCommand.Run(forwarded.ToArray());
    }

    public static int MeshRecords(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var forwarded = new List<string> { args.Required("--source") };
        if (args.Has("--collisions")) forwarded.Add("--collisions");
        else if (args.Value("--filter") is string filter) forwarded.Add(filter);
        return ListMeshRecordsCommand.Run(forwarded.ToArray());
    }

    public static int Record(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        return ListRecordsCommand.Run([
            args.Required("--source"), "detail", args.Required("--record")]);
    }

    public static int Database(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var forwarded = new List<string> { args.Required("--file") };
        string? filter = args.Value("--filter");
        if (filter is not null) forwarded.Add(filter);
        return InspectAsciiDatabaseCommand.Run(forwarded.ToArray());
    }

    public static int GameObjects(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var forwarded = new List<string> { args.Required("--file") };
        if (args.Has("--raw")) forwarded.Add("--raw");
        else if (args.Has("--resolved")) forwarded.Add("--resolved");
        else forwarded.Add("--direct");
        string? output = args.Value("--out");
        string? filter = args.Value("--filter");
        if (output is not null) forwarded.Add(output);
        if (filter is not null)
        {
            if (output is null)
                throw new ArgumentException("--filter requires --out for the game-objects inspector.");
            forwarded.Add(filter);
        }
        return DumpGameObjectsCommand.Run(forwarded.ToArray());
    }

    public static int Archive(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        string archive = args.Required("--file");
        if (args.Has("--list")) return ExtractPakCommand.Run(["--list", archive]);
        string? entry = args.Value("--entry");
        if (entry is not null)
            return ExtractPakCommand.Run(["--extract", archive, entry, args.Required("--out")]);
        return ExtractPakCommand.Run([archive, args.Required("--out")]);
    }
}
