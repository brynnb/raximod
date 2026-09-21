using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;
using Raximod.Generation.Assets;

namespace Raximod.Generation.Effects
{
    /// <summary>Exports effects.adb as a reusable, lossless graph plus practical layer/link indexes.</summary>
    public static class EffectGraphExportTool
    {
        private static readonly HashSet<string> LinkCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "ef_effect", "ef_emitter", "ef_emitter_immediate", "ef_hit_effect", "ef_collision_death"
        };
        private static readonly HashSet<string> ExternalEffectHandles = new(StringComparer.OrdinalIgnoreCase)
        {
            // Authored attachment/script handles used by many headlight graphs. They are not child
            // records in effects.adb and must not be reported as broken graph edges.
            "headlight2", "headlight3", "headlight4"
        };
        private static readonly HashSet<string> ProceduralMeshes = new(StringComparer.OrdinalIgnoreCase)
        {
            "null", "sphere"
        };
        private static readonly IReadOnlyDictionary<string, string> EmbeddedMeshSources =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // The same hatch mesh also exists in vehicles_low.ubr. Effects always use the HQ
                // vehicle source, matching the rest of the no-distance-LOD export policy.
                ["vanguard_hatch_b"] = "patch1/patch1.ubr:vanguard"
            };

        public sealed record Options(string PlanetSideDir, string OutputDirectory, string? AssetOutputDirectory = null);
        public sealed record Result(int Effects, int Layers, int Links, int Materials, int Meshes,
            IReadOnlyList<string> MissingLinks, IReadOnlyList<string> MissingMeshes, int Cycles);
        public sealed record Command(string Name, IReadOnlyList<string> Arguments, int StreamOffset);
        public sealed record Link(string Command, string Target, IReadOnlyList<string> Arguments,
            string? AuthoredTarget = null, string? RepairReason = null);
        public sealed record Layer(string Name, Command[] Commands, string? Material, string? Mesh,
            string? AuthoredMesh = null, string? MeshRepairReason = null, string? MeshRepairKind = null);
        public sealed record Action(string Command, string Kind, string? Target, string? Delay,
            string? Duration, string? Scope, IReadOnlyList<string> Arguments);
        public sealed record Recovery(string GeometrySource, string PaletteSource, string Reason);
        public sealed record Effect(string Name, Command[] Commands, Layer[] Layers, Link[] Links,
            string[] Materials, string[] Meshes, Action[] Actions, NativeAdbProvenance Provenance,
            Recovery? Recovery = null);

        /// <summary>The shared source interpretation for full and dependency-scoped exports.</summary>
        public static Effect[] ReadEffects(string planetside)
        {
            byte[] source = File.ReadAllBytes(Path.Combine(planetside, "startup.pak-out", "effects.adb"));
            AdbSemanticDatabase database = AdbSemanticDatabase.Parse(source);

            Effect[] effects = database.Records
                .Select(record => ParseEffect(database, record))
                .ToArray();
            effects = RecoverMissingGraphs(effects)
                .OrderBy(effect => effect.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            effects = effects.Select(ResolveMeshes).ToArray();
            var names = effects.Select(effect => effect.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return effects.Select(effect => effect with
            {
                Links = effect.Links.Select(link =>
                {
                    EffectLinkResolver.Resolution resolution =
                        EffectLinkResolver.Resolve(link.Target, names);
                    return resolution.Repaired
                        ? link with
                        {
                            Target = resolution.Target,
                            AuthoredTarget = resolution.AuthoredTarget,
                            RepairReason = resolution.RepairReason
                        }
                        : link;
                }).ToArray()
            }).ToArray();
        }

        public static Result Run(Options options, IProgress<string>? log = null, CancellationToken ct = default)
        {
            string planetside = Path.GetFullPath(options.PlanetSideDir);
            string output = Path.GetFullPath(options.OutputDirectory);
            Directory.CreateDirectory(output);
            Effect[] effects = ReadEffects(planetside);
            DecalCatalog.Definition[] decals = DecalCatalog.Load(planetside);
            var names = effects.Select(effect => effect.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] missingLinks = effects.SelectMany(effect => effect.Links)
                .Select(link => link.Target)
                .Where(target => !names.Contains(target) && !ExternalEffectHandles.Contains(target))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] externalLinks = effects.SelectMany(effect => effect.Links)
                .Select(link => link.Target)
                .Where(ExternalEffectHandles.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            object[] repairedLinks = effects.SelectMany(effect => effect.Links)
                .Where(link => link.AuthoredTarget != null)
                .Select(link => (object)new
                {
                    authoredTarget = link.AuthoredTarget,
                    resolvedTarget = link.Target,
                    reason = link.RepairReason
                })
                .DistinctBy(value => JsonSerializer.Serialize(value))
                .ToArray();
            object[] recoveredGraphs = effects.Where(effect => effect.Recovery != null)
                .Select(effect => (object)new { effect.Name, effect.Recovery })
                .ToArray();
            object[] repairedMeshes = effects.SelectMany(effect => effect.Layers)
                .Where(layer => layer.AuthoredMesh != null)
                .Select(layer => (object)new
                {
                    authoredMesh = layer.AuthoredMesh,
                    resolvedMesh = layer.Mesh,
                    reason = layer.MeshRepairReason,
                    kind = layer.MeshRepairKind
                })
                .DistinctBy(value => JsonSerializer.Serialize(value))
                .ToArray();
            string[][] cycles = FindCycles(effects);
            string[] materials = effects.SelectMany(effect => effect.Materials)
                .Concat(decals.SelectMany(decal => decal.Variants)
                    .Select(variant => variant.Material)
                    .OfType<string>()
                    .Where(material => !material.Equals("none", StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] meshes = effects.SelectMany(effect => effect.Meshes)
                .Where(mesh => !ProceduralMeshes.Contains(mesh))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var textures = new TextureProvider(planetside);
            NativeMaterialManifestTool.Write(
                Path.Combine(output, "effects.glb"),
                "effects",
                materials.Select(name => new NativeMaterialManifestTool.Usage(name, 1, true, false)).ToArray(),
                textures);

            IReadOnlyList<string> missingMeshes = Array.Empty<string>();
            if (!string.IsNullOrWhiteSpace(options.AssetOutputDirectory))
            {
                GlbBatchExportTool.Result exported = GlbBatchExportTool.Run(new GlbBatchExportTool.Options(
                    planetside, options.AssetOutputDirectory, meshes,
                    SearchAllInstalledLibraries: true,
                    EmbeddedMeshSources: EmbeddedMeshSources), log, ct);
                missingMeshes = exported.Missing.Concat(exported.Failed.Select(failure => failure.Record))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            object[] commandCoverage = effects.SelectMany(effect => effect.Commands)
                .GroupBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    EffectCommandSemantics.Definition semantics = EffectCommandSemantics.For(group.Key);
                    return (object)new
                    {
                        command = group.Key,
                        occurrences = group.Count(),
                        semantics.Category,
                        semantics.RuntimeSupport
                    };
                })
                .OrderBy(value => JsonSerializer.Serialize(value), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] unclassifiedCommands = effects.SelectMany(effect => effect.Commands)
                .Select(command => command.Name)
                .Where(name => EffectCommandSemantics.For(name).Category == "unknown")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var document = new
            {
                format = "raxicore-effect-graph",
                version = 2,
                effects,
                decals,
                diagnostics = new
                {
                    missingLinks,
                    recoveredGraphs,
                    repairedLinks,
                    repairedMeshes,
                    externalLinks,
                    proceduralMeshes = ProceduralMeshes,
                    missingMeshes,
                    cycles,
                    commandCoverage,
                    unclassifiedCommands
                }
            };
            File.WriteAllText(Path.Combine(output, "effects.json"), JsonSerializer.Serialize(document,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            int layerCount = effects.Sum(effect => effect.Layers.Length);
            int linkCount = effects.Sum(effect => effect.Links.Length);
            log?.Report($"effects: {effects.Length} graphs, {layerCount} layers, {linkCount} links, " +
                        $"{decals.Length} decals, {unclassifiedCommands.Length} unclassified commands, " +
                        $"{materials.Length} materials, {meshes.Length} meshes, {missingLinks.Length} missing links, " +
                        $"{missingMeshes.Count} missing meshes, {cycles.Length} cycles");
            return new Result(effects.Length, layerCount, linkCount, materials.Length, meshes.Length,
                missingLinks, missingMeshes, cycles.Length);
        }

        private static IEnumerable<Effect> RecoverMissingGraphs(IReadOnlyCollection<Effect> source)
        {
            foreach (Effect effect in source) yield return effect;
            if (source.Any(effect => effect.Name.Equals("ef_flames_vs", StringComparison.OrdinalIgnoreCase)))
                yield break;
            Effect? geometry = source.FirstOrDefault(effect =>
                effect.Name.Equals("ef_flames_a", StringComparison.OrdinalIgnoreCase));
            Effect? palette = source.FirstOrDefault(effect =>
                effect.Name.Equals("ef_flames_a_big_vs", StringComparison.OrdinalIgnoreCase));
            if (geometry == null || palette == null) yield break;

            string[] paletteCommands = { "ef_color", "ef_color_ramp1", "ef_color_ramp2", "ef_color_ramp3" };
            var replacements = palette.Commands
                .Where(command => paletteCommands.Contains(command.Name, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(command => command.Name, command => command, StringComparer.OrdinalIgnoreCase);
            Command Replace(Command command) => replacements.TryGetValue(command.Name, out Command? replacement)
                ? replacement : command;
            yield return geometry with
            {
                Name = "ef_flames_vs",
                Commands = geometry.Commands.Select(Replace).ToArray(),
                Layers = geometry.Layers.Select(layer => layer with
                {
                    Commands = layer.Commands.Select(Replace).ToArray()
                }).ToArray(),
                Recovery = new Recovery(
                    geometry.Name,
                    palette.Name,
                    "The retail graph link is present but its definition is absent; the normal flame graph " +
                    "provides authored size/motion and the shipped VS big-flame variant provides its palette.")
            };
        }

        private static Effect ResolveMeshes(Effect effect)
        {
            Layer ResolveLayer(Layer layer)
            {
                if (string.IsNullOrWhiteSpace(layer.Mesh)) return layer;
                EffectMeshResolver.Resolution? resolution = EffectMeshResolver.Resolve(layer.Mesh);
                return resolution == null ? layer : layer with
                {
                    Mesh = resolution.ResolvedMesh,
                    AuthoredMesh = resolution.AuthoredMesh,
                    MeshRepairReason = resolution.Reason,
                    MeshRepairKind = resolution.Kind
                };
            }

            Layer[] layers = effect.Layers.Select(ResolveLayer).ToArray();
            return effect with
            {
                Layers = layers,
                Meshes = layers.Select(layer => layer.Mesh).OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        private static Effect ParseEffect(AdbSemanticDatabase database, AdbSemanticRecord source)
        {
            Command[] commands = source.Commands.Select(command => new Command(
                command.Name,
                command.Arguments.Select(value => value.Text ?? $"0x{value.Word:x8}").ToArray(),
                command.StreamOffset)).ToArray();
            var layers = new List<Layer>();
            var pending = new List<Command>();
            foreach (Command command in commands)
            {
                if (!command.Name.Equals("ef_layer", StringComparison.OrdinalIgnoreCase))
                {
                    pending.Add(command);
                    continue;
                }
                string layerName = command.Arguments.FirstOrDefault() ?? "null";
                layers.Add(new Layer(
                    layerName,
                    pending.ToArray(),
                    Argument(pending, "ef_material"),
                    Argument(pending, "ef_mesh")));
                pending.Clear();
            }
            Link[] links = commands.Where(command => LinkCommands.Contains(command.Name)
                                                     && command.Arguments.Count > 0)
                .Select(command => new Link(command.Name, command.Arguments[0], command.Arguments.Skip(1).ToArray()))
                .ToArray();
            Action[] actions = commands.Select(ParseAction).OfType<Action>().ToArray();
            return new Effect(
                source.Name,
                commands,
                layers.ToArray(),
                links,
                commands.Where(command => command.Name.Equals("ef_material", StringComparison.OrdinalIgnoreCase))
                    .Select(command => command.Arguments.FirstOrDefault()).OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                commands.Where(command => command.Name.Equals("ef_mesh", StringComparison.OrdinalIgnoreCase))
                    .Select(command => command.Arguments.FirstOrDefault()).OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                actions,
                NativeAdbValue.Provenance(database, source));
        }

        private static Action? ParseAction(Command command)
        {
            string[] args = command.Arguments.ToArray();
            return command.Name.ToLowerInvariant() switch
            {
                "ef_effect_on" => new Action(command.Name, "effect-on", At(args, 0), At(args, 1), At(args, 2), At(args, 3), args),
                "ef_effect_off" => new Action(command.Name, "effect-off", At(args, 0), At(args, 1), At(args, 2), At(args, 3), args),
                "ef_set_swap_package" => new Action(command.Name, "swap-set", At(args, 0), At(args, 1), null, At(args, 3), args),
                "ef_remove_swap_package" => new Action(command.Name, "swap-remove", At(args, 1), At(args, 0), null, At(args, 2), args),
                "ef_trigger_animation" => new Action(command.Name, "animation", At(args, 0), At(args, 1), At(args, 2), At(args, 3), args),
                "ef_decal" => new Action(command.Name, "decal", At(args, 0), null, null, null, args),
                "ef_light" => new Action(command.Name, "light", At(args, 0), null, At(args, 1), null, args),
                "ef_attached_light" => new Action(command.Name, "attached-light", At(args, 0), null, null, "object", args),
                "ef_change_light" => new Action(command.Name, "light-change", At(args, 0), At(args, 1), At(args, 2), At(args, 3), args),
                "ef_joltcamera" => new Action(command.Name, "camera-jolt", At(args, 0), null, At(args, 2), null, args),
                _ => null
            };
        }

        private static string? At(IReadOnlyList<string> values, int index) =>
            index >= 0 && index < values.Count ? values[index] : null;

        private static string? Argument(IEnumerable<Command> commands, string name) => commands.LastOrDefault(command =>
            command.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Arguments.FirstOrDefault();

        private static string[][] FindCycles(IEnumerable<Effect> effects)
        {
            var graph = effects.ToDictionary(effect => effect.Name,
                effect => effect.Links.Select(link => link.Target).ToArray(), StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new List<string>();
            var cycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string name)
            {
                if (active.Contains(name))
                {
                    int start = stack.FindIndex(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (start >= 0) cycles.Add(string.Join(" -> ", stack.Skip(start).Append(name)));
                    return;
                }
                if (!visited.Add(name) || !graph.TryGetValue(name, out string[]? targets)) return;
                active.Add(name);
                stack.Add(name);
                foreach (string target in targets) Visit(target);
                stack.RemoveAt(stack.Count - 1);
                active.Remove(name);
            }
            foreach (string name in graph.Keys) Visit(name);
            return cycles.Order(StringComparer.OrdinalIgnoreCase)
                .Select(cycle => cycle.Split(" -> ", StringSplitOptions.None)).ToArray();
        }
    }
}
