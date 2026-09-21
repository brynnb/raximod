using System.Numerics;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Assets;

/// <summary>Exit endpoint evidence from the original ANIM tracks and UBR parent hierarchy.
/// Retail 0x57d400 uses Bip01 XY and Bip01_R_Toe0 Z, replacing XY with the retained entrance
/// when present. Keep full vectors: bank/pitch must be applied before selecting world height.</summary>
public sealed class VehicleExitBindings
{
    public sealed record Endpoint(string Rig, string SourceRecord, float[] Root, float[] RightToe, float[] Rotation);
    private readonly IReadOnlyDictionary<string, AnimRecord> clips;
    private readonly Dictionary<string, (string Record, UberModel.Skeleton Skeleton)> rigs = new();

    public VehicleExitBindings(IReadOnlyList<AnimRecord> clips,
        IReadOnlyDictionary<string, (string Record, UberModel.Skeleton Skeleton)> rigs)
    {
        this.clips = clips.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        this.rigs = new(rigs);
    }

    public Endpoint[] Resolve(string clipName, string occupant)
    {
        var clip = clips[clipName];
        string[] selected = occupant == "infantry" ? ["infantry-female", "infantry-male"] : [occupant];
        return selected.Select(id => {
            var (record, skeleton) = rigs[id];
            Matrix4x4 root = SampleBone(skeleton, clip, "Bip01");
            Matrix4x4 toe = SampleBone(skeleton, clip, "Bip01_R_Toe0");
            if (!Matrix4x4.Decompose(root, out _, out Quaternion rotation, out Vector3 position))
                throw new InvalidDataException($"{clipName}/{record}: invalid exit transform");
            return new Endpoint(id, record, Vector(position), Vector(toe.Translation),
                [rotation.X, rotation.Y, rotation.Z, rotation.W]);
        }).ToArray();
    }

    public static Matrix4x4 SampleBone(UberModel.Skeleton skeleton, AnimRecord clip, string name)
    {
        var indices = skeleton.Bones.Select((bone, index) => (bone, index))
            .Where(v => v.bone.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (indices.Length != 1) throw new InvalidDataException($"{skeleton.Name}: expected one {name}");
        var tracks = clip.Tracks.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<int>();
        var result = Matrix4x4.Identity;
        for (int index = indices[0].index; index >= 0; index = skeleton.Bones[index].Parent)
        {
            if (index >= skeleton.Bones.Count || !visited.Add(index))
                throw new InvalidDataException($"{skeleton.Name}: invalid parent chain for {name}");
            var bone = skeleton.Bones[index];
            var track = tracks.GetValueOrDefault(bone.Name);
            Vector3 position = track?.SamplePosition(clip.Duration) ?? bone.Position;
            Quaternion rotation = track?.SampleRotation(clip.Duration) ?? bone.Rotation;
            if (!Vector(position).All(float.IsFinite) || !float.IsFinite(rotation.LengthSquared())
                || rotation.LengthSquared() < 1e-12f)
                throw new InvalidDataException($"{clip.Name}/{bone.Name}: non-finite endpoint");
            result *= Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(position);
        }
        return result;
    }

    public static VehicleExitBindings Load(string directory, IReadOnlyList<AnimRecord> clips)
    {
        // These are the same original rig records used by ExportPlayers' five animation banks.
        var records = new Dictionary<string, string> {
            ["infantry-female"] = "ncflite", ["infantry-male"] = "ncmlite",
            ["max-nc"] = "nchev", ["max-tr"] = "trhev", ["max-vs"] = "vshev",
        };
        string[] libraries = ["uber.ubr", "patch1/patch1.ubr", "patch2/patch2.ubr", "patch3/patch3.ubr",
            "patch4/patch4.ubr", "patch5/patch5.ubr", "expansion1/expansion1.ubr"];
        var candidates = libraries.Select(relative => Path.Combine(directory, relative)).Where(File.Exists)
            .ToDictionary(path => path, path => GlbExportTool.ListRecords(path).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var decoded = new Dictionary<string, UberModel>();
        var rigs = new Dictionary<string, (string, UberModel.Skeleton)>();
        foreach (var (id, record) in records)
        {
            var owners = candidates.Where(p => p.Value.Contains(record)).Select(p => p.Key).ToArray();
            if (owners.Length != 1) throw new InvalidDataException($"{record}: expected one original rig archive, found {owners.Length}");
            if (!decoded.TryGetValue(owners[0], out var library))
                decoded[owners[0]] = library = UberModel.Load(File.ReadAllBytes(owners[0]));
            var system = library.FetchMeshSystem(record)!;
            var skeletons = system.Skeletons.Where(s => s.Bones.Any(b => b.Name.Equals("Bip01", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (skeletons.Length != 1) throw new InvalidDataException($"{record}: expected one native biped skeleton");
            rigs[id] = (record, skeletons[0]);
        }
        return new VehicleExitBindings(clips, rigs);
    }

    private static float[] Vector(Vector3 value) => [value.X, value.Y, value.Z];
}
