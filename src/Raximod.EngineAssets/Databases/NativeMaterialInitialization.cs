using System;
using System.Collections.Generic;
using System.Linq;

namespace Raximod.EngineAssets.Databases;

/// <summary>Recovered constructor defaults, kept separate from raw ADB commands.</summary>
public static class NativeMaterialInitialization
{
    public const string ExecutableSha256 = NativeMaterialProfiles.ExecutableSha256;

    public static IReadOnlyList<AsciiCommandDatabase.Command> UnconfiguredStages(
        string name, IReadOnlyList<AsciiCommandDatabase.Command> commands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // This boundary handles wholly unconfigured ordinary materials. Partial
        // stage overrides and compound/UI/map names have additional constructor
        // branches; do not claim their full initialization from this projection.
        if (commands.Any(c => c.Name.StartsWith("mat_texture", StringComparison.Ordinal)
            || c.Name.StartsWith("mat_anim", StringComparison.Ordinal)
            || c.Name.StartsWith("mat_stage", StringComparison.Ordinal))) return [];
        if (name.Contains('+') || name.StartsWith("ui_", StringComparison.Ordinal)
            || name.Contains("_gf3", StringComparison.Ordinal)
            || (name.Length == 9 && name.StartsWith("map", StringComparison.Ordinal)
                && name.AsSpan(3).IndexOfAnyExceptInRange('0', '9') < 0)) return [];

        // Original 0x9bd100 first installs disabled slots, then 0x9bdcd6 binds
        // this exact name and 0x9bdcfb selects default0. This occurs even when
        // materials.adb has no record. It is not a similar-name texture repair.
        return [new("mat_texture1", [name]), new("mat_stage1", ["default0"]),
            new("mat_stage2", ["disable2"]), new("mat_stage3", ["disable3"]),
            new("mat_stage4", ["disable4"])];
    }
}
