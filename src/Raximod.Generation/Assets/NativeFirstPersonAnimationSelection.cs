using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets;

/// <summary>Arm banks preserve the same native package clips as their weapons.
/// Gameplay state adapters must not discard sustained or alternate-fire tracks.</summary>
public static class NativeFirstPersonAnimationSelection
{
    public sealed record Result(string[] Clips, Dictionary<string, string[]> States,
        NativeAnimationClipSelection.Binding[] Bindings);

    public static Result Resolve(IEnumerable<NativeAnimationEntry> entries, IReadOnlyList<string> available,
        IReadOnlyDictionary<string, string[]>? explicitStates = null, IEnumerable<string>? explicitClips = null)
    {
        var bindings = entries.Select(entry => NativeAnimationClipSelection.Resolve(entry, available)).ToArray();
        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            string? clip = NativeAnimationClipSelection.NeutralClip(binding);
            if (clip != null) aliases[binding.Alias] = [clip];
        }
        var states = new Dictionary<string, string[]>(aliases, StringComparer.OrdinalIgnoreCase);
        Add("run", ["run", "jog", "walk"]);
        Add("fire", ["fire1", "fire1_start", "fire1_idle"]);
        if (explicitStates != null)
            foreach (var pair in explicitStates) states[pair.Key] = pair.Value;
        string[] requested = (explicitClips ?? []).Concat(explicitStates?.Values.SelectMany(value => value) ?? []).ToArray();
        foreach (string clip in requested)
            if (!available.Contains(clip, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"First-person override references missing source animation {clip}");
        string[] clips = bindings.SelectMany(binding => binding.ConcreteClips).Concat(requested)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new Result(clips, states, bindings);

        void Add(string state, string[] nativeAliases)
        {
            string[] names = nativeAliases.Where(aliases.ContainsKey).SelectMany(alias => aliases[alias]).ToArray();
            if (names.Length > 0) states[state] = names;
        }
    }
}
