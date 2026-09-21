using System.Globalization;
using System.Text.RegularExpressions;
using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets;

/// <summary>HART's detached building entrances and ranged passenger stations are
/// not ordinary infantry mount clips. All vectors here remain native Z-up.</summary>
public static class HartBindings
{
    public sealed record StationRange(int First, int Last);
    public sealed record Entrance(int Index, string? Name, float[] Location, float YawDegrees,
        StationRange? Stations);
    public sealed record Camera(string Bone, float[] Position, float[]? Orientation);
    public sealed record Station(int Index, StationRange Duplicates, string Name, bool HideAvatar,
        bool NeedsStateSent, string[] ArmorTypes, float TransitionRadius, float TransitionSector, Camera Camera);
    public sealed record Animation(string Name, float DurationSeconds, float Speed);
    public sealed record TimedEffect(string Phase, string Role, string Name, float OnSeconds, float OffSeconds);
    public sealed record Sound(string File, float Volume, float Range, float DelaySeconds);
    public sealed record Source(string Record, IReadOnlyDictionary<string, GameObjectDb.GameObjectPropertySource> Properties);
    public sealed record Shuttle(string Record, bool AttachedMountZones, bool ServerControlled,
        bool AllowsThirdPersonToggle, Entrance[] Entrances, Station Passenger, Camera ChaseCamera,
        float HoldingViewpointHeight, float TimeBeforeStasisSeconds, float AnimationViewDistance,
        float DockSeconds, float OrbitSeconds, Animation Land, Animation Liftoff, TimedEffect[] Effects,
        IReadOnlyDictionary<string, Sound> Sounds, Source Source, string AnimationAttachBone);
    public sealed record Barrier(int Index, string Model, float[] Position, float YawDegrees,
        float[] Size, float[] PrimitivePosition, float[] PrimitiveOrientation);
    public sealed record Path(int Index, float[][] Points);
    public sealed record Building(string Record, bool DetachedMountZones, bool TerrainAdjustedEntrances,
        string Shuttle, string Parent, float ShuttleYawDegrees, Entrance[] Entrances, Path[] Paths,
        string PathBoneSubstring, Barrier[] Barriers, Source Source);

    public static Entrance[] Entrances(GameObjectDb.GameObject record, bool building = false)
    {
        var r = new Reader(record);
        return r.Indices(@"^mountzone(\d+)_location$").Select(index => new Entrance(index,
            building ? null : r.Text($"mountzone{index}_name"), r.Vector($"mountzone{index}_location"),
            r.Number($"mountzone{index}_" + (building ? "zorient" : "zorientation")),
            building ? null : Range(r.List($"mountzone{index}_mountpointindexes"), record.Name))).ToArray();
    }

    public static StationRange Range(string[] values, string context)
    {
        if (values.Length != 3 || values[0] != "range"
            || !int.TryParse(values[1], out int first) || !int.TryParse(values[2], out int last)
            || first < 1 || last < first)
            throw new InvalidDataException($"{context}: invalid passenger station range [{string.Join(',', values)}]");
        return new StationRange(first, last);
    }

    public static Shuttle ResolveShuttle(GameObjectDb.GameObject record)
    {
        var r = new Reader(record);
        int[] stations = r.Indices(@"^mountpoint(\d+)_name$");
        if (stations.Length != 1) throw new InvalidDataException($"{record.Name}: expected one duplicated station template");
        int station = stations[0];
        string prefix = $"mountpoint{station}_";
        if (!r.Flag(prefix + "duplicate")) throw new InvalidDataException($"{record.Name}: passenger template is not duplicated");
        string[] duplicate = r.List(prefix + "duplicateindexes");
        var duplicates = Range(["range", .. duplicate], record.Name);
        if (duplicates.First != station + 1) throw new InvalidDataException($"{record.Name}: noncontiguous station duplicates");
        var entrances = Entrances(record);
        if (entrances.Length == 0 || entrances.Any(e => e.Stations!.First != station || e.Stations.Last > duplicates.Last))
            throw new InvalidDataException($"{record.Name}: entrance range exceeds passenger stations");
        var effects = record.Properties.Keys.Select(key => Regex.Match(key,
                @"^flight_os_(land|launch)_(.+)_effect_name$"))
            .Where(m => m.Success).OrderBy(m => m.Value, StringComparer.Ordinal).Select(m =>
            {
                string stem = m.Value[..^4]; // retain effect_ prefix for timing properties
                float on = r.Number(stem + "on_time"), off = r.Number(stem + "off_time");
                if (on < 0 || (off != -1 && off < on))
                    throw new InvalidDataException($"{record.Name}: invalid timed effect {m.Value}");
                return new TimedEffect(m.Groups[1].Value, m.Groups[2].Value, r.Text(m.Value), on, off);
            }).ToArray();
        var sounds = ResolveSounds(record);
        return new Shuttle(record.Name, r.Flag("hasattachedmountzones"), r.Flag("flightisalwaysservercontrolled"),
            r.Flag("allows_toggling_of_third_person"), entrances,
            new Station(station, duplicates, r.Text(prefix + "name"), r.Flag(prefix + "hideavatar"),
                r.Flag(prefix + "needsstatesent"), r.List(prefix + "validarmortypes"),
                r.Number(prefix + "mountdismountradius"), r.Number(prefix + "mountdismountsector"),
                new Camera(r.Text(prefix + "viewpointbone"), r.Vector(prefix + "viewpoint"), r.Vector(prefix + "viewpointorientation"))),
            new Camera(r.Text("chasecamerabonename"), r.Vector("chasecameraviewpoint"), null),
            r.Number("flightosheightforholdingviewpoint"), r.Number("flightostimebeforestasis"),
            r.Number("flightosyondistancewhenanimating"), r.Number("dock_duration"), r.Number("orbit_duration"),
            Clip("land"), Clip("liftoff"), effects, sounds, Evidence(record), r.Text("animattachbonename"));

        Animation Clip(string phase)
        {
            float duration = r.Number(phase + "_anim_duration"), speed = r.Number(phase + "_anim_speed");
            if (duration <= 0 || speed <= 0) throw new InvalidDataException($"{record.Name}: invalid {phase} animation timing");
            return new Animation(r.Text(phase + "_anim_name"), duration, speed);
        }
    }

    // soundkey_* is a filename/volume/range tuple, not a scalar filename.
    // Hangar sound_* records use separate suffix properties. Preserve both
    // authored representations rather than silently dropping the tuple form.
    public static IReadOnlyDictionary<string, Sound> ResolveSounds(GameObjectDb.GameObject record)
    {
        var r = new Reader(record);
        return record.Properties.Where(p => p.Key.StartsWith("sound", StringComparison.Ordinal)
                && p.Value.Any(v => v.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p =>
            {
                bool tuple = p.Key.StartsWith("soundkey_", StringComparison.Ordinal);
                var values = tuple ? GameObjectPropertyReader.Tuple(record, p.Key, 3)!.ToArray() : [r.Text(p.Key)];
                if (!values[0].EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{record.Name}: invalid sound filename {p.Key}");
                float volume = tuple ? r.Parse(values[1], p.Key) : r.Number(p.Key + "_volume");
                float range = tuple ? r.Parse(values[2], p.Key) : r.Number(p.Key + "_range");
                // Native naming pairs takeoff's soundkey with liftoff animation timing.
                string delayKey = tuple ? "sound_" + p.Key[9..].Replace("takeoff", "liftoff") + "_delay" : p.Key + "_delay";
                float delay = record.Properties.ContainsKey(delayKey) ? r.Number(delayKey) : 0;
                if (volume < 0 || range <= 0 || delay < 0)
                    throw new InvalidDataException($"{record.Name}: invalid sound parameters {p.Key}");
                return new Sound(values[0], volume, range, delay);
            });
    }

    public static Building ResolveBuilding(GameObjectDb.GameObject record, PhysicsListDatabase physics)
    {
        var r = new Reader(record);
        var entrances = Entrances(record, building: true);
        int count = r.Integer("numpoints");
        var paths = r.Indices(@"^path(\d+)_points$").Select(index =>
        {
            float[] values = r.Numbers($"path{index}_points");
            if (values.Length != count * 3) throw new InvalidDataException($"{record.Name}: invalid path {index} length");
            return new Path(index, values.Chunk(3).ToArray());
        }).ToArray();
        if (paths.Length != r.Integer("numpaths") || !paths.Select(p => p.Index).SequenceEqual(entrances.Select(e => e.Index)))
            throw new InvalidDataException($"{record.Name}: paths do not match entrances");
        var barriers = r.Indices(@"^physicsbarrier(\d+)$").Select(index =>
        {
            string name = r.Text($"physicsbarrier{index}");
            var model = physics.FindModel(name) ?? throw new InvalidDataException($"{record.Name}: missing barrier {name}");
            if (model.Shapes.Count != 1 || model.Shapes[0].Kind != PhysicsListDatabase.ShapeKind.Box)
                throw new InvalidDataException($"{record.Name}: unsupported barrier shape {name}");
            var shape = model.Shapes[0];
            return new Barrier(index, name, r.Vector($"barrier{index}"), r.Number($"anglebarrier{index}"),
                [shape.Size.X, shape.Size.Y, shape.Size.Z], [shape.Position.X, shape.Position.Y, shape.Position.Z],
                [shape.Orientation.X, shape.Orientation.Y, shape.Orientation.Z]);
        }).ToArray();
        return new Building(record.Name, r.Flag("hasdetachedmountzones"), r.Flag("useterrainformountzonez"),
            r.Text("orbital_shuttle_name"), r.Text("orbital_shuttle_parent_name"), r.Number("orbital_shuttle_z_rotation"),
            entrances, paths, r.Text("substringforpath"), barriers, Evidence(record));
    }

    private static Source Evidence(GameObjectDb.GameObject record) => new(record.Name,
        record.PropertySources.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value));

    private sealed class Reader(GameObjectDb.GameObject record)
    {
        public string Text(string key) => GameObjectPropertyReader.Scalar(record, key)
            ?? throw new InvalidDataException($"{record.Name}: missing {key}");
        public string[] List(string key) => GameObjectPropertyReader.List(record, key).ToArray();
        public bool Flag(string key) => bool.TryParse(Text(key), out bool value) ? value
            : throw new InvalidDataException($"{record.Name}: invalid boolean {key}");
        public int Integer(string key) => int.TryParse(Text(key), out int value) && value > 0 ? value
            : throw new InvalidDataException($"{record.Name}: invalid count {key}");
        public float Number(string key) => Parse(Text(key), key);
        public float[] Numbers(string key) => List(key).Select(value => Parse(value, key)).ToArray();
        public float[] Vector(string key) => (GameObjectPropertyReader.Tuple(record, key, 3)
            ?? throw new InvalidDataException($"{record.Name}: missing vector {key}")).Select(value => Parse(value, key)).ToArray();
        public float Parse(string text, string key) => float.TryParse(text, CultureInfo.InvariantCulture, out float value)
            && float.IsFinite(value) ? value : throw new InvalidDataException($"{record.Name}: invalid number {key}");
        public int[] Indices(string pattern) => record.Properties.Keys.Select(key => Regex.Match(key, pattern))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).Order().ToArray();
    }
}
