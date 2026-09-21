using System.Globalization;
using System.Text.Json;
using Raximod.EngineAssets.Archives;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;
using Raximod.Generation.Assets;

namespace Raximod.Generation.Continents
{
    /// <summary>Decodes groundcover.adb recipes and distribution.lst surface bindings without flattening their authored ranges.</summary>
    public static class GroundcoverCatalog
    {
        public sealed record WeightedReference(string Name, float Weight);
        public sealed record MeshReference(string Name, string Scale, int Unknown1, int Unknown2);
        public sealed record Recipe(
            string Name, string Density, string Texture, int CellCount,
            int[] Distribution, bool[] Cross, string[] Width, string[] Height,
            string? DetailMesh, string? DetailDensity, MeshReference[] Meshes,
            IReadOnlyList<AsciiCommandDatabase.Command> NativeCommands);
        public sealed record SurfaceDistribution(
            string Surface, WeightedReference[] Flora, WeightedReference[] Meshes);
        public sealed record AliasCorrection(string Kind, string Source, string Target, string Reason);
        public sealed record Result(
            Recipe[] Recipes, SurfaceDistribution[] Surfaces,
            string[] ActiveMissingTextures, string[] UnusedMissingTextures,
            string[] MissingRecipes, AliasCorrection[] AliasCorrections);

        private static readonly IReadOnlyDictionary<string, string> RecipeAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["jdesertwatch"] = "jdesertswatch",
                ["volcanicgroundcovercb"] = "volcanicgroundcoverc",
            };

        private static readonly IReadOnlyDictionary<string, string> TextureAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["articcultterf"] = "articclutterf",
            };

        public static Result Export(string planetside, string outDir, IProgress<string> log)
        {
            AsciiCommandDatabase? database = AsciiCommandDatabase.TryLoad(planetside, "groundcover.adb");
            if (database == null)
                throw new FileNotFoundException("Required startup database groundcover.adb was not found");
            Recipe[] recipes = database.Records
                .Select(pair => ParseRecipe(pair.Key, pair.Value))
                .OrderBy(recipe => recipe.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var aliasCorrections = new List<AliasCorrection>();
            SurfaceDistribution[] surfaces = ApplyRecipeAliases(
                ParseDistributions(ReadStartupText(planetside, "distribution.lst")), aliasCorrections);
            var recipeNames = recipes.Select(recipe => recipe.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] missingRecipes = surfaces
                .SelectMany(surface => surface.Flora.Concat(surface.Meshes))
                .Select(reference => reference.Name)
                .Where(name => !recipeNames.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string textureDir = Path.Combine(outDir, "groundcover-textures");
            Directory.CreateDirectory(textureDir);
            var textures = new TextureProvider(planetside);
            var activeRecipes = surfaces
                .SelectMany(surface => surface.Flora.Concat(surface.Meshes))
                .Select(reference => reference.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var activeMissingTextures = new List<string>();
            var unusedMissingTextures = new List<string>();
            var textureUris = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (IGrouping<string, Recipe> textureGroup in recipes
                         .Where(recipe => recipe.Texture.Length > 0)
                         .GroupBy(recipe => recipe.Texture, StringComparer.OrdinalIgnoreCase))
            {
                string sourceTexture = textureGroup.Key;
                string textureName = TextureAliases.TryGetValue(sourceTexture, out string? alias)
                    ? alias : sourceTexture;
                if (!sourceTexture.Equals(textureName, StringComparison.OrdinalIgnoreCase))
                    aliasCorrections.Add(new AliasCorrection("texture", sourceTexture, textureName,
                        "groundcover.adb spelling differs from the texture archive"));
                DdsImage? image = textures.Get(textureName);
                if (image == null)
                {
                    (textureGroup.Any(recipe => activeRecipes.Contains(recipe.Name))
                        ? activeMissingTextures : unusedMissingTextures).Add(sourceTexture);
                    continue;
                }
                string filename = SafeFilename(sourceTexture) + ".png";
                File.WriteAllBytes(
                    Path.Combine(textureDir, filename),
                    PngEncoder.EncodeBgra(image.Bgra, image.Width, image.Height));
                textureUris[sourceTexture] = "groundcover-textures/" + filename;
            }

            var document = new
            {
                format = "raxicore-groundcover-catalog",
                version = 4,
                provenance = new { recipes = "startup.pak/groundcover.adb", distributions = "startup.pak/distribution.lst" },
                // Executable-backed spatial selection/quality, not per-cell independent lottery.
                // Keep native settings separate from browser draw-distance and batching policy.
                placement = new { cellSize = 10, densityDivisor = 256, defaultDetailFlora = 500,
                    qualityTiers = new[] {
                        new { name = "off", densityScale = 0, floraVertexLimit = 0 },
                        new { name = "low", densityScale = 85, floraVertexLimit = 400 },
                        new { name = "medium", densityScale = 170, floraVertexLimit = 800 },
                        new { name = "high", densityScale = 256, floraVertexLimit = 1600 } },
                    recipeSelection = new {
                        algorithm = "native-gradient-noise-v1", cellScale = (double)(1f / 800f), octaves = 6,
                        flora = new { seed = 0, frequency = 12 }, mesh = new { seed = 43, frequency = 8 } },
                    source = "planetside.exe", sha256 = "7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a",
                    evidence = new[] { "0xc9acbc", "0x86b3f0", "0x86b8e3-0x86b968", "0x86c380-0x86c3de", "0x86d043-0x86d061",
                        "0x8538aa-0x853903", "0x86c2b9-0x86c3de", "0x86ee76-0x86eed8", "0x8700e0-0x87021c",
                        "0x8704e0-0x8705bd", "0xadd290-0xadd6b4", "0x4026bf-0x4026c9" } },
                surfaceMaps = GroundcoverSurfaceExport.Export(planetside, outDir, log),
                textureUris,
                recipes,
                surfaces,
                diagnostics = new
                {
                    aliasCorrections,
                    activeMissingTextures = activeMissingTextures.Order(StringComparer.OrdinalIgnoreCase),
                    unusedMissingTextures = unusedMissingTextures.Order(StringComparer.OrdinalIgnoreCase),
                    missingRecipes
                }
            };
            File.WriteAllText(
                Path.Combine(outDir, "groundcover.json"),
                JsonSerializer.Serialize(document, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
            log.Report($"groundcover: {recipes.Length} recipes, {surfaces.Length} surface distributions, " +
                       $"{activeMissingTextures.Count} active missing textures, " +
                       $"{unusedMissingTextures.Count} unused missing textures, " +
                       $"{missingRecipes.Length} unresolved active recipe links, " +
                       $"{aliasCorrections.Count} evidence-backed aliases");
            return new Result(recipes, surfaces, activeMissingTextures.ToArray(),
                unusedMissingTextures.ToArray(), missingRecipes, aliasCorrections.ToArray());
        }

        private static SurfaceDistribution[] ApplyRecipeAliases(
            IEnumerable<SurfaceDistribution> surfaces, ICollection<AliasCorrection> corrections)
        {
            WeightedReference Resolve(WeightedReference reference)
            {
                if (!RecipeAliases.TryGetValue(reference.Name, out string? target)) return reference;
                string reason = reference.Name.Equals("volcanicgroundcovercb", StringComparison.OrdinalIgnoreCase)
                    ? "distribution.lst appends a stray 'b'; the exact volcanicgroundcoverc recipe and texture ship, while no cb record or texture exists"
                    : "distribution.lst spelling differs from groundcover.adb";
                corrections.Add(new AliasCorrection("recipe", reference.Name, target, reason));
                return reference with { Name = target };
            }
            return surfaces.Select(surface => surface with
            {
                Flora = surface.Flora.Select(Resolve).ToArray(),
                Meshes = surface.Meshes.Select(Resolve).ToArray(),
            }).ToArray();
        }

        private static Recipe ParseRecipe(string name, IReadOnlyList<AsciiCommandDatabase.Command> commands)
        {
            var properties = commands
                .Where(command => command.Name.Equals("add_property", StringComparison.OrdinalIgnoreCase)
                                  && command.Arguments.Count >= 3)
                .GroupBy(command => command.Arguments[1], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Arguments.Skip(2).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            string[] Property(string key) => properties.TryGetValue(key, out string[]? values)
                ? values : Array.Empty<string>();
            string Scalar(string key, string fallback = "") => Property(key).FirstOrDefault() ?? fallback;
            int.TryParse(Scalar("cellcount", "1"), out int cellCount);
            int[] distribution = Property("distribution").Select(ParseInt).ToArray();
            bool[] cross = Property("cross").Select(value => ParseInt(value) != 0).ToArray();
            var meshes = properties
                .Where(pair => pair.Key.StartsWith("mesh", StringComparison.OrdinalIgnoreCase)
                               && int.TryParse(pair.Key.AsSpan(4), out _)
                               && pair.Value.Length > 0)
                .OrderBy(pair => int.Parse(pair.Key.AsSpan(4), CultureInfo.InvariantCulture))
                .Select(pair => new MeshReference(
                    pair.Value[0],
                    pair.Value.ElementAtOrDefault(1) ?? "1",
                    ParseInt(pair.Value.ElementAtOrDefault(2)),
                    ParseInt(pair.Value.ElementAtOrDefault(3))))
                .ToArray();
            string[] detail = Property("detail_mesh");
            return new Recipe(
                name,
                Scalar("density", "0"),
                Scalar("texture", name),
                Math.Max(1, cellCount),
                distribution,
                cross,
                Property("width"),
                Property("height"),
                detail.ElementAtOrDefault(0),
                detail.ElementAtOrDefault(1),
                meshes,
                commands);
        }

        private static SurfaceDistribution[] ParseDistributions(string text)
        {
            var result = new List<SurfaceDistribution>();
            string? current = null;
            var flora = new List<WeightedReference>();
            var meshes = new List<WeightedReference>();
            void Flush()
            {
                if (current != null)
                    result.Add(new SurfaceDistribution(current, flora.ToArray(), meshes.ToArray()));
                current = null;
                flora.Clear();
                meshes.Clear();
            }
            foreach (string sourceLine in text.Replace("\r", "").Split('\n'))
            {
                string line = sourceLine.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                string[] words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 2 && words[0].Equals("df_begin", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    current = words[1];
                }
                else if (words[0].Equals("df_end", StringComparison.OrdinalIgnoreCase)) Flush();
                else if (current != null && words.Length >= 3
                         && (words[0].Equals("df_flora", StringComparison.OrdinalIgnoreCase)
                             || words[0].Equals("df_mesh", StringComparison.OrdinalIgnoreCase)))
                {
                    var reference = new WeightedReference(words[1], ParseFloat(words[2], 1));
                    (words[0].Equals("df_flora", StringComparison.OrdinalIgnoreCase) ? flora : meshes).Add(reference);
                }
            }
            Flush();
            return result.ToArray();
        }

        private static string ReadStartupText(string planetside, string entryName)
        {
            string extracted = Path.Combine(planetside, "startup.pak-out", entryName);
            if (File.Exists(extracted)) return File.ReadAllText(extracted);
            string pakPath = Path.Combine(planetside, "startup.pak");
            if (!File.Exists(pakPath)) return "";
            try
            {
                PakArchive pak = PakArchive.Load(File.ReadAllBytes(pakPath));
                int index = pak.IndexOf(entryName);
                return index < 0 ? "" : System.Text.Encoding.Latin1.GetString(pak.Extract(index));
            }
            catch { return ""; }
        }

        private static int ParseInt(string? value) => int.TryParse(value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int result) ? result : 0;
        private static float ParseFloat(string? value, float fallback = 0) => float.TryParse(value,
            NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ? result : fallback;
        private static string SafeFilename(string name) => string.Concat(name.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_'));
    }
}
