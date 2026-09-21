using System.Globalization;

namespace Raximod.Generation.Effects
{
    /// <summary>Lossless-enough reader for startup.pak-out/decals.lst and its material variants.</summary>
    public static class DecalCatalog
    {
        public sealed record Command(string Name, string[] Arguments);
        public sealed record Variant(string? Material, Command[] Commands, string[] Include, string[] Exclude);
        public sealed record Definition(string Name, Command[] Commands, Variant[] Variants);

        public static Definition[] Load(string planetSideDir)
        {
            string path = Path.Combine(planetSideDir, "startup.pak-out", "decals.lst");
            if (!File.Exists(path)) return Array.Empty<Definition>();
            var result = new List<Definition>();
            string? name = null;
            var commands = new List<Command>();
            foreach (string sourceLine in File.ReadLines(path))
            {
                string line = sourceLine.Split('#', 2)[0].Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                string command = parts[0].ToLowerInvariant();
                string[] arguments = parts.Skip(1).ToArray();
                if (command == "decal_begin")
                {
                    if (name != null) result.Add(Build(name, commands));
                    name = arguments.FirstOrDefault();
                    commands.Clear();
                }
                else if (name != null && command == "decal_end")
                {
                    result.Add(Build(name, commands));
                    name = null;
                    commands.Clear();
                }
                else if (name != null)
                {
                    commands.Add(new Command(command, arguments));
                }
            }
            if (name != null) result.Add(Build(name, commands));
            return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static Definition Build(string name, IReadOnlyList<Command> commands)
        {
            var defaults = new List<Command>();
            var variants = new List<List<Command>>();
            foreach (Command command in commands)
            {
                if (command.Name == "decal_material") variants.Add(new List<Command> { command });
                else if (variants.Count == 0) defaults.Add(command);
                else variants[^1].Add(command);
            }
            if (variants.Count == 0) variants.Add(new List<Command>());
            Variant[] resolved = variants.Select(entries =>
            {
                Command[] complete = defaults.Concat(entries).ToArray();
                return new Variant(
                    complete.LastOrDefault(command => command.Name == "decal_material")?.Arguments.FirstOrDefault(),
                    complete,
                    complete.Where(command => command.Name == "decal_include")
                        .SelectMany(command => command.Arguments).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    complete.Where(command => command.Name == "decal_exclude")
                        .SelectMany(command => command.Arguments).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }).ToArray();
            return new Definition(name, commands.ToArray(), resolved);
        }
    }
}
