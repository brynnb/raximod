namespace Raximod.Generation.Packaging
{
    /// <summary>
    /// Controls how portable assets are packaged without changing how the native source is decoded.
    /// </summary>
    public enum ExportProfile
    {
        /// <summary>
        /// A self-contained GLB with standard materials, embedded textures, its native rig, and every
        /// compatible skeletal animation. It is intended to open in an ordinary glTF viewer without
        /// a Raximod-specific loader.
        /// </summary>
        Standalone,

        /// <summary>
        /// A game-oriented export that may reference shared textures and accompanying manifests.
        /// TerraSunder's family exporters remain authoritative for shared rig and animation packages.
        /// </summary>
        Shared,
    }

    public static class ExportProfiles
    {
        public static ExportProfile Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Equals("standalone", StringComparison.OrdinalIgnoreCase))
            {
                return ExportProfile.Standalone;
            }

            if (value.Equals("shared", StringComparison.OrdinalIgnoreCase))
            {
                return ExportProfile.Shared;
            }

            throw new ArgumentException(
                $"Unknown export profile '{value}'. Expected 'standalone' or 'shared'.",
                nameof(value));
        }
    }
}
