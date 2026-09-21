using System.Globalization;
using System.Numerics;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Maps;

namespace Raximod.Generation.Continents;

/// <summary>
/// Resolves the separately-authored visual shell for warpgates whose gameplay record has no
/// meshsequence. Retail associates these meshes through the warpgate barrier contract rather than
/// through the MPO composite: each mesh radius matches its <c>wrp_barrier_radius</c> and uses the
/// native <c>warpgate_dome</c> alpha-sorted surface.
/// </summary>
public static class WarpgateBarrierCatalog
{
    public sealed record Definition(
        string SourceRecord,
        string Visual,
        string Physics,
        float Radius,
        Vector3 SourceOffset,
        GameObjectDb.GameObjectProvenance Provenance);

    public sealed record Placement(
        Definition Definition,
        Vector3 Position,
        Quaternion Rotation,
        Vector3 Scale);

    private static readonly IReadOnlyDictionary<string, string> NativeVisuals =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["warpgate"] = "dome3",
            ["warpgate_small"] = "warpgate_small_dome",
            ["warpgate_cavern"] = "warpgate_cavern_dome",
        };

    public static IReadOnlyDictionary<string, Definition> Read(GameObjectDb database)
    {
        Dictionary<string, GameObjectDb.GameObject> objects = database.ResolvedObjects
            .ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var definitions = new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase);
        foreach ((string sourceRecord, string visual) in NativeVisuals)
        {
            if (!objects.TryGetValue(sourceRecord, out GameObjectDb.GameObject? gameObject))
                throw new InvalidDataException($"game_objects.adb is missing warpgate record '{sourceRecord}'");
            if (!gameObject.Type.Equals("warpgate", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"'{sourceRecord}' is no longer a warpgate record");
            string physics = RequiredScalar(gameObject, "wrp_barrier_physics");
            float radius = RequiredFloat(gameObject, "wrp_barrier_radius");
            IReadOnlyList<string> offset = GameObjectPropertyReader.Tuple(
                gameObject, "wrp_barrier_offset", 3)
                ?? throw new InvalidDataException($"'{sourceRecord}' is missing wrp_barrier_offset");
            definitions.Add(sourceRecord, new Definition(
                sourceRecord,
                visual,
                physics,
                radius,
                new Vector3(Float(offset[0]), Float(offset[1]), Float(offset[2])),
                gameObject.Provenance));
        }
        return definitions;
    }

    public static IReadOnlyList<Placement> Build(
        IEnumerable<MapObject> mapObjects,
        IReadOnlyDictionary<string, Definition> definitions)
    {
        var output = new List<Placement>();
        foreach (MapObject mapObject in mapObjects)
        {
            if (!definitions.TryGetValue(mapObject.Name, out Definition? definition)) continue;
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, mapObject.Yaw);
            Vector3 sourceOffset = definition.SourceOffset * mapObject.Scale;
            Vector3 localOffset = new(sourceOffset.X, sourceOffset.Z, -sourceOffset.Y);
            Vector3 parentPosition = new(mapObject.Position.X, mapObject.Position.Z, -mapObject.Position.Y);
            output.Add(new Placement(
                definition,
                parentPosition + Vector3.Transform(localOffset, rotation),
                rotation,
                new Vector3(mapObject.Scale.X, mapObject.Scale.Z, mapObject.Scale.Y)));
        }
        return output;
    }

    private static string RequiredScalar(GameObjectDb.GameObject gameObject, string property) =>
        GameObjectPropertyReader.Scalar(gameObject, property)
        ?? throw new InvalidDataException($"'{gameObject.Name}' is missing {property}");

    private static float RequiredFloat(GameObjectDb.GameObject gameObject, string property) =>
        Float(RequiredScalar(gameObject, property));

    private static float Float(string value) =>
        float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
}
