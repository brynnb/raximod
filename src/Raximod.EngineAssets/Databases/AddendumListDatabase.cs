using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace Raximod.EngineAssets.Databases;

/// <summary>Ordered retail asset amendments, separate from the original UBR rig.
/// Keep all commands; typed bone additions do not imply support for the other edits.</summary>
public sealed class AddendumListDatabase
{
    public sealed record Command(string? Package, string Name, string[] Arguments, string Source, int Line);
    public sealed record Bone(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("parent")] string Parent,
        [property: JsonPropertyName("position")] float[] Position,
        [property: JsonPropertyName("rotationFixedTurns")] int[] RotationFixedTurns,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("coordinateSystem")] string CoordinateSystem = "right-handed-z-up");

    public IReadOnlyList<Command> Commands { get; }
    private AddendumListDatabase(List<Command> commands) => Commands = commands;

    public static AddendumListDatabase Parse(string text, string source = "addendum.lst")
    {
        var commands = new List<Command>();
        string? package = null;
        int line = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is string value)
        {
            line++;
            string[] tokens = value.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            if (tokens[0] == "addendum_package")
            {
                if (tokens.Length != 2) throw new InvalidDataException($"{source}:{line}: invalid addendum_package");
                package = tokens[1];
            }
            var command = new Command(package, tokens[0], tokens[1..], source, line);
            if (command.Name == "addendum_bone") _ = ReadBone(command);
            commands.Add(command);
        }
        return new(commands);
    }

    public Bone[] Bones(string package) => Commands
        .Where(command => command.Name == "addendum_bone" && string.Equals(command.Package, package, StringComparison.OrdinalIgnoreCase))
        .Select(ReadBone).ToArray();

    private static Bone ReadBone(Command command)
    {
        string[] args = command.Arguments;
        if (command.Package is null || args.Length != 8)
            throw new InvalidDataException($"{command.Source}:{command.Line}: addendum_bone needs a package, two names, position and rotation");
        try
        {
            float[] position = args[2..5].Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            int[] rotation = args[5..8].Select(value => int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            if (position.Any(value => !float.IsFinite(value)) || rotation.Any(value => value is < 0 or > 0xffff))
                throw new FormatException("non-finite position or invalid fixed-turn word");
            return new(args[0], args[1], position, rotation, command.Source, command.Line);
        }
        catch (Exception error) when (error is FormatException or OverflowException)
        {
            throw new InvalidDataException($"{command.Source}:{command.Line}: invalid addendum_bone transform", error);
        }
    }
}
