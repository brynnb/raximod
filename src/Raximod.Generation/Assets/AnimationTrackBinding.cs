namespace Raximod.Generation.Assets
{
    /// <summary>
    /// Resolves an authored animation track onto the target model's skeleton. Aliases are explicit
    /// export metadata: the exporter never strips prefixes or digits heuristically.
    /// </summary>
    public static class AnimationTrackBinding
    {
        public static string ResolveTarget(
            string sourceTrack,
            IReadOnlyDictionary<string, string>? aliases)
        {
            if (aliases != null && aliases.TryGetValue(sourceTrack, out string? target) &&
                !string.IsNullOrWhiteSpace(target))
                return target;
            return sourceTrack;
        }
    }
}
