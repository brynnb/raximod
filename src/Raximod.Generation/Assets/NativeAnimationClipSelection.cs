using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets
{
    /// <summary>
    /// Resolves an apackage animation entry to the concrete records present in the installed
    /// animation archives. Reference-pose families are virtual package entries: their authored
    /// name is a prefix for one or more <c>_refNN</c> records rather than necessarily being a
    /// record itself.
    /// </summary>
    public static class NativeAnimationClipSelection
    {
        public sealed record Binding(
            string Animation,
            string Alias,
            string? PlaybackMode,
            string Resolution,
            IReadOnlyList<string> ConcreteClips);

        public static bool IsVirtualReferencePoseMode(string? playbackMode) =>
            playbackMode?.StartsWith("refpose", StringComparison.OrdinalIgnoreCase) == true
            || playbackMode?.Equals("blended_refpose", StringComparison.OrdinalIgnoreCase) == true;

        public static string? NeutralClip(Binding binding) => binding.Resolution == "authored-exact"
            ? binding.ConcreteClips.Single()
            : binding.ConcreteClips.SingleOrDefault(name => name.EndsWith("_ref00", StringComparison.OrdinalIgnoreCase));

        public static Binding Resolve(
            NativeAnimationEntry animation,
            IReadOnlyList<string> availableClips)
        {
            ArgumentNullException.ThrowIfNull(animation);
            ArgumentNullException.ThrowIfNull(availableClips);

            string prefix = animation.Animation + "_ref";
            string[] concrete = availableClips
                .Where(name => IsConcreteReferencePose(name, prefix))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            bool exactExists = availableClips.Any(name =>
                name.Equals(animation.Animation, StringComparison.OrdinalIgnoreCase));

            if (concrete.Length > 0 && IsVirtualReferencePoseMode(animation.PlaybackMode))
            {
                return new Binding(
                    animation.Animation, animation.Alias, animation.PlaybackMode,
                    "virtual-reference-poses", concrete);
            }

            // One installed apackage entry currently serializes its playback enum as literal "0"
            // (ncflite walkbackward_rifle). Do not guess what that enum value means. The source
            // records themselves provide a stronger invariant: when the authored exact name is
            // absent and a matching _refNN family exists, that family is the only concrete source.
            if (!exactExists && concrete.Length > 0)
            {
                return new Binding(
                    animation.Animation, animation.Alias, animation.PlaybackMode,
                    "concrete-reference-family", concrete);
            }

            if (exactExists)
            {
                return new Binding(
                    animation.Animation, animation.Alias, animation.PlaybackMode,
                    "authored-exact", [animation.Animation]);
            }

            return new Binding(
                animation.Animation, animation.Alias, animation.PlaybackMode,
                "missing", Array.Empty<string>());
        }

        private static bool IsConcreteReferencePose(string candidate, string prefix)
        {
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            ReadOnlySpan<char> suffix = candidate.AsSpan(prefix.Length);
            return suffix.Length == 2 && char.IsAsciiDigit(suffix[0]) && char.IsAsciiDigit(suffix[1]);
        }
    }
}
