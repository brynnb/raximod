using System;
using System.Collections.Generic;
using System.Linq;

namespace Raximod.EngineAssets.Databases;

/// <summary>The retail material capability-profile lookup, separate from material definitions.</summary>
public static class NativeMaterialProfiles
{
    public const int GenericFourStageProfile = 8;
    public const string ExecutableSha256 = "7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a";
    private static readonly string[] VariantSuffixes =
        ["_gf2", "_gf3", "_gf4", "_at2", "_at3", "_at4", "_ot2", "_ot3", "_ot4"];

    // planetside.exe 0x9c0230 dispatches the native profile through 0x9c03e4.
    // 0x9c0410 appends each suffix and keeps the FIRST existing record in +0x80.
    // These are not spelling repairs: variants replace complete definitions,
    // even when the unsuffixed material already exists.
    public static IReadOnlyList<string> Suffixes(int profile) => profile switch
    {
        0 => [],
        1 => ["_gf3"],
        2 => ["_gf4", "_gf3"],
        3 or 6 => ["_gf2"],
        4 => ["_at3", "_gf3", "_at2"],
        5 => ["_at4", "_gf4", "_at3", "_gf3", "_at2"],
        7 => ["_ot3", "_gf3", "_ot2"],
        8 => ["_ot4", "_gf4", "_ot3", "_gf3", "_ot2"],
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown native material profile")
    };

    public static string Resolve(string authored, int profile, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authored);
        ArgumentNullException.ThrowIfNull(exists);
        IReadOnlyList<string> suffixes = Suffixes(profile);
        // Already suffixed material references bypass profile selection (0x9c0262-0x9c031c).
        if (!VariantSuffixes.Any(suffix => authored.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            foreach (string suffix in suffixes)
                if (exists(authored + suffix)) return authored + suffix;
        return authored;
    }

    /// <summary>Capability suffixes identify a material definition, never a texture filename.</summary>
    public static string UnsuffixedName(string material) =>
        VariantSuffixes.Any(suffix => material.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ? material[..^4] : material;
}
