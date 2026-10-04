using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Assets
{
    /// <summary>Exports render-independent, Babylon-ready collision descriptions beside GLBs.</summary>
    public static class CollisionManifestTool
    {
        public sealed record Options(
            string PlanetSideDir,
            string GlbDirectory,
            string? RecordName = null,
            IReadOnlyCollection<string>? AabFallbackRecords = null,
            IReadOnlyDictionary<string, string>? GameObjectDefinitionsByRecord = null,
            IReadOnlyDictionary<string,
                IReadOnlyCollection<VehicleManifestContract.ActiveCollisionShape>>?
                ExpectedActivePhysicsByRecord = null,
            IReadOnlyCollection<string>? SelectedRecords = null);
        public sealed record Failure(string Record, string Reason);
        public sealed record Result(int Examined, int Written, int Shapes, int ForcefieldVisuals,
            IReadOnlyList<Failure> Failures)
        {
            public bool Complete => Failures.Count == 0;
        }

        private sealed class Manifest
        {
            public string Format { get; init; } = "raxicore-collision";
            public int Version { get; init; } = 2;
            public string CoordinateSystem { get; init; } = "right-handed-y-up";
            public string Record { get; init; } = "";
            public string Mode { get; init; } = "explicit";
            public GameObjectDb.GameObjectProvenance? Provenance { get; init; }
            public List<ManifestShape> Shapes { get; init; } = new();
            public List<ManifestForcefield>? Forcefields { get; init; }
        }

        private sealed class ManifestForcefield
        {
            public string Group { get; init; } = "";
            public string Kind { get; init; } = "barrier";
            public string Visual { get; init; } = "";
            public float[] Center { get; init; } = Array.Empty<float>();
            public float[] Rotation { get; init; } = Array.Empty<float>();
        }

        private sealed class ManifestShape
        {
            public string Id { get; init; } = "";
            public string Type { get; init; } = "";
            public string Source { get; init; } = "";
            public string Group { get; init; } = "static";
            public string Behavior { get; init; } = "static";
            public bool Enabled { get; init; } = true;
            public string? Node { get; init; }
            public float[]? Center { get; init; }
            public float[]? Size { get; init; }
            public float[]? Rotation { get; init; }
            public float? Radius { get; init; }
            public float? Height { get; init; }
            public float[]? Vertices { get; init; }
            public int[]? Indices { get; init; }
        }

        private static readonly string[] Libraries = {
            "uber.ubr", "patch1/patch1.ubr", "patch2/patch2.ubr", "patch3/patch3.ubr",
            "patch4/patch4.ubr", "patch5/patch5.ubr", "expansion1/expansion1.ubr",
        };

        public static Result Run(Options options, IProgress<string>? log = null, CancellationToken ct = default)
        {
            string planetside = Path.GetFullPath(options.PlanetSideDir);
            string glbDirectory = Path.GetFullPath(options.GlbDirectory);
            string startup = Path.Combine(planetside, "startup.pak-out");
            var physics = PhysicsListDatabase.ParseDirectory(startup);
            string gameObjectsPath = Path.Combine(startup, "game_objects.adb");
            GameObjectDb? gameObjects = File.Exists(gameObjectsPath)
                ? GameObjectDb.Parse(File.ReadAllBytes(gameObjectsPath)) : null;
            Dictionary<string, GameObjectDb.GameObject> objects = gameObjects?.ResolvedObjects
                .ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, GameObjectDb.GameObject>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, GameObjectDb.GameObject> objectsByMesh = gameObjects?.ResolvedObjects
                .SelectMany(value => GameObjectPropertyReader.List(value, "meshsequence")
                    .Where(mesh => !mesh.Equals("none", StringComparison.OrdinalIgnoreCase))
                    .Select(mesh => (Mesh: mesh, Object: value)))
                .GroupBy(value => value.Mesh, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group
                    .Select(value => value.Object)
                    .OrderByDescending(value => value.Properties.ContainsKey("physics")).First(),
                    StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, GameObjectDb.GameObject>(StringComparer.OrdinalIgnoreCase);

            if (options.RecordName != null && options.SelectedRecords != null)
                throw new ArgumentException("Specify one collision record selection");
            string[] records = options.SelectedRecords?.Order(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? (options.RecordName != null
                ? new[] { options.RecordName }
                : Directory.EnumerateFiles(glbDirectory, "*.glb", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileNameWithoutExtension).Where(name => name != null).Cast<string>()
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray());
            foreach (string record in records)
                if (!File.Exists(Path.Combine(glbDirectory, record + ".glb")))
                    throw new FileNotFoundException($"Selected collision record '{record}' has no exported GLB");
            HashSet<string>? aabFallbackRecords = options.AabFallbackRecords?.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            int written = 0, shapeCount = 0;
            var writtenRecords = new List<string>();
            var forcefieldVisuals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<Failure>();
            if (options.ExpectedActivePhysicsByRecord != null)
            {
                foreach (string missing in options.ExpectedActivePhysicsByRecord.Keys
                    .Except(records, StringComparer.OrdinalIgnoreCase))
                    failures.Add(new Failure(missing, "expected collision record has no exported GLB"));
                foreach (string unexpected in records
                    .Except(options.ExpectedActivePhysicsByRecord.Keys, StringComparer.OrdinalIgnoreCase))
                    failures.Add(new Failure(unexpected, "exported GLB has no active vehicle collision contract"));
                if (options.GameObjectDefinitionsByRecord == null)
                {
                    failures.Add(new Failure(
                        "<vehicle-family>",
                        "active vehicle collision contracts require explicit game-object definitions"));
                }
                else
                {
                    foreach (string missing in options.ExpectedActivePhysicsByRecord.Keys.Except(
                        options.GameObjectDefinitionsByRecord.Keys,
                        StringComparer.OrdinalIgnoreCase))
                    {
                        failures.Add(new Failure(
                            missing,
                            "active vehicle collision contract has no explicit game-object definition"));
                    }
                    foreach (string unexpected in options.GameObjectDefinitionsByRecord.Keys.Except(
                        options.ExpectedActivePhysicsByRecord.Keys,
                        StringComparer.OrdinalIgnoreCase))
                    {
                        failures.Add(new Failure(
                            unexpected,
                            "explicit game-object definition has no active vehicle collision contract"));
                    }
                }
            }
            Dictionary<string, List<ManifestShape>> embedded = CollectEmbedded(
                planetside, glbDirectory, records, failures, log, ct,
                out Dictionary<string, List<ManifestShape>> nativeAab);
            var pending = new List<(string Record, Manifest Manifest)>();
            var sourceAudit = new List<object>();
            foreach (string record in records)
            {
                ct.ThrowIfCancellationRequested();
                var shapes = embedded.TryGetValue(record, out List<ManifestShape>? native)
                    ? new List<ManifestShape>(native) : new List<ManifestShape>();
                var forcefields = new List<ManifestForcefield>();
                GameObjectDb.GameObject? collisionObject = null;
                if (options.GameObjectDefinitionsByRecord?.TryGetValue(
                        record, out string? definition) == true)
                {
                    if (objects.TryGetValue(definition, out GameObjectDb.GameObject? explicitObject))
                    {
                        collisionObject = explicitObject;
                        AddObjectPhysics(explicitObject, physics, shapes, forcefields);
                    }
                    else
                    {
                        failures.Add(new Failure(
                            record, $"explicit game-object definition '{definition}' was not found"));
                    }
                }
                else if (objects.TryGetValue(record, out GameObjectDb.GameObject? gameObject))
                {
                    collisionObject = gameObject;
                    AddObjectPhysics(gameObject, physics, shapes, forcefields);
                }
                else if (objectsByMesh.TryGetValue(record, out gameObject))
                {
                    collisionObject = gameObject;
                    AddObjectPhysics(gameObject, physics, shapes, forcefields);
                }
                bool hasExplicitStructuralCollision = shapes.Any(IsStructuralCollision);
                string mode = hasExplicitStructuralCollision ? "explicit" : "none";
                bool permitsAabFallback = aabFallbackRecords == null || aabFallbackRecords.Contains(record);
                // A decoded cdnull (or empty collision set) is an explicit native
                // choice, not missing data. Never replace it with spatial AAB faces.
                if (!hasExplicitStructuralCollision && !embedded.ContainsKey(record) && permitsAabFallback
                    && nativeAab.TryGetValue(record, out List<ManifestShape>? aabShapes))
                {
                    shapes.AddRange(aabShapes);
                    mode = "aab";
                }
                if (options.ExpectedActivePhysicsByRecord?.TryGetValue(
                        record, out IReadOnlyCollection<VehicleManifestContract.ActiveCollisionShape>?
                            expectedActive) == true)
                {
                    try
                    {
                        VehicleManifestContract.RequireMatchingActiveCollisionSidecar(
                            $"vehicle collision record '{record}'",
                            expectedActive,
                            shapes
                                .Where(shape => shape.Source.Equals(
                                    "physics_lst", StringComparison.OrdinalIgnoreCase)
                                    && shape.Group.Equals("active", StringComparison.OrdinalIgnoreCase)
                                    && shape.Enabled)
                                .Select(shape => new VehicleManifestContract.EmittedCollisionShape(
                                    shape.Id, shape.Center))
                                .ToArray());
                    }
                    catch (InvalidDataException exception)
                    {
                        failures.Add(new Failure(record, exception.Message));
                    }
                }
                string[] sources = shapes.Select(shape => shape.Source)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                sourceAudit.Add(new
                {
                    record,
                    mode,
                    sources,
                    provenance = collisionObject?.Provenance,
                    embeddedCollisionDeclared = embedded.ContainsKey(record),
                    collisionManifest = shapes.Count > 0,
                });
                if (shapes.Count == 0) continue;
                foreach (ManifestForcefield forcefield in forcefields) forcefieldVisuals.Add(forcefield.Visual);
                var manifest = new Manifest {
                    Record = record,
                    Mode = mode,
                    Provenance = collisionObject?.Provenance,
                    Shapes = shapes,
                    Forcefields = forcefields.Count > 0 ? forcefields : null,
                };
                pending.Add((record, manifest));
            }

            // Do not publish an index that silently omits collision declared by a native model.
            // Consumers keep the last complete collision set until the decoder/source issue is fixed.
            if (failures.Count != 0)
            {
                foreach (Failure failure in failures)
                    log?.Report($"collision failed {failure.Record}: {failure.Reason}");
                return new Result(records.Length, 0, 0, 0, failures);
            }

            foreach ((string record, Manifest manifest) in pending)
            {
                WriteIfChanged(Path.Combine(glbDirectory, record + ".collision.json"),
                    JsonSerializer.Serialize(manifest, JsonOptions));
                written++;
                writtenRecords.Add(record);
                shapeCount += manifest.Shapes.Count;
                log?.Report($"{record}: {manifest.Shapes.Count} collision shapes");
            }
            // Remove stale companions only after the complete native audit succeeds.
            foreach (string record in records.Except(writtenRecords, StringComparer.OrdinalIgnoreCase))
            {
                string stale = Path.Combine(glbDirectory, record + ".collision.json");
                if (File.Exists(stale)) File.Delete(stale);
            }

            // A collision-only forcefield is an invisible wall. Export every
            // visual referenced by the same native building data so consumers
            // cannot accidentally deploy only half of the feature.
            foreach (string visual in forcefieldVisuals.Order(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                GlbExportTool.Run(new GlbExportTool.Options(
                    planetside,
                    visual,
                    Path.Combine(glbDirectory, visual + ".glb"),
                    IncludeAnimations: false), log, ct);
            }
            WriteIfChanged(Path.Combine(glbDirectory, "collision-manifest.json"), JsonSerializer.Serialize(new {
                format = "raxicore-collision-index", version = 3,
                gameObjects = gameObjects?.Diagnostics,
                records = writtenRecords.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                sources = sourceAudit,
            }, JsonOptions));
            return new Result(records.Length, written, shapeCount, forcefieldVisuals.Count, failures);
        }

        private static bool IsStructuralCollision(ManifestShape shape) =>
            !shape.Behavior.Equals("forcefield", StringComparison.OrdinalIgnoreCase)
            && !shape.Behavior.Equals("vehicle-barrier", StringComparison.OrdinalIgnoreCase)
            && !shape.Behavior.Equals("training-zone-trigger", StringComparison.OrdinalIgnoreCase)
            && !shape.Group.Equals("destroyed", StringComparison.OrdinalIgnoreCase);

        private static void WriteIfChanged(string path, string contents)
        {
            if (File.Exists(path) && File.ReadAllText(path).Equals(contents, StringComparison.Ordinal)) return;
            File.WriteAllText(path, contents);
        }

        private static Dictionary<string, List<ManifestShape>> CollectEmbedded(string planetside,
            string glbDirectory,
            IReadOnlyCollection<string> records, List<Failure> failures, IProgress<string>? log,
            CancellationToken ct, out Dictionary<string, List<ManifestShape>> nativeAab)
        {
            var output = new Dictionary<string, List<ManifestShape>>(StringComparer.OrdinalIgnoreCase);
            nativeAab = new Dictionary<string, List<ManifestShape>>(StringComparer.OrdinalIgnoreCase);
            var unresolved = new HashSet<string>(records, StringComparer.OrdinalIgnoreCase);
            foreach (string relative in Libraries)
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.Combine(planetside, relative);
                if (!File.Exists(path)) continue;
                var available = new HashSet<string>(GlbExportTool.ListRecords(path),
                    StringComparer.OrdinalIgnoreCase);
                string[] matches = unresolved.Where(available.Contains).ToArray();
                if (matches.Length == 0) continue;
                log?.Report($"auditing native collision in {relative} for {matches.Length} assets");
                UberModel model = UberModel.Load(File.ReadAllBytes(path));
                foreach (string record in matches)
                {
                    ct.ThrowIfCancellationRequested();
                    UberModel.MeshSystem? system = model.FetchMeshSystem(record);
                    if (system == null)
                    {
                        failures.Add(new Failure(record, model.RefuseReason ?? "mesh-system decode failed"));
                        unresolved.Remove(record);
                        continue;
                    }
                    if (system.DeclaresCollision)
                    {
                        if (system.Collisions == null)
                        {
                            failures.Add(new Failure(record,
                                system.CollisionDecodeFailure ?? "declared collision was discarded"));
                        }
                        else
                        {
                            var shapes = new List<ManifestShape>();
                            string behavior = BehaviorFor(record);
                            UberModel.CollisionPart[] exportable = system.Collisions.Parts
                                .Where(part => part.Type != UberModel.CollisionPartType.None).ToArray();
                            foreach (UberModel.CollisionPart part in exportable)
                            {
                                ManifestShape? shape = EmbeddedShape(part, record, behavior);
                                if (shape != null) shapes.Add(shape);
                            }
                            if (shapes.Count != exportable.Length)
                            {
                                failures.Add(new Failure(record,
                                    $"exported {shapes.Count}/{exportable.Length} native collision parts"));
                            }
                            else output[record] = shapes; // Retain explicitly empty native collision.
                        }
                    }
                    if (system.NativeAab?.Faces.Count > 0)
                    {
                        List<ManifestShape> shapes = AabShapes(system, record,
                            NonCollidingMaterials(glbDirectory, record));
                        if (shapes.Count > 0) nativeAab[record] = shapes;
                    }
                    unresolved.Remove(record);
                }
            }
            return output;
        }

        private static List<ManifestShape> AabShapes(UberModel.MeshSystem system, string record,
            IReadOnlySet<string> nonCollidingMaterials)
        {
            NativeAabSelectionEvidence evidence = NativeAabSelectionEvidence.Inspect(system);
            if (!evidence.Complete)
                throw new InvalidDataException($"native AAB for {record} is incomplete: {string.Join("; ", evidence.Failures)}");
            var owners = new Dictionary<uint, UberModel.MeshSection>();
            foreach (UberModel.Mesh mesh in system.Meshes)
            foreach (UberModel.MeshSection section in mesh.Sections)
                owners[((section.Id & 0xffffu) << 16) | (section.MeshId & 0xffffu)] = section;

            string behavior = BehaviorFor(record);
            var aggregates = new Dictionary<(string Behavior, string Group), (List<float> Vertices, List<int> Indices)>();
            foreach (IGrouping<uint, UberModel.NativeAabFace> group in system.NativeAab!.Faces
                         .GroupBy(face => face.PackedSectionKey).OrderBy(group => group.Key))
            {
                UberModel.MeshSection section = owners[group.Key];
                if (nonCollidingMaterials.Contains(section.MaterialName)) continue;
                // Material names do not describe collision orientation. Keep vertical
                // grate faces solid; locomotion decides step/slope support from geometry.
                string collisionGroup = behavior == "door" ? "door" : "static";
                var aggregateKey = (behavior, collisionGroup);
                if (!aggregates.TryGetValue(aggregateKey, out var aggregate))
                {
                    aggregate = (new List<float>(), new List<int>());
                    aggregates.Add(aggregateKey, aggregate);
                }
                var remap = new Dictionary<ushort, int>();
                int vertexOffset = aggregate.Vertices.Count / 3;
                int Vertex(ushort nativeIndex)
                {
                    if (remap.TryGetValue(nativeIndex, out int existing)) return existing;
                    int compact = vertexOffset + remap.Count;
                    remap.Add(nativeIndex, compact);
                    aggregate.Vertices.AddRange(YUp(section.Verts[nativeIndex].Position));
                    return compact;
                }
                foreach (UberModel.NativeAabFace face in group)
                {
                    aggregate.Indices.Add(Vertex(face.VertexA));
                    aggregate.Indices.Add(Vertex(face.VertexB));
                    aggregate.Indices.Add(Vertex(face.VertexC));
                }
            }
            return aggregates.OrderBy(pair => pair.Key.Group, StringComparer.OrdinalIgnoreCase)
                .Select((pair, index) => Shape($"aab:{index}", "mesh", "native_aab", pair.Key.Behavior,
                    pair.Key.Group, true, null, vertices: pair.Value.Vertices.ToArray(),
                    indices: pair.Value.Indices.ToArray()))
                .ToList();
        }

        private static HashSet<string> NonCollidingMaterials(string glbDirectory, string record)
        {
            var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(glbDirectory, record + ".materials.json");
            if (!File.Exists(path)) return output;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("materials", out JsonElement materials)
                || materials.ValueKind != JsonValueKind.Array) return output;
            foreach (JsonElement material in materials.EnumerateArray())
            {
                if (!material.TryGetProperty("name", out JsonElement nameElement)
                    || nameElement.GetString() is not string name) continue;
                bool nonColliding = false;
                foreach (string property in new[] { "sectionCommands", "baseCommands" })
                {
                    if (!material.TryGetProperty(property, out JsonElement commands)
                        || commands.ValueKind != JsonValueKind.Array) continue;
                    foreach (JsonElement command in commands.EnumerateArray())
                    {
                        if (!command.TryGetProperty("name", out JsonElement commandName)
                            || !commandName.ValueEquals("mat_surface")
                            || !command.TryGetProperty("arguments", out JsonElement arguments)
                            || arguments.ValueKind != JsonValueKind.Array) continue;
                        nonColliding |= arguments.EnumerateArray().Any(argument =>
                            argument.GetString() is string surface && NonCollidingSurfaces.Contains(surface));
                    }
                }
                if (nonColliding) output.Add(name);
            }
            return output;
        }

        private static readonly HashSet<string> NonCollidingSurfaces = new(
            new[] { "nocollide", "nodraw_nocollide", "effect", "leaves" },
            StringComparer.OrdinalIgnoreCase);

        private static ManifestShape? EmbeddedShape(UberModel.CollisionPart part, string record, string behavior)
        {
            string id = "uber:" + part.Name;
            switch (part.Type)
            {
                case UberModel.CollisionPartType.Sphere:
                    return Shape(id, "sphere", "uber", behavior, center: YUp(part.Center), radius: part.Radius);
                case UberModel.CollisionPartType.Box:
                    return Box(id, "uber", behavior, part.Min, part.Max, Matrix4x4.Identity);
                case UberModel.CollisionPartType.Cylinder:
                    return Shape(id, "cylinder", "uber", behavior, center: YUp(part.Center),
                        radius: part.Radius, height: part.Length);
                case UberModel.CollisionPartType.OrientedBox:
                    return Box(id, "uber", behavior, part.Min, part.Max, part.Transform);
                case UberModel.CollisionPartType.Mesh:
                    return Shape(id, "mesh", "uber", behavior,
                        vertices: part.Vertices.SelectMany(value => YUp(value)).ToArray(),
                        indices: part.Indices.Select(value => (int)value).ToArray());
                default:
                    return null;
            }
        }

        private static ManifestShape Box(string id, string source, string behavior,
            Vector3 min, Vector3 max, Matrix4x4 transform, string group = "static", bool enabled = true, string? node = null)
        {
            Vector3 localCenter = (min + max) * 0.5f;
            Vector3 center = Vector3.Transform(localCenter, transform);
            Vector3 size = max - min;
            Matrix4x4.Decompose(transform, out _, out Quaternion rotation, out _);
            return Shape(id, "box", source, behavior, group, enabled, node, YUp(center),
                new[] { MathF.Abs(size.X), MathF.Abs(size.Z), MathF.Abs(size.Y) }, RotationYUp(rotation));
        }

        private static void AddObjectPhysics(GameObjectDb.GameObject gameObject,
            PhysicsListDatabase physics, List<ManifestShape> output, List<ManifestForcefield> forcefields)
        {
            int initialCount = output.Count;
            AddModelProperty(gameObject, "physics", "active", true, "static", physics, output,
                objectCollidingOnly: true);
            AddModelProperty(gameObject, "physics_deployed", "deployed", false, "static", physics, output,
                objectCollidingOnly: true);
            AddModelProperty(gameObject, "destroyedphysics", "destroyed", false, "destroyed", physics, output,
                objectCollidingOnly: false);

            // Cargo carriers supply a separate fixed loading compound in
            // physics_fixed_misc.lst. It is not part of the flying body's hull.
            // Keep its native ramp/floor/stop/funnel surfaces as an explicit
            // inactive group for the accepted, open-bay workflow.
            if (GameObjectPropertyReader.List(gameObject, "cargomountzone1_cargomountpointindexes").Count > 0)
            {
                string name = Scalar(gameObject, "physics") + "_fixed";
                PhysicsListDatabase.Model model = physics.FindModel(name)
                    ?? throw new InvalidDataException($"Cargo carrier {gameObject.Name} has no fixed loading compound {name}");
                AddPhysicsModel(model, "cargo", false, "cargo", physicsOutput: output);
            }

            foreach ((string key, List<string> values) in gameObject.Properties)
            {
                if (!key.StartsWith("physicsbarrier", StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(key.AsSpan("physicsbarrier".Length), out int index)
                    || values.Count == 0) continue;
                string modelName = GameObjectPropertyReader.Scalar(
                    new[] { new KeyValuePair<string, List<string>>(key, values) }, gameObject.Name, key)!;
                PhysicsListDatabase.Model? model = physics.FindModel(modelName);
                if (model == null) continue;
                // These volumes are not interchangeable. Expansion barriers
                // have a matching visible energy mesh and block hostile
                // infantry. Vehicle barriers deliberately have no visual and
                // are scoped only to vehicle movement. The BFR garage door is
                // excluded from TerraSunder along with the rest of BFR play.
                if (model.Name.Equals("bfr_door_barrier", StringComparison.OrdinalIgnoreCase)) continue;
                string? visual = Scalar(gameObject, "barriername" + index);
                bool hasVisual = !string.IsNullOrWhiteSpace(visual)
                    && !visual.Equals("none", StringComparison.OrdinalIgnoreCase);
                // The six VT hallway volumes are avatar interaction barriers,
                // not vehicle-only walls. Keep their authored transforms and
                // dimensions for the client's training-area prompt. They must
                // not suppress the building's structural AAB collision fallback.
                string behavior = model.Name.Equals("avatar_barrier_virtual_training", StringComparison.OrdinalIgnoreCase)
                    ? "training-zone-trigger" : hasVisual ? "forcefield" : "vehicle-barrier";
                Vector3 position = PropertyVector(gameObject, "barrier" + index);
                float angle = PropertyFloat(gameObject, "anglebarrier" + index) * MathF.PI / 180f;
                AddPhysicsModel(model, $"barrier:{index}", true, behavior, output, position, angle);
                if (hasVisual)
                {
                    forcefields.Add(new ManifestForcefield {
                        Group = $"barrier:{index}",
                        Kind = "barrier",
                        Visual = visual!,
                        Center = YUp(PropertyVector(gameObject, "barriermeshseq" + index)),
                        Rotation = RotationYUp(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle)),
                    });
                }
            }

            if (Scalar(gameObject, "forcedomename") is string domeName
                && (physics.FindTriangleMesh(domeName)
                    ?? physics.FindTriangleMesh(domeName.EndsWith("_physics", StringComparison.OrdinalIgnoreCase)
                        ? domeName[..^"_physics".Length] : domeName)) is PhysicsListDatabase.TriangleMesh dome)
            {
                output.Add(Shape("physics:" + domeName, "mesh", "physics_force_dome", "forcefield",
                    "force-dome", true, null, vertices: dome.Vertices.SelectMany(YUp).ToArray(),
                    indices: Enumerable.Range(0, dome.Vertices.Count).ToArray()));
                string visual = domeName.EndsWith("_physics", StringComparison.OrdinalIgnoreCase)
                    ? domeName[..^"_physics".Length] : domeName;
                forcefields.Add(new ManifestForcefield {
                    Group = "force-dome",
                    Kind = "dome",
                    Visual = visual,
                    Center = new[] { 0f, 0f, 0f },
                    Rotation = new[] { 0f, 0f, 0f, 1f },
                });
            }

            // Door records usually carry their aperture dimensions in game_objects rather than a
            // separate physics model. Keep that native moving blocker distinct from the frame.
            if (gameObject.Type.Equals("door", StringComparison.OrdinalIgnoreCase)
                && output.Count == initialCount
                && PropertyFloat(gameObject, "door_width") is float width && width > 0
                && PropertyFloat(gameObject, "door_height") is float height && height > 0)
            {
                Vector3 sourceCenter = PropertyVector(gameObject, "sphere_offset");
                output.Add(Shape("game_objects:door", "box", "game_objects", "door", "door", true,
                    null, YUp(sourceCenter), new[] { width, height, 0.35f }, new[] { 0f, 0f, 0f, 1f }));
            }
        }

        private static void AddModelProperty(GameObjectDb.GameObject gameObject, string property,
            string group, bool enabled, string behavior, PhysicsListDatabase physics,
            List<ManifestShape> output, bool objectCollidingOnly)
        {
            string? name = Scalar(gameObject, property);
            PhysicsListDatabase.Model? model = physics.FindModel(name);
            if (property == "physics_deployed" && name is not null
                && !name.Equals("none", StringComparison.OrdinalIgnoreCase) && model is null)
                throw new InvalidDataException($"{gameObject.Name}.{property}: native physics model '{name}' is missing");
            if (model != null) AddPhysicsModel(
                model, group, enabled, behavior, output, Vector3.Zero, 0f, objectCollidingOnly);
        }

        private static void AddPhysicsModel(PhysicsListDatabase.Model model, string group, bool enabled,
            string behavior, List<ManifestShape> physicsOutput) =>
            AddPhysicsModel(model, group, enabled, behavior, physicsOutput, Vector3.Zero, 0f);

        private static void AddPhysicsModel(PhysicsListDatabase.Model model, string group, bool enabled,
            string behavior, List<ManifestShape> output, Vector3 offset, float yaw,
            bool objectCollidingOnly = false)
        {
            Matrix4x4 placement = Matrix4x4.CreateRotationZ(yaw) * Matrix4x4.CreateTranslation(offset);
            foreach (PhysicsListDatabase.Shape primitive in
                VehicleManifestContract.CollisionPrimitives(model, objectCollidingOnly))
            {
                Matrix4x4 local = Matrix4x4.CreateFromYawPitchRoll(
                    primitive.Orientation.Y, primitive.Orientation.X, primitive.Orientation.Z)
                    * Matrix4x4.CreateTranslation(primitive.Position) * placement;
                Matrix4x4.Decompose(local, out _, out Quaternion rotation, out Vector3 center);
                string? node = primitive.Cookie.Length > 0 && !primitive.Cookie.Equals("root", StringComparison.OrdinalIgnoreCase)
                    ? primitive.Cookie : null;
                string id = $"physics:{model.Name}:{primitive.Name}";
                ManifestShape? shape = primitive.Kind switch
                {
                    PhysicsListDatabase.ShapeKind.Box => Shape(id, "box", "physics_lst", behavior,
                        group, enabled, node, YUp(center),
                        new[] { primitive.Size.X, primitive.Size.Z, primitive.Size.Y }, RotationYUp(rotation)),
                    PhysicsListDatabase.ShapeKind.Sphere => Shape(id, "sphere", "physics_lst", behavior,
                        group, enabled, node, YUp(center), radius: primitive.Radius),
                    PhysicsListDatabase.ShapeKind.Cylinder => Shape(id, "cylinder", "physics_lst", behavior,
                        group, enabled, node, YUp(center), rotation: RotationYUp(rotation),
                        radius: primitive.Radius, height: primitive.Length),
                    _ => null,
                };
                if (shape != null) output.Add(shape);
            }
        }

        private static ManifestShape Shape(string id, string type, string source, string behavior,
            string group = "static", bool enabled = true, string? node = null, float[]? center = null,
            float[]? size = null, float[]? rotation = null, float? radius = null, float? height = null,
            float[]? vertices = null, int[]? indices = null) => new() {
                Id = id, Type = type, Source = source, Group = group, Behavior = behavior, Enabled = enabled,
                Node = node, Center = center, Size = size, Rotation = rotation, Radius = radius, Height = height,
                Vertices = vertices, Indices = indices,
            };

        private static string BehaviorFor(string record) =>
            record.Contains("door", StringComparison.OrdinalIgnoreCase) ? "door"
            : record.Contains("forcefield", StringComparison.OrdinalIgnoreCase) || record.Contains("barrier", StringComparison.OrdinalIgnoreCase) ? "forcefield"
            : record.Contains("pad", StringComparison.OrdinalIgnoreCase) ? "pad"
            : "static";

        private static string? Scalar(GameObjectDb.GameObject value, string key) =>
            GameObjectPropertyReader.Scalar(value, key);
        private static Vector3 PropertyVector(GameObjectDb.GameObject value, string key)
        {
            IReadOnlyList<string>? fields = GameObjectPropertyReader.Tuple(value, key, 3);
            return fields is null ? Vector3.Zero : new Vector3(F(fields[0]), F(fields[1]), F(fields[2]));
        }
        private static float PropertyFloat(GameObjectDb.GameObject value, string key) =>
            GameObjectPropertyReader.Scalar(value, key) is string field ? F(field) : 0f;
        private static float F(string value) => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        private static float[] YUp(Vector3 value) => new[] { value.X, value.Z, -value.Y };
        private static float[] RotationYUp(Quaternion source)
        {
            Matrix4x4 basis = Matrix4x4.CreateRotationX(-MathF.PI / 2f);
            Matrix4x4.Invert(basis, out Matrix4x4 inverse);
            Matrix4x4 converted = inverse * Matrix4x4.CreateFromQuaternion(source) * basis;
            Matrix4x4.Decompose(converted, out _, out Quaternion result, out _);
            return new[] { result.X, result.Y, result.Z, result.W };
        }

        private static readonly JsonSerializerOptions JsonOptions = new() {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
