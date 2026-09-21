using System.Text.Json.Serialization;
using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets;

/// <summary>Resolve presentation identities from authored records, independently of gameplay/physics identity.</summary>
public static class VehiclePresentationBindings
{
    public static string BodyRecord(string sourceRecord, params GameObjectDb.GameObject?[] sources)
    {
        string[] sequence = [];
        foreach (var source in sources)
        {
            var authored = GameObjectPropertyReader.List(source, "meshsequence").ToArray();
            if (authored.Length > 0) sequence = authored;
        }
        // Missing meshsequence retains the explicitly registered source record.
        // A present sequence must never silently lose later members.
        return sequence.Length switch
        {
            0 => sourceRecord,
            1 => sequence[0],
            _ => throw new InvalidDataException($"Vehicle '{sourceRecord}' requires a multi-body meshsequence: {string.Join(", ", sequence)}"),
        };
    }

    public sealed record BodyComponent(string Record, string PlatformBone, string ChildBone);

    public static BodyComponent[] BodyComponents(params GameObjectDb.GameObject?[] sources)
    {
        IReadOnlyList<string> Values(string key) => sources.Reverse().Select(source => GameObjectPropertyReader.List(source, key))
            .FirstOrDefault(values => values.Count > 0) ?? [];
        var meshes = Values("attach_sequence_mesh_name");
        var platforms = Values("attach_sequence_my_bone_name");
        var children = Values("attach_sequence_child_bone_name");
        if (meshes.Count != platforms.Count || meshes.Count != children.Count)
            throw new InvalidDataException("Vehicle attach_sequence lists must have matching cardinality");
        return meshes.Select((record, index) => new BodyComponent(record, platforms[index], children[index])).ToArray();
    }

    public sealed record DeploymentTransform(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("child")] string? Child,
        [property: JsonPropertyName("rotationBone")] string? RotationBone,
        [property: JsonPropertyName("rotationDegrees")] float[]? RotationDegrees,
        [property: JsonPropertyName("translationBone")] string? TranslationBone,
        [property: JsonPropertyName("translation")] float[]? Translation);

    public static DeploymentTransform[] Deployment(GameObjectDb.GameObject source)
    {
        string? Value(string key) => GameObjectPropertyReader.Scalar(source, key);
        float[]? Vector(string key) => GameObjectPropertyReader.Tuple(source, key, 3)?
            .Select(value => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var indices = source.Properties.Keys.Select(key => System.Text.RegularExpressions.Regex.Match(key, @"^deploy(\d+)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Where(match => match.Success).Select(match => int.Parse(match.Groups[1].Value)).Distinct().Order();
        return indices.Select(index => new DeploymentTransform(index, Value($"deploy{index}_childname"),
            Value($"deploy{index}_rotbonename"), Vector($"deploy{index}_rotmaxangle"),
            Value($"deploy{index}_transbonename"), Vector($"deploy{index}_transmaxdist"))).ToArray();
    }

    public static void RequireMounts(string context, IEnumerable<string> requested, IEnumerable<string> available)
    {
        var nodes = available.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = requested.Distinct(StringComparer.OrdinalIgnoreCase).Where(name => !nodes.Contains(name)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"{context}: missing authored platform joints: {string.Join(", ", missing)}");
    }

    public sealed record MuzzleComponent(int Index, string Record, string[] Nodes, NativeEffectPackage? Effects);
    public sealed record MuzzleBinding(
        [property: JsonPropertyName("requested")] string Requested,
        [property: JsonPropertyName("bone")] string Bone,
        [property: JsonPropertyName("componentIndices")] int[] ComponentIndices,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("event")] string? Event);

    public static MuzzleBinding[] Muzzles(string context, string[] requested, string fireEvent, MuzzleComponent[] components)
    {
        return requested.Select(name =>
        {
            var exact = components.Where(c => c.Nodes.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (exact.Length > 0)
                return new MuzzleBinding(name, name, exact.Select(c => c.Index).ToArray(), "native-muzzle", null);

            // Some original logical weapons retain a muzzle from an older mesh.
            // Use the selected component's authored firing-event socket, the same
            // source already used for its muzzle flash. This is an explicit browser
            // repair policy, not a claim about retail's missing-name fallback.
            var effects = components.SelectMany(c => (c.Effects?.Effects ?? [])
                .Where(e => string.Equals(e.Event, fireEvent, StringComparison.OrdinalIgnoreCase)
                    && e.Attachment is not null && c.Nodes.Contains(e.Attachment, StringComparer.OrdinalIgnoreCase))
                .Select(e => (Component: c.Index, Bone: e.Attachment!))).ToArray();
            var bones = effects.Select(e => e.Bone).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (bones.Length != 1)
                throw new InvalidDataException($"{context}: unresolved muzzle '{name}'; {fireEvent} has {bones.Length} distinct native attachment candidates");
            return new MuzzleBinding(name, bones[0], effects.Select(e => e.Component).Distinct().ToArray(), "native-fire-event", fireEvent);
        }).ToArray();
    }
}
