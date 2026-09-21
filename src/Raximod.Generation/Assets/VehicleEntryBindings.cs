using System.Globalization;
using System.Text.RegularExpressions;
using Raximod.EngineAssets.Databases;
using Raximod.EngineAssets.Meshes;

namespace Raximod.Generation.Assets;

/// <summary>Native mountzones are entrances; mountpoints are occupant stations. PSForever's
/// MountInfo keys refer to the former. Never use that key directly to read mountpoint properties.</summary>
public sealed class VehicleEntryBindings
{
    public sealed record Clip(string Name, float DurationSeconds, string? PlaybackMode,
        string[] VehicleTracks, string Package, string[]? UnboundTracks = null,
        string[]? AnimatedVehicleTracks = null);
    public sealed record SeatedPose(string Name, string Mode, string? PlaybackMode, string Package, string Alias);
    public sealed record Variant(string Occupant, Clip Mount, Clip Dismount, SeatedPose Seated,
        VehicleExitBindings.Endpoint[]? ExitEndpoints = null);
    public sealed record Entry(int EntryPoint, int NativeMountPoint, string Name,
        float[]? Location, Variant[] Variants, bool MirrorYPositionForDismount = false);

    private readonly IReadOnlyDictionary<string, NativeAnimationPackage> packages;
    private readonly Dictionary<string, AnimRecord> clips;
    private readonly string[] names;
    private readonly VehicleExitBindings? exits;

    public VehicleEntryBindings(IReadOnlyList<NativeAnimationPackage> packages, IReadOnlyList<AnimRecord> clips,
        VehicleExitBindings? exits = null)
    {
        this.packages = packages.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        this.clips = clips.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        names = clips.Select(c => c.Name).ToArray();
        this.exits = exits;
    }

    public string Package(GameObjectDb.GameObject definition, GameObjectDb.GameObject? model) =>
        GameObjectPropertyReader.Scalar(definition, "animationpackage")
        ?? GameObjectPropertyReader.Scalar(model, "animationpackage")
        // Absent an explicit package override, the mesh sequence owns the package.
        ?? model?.Name ?? definition.Name;

    public string[] PackageClips(string package)
    {
        if (!packages.TryGetValue(package, out var source))
            throw new InvalidDataException($"Missing vehicle animation package '{package}'");
        return source.Animations.SelectMany(a => NativeAnimationClipSelection.Resolve(a, names).ConcreteClips)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Entry[] Resolve(GameObjectDb.GameObject definition, GameObjectDb.GameObject? model)
    {
        // Preserve HART entrances/station metadata without fabricating paired clips.
        // Every station in a range uses the explicitly duplicated passenger template;
        // the full range (including entrance 7's shorter range) stays in hart/manifest.
        if (definition.Name == "orbital_shuttle")
        {
            var shuttle = HartBindings.ResolveShuttle(definition);
            return shuttle.Entrances.Select(e => new Entry(e.Index, shuttle.Passenger.Index,
                e.Name!, e.Location, [])).ToArray();
        }
        var properties = (model?.Properties ?? []).Concat(definition.Properties)
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToArray();
        string? Value(string key) => GameObjectPropertyReader.Scalar(properties, definition.Name, key);
        string[] List(string key) => GameObjectPropertyReader.List(properties, key).ToArray();
        string package = Package(definition, model);
        if (!packages.ContainsKey(package)) throw new InvalidDataException($"{definition.Name}: missing package {package}");
        return properties.Select(p => Regex.Match(p.Key, @"^mountzone(\d+)_name$", RegexOptions.IgnoreCase))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).Order().Select(index =>
            {
                string role = Value($"mountzone{index}_name")!;
                string[] stations = List($"mountzone{index}_mountpointindexes");
                if (stations.Length != 1 || !int.TryParse(stations[0], out int station))
                    throw new InvalidDataException($"{definition.Name}: mountzone{index} has unsupported station list [{string.Join(',', stations)}]");
                string[] position = List($"mountzone{index}_location");
                if (position.Length is not (0 or 3)) throw new InvalidDataException($"{definition.Name}: malformed mountzone{index} location");
                var variants = new List<Variant>();
                Add("infantry", role);
                foreach (string faction in new[] { "nc", "tr", "vs" })
                {
                    Add($"max-{faction}", $"{role}_{faction}heavy");
                    Add($"max-{faction}", $"{faction}hev"); // drop pod's authored aliases
                }
                if (variants.Count == 0) throw new InvalidDataException($"{definition.Name}: package {package} has no entry pair for mountzone{index} '{role}'");
                return new Entry(index, station, role, position.Length == 0 ? null
                    : position.Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray(), variants.ToArray(),
                    bool.Parse(Value($"mountzone{index}_mirroryposfordismount") ?? "false"));

                void Add(string occupant, string alias)
                {
                    Clip? mount = ResolveClip(package, alias + "_mount");
                    Clip? dismount = ResolveClip(package, alias + "_dismount");
                    if (mount is null && dismount is null) return;
                    if (mount is null || dismount is null) throw new InvalidDataException($"{definition.Name}: incomplete {alias} mount/dismount pair");
                    if (variants.Any(v => v.Occupant == occupant)) throw new InvalidDataException($"{definition.Name}: ambiguous {occupant} entry");
                    variants.Add(new Variant(occupant, mount, dismount,
                        ResolveSeatedPose(package, alias, Value($"mountpoint{station}_name"), mount),
                        exits?.Resolve(dismount.Name, occupant)));
                }
            }).ToArray();
    }

    private SeatedPose ResolveSeatedPose(string package, string entrance, string? station, Clip mount)
    {
        // An authored entrance pose wins (Liberator's gunnerb station is misleadingly named
        // gunnera). Alternate entrances without a pose share their station (ANT DriverB).
        // Resolve package aliases, never clip filenames. An explicit idle alias can be a loop.
        foreach (string alias in new[] { entrance, entrance + "_idle", station, station is null ? null : station + "_idle" }
                     .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = FindAlias(package, alias);
            if (found is null) continue;
            var (owner, animation) = found.Value;
            bool reference = NativeAnimationClipSelection.IsVirtualReferencePoseMode(animation.PlaybackMode);
            bool loop = animation.PlaybackMode == "loop";
            if (!reference && !loop) continue;
            var binding = NativeAnimationClipSelection.Resolve(animation, names);
            // Some refpose packages store one exact clip rather than a _refNN family. Do not
            // drop those clips (Liberator) or silently pick a non-neutral numbered reference.
            string? name = NativeAnimationClipSelection.NeutralClip(binding);
            if (name is null) throw new InvalidDataException($"{owner}/{alias}: no neutral seated clip for {animation.Animation}");
            // `driver_idle` is not necessarily an occupant animation: Flail's six tracks are
            // vehicle wings/body only. Require the common native biped before selecting it.
            if (!clips[name].Tracks.Any(t => t.Name.StartsWith("bip", StringComparison.OrdinalIgnoreCase)))
            {
                if (loop) continue;
                throw new InvalidDataException($"{owner}/{alias}: seated reference has no biped tracks");
            }
            return new SeatedPose(name, loop ? "loop" : "reference", animation.PlaybackMode, owner, alias);
        }
        if (mount.PlaybackMode != "play_once_hold")
            throw new InvalidDataException($"{package}/{entrance}: no seated pose or authored mount-end hold");
        return new SeatedPose(mount.Name, "mount-end", mount.PlaybackMode, mount.Package, entrance + "_mount");
    }

    private (string Package, NativeAnimationEntry Entry)? FindAlias(string package, string alias, HashSet<string>? visited = null)
    {
        visited ??= new(StringComparer.OrdinalIgnoreCase);
        if (!visited.Add(package)) throw new InvalidDataException($"Cyclic animation package fallback at {package}");
        if (!packages.TryGetValue(package, out var source)) throw new InvalidDataException($"Missing animation package {package}");
        var matches = source.Animations.Where(a => a.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"{package}: ambiguous animation alias {alias}");
        return matches.Length == 1 ? (package, matches[0])
            : source.Fallback is null ? null : FindAlias(source.Fallback, alias, visited);
    }

    private Clip? ResolveClip(string package, string alias, HashSet<string>? visited = null)
    {
        visited ??= new(StringComparer.OrdinalIgnoreCase);
        if (!visited.Add(package)) throw new InvalidDataException($"Cyclic animation package fallback at {package}");
        var source = packages[package];
        var matches = source.Animations.Where(a => a.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"{package}: ambiguous animation alias {alias}");
        if (matches.Length == 0) return source.Fallback is null ? null : ResolveClip(source.Fallback, alias, visited);
        var entry = matches[0];
        if (!clips.TryGetValue(entry.Animation, out var clip)) throw new InvalidDataException($"{package}/{alias}: missing clip {entry.Animation}");
        if (clip.Duration <= 0 || !float.IsFinite(clip.Duration)) throw new InvalidDataException($"Invalid duration: {clip.Name}");
        return new Clip(clip.Name, clip.Duration, entry.PlaybackMode,
            clip.Tracks.Where(t => !t.Name.StartsWith("bip01", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), package,
            AnimatedVehicleTracks: clip.Tracks.Where(t => !t.Name.StartsWith("bip", StringComparison.OrdinalIgnoreCase)
                && (t.PosKeys.Any(k => System.Numerics.Vector3.DistanceSquared(k.Value, t.PosKeys[0].Value) > 1e-10f)
                    || t.RotKeys.Any(k => 1f - Math.Abs(System.Numerics.Quaternion.Dot(k.Value, t.RotKeys[0].Value)) > 1e-6f)))
                .Select(t => t.Name).ToArray());
    }

    public static Entry[] BindBody(string definition, IEnumerable<Entry> entries,
        IReadOnlyList<string> nodes, IReadOnlyList<string> exportedClips)
    {
        var targets = nodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var animations = exportedClips.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return entries.Select(e => e with { Variants = e.Variants.Select(v => v with
            { Mount = Bind(v.Mount), Dismount = Bind(v.Dismount) }).ToArray() }).ToArray();
        Clip Bind(Clip clip)
        {
            string[] missing = clip.VehicleTracks.Where(t => !targets.Contains(t)).ToArray();
            var bound = clip.VehicleTracks.Where(targets.Contains).ToArray();
            // ANIM clips can also carry tracks for attachments absent from the body skeleton.
            // Preserve/report those names explicitly; never invent a bone-prefix remapping.
            if (bound.Length > 0 && !animations.Contains(clip.Name))
                throw new InvalidDataException($"{definition}: vehicle clip {clip.Name} was not exported");
            return clip with { VehicleTracks = bound, UnboundTracks = missing,
                AnimatedVehicleTracks = clip.AnimatedVehicleTracks?.Where(targets.Contains).ToArray() };
        }
    }
}
