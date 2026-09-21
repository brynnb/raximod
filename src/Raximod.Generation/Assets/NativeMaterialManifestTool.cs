using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Textures;

namespace Raximod.Generation.Assets
{
    /// <summary>
    /// Writes the native material state that glTF metallic/roughness cannot express. Referenced source
    /// textures are emitted once into a shared folder so Babylon shader adapters can reconstruct stages,
    /// lightmaps, detail layers, and animated/effect materials without scraping GLB internals.
    /// </summary>
    internal static class NativeMaterialManifestTool
    {
        internal readonly record struct Usage(string Name, int Sections, bool HasUv0, bool HasUv1);

        public static void Write(
            string glbPath,
            string record,
            IReadOnlyCollection<Usage> usages,
            TextureProvider textures,
            string? sharedTextureDirectory = null)
        {
            var document = BuildDocument(Path.GetDirectoryName(glbPath)!, record, usages, textures, sharedTextureDirectory);
            string path = Path.ChangeExtension(glbPath, ".materials.json");
            string json = JsonSerializer.Serialize(document);
            if (!File.Exists(path) || File.ReadAllText(path) != json) File.WriteAllText(path, json);
        }

        internal static object BuildDocument(
            string assetDirectory,
            string record,
            IReadOnlyCollection<Usage> usages,
            TextureProvider textures,
            string? sharedTextureDirectory = null,
            bool includePackageBindings = true)
        {
            string textureDirectory = string.IsNullOrWhiteSpace(sharedTextureDirectory)
                ? Path.Combine(assetDirectory, "material-textures")
                : Path.GetFullPath(sharedTextureDirectory);
            var textureUris = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var missingTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var bindings = includePackageBindings ? NativeMaterialFactionBindings.Load(record, textures) : null;
            var materialUsages = usages.ToDictionary(usage => usage.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var swap in bindings?.MaterialSwaps ?? [])
            {
                // State scopes (HART lamps, for example) are authored replacements
                // too. Export their complete definitions, not just empire skins.
                if (!materialUsages.TryGetValue(swap.Source, out var source)) continue;
                if (swap.Source.Equals(swap.Replacement, StringComparison.OrdinalIgnoreCase)) continue;
                if (textures.MaterialCommands?.Lookup(swap.Replacement) is not { Count: > 0 })
                {
                    // Native packages themselves contain dangling material references. Keep
                    // the unresolved binding explicit; never guess a similarly named skin.
                    Console.Error.WriteLine($"warning: {record}.{swap.Scope}: missing native material {swap.Replacement}");
                    continue;
                }
                var replacement = source with { Name = swap.Replacement, Sections = 0 };
                if (materialUsages.TryGetValue(swap.Replacement, out var previous))
                    replacement = replacement with { HasUv0 = previous.HasUv0 || source.HasUv0,
                        HasUv1 = previous.HasUv1 || source.HasUv1, Sections = previous.Sections };
                materialUsages[swap.Replacement] = replacement;
            }
            object[] materials = materialUsages.Values.OrderBy(usage => usage.Name, StringComparer.OrdinalIgnoreCase)
                .Select(usage => BuildMaterial(
                    usage,
                    textures,
                    assetDirectory,
                    textureDirectory,
                    textureUris,
                    missingTextures,
                    inferTextureVariants: includePackageBindings && bindings == null))
                .ToArray();
            var document = new
            {
                format = "raxicore-native-materials",
                version = 1,
                record,
                coordinateSystem = "right-handed-y-up",
                materials,
                stateBindings = bindings == null ? null : new
                {
                    package = bindings.Name,
                    sourceDatabase = "epackage.adb",
                    swaps = bindings.MaterialSwaps.Where(swap => !NativeMaterialFactionBindings.IsEmpireScope(swap.Scope)
                        && usages.Any(usage => usage.Name.Equals(swap.Source, StringComparison.OrdinalIgnoreCase)))
                        .Select(swap => new { scope = swap.Scope, source = swap.Source,
                            replacement = swap.Replacement, streamOffset = swap.StreamOffset,
                            resolved = materialUsages.ContainsKey(swap.Replacement) }).ToArray()
                },
                factionBindings = bindings == null ? null : new
                {
                    package = bindings.Name,
                    provenance = new { sourceDatabase = "epackage.adb", section = bindings.Provenance.Section,
                        streamStart = bindings.Provenance.StreamStart, streamEnd = bindings.Provenance.StreamEnd,
                        nameIndex = bindings.Provenance.NameIndex },
                    swaps = bindings.MaterialSwaps.Where(swap => NativeMaterialFactionBindings.IsEmpireScope(swap.Scope)
                        && usages.Any(usage => usage.Name.Equals(swap.Source, StringComparison.OrdinalIgnoreCase)))
                        .Select(swap => new { scope = swap.Scope.ToLowerInvariant(), source = swap.Source,
                            replacement = swap.Replacement, streamOffset = swap.StreamOffset,
                            resolved = textures.MaterialCommands?.Lookup(swap.Replacement) is { Count: > 0 } }).ToArray(),
                    hiddenParts = bindings.HiddenParts.Where(part => part.SwapScope == null
                        || NativeMaterialFactionBindings.IsEmpireScope(part.SwapScope))
                        .Select(part => new { part = part.Part, scope = part.SwapScope?.ToLowerInvariant(),
                            streamOffset = part.StreamOffset }).ToArray()
                },
                missingTextures = missingTextures.Order(StringComparer.OrdinalIgnoreCase).ToArray()
            };
            return document;
        }

        private static object BuildMaterial(
            Usage usage,
            TextureProvider textures,
            string assetDirectory,
            string textureDirectory,
            Dictionary<string, string> textureUris,
            HashSet<string> missingTextures,
            bool inferTextureVariants)
        {
            (string baseRecord, string? lightmapRecord) = Split(usage.Name);
            IReadOnlyList<AsciiCommandDatabase.Command> sectionCommands =
                textures.MaterialCommands?.Lookup(usage.Name) ?? Array.Empty<AsciiCommandDatabase.Command>();
            IReadOnlyList<AsciiCommandDatabase.Command> baseCommands =
                textures.MaterialCommands?.Lookup(baseRecord) ?? Array.Empty<AsciiCommandDatabase.Command>();
            var stageSources = sectionCommands.Count > 0 ? sectionCommands : baseCommands;
            var initialStages = NativeMaterialInitialization.UnconfiguredStages(usage.Name, stageSources);
            if (initialStages.Count > 0) stageSources = initialStages;
            var stages = new List<object>();
            for (int slot = 1; slot <= 8; slot++)
            {
                string? texture = Argument(stageSources, $"mat_texture{slot}");
                string? animation = Argument(stageSources, $"mat_anim{slot}");
                string? program = Argument(stageSources, $"mat_stage{slot}");
                if (texture == null && animation == null && program == null) continue;
                NativeTextureSourceResolver.Resolution? textureResolution = texture == null
                    ? null : NativeTextureSourceResolver.Resolve(textures, texture);
                string? textureUri = texture == null ? null : ExportTexture(
                    texture,
                    textures,
                    assetDirectory,
                    textureDirectory,
                    textureUris,
                    missingTextures);
                IReadOnlyDictionary<string, object> empireVariants = texture == null || !inferTextureVariants
                    ? new Dictionary<string, object>()
                    : ExportEmpireVariants(
                        texture,
                        textures,
                        assetDirectory,
                        textureDirectory,
                        textureUris,
                        missingTextures);
                object? animationDefinition = animation == null ? null : BuildAnimation(
                    animation,
                    textures,
                    assetDirectory,
                    textureDirectory,
                    textureUris,
                    missingTextures);
                NativeStageProgramResolver.Resolution stageProgram =
                    NativeStageProgramResolver.Resolve(textures.StageCommands, program);
                stages.Add(new
                {
                    slot,
                    texture,
                    resolvedTexture = textureResolution?.ResolvedKey,
                    textureUri,
                    sourceAvailability = textureUri == null && textureResolution?.MissingSource is { } absent ? new
                    {
                        status = "absent",
                        lookup = texture + ".dds",
                        scope = "installed-flat-pack-and-loose-dds",
                        archiveCount = absent.ArchiveCount,
                        looseFileCount = absent.LooseFileCount,
                        indexSha256 = absent.IndexSha256,
                        numericDataPacks = absent.NumericDataPacks
                    } : null,
                    textureRepair = textureResolution?.Repaired == true ? new
                    {
                        authoredTexture = textureResolution.AuthoredKey,
                        resolvedTexture = textureResolution.ResolvedKey,
                        reason = textureResolution.RepairReason,
                        sourceArchive = textureResolution.SourceArchive
                    } : null,
                    textureKind = texture != null && IsCubeTexture(texture, textures) ? "cube" : "2d",
                    empireVariants,
                    animation,
                    animationCommands = Commands(textures.AnimationCommands?.Lookup(animation ?? "")),
                    animationDefinition,
                    program,
                    resolvedProgram = stageProgram.ResolvedProgram,
                    programRepair = stageProgram.Repaired ? new
                    {
                        authoredProgram = stageProgram.AuthoredProgram,
                        resolvedProgram = stageProgram.ResolvedProgram,
                        reason = stageProgram.RepairReason
                    } : null,
                    commands = Commands(stageProgram.Commands)
                });
            }

            string? detail = Argument(sectionCommands, "mat_detail")
                ?? Argument(baseCommands, "mat_detail");
            float detailTileRate = Number(
                sectionCommands.Count > 0 ? sectionCommands : baseCommands,
                "mat_tilerate",
                1f);
            if (textures.Materials?.TryGetDetail(baseRecord, out string? parsedDetail, out float parsedTileRate) == true)
            {
                detail ??= parsedDetail;
                if (Math.Abs(detailTileRate - 1f) < 0.0001f) detailTileRate = parsedTileRate;
            }
            string? detailUri = detail == null ? null : ExportTexture(
                detail,
                textures,
                assetDirectory,
                textureDirectory,
                textureUris,
                missingTextures);
            string? lightmapTexture = lightmapRecord == null
                ? null
                : Argument(sectionCommands, "mat_texture1") ?? lightmapRecord;
            // A +null suffix means no authored lightmap. That naming convention
            // is separate from an explicit mat_textureN null: the latter binds
            // the real, named null.dds through the native texture resource loader.
            string? lightmapUri = usage.HasUv1 && lightmapTexture != null
                && !string.Equals(lightmapRecord, "null", StringComparison.OrdinalIgnoreCase)
                ? ExportTexture(
                    lightmapTexture,
                    textures,
                    assetDirectory,
                    textureDirectory,
                    textureUris,
                    missingTextures)
                : null;
            return new
            {
                name = usage.Name,
                sections = usage.Sections,
                hasUv0 = usage.HasUv0,
                hasUv1 = usage.HasUv1,
                baseRecord,
                lightmapRecord,
                lightmapTexture,
                lightmapUri,
                detailTexture = detail,
                detailUri,
                detailTileRate,
                sectionCommands = Commands(sectionCommands),
                baseCommands = Commands(baseCommands),
                sectionRenderStates = RenderStates(sectionCommands, textures),
                baseRenderStates = RenderStates(baseCommands, textures),
                stageInitialization = initialStages.Count == 0 ? null : new
                {
                    source = "native-material-constructor",
                    executableSha256 = NativeMaterialInitialization.ExecutableSha256,
                    entryPoint = "0x9bd100",
                    rule = "unconfigured-ordinary-material",
                    materialRecordPresent = sectionCommands.Count > 0 || baseCommands.Count > 0,
                    commands = Commands(initialStages)
                },
                stages
            };
        }

        internal static object[] RenderStates(
            IReadOnlyList<AsciiCommandDatabase.Command> materialCommands, TextureProvider textures) =>
            materialCommands.Where(command => command.Name.Equals("mat_state", StringComparison.OrdinalIgnoreCase))
                .Select(command =>
                {
                    if (command.Arguments.Count != 1 || string.IsNullOrWhiteSpace(command.Arguments[0]))
                        throw new InvalidDataException("mat_state requires exactly one renderstate.adb record name");
                    string name = command.Arguments[0];
                    var commands = textures.RenderStateCommands?.Lookup(name);
                    // Preserve ordered applications and unresolved references. A name
                    // such as cs9_ef_sun is not itself a blend-mode specification.
                    // Section and base views stay separate, just like stage sources.
                    return (object)new
                    {
                        name,
                        sourceDatabase = "renderstate.adb",
                        resolved = commands != null,
                        commands = Commands(commands)
                    };
                }).ToArray();

        private static object BuildAnimation(
            string name,
            TextureProvider textures,
            string assetDirectory,
            string textureDirectory,
            Dictionary<string, string> textureUris,
            HashSet<string> missingTextures)
        {
            if (name.Equals("static", StringComparison.OrdinalIgnoreCase)) return new
            {
                name,
                frameCount = 0,
                columns = 1,
                rows = 1,
                framesPerSecond = 0f,
                loop = false,
                frames = Array.Empty<object>()
            };
            IReadOnlyList<AsciiCommandDatabase.Command> commands =
                textures.AnimationCommands?.Lookup(name) ?? Array.Empty<AsciiCommandDatabase.Command>();
            IReadOnlyList<string>? arguments = commands.FirstOrDefault(command =>
                command.Name.Equals("anc_anim", StringComparison.OrdinalIgnoreCase)).Arguments;
            int frameCount = Integer(arguments, 0, 1);
            int columns = Math.Max(1, Integer(arguments, 1, 1));
            int rows = Math.Max(1, Integer(arguments, 2, 1));
            float framesPerSecond = Float(arguments, 3, 1f);
            bool loop = !bool.TryParse(arguments?.ElementAtOrDefault(4), out bool parsedLoop) || parsedLoop;
            int pages = Math.Max(1, (int)Math.Ceiling(frameCount / (double)(columns * rows)));
            var frames = new List<object>();
            for (int page = 1; page <= pages; page++)
            {
                string texture = name + page.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
                NativeTextureSourceResolver.Resolution resolution =
                    NativeTextureSourceResolver.Resolve(textures, texture);
                string? uri = ExportTexture(
                    texture,
                    textures,
                    assetDirectory,
                    textureDirectory,
                    textureUris,
                    missingTextures);
                if (uri != null) frames.Add(new
                {
                    texture,
                    resolvedTexture = resolution.ResolvedKey,
                    uri,
                    textureRepair = resolution.Repaired ? new
                    {
                        authoredTexture = resolution.AuthoredKey,
                        resolvedTexture = resolution.ResolvedKey,
                        reason = resolution.RepairReason,
                        sourceArchive = resolution.SourceArchive
                    } : null
                });
            }
            return new
            {
                name,
                frameCount,
                columns,
                rows,
                framesPerSecond,
                loop,
                frames
            };
        }

        private static int Integer(IReadOnlyList<string>? values, int index, int fallback) =>
            int.TryParse(values?.ElementAtOrDefault(index), out int value) ? value : fallback;

        private static float Float(IReadOnlyList<string>? values, int index, float fallback) =>
            float.TryParse(
                values?.ElementAtOrDefault(index),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float value) ? value : fallback;

        private static object[] Commands(IReadOnlyList<AsciiCommandDatabase.Command>? commands) =>
            commands?.Select(command => (object)new
            {
                name = command.Name,
                arguments = command.Arguments
            }).ToArray() ?? Array.Empty<object>();

        private static string? Argument(
            IReadOnlyList<AsciiCommandDatabase.Command> commands,
            string name) => commands.FirstOrDefault(command =>
                command.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Arguments?.FirstOrDefault();

        private static float Number(
            IReadOnlyList<AsciiCommandDatabase.Command> commands,
            string name,
            float fallback) => float.TryParse(
                Argument(commands, name),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float value) ? value : fallback;

        private static (string Base, string? Lightmap) Split(string material)
        {
            // Profile selection appends a material capability suffix, not a texture
            // suffix. In particular, +null_gf3 is still no lightmap and
            // +fadegridsides_gf3 still names fadegridsides. Retain the full name for
            // command lookup above; retail also strips _gf3 in 0x9bd601-0x9bd62f.
            material = NativeMaterialProfiles.UnsuffixedName(material);
            int plus = material.LastIndexOf('+');
            return plus > 0 && plus + 1 < material.Length
                ? (material.Substring(0, plus), material.Substring(plus + 1))
                : (material, null);
        }

        private static string? ExportTexture(
            string key,
            TextureProvider textures,
            string assetDirectory,
            string textureDirectory,
            Dictionary<string, string> uris,
            HashSet<string> missing)
        {
            // Do not turn a resource named "null" into a missing texture. Retail
            // mat_textureN resolves it normally (0x9bf188 -> 0x9c0f00 -> 0x9fe170),
            // and the archives supply null.dds. Dropping it destroys active stage
            // semantics, notably skydome20+null's first stage. No-texture commands
            // are distinct; see the pipeline guide's native sky investigation.
            if (uris.TryGetValue(key, out string? existing)) return existing;
            if (TryExportCubeTexture(key, textures, assetDirectory, textureDirectory, out string cubeUri))
            {
                uris[key] = cubeUri;
                return cubeUri;
            }
            NativeTextureSourceResolver.Resolution resolution =
                NativeTextureSourceResolver.Resolve(textures, key);
            DdsImage? image = resolution.Image;
            if (image == null)
            {
                missing.Add(key);
                return null;
            }
            Directory.CreateDirectory(textureDirectory);
            byte[] png = PngEncoder.EncodeBgra(image.Bgra, image.Width, image.Height);
            string path = SharedTextureOutput.WritePng(textureDirectory, key, png);
            string uri = Path.GetRelativePath(assetDirectory, path).Replace('\\', '/');
            uris[key] = uri;
            return uri;
        }

        private static bool IsCubeTexture(string key, TextureProvider textures) =>
            TryGetCubeTexturePath(key, textures, out _);

        private static bool TryExportCubeTexture(
            string key,
            TextureProvider textures,
            string assetDirectory,
            string textureDirectory,
            out string uri)
        {
            uri = "";
            if (!TryGetCubeTexturePath(key, textures, out string source)) return false;
            Directory.CreateDirectory(textureDirectory);
            string destination = Path.Combine(textureDirectory, SafeFilename(key) + ".dds");
            byte[] bytes = File.ReadAllBytes(source);
            if (File.Exists(destination) && !File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException($"cube texture output collision for '{key}'");
            File.WriteAllBytes(destination, bytes);
            uri = Path.GetRelativePath(assetDirectory, destination).Replace('\\', '/');
            return true;
        }

        private static bool TryGetCubeTexturePath(
            string key, TextureProvider textures, out string path)
        {
            path = textures.AssetDirectory == null ? "" : Path.Combine(
                textures.AssetDirectory, "startup.pak-out", key + ".dds");
            if (path.Length == 0 || !File.Exists(path)) return false;
            using FileStream stream = File.OpenRead(path);
            if (stream.Length < 116) return false;
            Span<byte> header = stackalloc byte[116];
            if (stream.Read(header) != header.Length || !header[..4].SequenceEqual("DDS "u8)) return false;
            uint caps2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[112..116]);
            return (caps2 & 0x200u) != 0;
        }

        private static string SafeFilename(string name) => string.Concat(name.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_'));

        private static IReadOnlyDictionary<string, object> ExportEmpireVariants(
            string key,
            TextureProvider textures,
            string assetDirectory,
            string textureDirectory,
            Dictionary<string, string> uris,
            HashSet<string> missing)
        {
            string? authoredEmpire = textures.DetectEmpire(key);
            if (authoredEmpire == null) return new Dictionary<string, object>();

            var variants = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string empire in new[] { "nc", "tr", "vs" })
            {
                string? variant = empire.Equals(authoredEmpire, StringComparison.OrdinalIgnoreCase)
                    ? key
                    : textures.EmpireVariant(key, empire);
                if (variant == null) continue;
                string? uri = ExportTexture(
                    variant,
                    textures,
                    assetDirectory,
                    textureDirectory,
                    uris,
                    missing);
                if (uri != null) variants[empire] = new { texture = variant, textureUri = uri };
            }
            return variants;
        }
    }
}
