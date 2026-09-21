using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets;

/// <summary>
/// Shared invariants for the transport-neutral vehicle manifest. Keeping these checks outside the
/// CLI prevents the exporter and its tests from drifting into different definitions of a body.
/// </summary>
public static class VehicleManifestContract
{
    // PSForever's optional Vector3 is a server dismount position, not the native
    // entrance. Parse the binding without losing HART's eight two-argument entries.
    public static Match ServerMountDeclaration(string line)
    {
        var match = Regex.Match(line,
            @"^\s*(\w+)\.MountPoints\s*\+=\s*(\d+)\s*->\s*MountInfo\(\s*(\d+)\s*(?:,\s*Vector3\([^()]*\)\s*)?\)");
        if (!match.Success && Regex.IsMatch(line, @"^\s*\w+\.MountPoints\s*\+="))
            throw new InvalidDataException($"Unsupported PSForever mount declaration: {line.Trim()}");
        return match;
    }

    public const int SchemaVersion = 14;
    public const string ModelAssetCoordinateSystem = "right-handed-y-up";
    public const string NativeDataCoordinateSystem = "right-handed-z-up";
    public const string UnsupportedRuntimeFidelity = "transported-unsupported";
    public const string CenterOfMassRuntimeFidelity = "runtime-consumed-approximation";
    public const string PilotFlightControl = "pilot";
    public const string DropPodFlightControl = "drop-pod";
    public const string ServerScriptedFlightControl = "server-scripted";

    public static IReadOnlyDictionary<string, int> OrdinaryAircraftBodyPrimitiveCounts { get; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["mosquito"] = 22,
            ["lightgunship"] = 22,
            ["wasp"] = 22,
            ["liberator"] = 36,
            ["vulture"] = 36,
            ["dropship"] = 49,
            ["galaxy_gunship"] = 49,
            ["lodestar"] = 30,
            ["phantasm"] = 28,
        };

    public static IReadOnlyDictionary<string, string> NonBfrFlightControlClassifications { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mosquito"] = PilotFlightControl,
            ["lightgunship"] = PilotFlightControl,
            ["wasp"] = PilotFlightControl,
            ["liberator"] = PilotFlightControl,
            ["vulture"] = PilotFlightControl,
            ["dropship"] = PilotFlightControl,
            ["galaxy_gunship"] = PilotFlightControl,
            ["lodestar"] = PilotFlightControl,
            ["phantasm"] = PilotFlightControl,
            ["droppod"] = DropPodFlightControl,
            ["orbital_shuttle"] = ServerScriptedFlightControl,
        };

    public sealed record TrackedTreadPresentation(
        int WheelIndex,
        float Direction,
        bool RightTread,
        float RollLength,
        string Material);

    public static string? ResolveAnimationAttachBone(
        string definition,
        string? definitionBone,
        string? sourceModelBone,
        IEnumerable<string> emittedNodes)
    {
        string?[] candidates = [definitionBone, sourceModelBone];
        string[] authored = candidates
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (authored.Length == 0) return null;
        var nodes = emittedNodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? resolved = authored.FirstOrDefault(nodes.Contains);
        if (resolved != null) return resolved;
        string? bodyRoot = authored
            .Where(value => value.EndsWith("_body", StringComparison.OrdinalIgnoreCase))
            .Select(value => value[..^"_body".Length])
            .FirstOrDefault(nodes.Contains);
        if (bodyRoot != null) return bodyRoot;
        // Some gameplay aliases retain the definition's *_body name while the
        // selected render record uses another vehicle-family body node (for
        // example battlewagon -> deliverer_body). Resolve that data-driven
        // alias only when the emitted model has one unambiguous body node.
        if (authored.All(value => value.EndsWith("_body", StringComparison.OrdinalIgnoreCase)))
        {
            string[] emittedBodies = nodes
                .Where(value => value.EndsWith("_body", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (emittedBodies.Length == 1) return emittedBodies[0];
        }
        throw new InvalidDataException(
            $"Vehicle '{definition}' animation attach bone(s) [{string.Join(", ", authored)}] "
            + "do not exist in its emitted model");
    }

    public static IReadOnlyDictionary<string, TrackedTreadPresentation[]> TrackedVehiclePresentations
        { get; } = new Dictionary<string, TrackedTreadPresentation[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["lightning"] =
            [
                new(1, -1, false, 1.6f, "vehiclegentread_2b"),
                new(2, -1, true, 1.6f, "vehiclegentread_2a"),
            ],
            ["prowler"] =
            [
                new(1, 1, false, 1f, "vehiclegentread_3b"),
                new(2, 1, true, 1f, "vehiclegentread_3a"),
            ],
            ["vanguard"] =
            [
                new(1, 1, false, 0.56015f, "vehiclegentread_1a"),
                new(2, 1, true, 0.56015f, "vehiclegentread_1b"),
            ],
        };

    public static void RequireExpectedTrackedVehiclePresentations(
        IReadOnlyDictionary<string, TrackedTreadPresentation[]> actual)
    {
        RequireSameIds("tracked vehicle presentation", TrackedVehiclePresentations.Keys, actual.Keys);
        foreach ((string definition, TrackedTreadPresentation[] expected) in TrackedVehiclePresentations)
        {
            if (!actual.TryGetValue(definition, out TrackedTreadPresentation[]? found))
                throw new InvalidDataException($"Tracked vehicle presentation is missing '{definition}'");
            TrackedTreadPresentation[] ordered = found.OrderBy(value => value.WheelIndex).ToArray();
            if (!expected.SequenceEqual(ordered))
                throw new InvalidDataException(
                    $"Tracked vehicle '{definition}' tread presentation differs from the audited client corpus");
        }
    }

    public sealed record PhysicsCoverage(
        string Definition,
        string PhysicsModel,
        int CollidingBodyPrimitiveCount);

    public sealed record ActiveCollisionShape(string Id, Vector3 SourceCenter);

    public sealed record EmittedCollisionShape(string Id, float[]? BrowserCenter);

    public static string PrimitiveRole(
        PhysicsListDatabase.Model model,
        PhysicsListDatabase.Shape primitive)
    {
        bool wheelContact = model.Shapes.Any(shape =>
            shape.Kind == PhysicsListDatabase.ShapeKind.CarWheel
            && shape.Primitive.Equals(primitive.Name, StringComparison.OrdinalIgnoreCase));
        return wheelContact ? "wheel-contact" : "body";
    }

    /// <summary>
    /// Returns native geometry primitives for collision export. A car-wheel is a constraint rather
    /// than geometry. Active vehicle collision additionally keeps only primitives whose native
    /// <c>phys_model_collides_with_objects</c> value is true; destroyed groups retain every authored
    /// geometry primitive because they are presentation/state provenance, not the mover footprint.
    /// </summary>
    public static PhysicsListDatabase.Shape[] CollisionPrimitives(
        PhysicsListDatabase.Model model,
        bool objectCollidingOnly) =>
        model.Shapes
            .Where(shape => shape.Kind != PhysicsListDatabase.ShapeKind.CarWheel
                && (!objectCollidingOnly || shape.CollidesWithObjects))
            .ToArray();

    /// <summary>
    /// Returns the exact native primitives used by the vehicle mover for object collision.
    /// </summary>
    public static ActiveCollisionShape[] ActiveObjectCollisionShapes(PhysicsListDatabase.Model model) =>
        CollisionPrimitives(model, objectCollidingOnly: true)
            .Select(shape => new ActiveCollisionShape(
                $"physics:{model.Name}:{shape.Name}",
                shape.Position))
            .OrderBy(shape => shape.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static void RequireEquivalentActiveCollisionContracts(
        string context,
        IReadOnlyCollection<ActiveCollisionShape> expected,
        IReadOnlyCollection<ActiveCollisionShape> actual)
    {
        Dictionary<string, ActiveCollisionShape> expectedById = ActiveShapesById(context, expected);
        Dictionary<string, ActiveCollisionShape> actualById = ActiveShapesById(context, actual);
        RequireSameIds(context, expectedById.Keys, actualById.Keys);
        foreach ((string id, ActiveCollisionShape source) in expectedById)
        {
            Vector3 actualSourceCenter = actualById[id].SourceCenter;
            if (Vector3.DistanceSquared(source.SourceCenter, actualSourceCenter) > 1e-8f)
                throw new InvalidDataException(
                    $"{context} active collision '{id}' source center differs; expected "
                    + $"{source.SourceCenter}, found {actualSourceCenter}");
        }
    }

    public static void RequireMatchingActiveCollisionSidecar(
        string context,
        IReadOnlyCollection<ActiveCollisionShape> expected,
        IReadOnlyCollection<EmittedCollisionShape> actual)
    {
        Dictionary<string, ActiveCollisionShape> expectedById = ActiveShapesById(context, expected);
        Dictionary<string, EmittedCollisionShape> actualById;
        try
        {
            actualById = actual.ToDictionary(shape => shape.Id, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"{context} has duplicate active collision shape IDs", exception);
        }

        RequireSameIds(context, expectedById.Keys, actualById.Keys);

        foreach ((string id, ActiveCollisionShape source) in expectedById)
        {
            float[]? browser = actualById[id].BrowserCenter;
            if (browser is not { Length: 3 })
                throw new InvalidDataException($"{context} active collision '{id}' has no 3D center");
            // Collision sidecars use the GLB basis (x,z,-y); compare after reversing that one
            // conversion so an ID match cannot conceal a collider sourced from the wrong record.
            var actualSourceCenter = new Vector3(browser[0], -browser[2], browser[1]);
            if (Vector3.DistanceSquared(source.SourceCenter, actualSourceCenter) > 1e-8f)
                throw new InvalidDataException(
                    $"{context} active collision '{id}' source center differs; expected "
                    + $"{source.SourceCenter}, found {actualSourceCenter}");
        }
    }

    /// <summary>
    /// Audits the serialized publication artifacts, not merely their source objects. Every vehicle
    /// definition must resolve its model sidecar, and that sidecar's active native shapes must equal
    /// the manifest mover primitives by ID and source-basis center. Shared GLBs are deliberately
    /// checked once per definition so an alias cannot escape the family coverage count.
    /// </summary>
    public static int RequireGeneratedCollisionCoverage(
        string manifestJson,
        string modelsDirectory,
        int expectedDefinitionCount)
    {
        using JsonDocument manifest = JsonDocument.Parse(manifestJson);
        if (!manifest.RootElement.TryGetProperty("vehicles", out JsonElement vehicles)
            || vehicles.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Vehicle manifest has no vehicles array");

        var definitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int audited = 0;
        foreach (JsonElement vehicle in vehicles.EnumerateArray())
        {
            string definition = RequiredString(vehicle, "definition", "vehicle manifest record");
            if (!definitions.Add(definition))
                throw new InvalidDataException(
                    $"Vehicle collision coverage contains duplicate definition '{definition}'");
            string modelUri = RequiredString(vehicle, "model", $"vehicle '{definition}'");
            string record = Path.GetFileNameWithoutExtension(modelUri);
            if (string.IsNullOrWhiteSpace(record))
                throw new InvalidDataException(
                    $"Vehicle '{definition}' has invalid model URI '{modelUri}'");
            if (!vehicle.TryGetProperty("physics", out JsonElement physics)
                || physics.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Vehicle '{definition}' has no physics object");
            string physicsModel = RequiredString(physics, "model", $"vehicle '{definition}' physics");
            if (!physics.TryGetProperty("primitives", out JsonElement primitives)
                || primitives.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Vehicle '{definition}' physics has no primitives array");

            var expected = new List<ActiveCollisionShape>();
            foreach (JsonElement primitive in primitives.EnumerateArray())
            {
                if (!primitive.TryGetProperty("collidesWithObjects", out JsonElement collides)
                    || collides.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidDataException(
                        $"Vehicle '{definition}' physics primitive has no boolean collidesWithObjects");
                }
                if (collides.ValueKind == JsonValueKind.False) continue;
                string role = RequiredString(
                    primitive, "role", $"vehicle '{definition}' physics primitive");
                if (!role.Equals("body", StringComparison.Ordinal)
                    && !role.Equals("wheel-contact", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Vehicle '{definition}' has object-colliding primitive with unknown role '{role}'");
                }
                string name = RequiredString(
                    primitive, "name", $"vehicle '{definition}' physics primitive");
                expected.Add(new ActiveCollisionShape(
                    $"physics:{physicsModel}:{name}",
                    RequiredVector3(
                        primitive, "position", $"vehicle '{definition}' physics primitive '{name}'")));
            }

            string sidecarPath = Path.Combine(modelsDirectory, record + ".collision.json");
            if (!File.Exists(sidecarPath))
                throw new InvalidDataException(
                    $"Vehicle '{definition}' collision sidecar is missing: {sidecarPath}");
            using JsonDocument sidecar = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            string sidecarRecord = RequiredString(
                sidecar.RootElement, "record", $"vehicle '{definition}' collision sidecar");
            if (!sidecarRecord.Equals(record, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Vehicle '{definition}' expected collision record '{record}', found '{sidecarRecord}'");
            if (!sidecar.RootElement.TryGetProperty("shapes", out JsonElement shapes)
                || shapes.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException(
                    $"Vehicle '{definition}' collision sidecar has no shapes array");

            EmittedCollisionShape[] active = shapes.EnumerateArray()
                .Where(shape => StringPropertyEquals(shape, "source", "physics_lst")
                    && StringPropertyEquals(shape, "group", "active")
                    && shape.TryGetProperty("enabled", out JsonElement enabled)
                    && enabled.ValueKind is JsonValueKind.True)
                .Select(shape => new EmittedCollisionShape(
                    RequiredString(shape, "id", $"vehicle '{definition}' active collision shape"),
                    RequiredFloatArray(
                        shape, "center", $"vehicle '{definition}' active collision shape")))
                .ToArray();
            RequireMatchingActiveCollisionSidecar(
                $"vehicle '{definition}' collision record '{record}'", expected, active);

            if (vehicle.TryGetProperty("destruction", out JsonElement destruction)
                && destruction.ValueKind == JsonValueKind.Object
                && OptionalString(destruction, "destroyedPhysics") is string destroyedPhysics
                && !destroyedPhysics.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                string[] destroyedIds = shapes.EnumerateArray()
                    .Where(shape => StringPropertyEquals(shape, "source", "physics_lst")
                        && StringPropertyEquals(shape, "group", "destroyed")
                        && shape.TryGetProperty("enabled", out JsonElement enabled)
                        && enabled.ValueKind is JsonValueKind.False)
                    .Select(shape => RequiredString(
                        shape, "id", $"vehicle '{definition}' destroyed collision shape"))
                    .ToArray();
                string prefix = $"physics:{destroyedPhysics}:";
                if (destroyedIds.Length == 0
                    || destroyedIds.Any(id => !id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException(
                        $"Vehicle '{definition}' collision record '{record}' does not retain destroyed "
                        + $"physics group '{destroyedPhysics}'");
                }
            }
            audited++;
        }

        if (audited != expectedDefinitionCount)
            throw new InvalidDataException(
                $"Vehicle collision publication audited {audited} of {expectedDefinitionCount} definitions");
        return audited;
    }

    private static Dictionary<string, ActiveCollisionShape> ActiveShapesById(
        string context,
        IReadOnlyCollection<ActiveCollisionShape> shapes)
    {
        try
        {
            return shapes.ToDictionary(shape => shape.Id, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"{context} has duplicate active collision shape IDs", exception);
        }
    }

    private static string RequiredString(JsonElement value, string property, string context) =>
        OptionalString(value, property)
        ?? throw new InvalidDataException($"{context} has no string {property}");

    private static string? OptionalString(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement field)
            && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    private static bool StringPropertyEquals(
        JsonElement value,
        string property,
        string expected) =>
        string.Equals(OptionalString(value, property), expected, StringComparison.OrdinalIgnoreCase);

    private static Vector3 RequiredVector3(JsonElement value, string property, string context)
    {
        float[] fields = RequiredFloatArray(value, property, context);
        if (fields.Length != 3)
            throw new InvalidDataException($"{context} {property} is not a 3D vector");
        return new Vector3(fields[0], fields[1], fields[2]);
    }

    private static float[] RequiredFloatArray(JsonElement value, string property, string context)
    {
        if (!value.TryGetProperty(property, out JsonElement fields)
            || fields.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{context} has no numeric {property} array");
        try
        {
            return fields.EnumerateArray().Select(field => field.GetSingle()).ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"{context} has invalid numeric {property} values", exception);
        }
    }

    private static void RequireSameIds(
        string context,
        IEnumerable<string> expected,
        IEnumerable<string> actual)
    {
        string[] missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] unexpected = actual.Except(expected, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0 || unexpected.Length > 0)
            throw new InvalidDataException(
                $"{context} active collision IDs differ; missing: "
                + (missing.Length == 0 ? "none" : string.Join(", ", missing))
                + "; unexpected: "
                + (unexpected.Length == 0 ? "none" : string.Join(", ", unexpected)));
    }

    public static string? ClassifyFlightControl(
        string definition,
        string sourceRecord,
        bool canFly,
        bool? flightIsAlwaysServerControlled)
    {
        if (!canFly) return null;
        if (definition.Equals("droppod", StringComparison.OrdinalIgnoreCase)
            || sourceRecord.Equals("droppod", StringComparison.OrdinalIgnoreCase))
            return DropPodFlightControl;
        return flightIsAlwaysServerControlled == true
            ? ServerScriptedFlightControl
            : PilotFlightControl;
    }

    public static void RequireExpectedFlightControls(
        IReadOnlyDictionary<string, string> actual,
        IReadOnlyDictionary<string, string> expected)
    {
        string[] missing = expected.Keys.Except(actual.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] unexpected = actual.Keys.Except(expected.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0 || unexpected.Length > 0)
            throw new InvalidDataException(
                "Non-BFR flight-control corpus changed; missing: "
                + (missing.Length == 0 ? "none" : string.Join(", ", missing))
                + "; unexpected: "
                + (unexpected.Length == 0 ? "none" : string.Join(", ", unexpected)));
        foreach ((string definition, string expectedClassification) in expected)
        {
            string actualClassification = actual[definition];
            if (!actualClassification.Equals(expectedClassification, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Non-BFR flight-control corpus expected '{definition}' to be "
                    + $"'{expectedClassification}', found '{actualClassification}'");
        }
    }

    /// <summary>
    /// Requires an authored physics reference, its resolved model, and at least one object-colliding
    /// body primitive. Wheel-contact primitives never satisfy the body-footprint invariant.
    /// </summary>
    public static PhysicsCoverage RequireVehiclePhysics(
        PhysicsListDatabase database,
        string definition,
        string? authoredPhysicsName)
    {
        if (string.IsNullOrWhiteSpace(authoredPhysicsName))
            throw new InvalidDataException(
                $"Supported non-BFR vehicle '{definition}' has no authored physics name");

        PhysicsListDatabase.Model model = database.FindModel(authoredPhysicsName)
            ?? throw new InvalidDataException(
                $"Supported non-BFR vehicle '{definition}' references missing physics model "
                + $"'{authoredPhysicsName}'");
        return RequireCollidingBody(model, definition, "Supported non-BFR vehicle");
    }

    /// <summary>
    /// Static turret records are presentation/mount exports and some same-named identifiers have no
    /// physics-list model. A model that does resolve is still required to contain a real body.
    /// </summary>
    public static PhysicsCoverage? AuditTurretPhysics(
        PhysicsListDatabase database,
        string definition,
        string? authoredPhysicsName)
    {
        PhysicsListDatabase.Model? model = database.FindModel(authoredPhysicsName);
        return model == null ? null : RequireCollidingBody(model, definition, "Turret");
    }

    private static PhysicsCoverage RequireCollidingBody(
        PhysicsListDatabase.Model model,
        string definition,
        string subject)
    {
        int bodyCount = model.Shapes.Count(shape =>
            shape.Kind != PhysicsListDatabase.ShapeKind.CarWheel
            && PrimitiveRole(model, shape) == "body"
            && shape.CollidesWithObjects);
        if (bodyCount == 0)
            throw new InvalidDataException(
                $"{subject} '{definition}' physics model '{model.Name}' has zero "
                + "role=body primitives that collide with objects");
        return new PhysicsCoverage(definition, model.Name, bodyCount);
    }

    public static void RequireExpectedBodyCounts(
        IReadOnlyDictionary<string, PhysicsCoverage> actual,
        IReadOnlyDictionary<string, int> expected)
    {
        foreach ((string definition, int expectedCount) in expected)
        {
            if (!actual.TryGetValue(definition, out PhysicsCoverage? coverage))
                throw new InvalidDataException(
                    $"Vehicle physics corpus audit has no coverage for '{definition}'");
            if (coverage.CollidingBodyPrimitiveCount != expectedCount)
                throw new InvalidDataException(
                    $"Vehicle physics corpus audit expected '{definition}' to have {expectedCount} "
                    + $"object-colliding body primitives, found {coverage.CollidingBodyPrimitiveCount} "
                    + $"in '{coverage.PhysicsModel}'");
        }
    }
}
