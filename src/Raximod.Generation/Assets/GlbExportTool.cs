using System.Numerics;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Meshes;
using Raximod.EngineAssets.Textures;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;

namespace Raximod.Generation.Assets
{
    using StaticMesh = MeshBuilder<VertexPositionNormal, VertexColor1Texture2, VertexEmpty>;
    using StaticVertex = VertexBuilder<VertexPositionNormal, VertexColor1Texture2, VertexEmpty>;
    using SkinnedMesh = MeshBuilder<VertexPositionNormal, VertexColor1Texture2, VertexJoints4>;
    using SkinnedVertex = VertexBuilder<VertexPositionNormal, VertexColor1Texture2, VertexJoints4>;

    /// <summary>
    /// Headless PlanetSide UberMesh to glTF 2.0 binary exporter. The resulting GLB is Y-up and suitable
    /// for Babylon.js. Base textures are resolved from the client's FLAT archives and embedded as PNG;
    /// skeletons, rigid mesh-on-bone attachments and compatible clips from anims.ubr are preserved.
    /// </summary>
    public static class GlbExportTool
    {
        public sealed record Options(
            string PlanetSideDir,
            string RecordName,
            string OutputPath,
            string? LibraryPath = null,
            bool IncludeTextures = true,
            bool IncludeAnimations = true,
            int MaxAnimations = 250,
            bool BakeWorldOffset = false,
            IReadOnlyList<string>? AnimationPrefixes = null,
            string? SharedTextureDirectory = null,
            IReadOnlyList<string>? MeshMaterials = null,
            IReadOnlyDictionary<string, string>? MaterialReplacements = null,
            IReadOnlyList<string>? AnimationNames = null,
            IReadOnlyList<string>? MeshNames = null,
            IReadOnlyDictionary<string, string>? AnimationTrackAliases = null,
            bool PreserveRigidBoneAttachments = false,
            IReadOnlyList<AnimRecord>? AnimationCatalog = null,
            bool WriteTextureCompanions = true,
            bool WriteNativeSidecars = true);

        public sealed record Result(
            string LibraryPath,
            string RecordName,
            string OutputPath,
            int Meshes,
            int Sections,
            int Triangles,
            int Materials,
            int Textures,
            int Bones,
            int Animations,
            Vector3 SourceWorldOffset);

        private static readonly string[] SharedLibraries =
        {
            "uber.ubr",
            "patch1/patch1.ubr",
            "patch2/patch2.ubr",
            "patch3/patch3.ubr",
            "patch4/patch4.ubr",
            "patch5/patch5.ubr",
            "expansion1/expansion1.ubr",
        };

        private static readonly string[] AnimationLibraries =
        {
            "anims.ubr",
            "patch1/anim_patch1.ubr",
            "patch2/anim_patch2.ubr",
            "patch3/anim_patch3.ubr",
            "patch4/anim_patch4.ubr",
            "patch5/anim_patch5.ubr",
        };

        public static Result Run(Options options, IProgress<string>? log = null, CancellationToken ct = default)
        {
            Validate(options);
            string assetRoot = Path.GetFullPath(options.PlanetSideDir);
            string libraryPath = FindLibrary(assetRoot, options.LibraryPath, options.RecordName);

            log?.Report($"loading {Path.GetRelativePath(assetRoot, libraryPath)}");
            var model = UberModel.Load(File.ReadAllBytes(libraryPath));
            var textures = new TextureProvider(assetRoot);
            return RunDecoded(options, libraryPath, model, textures, log, ct);
        }

        /// <summary>
        /// Exports from an already decoded library. Batch tools use this to avoid decoding the same
        /// multi-record UBR once per output asset.
        /// </summary>
        public static Result RunDecoded(
            Options options,
            string libraryPath,
            UberModel model,
            TextureProvider textures,
            IProgress<string>? log = null,
            CancellationToken ct = default)
        {
            UberModel.MeshSystem? system = model.FetchMeshSystem(options.RecordName);
            if (system == null)
            {
                throw new InvalidOperationException(
                    $"Mesh record '{options.RecordName}' could not be decoded from '{libraryPath}': " +
                    (model.RefuseReason ?? "record not found"));
            }

            return RunMeshSystem(options, libraryPath, system, textures, log, ct);
        }

        /// <summary>Shared geometry/material export for source records and validated static derivatives.</summary>
        public static Result RunMeshSystem(
            Options options, string libraryPath, UberModel.MeshSystem system, TextureProvider textures,
            IProgress<string>? log = null, CancellationToken ct = default)
        {
            Validate(options);
            string assetRoot = Path.GetFullPath(options.PlanetSideDir);
            string? sharedTextureDirectory = options.WriteTextureCompanions
                ? options.SharedTextureDirectory
                    ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!, "material-textures")
                : null;
            ct.ThrowIfCancellationRequested();
            var materials = new Dictionary<string, MaterialBuilder>(StringComparer.OrdinalIgnoreCase);
            int embeddedTextures = 0;
            var nativeMaterialUsage = new Dictionary<string, NativeMaterialManifestTool.Usage>(
                StringComparer.OrdinalIgnoreCase);
            var submissionSections = new List<NativeMaterialMetadataRefresh.SubmissionSection>();
            MaterialBuilder MaterialFor(string name)
            {
                if (materials.TryGetValue(name, out MaterialBuilder? cached)) return cached;
                MaterialBuilder created = BuildMaterial(
                    name,
                    textures,
                    options.IncludeTextures,
                    sharedTextureDirectory,
                    out bool embedded);
                if (embedded) embeddedTextures++;
                materials[name] = created;
                return created;
            }

            // Preserve each native mesh as a separate GLB node. Portalized
            // facilities use those native mesh names as their authored cell
            // identities (for example tower_a_001). Collapsing every static
            // section into one MeshBuilder irreversibly destroyed that
            // ownership and made correct cell-and-portal culling impossible.
            // The browser remains free to batch non-portal assets after load.
            var staticMeshes = new Dictionary<int, StaticMesh>();
            HighestQualityMeshSelectionReport selection = HighestQualityMeshSelector.Select(system);
            selection.ThrowIfInvalid();
            log?.Report(selection.Summary);
            bool[] keep = selection.CreateKeepMask();
            string selectionPolicy = selection.UsesNativeAab
                ? "native-aab-validated-highest-quality"
                : "highest-native-detail-no-distance-lod";
            if (options.MeshMaterials is { Count: > 0 })
            {
                var requested = options.MeshMaterials.ToHashSet(StringComparer.OrdinalIgnoreCase);
                keep = system.Meshes.Select(mesh => mesh.Sections.Any(section =>
                    requested.Contains(section.MaterialName))).ToArray();
                string[] matched = system.Meshes.Where((_, index) => keep[index])
                    .SelectMany(mesh => mesh.Sections.Select(section => section.MaterialName))
                    .Where(requested.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string[] missing = requested.Except(matched, StringComparer.OrdinalIgnoreCase).ToArray();
                if (missing.Length > 0)
                {
                    throw new KeyNotFoundException(
                        $"Mesh record '{options.RecordName}' has no mesh using material(s): " +
                        string.Join(", ", missing));
                }
                selectionPolicy = "explicit-native-material";
                log?.Report($"explicit mesh materials: {string.Join(", ", matched)} " +
                    $"({keep.Count(value => value)} native mesh(es))");
            }
            if (options.MeshNames is { Count: > 0 })
            {
                var requested = options.MeshNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
                keep = system.Meshes.Select(mesh => requested.Contains(mesh.Name)).ToArray();
                string[] matched = system.Meshes.Where((_, index) => keep[index])
                    .Select(mesh => mesh.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                string[] missing = requested.Except(matched, StringComparer.OrdinalIgnoreCase).ToArray();
                if (missing.Length > 0)
                    throw new KeyNotFoundException(
                        $"Mesh record '{options.RecordName}' has no embedded mesh(es): {string.Join(", ", missing)}");
                selectionPolicy = "explicit-native-mesh";
                log?.Report($"explicit embedded meshes: {string.Join(", ", matched)}");
            }
            HighestQualitySkeletonBindingReport skeletonBindings =
                HighestQualitySkeletonSelector.Bind(system, keep);
            if (skeletonBindings.InvalidBindings.Count > 0)
            {
                string failures = string.Join("; ", skeletonBindings.InvalidBindings.Select(binding =>
                    $"mesh '{binding.MeshName}': {binding.Reason}"));
                throw new InvalidOperationException(
                    $"Record '{options.RecordName}' has unresolved native skeleton ownership: {failures}.");
            }
            var rigs = skeletonBindings.UsedSkeletonIndices.ToDictionary(
                index => index,
                index => new RigExport(index, system.Skeletons[index], options.RecordName));
            Vector3 geometryOffset = options.BakeWorldOffset ? system.WorldOffset : Vector3.Zero;

            int meshCount = 0, sectionCount = 0, triangleCount = 0, staticTriangles = 0;
            for (int meshIndex = 0; meshIndex < system.Meshes.Count; meshIndex++)
            {
                ct.ThrowIfCancellationRequested();
                if (!keep[meshIndex]) continue;
                UberModel.Mesh mesh = system.Meshes[meshIndex];
                StaticMesh StaticMeshForSource()
                {
                    if (staticMeshes.TryGetValue(meshIndex, out StaticMesh? existing)) return existing;
                    var created = new StaticMesh(string.IsNullOrWhiteSpace(mesh.Name)
                        ? $"{options.RecordName}_mesh_{meshIndex}"
                        : mesh.Name);
                    staticMeshes.Add(meshIndex, created);
                    return created;
                }
                int? rigIndex = skeletonBindings.SkeletonForMesh(meshIndex);
                RigExport? rig = rigIndex.HasValue ? rigs[rigIndex.Value] : null;
                int rigidBone = rig != null && rig.BoneByName.TryGetValue(mesh.Name, out int foundBone)
                    ? foundBone : -1;
                bool addedMesh = false;

                foreach (UberModel.MeshSection section in mesh.Sections)
                {
                    if (section.VertexCount == 0 || section.IndexCount < 3 ||
                        section.MaterialName.Equals("ocean_floor", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string materialName = options.MaterialReplacements is not null
                        && options.MaterialReplacements.TryGetValue(section.MaterialName, out string? replacement)
                            ? replacement
                            : section.MaterialName;
                    MaterialBuilder material = MaterialFor(materialName);
                    bool isSkinned = ShouldExportSectionAsSkinned(
                        rig != null,
                        section.HasSkin,
                        options.IncludeAnimations,
                        rigidBone >= 0,
                        options.PreserveRigidBoneAttachments);
                    int added = isSkinned
                        ? AddSkinnedSection(rig!.Mesh, material, section, geometryOffset, rigidBone)
                        : AddStaticSection(StaticMeshForSource(), material, section, geometryOffset);
                    if (added == 0) continue;
                    submissionSections.Add(new(mesh.Id, section.Id, materialName));

                    if (nativeMaterialUsage.TryGetValue(materialName, out var usage))
                    {
                        nativeMaterialUsage[materialName] = usage with
                        {
                            Sections = usage.Sections + 1,
                            HasUv0 = usage.HasUv0 || section.HasUv0,
                            HasUv1 = usage.HasUv1 || section.HasUv1
                        };
                    }
                    else
                    {
                        nativeMaterialUsage[materialName] = new NativeMaterialManifestTool.Usage(
                            materialName,
                            1,
                            section.HasUv0,
                            section.HasUv1);
                    }

                    triangleCount += added;
                    if (isSkinned)
                    {
                        rig!.Triangles += added;
                    }
                    else staticTriangles += added;
                    sectionCount++;
                    addedMesh = true;
                }
                if (addedMesh) meshCount++;
            }

            if (triangleCount == 0)
            {
                throw new InvalidOperationException($"Mesh record '{options.RecordName}' contains no exportable triangles.");
            }

            var scene = new SceneBuilder(options.RecordName);
            Matrix4x4 zUpToYUp = Matrix4x4.CreateRotationX(-MathF.PI / 2f);
            int animationCount = 0;

            foreach (RigExport rig in rigs.Values.Where(value => value.Triangles > 0).OrderBy(value => value.Index))
            {
                NodeBuilder[] joints = BuildSkeleton(rig.Skeleton, zUpToYUp, out NodeBuilder armature);
                if (options.IncludeAnimations)
                {
                    animationCount += AddAnimations(assetRoot, options.RecordName, rig.Skeleton, joints,
                        options.MaxAnimations, options.AnimationPrefixes, options.AnimationNames,
                        options.AnimationTrackAliases, options.AnimationCatalog, log, ct);
                }
                if (!NodeBuilder.IsValidArmature(joints))
                {
                    string roots = string.Join(", ", joints
                        .GroupBy(joint => joint.Root)
                        .Select(group => $"'{group.Key.Name}' ({group.Count()} joints)"));
                    throw new InvalidOperationException($"Invalid exported skeleton; joint roots: {roots}.");
                }
                log?.Report($"skeleton: {joints.Length} joints, root '{joints[0].Root.Name}'");
                InstanceBuilder instance = scene.AddSkinnedMesh(rig.Mesh, armature.WorldMatrix, joints);
                // SharpGLTF otherwise emits the skinned geometry node without
                // a name. The native skeleton name is also its portal-region
                // identity for facility cells, so retain it on the drawable
                // node instead of only on the joint hierarchy.
                instance.Content.Name = rig.Skeleton.Name;
            }
            if (staticTriangles > 0)
            {
                foreach (KeyValuePair<int, StaticMesh> pair in staticMeshes.OrderBy(pair => pair.Key))
                {
                    // The fixed-transform overload emits an anonymous glTF
                    // node. Babylon then renames it nodeN and loses the native
                    // region identity even though the mesh itself is named.
                    // Attach through a named node so portal ownership survives
                    // all the way into the runtime hierarchy.
                    var node = new NodeBuilder(pair.Value.Name) { LocalMatrix = zUpToYUp };
                    scene.AddRigidMesh(pair.Value, node);
                }
            }

            string outputPath = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            log?.Report($"writing {outputPath}");
            string temporaryOutput = Path.Combine(
                Path.GetDirectoryName(outputPath)!,
                $".{Path.GetFileNameWithoutExtension(outputPath)}.raximod-{Guid.NewGuid():N}.glb");
            try
            {
                scene.ToGltf2().SaveGLB(temporaryOutput);
                File.Move(temporaryOutput, outputPath, overwrite: true);
                // Enforce the browser loading contract at the source: every
                // embedded PNG must have a byte-identical external companion
                // before browser family packaging removes its embedded copy.
                if (sharedTextureDirectory is not null)
                    SharedTextureOutput.SynchronizeEmbeddedPngs(outputPath, sharedTextureDirectory);
            }
            finally
            {
                if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput);
            }
            if (options.WriteNativeSidecars)
            {
                NativeMaterialManifestTool.Write(
                    outputPath,
                    options.RecordName,
                    nativeMaterialUsage.Values.ToArray(),
                    textures,
                    sharedTextureDirectory,
                    NativeMaterialMetadataRefresh.OrderedSections(submissionSections));
                HighestQualityMeshSelectionManifestTool.Write(
                    outputPath, options.RecordName, selection, keep, selectionPolicy);
            }

            return new Result(libraryPath, options.RecordName, outputPath, meshCount, sectionCount,
                triangleCount, materials.Count, embeddedTextures,
                rigs.Values.Where(rig => rig.Triangles > 0).Sum(rig => rig.Skeleton.Bones.Count), animationCount,
                system.WorldOffset);
        }

        /// <summary>
        /// Vertex-skinned geometry always retains its native rig. A rigid mesh that merely shares a
        /// bone name needs a skin only when animations are exported; static facility/world exports
        /// must remain ordinary geometry so their instances can be batched by the browser runtime.
        /// </summary>
        public static bool ShouldExportSectionAsSkinned(
            bool hasRig,
            bool sectionHasSkin,
            bool includeAnimations,
            bool hasRigidBone,
            bool preserveRigidBoneAttachments = false) =>
            hasRig && (sectionHasSkin || ((includeAnimations || preserveRigidBoneAttachments) && hasRigidBone));

        /// <summary>
        /// Loads the installed native animation catalog once, applying the same later-patch-wins
        /// precedence as the original client. Batch exporters should reuse this result instead of
        /// decoding every animation archive once per model.
        /// </summary>
        public static IReadOnlyList<AnimRecord> LoadAnimationCatalog(string planetSideDirectory)
        {
            string root = Path.GetFullPath(planetSideDirectory);
            var records = new Dictionary<string, AnimRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (string relative in AnimationLibraries)
            {
                string path = Path.Combine(root, relative);
                if (!File.Exists(path)) continue;
                foreach (AnimRecord clip in AnimDb.Load(File.ReadAllBytes(path)).Records)
                    records[clip.Name] = clip;
            }
            return records.Values.OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>
        /// Finds clips genuinely compatible with the selected highest-quality native mesh and its
        /// owning skeletons. This is the authoritative source-driven alternative to guessing from
        /// object names such as "door".
        /// </summary>
        public static IReadOnlyList<string> CompatibleAnimationNames(
            UberModel.MeshSystem system,
            string modelName,
            IReadOnlyList<AnimRecord> catalog,
            IReadOnlyList<string>? animationPrefixes = null,
            IReadOnlyList<string>? animationNames = null,
            IReadOnlyDictionary<string, string>? animationTrackAliases = null)
        {
            HighestQualityMeshSelectionReport selection = HighestQualityMeshSelector.Select(system);
            selection.ThrowIfInvalid();
            bool[] keep = selection.CreateKeepMask();
            HighestQualitySkeletonBindingReport bindings = HighestQualitySkeletonSelector.Bind(system, keep);
            if (bindings.InvalidBindings.Count > 0) return Array.Empty<string>();

            return bindings.UsedSkeletonIndices
                .SelectMany(index => CompatibleAnimations(catalog, modelName, system.Skeletons[index], int.MaxValue,
                    animationPrefixes, animationNames, animationTrackAliases))
                .Select(clip => clip.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>Read only the cheap .ubr directory table and return its record names.</summary>
        public static IReadOnlyList<string> ListRecords(string libraryPath)
        {
            using FileStream stream = File.OpenRead(libraryPath);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != 0x72656275u) throw new InvalidOperationException("Not an 'uber' library.");
            reader.ReadUInt32();
            reader.ReadUInt32();
            uint count = reader.ReadUInt32();
            stream.Position = UberMesh.HeaderSize;
            var names = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                byte[] nameBytes = reader.ReadBytes(64);
                int end = Array.IndexOf(nameBytes, (byte)0);
                if (end < 0) end = nameBytes.Length;
                names.Add(System.Text.Encoding.Latin1.GetString(nameBytes, 0, end));
                stream.Position += 16;
            }
            return names;
        }

        private static void Validate(Options options)
        {
            if (string.IsNullOrWhiteSpace(options.PlanetSideDir) || !Directory.Exists(options.PlanetSideDir))
                throw new ArgumentException("A valid PlanetSide directory is required.", nameof(options));
            if (string.IsNullOrWhiteSpace(options.RecordName))
                throw new ArgumentException("A mesh record name is required.", nameof(options));
            if (string.IsNullOrWhiteSpace(options.OutputPath))
                throw new ArgumentException("An output .glb path is required.", nameof(options));
            if (options.MaxAnimations < 0)
                throw new ArgumentException("MaxAnimations cannot be negative.", nameof(options));
        }

        internal static string FindLibrary(string root, string? requested, string recordName)
        {
            if (!string.IsNullOrWhiteSpace(requested))
            {
                string explicitPath = Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested);
                if (!File.Exists(explicitPath)) throw new FileNotFoundException("UberMesh library not found.", explicitPath);
                if (!ContainsRecord(explicitPath, recordName))
                    throw new KeyNotFoundException($"'{recordName}' is not present in '{explicitPath}'.");
                return Path.GetFullPath(explicitPath);
            }

            foreach (string relative in SharedLibraries)
            {
                string candidate = Path.Combine(root, relative);
                if (File.Exists(candidate) && ContainsRecord(candidate, recordName)) return candidate;
            }
            throw new KeyNotFoundException($"'{recordName}' was not found in the shared PlanetSide mesh libraries.");
        }

        private static bool ContainsRecord(string libraryPath, string recordName)
        {
            foreach (string name in ListRecords(libraryPath))
            {
                if (string.Equals(name, recordName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static MaterialBuilder BuildMaterial(
            string name,
            TextureProvider textures,
            bool includeTexture,
            string? sharedTextureDirectory,
            out bool embedded)
        {
            var material = new MaterialBuilder(name)
                .WithMetallicRoughnessShader()
                .WithMetallicRoughness(0f, 0.85f)
                .WithDoubleSide(false);

            embedded = false;
            if (!includeTexture) return material;
            (DdsImage? image, string? key) = textures.ResolveNamed(name);
            if (image == null) return material;

            byte[] png = PngEncoder.EncodeBgra(image.Bgra, image.Width, image.Height);
            string imageName = SharedTextureOutput.SafePngFileName(key ?? name);
            if (!string.IsNullOrWhiteSpace(sharedTextureDirectory))
            {
                SharedTextureOutput.WritePng(sharedTextureDirectory, imageName, png);
            }
            var imageBuilder = ImageBuilder.From(new MemoryImage(png), imageName);
            material.WithChannelImage(KnownChannel.BaseColor, imageBuilder);
            embedded = true;

            bool translucent = (key?.Contains("mask", StringComparison.OrdinalIgnoreCase) ?? false) ||
                               (textures.Materials?.IsTranslucent(name) ?? false);
            MaterialsAdb.AlphaRole role = textures.Materials?.GetAlphaRole(name) ?? MaterialsAdb.AlphaRole.Unknown;
            if (translucent) material.WithAlpha(AlphaMode.BLEND);
            else if (role == MaterialsAdb.AlphaRole.Cutout || (role == MaterialsAdb.AlphaRole.Unknown && IsCutout(image.Bgra)))
                material.WithAlpha(AlphaMode.MASK, 0.5f);
            else
                material.WithAlpha(AlphaMode.OPAQUE);
            return material;
        }

        private static bool IsCutout(byte[] bgra)
        {
            int count = bgra.Length / 4;
            if (count == 0) return false;
            int transparent = 0, opaque = 0, extreme = 0;
            for (int i = 3; i < bgra.Length; i += 4)
            {
                byte alpha = bgra[i];
                if (alpha < 128) transparent++; else opaque++;
                if (alpha < 32 || alpha > 224) extreme++;
            }
            return transparent / (double)count >= 0.15 && opaque / (double)count >= 0.15 &&
                   extreme / (double)count >= 0.60;
        }

        private static int AddStaticSection(StaticMesh output, MaterialBuilder material,
            UberModel.MeshSection section, Vector3 offset)
        {
            var primitive = output.UsePrimitive(material);
            int count = 0;
            foreach ((int a, int b, int c) in Triangles(section))
            {
                Vector3 fallback = FaceNormal(section.Verts[a].Position, section.Verts[b].Position, section.Verts[c].Position);
                primitive.AddTriangle(
                    Static(section, a, offset, fallback),
                    Static(section, b, offset, fallback),
                    Static(section, c, offset, fallback));
                count++;
            }
            return count;
        }

        private sealed class RigExport
        {
            public RigExport(int index, UberModel.Skeleton skeleton, string recordName)
            {
                Index = index;
                Skeleton = skeleton;
                Mesh = new SkinnedMesh($"{recordName}_skinned_{index}");
                BoneByName = skeleton.Bones.Select((bone, boneIndex) => (bone.Name, boneIndex))
                    .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().boneIndex,
                        StringComparer.OrdinalIgnoreCase);
            }

            public int Index { get; }
            public UberModel.Skeleton Skeleton { get; }
            public SkinnedMesh Mesh { get; }
            public IReadOnlyDictionary<string, int> BoneByName { get; }
            public int Triangles { get; set; }
        }

        private static int AddSkinnedSection(SkinnedMesh output, MaterialBuilder material,
            UberModel.MeshSection section, Vector3 offset, int rigidBone)
        {
            var primitive = output.UsePrimitive(material);
            int count = 0;
            foreach ((int a, int b, int c) in Triangles(section))
            {
                Vector3 fallback = FaceNormal(section.Verts[a].Position, section.Verts[b].Position, section.Verts[c].Position);
                primitive.AddTriangle(
                    Skinned(section, a, offset, fallback, rigidBone),
                    Skinned(section, b, offset, fallback, rigidBone),
                    Skinned(section, c, offset, fallback, rigidBone));
                count++;
            }
            return count;
        }

        private static StaticVertex Static(UberModel.MeshSection section, int index, Vector3 offset, Vector3 fallback)
        {
            UberModel.UberVert vertex = section.Verts[index];
            return new StaticVertex(
                new VertexPositionNormal(vertex.Position + offset, Normal(section, vertex, fallback)),
                new VertexColor1Texture2(
                    Color(section, vertex),
                    vertex.Uv0,
                    section.HasUv1 ? vertex.Uv1 : vertex.Uv0));
        }


        private static SkinnedVertex Skinned(UberModel.MeshSection section, int index, Vector3 offset,
            Vector3 fallback, int rigidBone)
        {
            UberModel.UberVert vertex = section.Verts[index];
            VertexJoints4 joints = section.HasSkin
                ? new VertexJoints4((vertex.BoneA, vertex.Weight), (vertex.BoneB, 1f - vertex.Weight))
                : new VertexJoints4(rigidBone);
            return new SkinnedVertex(
                new VertexPositionNormal(vertex.Position + offset, Normal(section, vertex, fallback)),
                new VertexColor1Texture2(
                    Color(section, vertex),
                    vertex.Uv0,
                    section.HasUv1 ? vertex.Uv1 : vertex.Uv0),
                joints);
        }

        private static Vector3 Normal(UberModel.MeshSection section, UberModel.UberVert vertex, Vector3 fallback)
        {
            if (!section.HasNormal || vertex.Normal.LengthSquared() < 1e-10f) return fallback;
            return Vector3.Normalize(vertex.Normal);
        }

        private static Vector3 FaceNormal(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            return normal.LengthSquared() > 1e-10f ? Vector3.Normalize(normal) : Vector3.UnitZ;
        }

        private static Vector4 Color(UberModel.MeshSection section, UberModel.UberVert vertex)
        {
            if (!section.HasColor) return Vector4.One;
            uint c = vertex.Diffuse;
            return new Vector4(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f,
                (c & 0xFF) / 255f, 1f);
        }

        private static IEnumerable<(int A, int B, int C)> Triangles(UberModel.MeshSection section)
        {
            ushort[] indices = section.Indices;
            int vertexCount = (int)section.VertexCount;
            if (section.IsTriStrip)
            {
                for (int i = 0; i + 2 < indices.Length; i++)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    if (a == b || b == c || a == c || a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
                    yield return (i & 1) == 0 ? (a, b, c) : (b, a, c);
                }
            }
            else
            {
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    if (a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
                    yield return (a, b, c);
                }
            }
        }

        private static NodeBuilder[] BuildSkeleton(UberModel.Skeleton skeleton, Matrix4x4 rootTransform,
            out NodeBuilder armature)
        {
            // SharpGLTF requires every node in an armature tree to have a unique name. PlanetSide
            // commonly gives the skeleton container and its root bone the same name (for example
            // "bip01"), so keep the container distinct and disambiguate any repeated bone names.
            string skeletonName = skeleton.Name.Length > 0 ? skeleton.Name : "Armature";
            armature = new NodeBuilder(skeletonName + "_armature");
            armature.LocalTransform = rootTransform;
            var nodes = new NodeBuilder[skeleton.Bones.Count];
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < nodes.Length; i++)
            {
                UberModel.Bone bone = skeleton.Bones[i];
                string baseName = bone.Name.Length > 0 ? bone.Name : $"bone_{i}";
                string nodeName = baseName;
                for (int suffix = 2; !usedNames.Add(nodeName); suffix++) nodeName = $"{baseName}_{suffix}";
                nodes[i] = new NodeBuilder(nodeName)
                    .WithLocalTranslation(bone.Position)
                    .WithLocalRotation(bone.Rotation);
            }
            // Parent indices are not guaranteed to precede their children in every client asset.
            // Create all joints first so forward references retain the original hierarchy.
            for (int i = 0; i < nodes.Length; i++)
            {
                int parent = skeleton.Bones[i].Parent;
                if (parent >= 0 && parent < nodes.Length && parent != i)
                    nodes[parent].AddNode(nodes[i]);
                else
                    armature.AddNode(nodes[i]);
            }
            return nodes;
        }

        private static int AddAnimations(string assetRoot, string modelName, UberModel.Skeleton skeleton,
            NodeBuilder[] joints, int maxAnimations, IReadOnlyList<string>? animationPrefixes,
            IReadOnlyList<string>? animationNames, IReadOnlyDictionary<string, string>? animationTrackAliases,
            IReadOnlyList<AnimRecord>? animationCatalog, IProgress<string>? log, CancellationToken ct)
        {
            if (maxAnimations == 0) return 0;
            // Patches contain both corrections and entirely new object clips (the Vanu vehicle pad,
            // for example, lives in anim_patch2). Later patch records replace earlier clips by name,
            // matching the client's asset precedence.
            IReadOnlyList<AnimRecord> records;
            if (animationCatalog is not null)
            {
                records = animationCatalog;
            }
            else
            {
                log?.Report("loading native animation catalog");
                records = LoadAnimationCatalog(assetRoot);
            }
            List<AnimRecord> clips = CompatibleAnimations(records, modelName, skeleton, maxAnimations,
                animationPrefixes, animationNames, animationTrackAliases);
            var jointByName = new Dictionary<string, NodeBuilder>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < skeleton.Bones.Count; i++) jointByName[skeleton.Bones[i].Name] = joints[i];

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (AnimRecord clip in clips)
            {
                ct.ThrowIfCancellationRequested();
                string clipName = UniqueName(clip.Name, usedNames);
                bool any = false;
                foreach (AnimTrack track in clip.Tracks)
                {
                    string targetTrack = AnimationTrackBinding.ResolveTarget(track.Name, animationTrackAliases);
                    if (!jointByName.TryGetValue(targetTrack, out NodeBuilder? joint)) continue;

                    var positions = joint.UseTranslation(clipName);
                    if (track.PosKeys.Count > 0)
                        foreach (AnimKey<Vector3> key in track.PosKeys) positions.WithPoint(key.Time, key.Value);
                    else
                    {
                        positions.WithPoint(0, track.StaticPosition);
                        if (clip.Duration > 0) positions.WithPoint(clip.Duration, track.StaticPosition);
                    }

                    var rotations = joint.UseRotation(clipName);
                    if (track.RotKeys.Count > 0)
                        foreach (AnimKey<Quaternion> key in track.RotKeys) rotations.WithPoint(key.Time, key.Value);
                    else
                    {
                        rotations.WithPoint(0, track.StaticRotation);
                        if (clip.Duration > 0) rotations.WithPoint(clip.Duration, track.StaticRotation);
                    }
                    any = true;
                }
                if (any) added++;
            }
            return added;
        }

        private static List<AnimRecord> CompatibleAnimations(IEnumerable<AnimRecord> records, string modelName,
            UberModel.Skeleton skeleton, int maxAnimations, IReadOnlyList<string>? animationPrefixes,
            IReadOnlyList<string>? animationNames, IReadOnlyDictionary<string, string>? animationTrackAliases)
        {
            var boneNames = new HashSet<string>(skeleton.Bones.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
            string model = modelName.ToLowerInvariant();
            bool firstPerson = model.StartsWith("fp_", StringComparison.Ordinal);
            int minOverlap = Math.Max(1, skeleton.Bones.Count / 4);
            var scored = new List<(AnimRecord Clip, int Score, bool Own)>();
            HashSet<string>? exactNames = animationNames is { Count: > 0 }
                ? animationNames.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            foreach (AnimRecord clip in records)
            {
                if (exactNames is not null && !exactNames.Contains(clip.Name)) continue;
                bool explicitlyRequested = animationPrefixes is { Count: > 0 } && animationPrefixes.Any(prefix =>
                    clip.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                    clip.Name.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase));
                if (exactNames is null && animationPrefixes is { Count: > 0 } && !explicitlyRequested)
                    continue;
                int overlap = clip.Tracks.Count(track => boneNames.Contains(
                    AnimationTrackBinding.ResolveTarget(track.Name, animationTrackAliases)));
                bool own = clip.Name.StartsWith(model, StringComparison.OrdinalIgnoreCase);
                // Mobile-base pads share their post extension/retraction clips with the other
                // creation-pad variants, so those valid clips intentionally lack the `mb_` prefix.
                if (!own && model.EndsWith("pad_creation", StringComparison.OrdinalIgnoreCase))
                    own = clip.Name.StartsWith("pad_creation_", StringComparison.OrdinalIgnoreCase);
                // A model-owned effect clip may intentionally animate a single effect bone; the
                // broader overlap threshold exists only to reject unrelated shared skeleton clips.
                if (overlap < (own || exactNames is not null ? 1 : minOverlap)) continue;
                if (clip.Name.StartsWith("fp_", StringComparison.OrdinalIgnoreCase) != firstPerson) continue;
                scored.Add((clip, overlap, own));
            }
            // An explicit list can intentionally combine the model's ordinary clips with shared
            // interaction clips (vehicle seats, drop pods and implant machinery). Do not discard
            // those shared clips merely because model-owned locomotion clips are also present.
            IEnumerable<(AnimRecord Clip, int Score, bool Own)> candidates = exactNames is not null
                || animationPrefixes is { Count: > 0 }
                ? scored
                : scored.Any(s => s.Own)
                    ? scored.Where(s => s.Own)
                    : scored.OrderByDescending(s => s.Score)
                        .ThenBy(s => s.Clip.Name, StringComparer.OrdinalIgnoreCase);
            return candidates.Take(maxAnimations).Select(s => s.Clip).ToList();
        }

        private static string UniqueName(string requested, HashSet<string> used)
        {
            string baseName = string.IsNullOrWhiteSpace(requested) ? "animation" : requested;
            string candidate = baseName;
            int suffix = 2;
            while (!used.Add(candidate)) candidate = baseName + "_" + suffix++;
            return candidate;
        }

    }
}
