using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeFirstPersonAnimationSelectionTests
{
    [Fact]
    public void PreservesAllAuthoredPhasesAndAlternateActionsWithoutArchiveExtras()
    {
        string[] aliases = ["idle", "fire1_start", "fire1_idle", "fire1_end", "fire2", "equip"];
        var entries = aliases.Select(alias => Entry("weapon_" + alias, alias)).ToArray();
        string[] available = [.. entries.Select(entry => entry.Animation), "weapon_unused"];
        var result = NativeFirstPersonAnimationSelection.Resolve(entries.Reverse(), available.Reverse().ToArray());

        Assert.Equal(entries.Select(entry => entry.Animation).Order(), result.Clips);
        Assert.Equal(["weapon_fire1_start", "weapon_fire1_idle"], result.States["fire"]);
        foreach (string alias in aliases) Assert.Equal(["weapon_" + alias], result.States[alias]);
        Assert.DoesNotContain("weapon_unused", result.Clips);
    }

    [Fact]
    public void ExplicitSharedRigBindingsArePreservedAndValidated()
    {
        var states = new Dictionary<string, string[]> { ["fire"] = ["other_fire"] };
        var result = NativeFirstPersonAnimationSelection.Resolve([Entry("weapon_fire1", "fire1")],
            ["other_fire", "other_alt", "weapon_fire1"], states, ["other_alt"]);
        Assert.Equal(["other_alt", "other_fire", "weapon_fire1"], result.Clips);
        Assert.Equal(["other_fire"], result.States["fire"]);
        Assert.Throws<InvalidOperationException>(() => NativeFirstPersonAnimationSelection.Resolve(
            [], [], states));
    }

    [Fact]
    public void ReferenceFamiliesRetainTheirSourceBindings()
    {
        var result = NativeFirstPersonAnimationSelection.Resolve([Entry("weapon_fire1", "fire1")],
            ["weapon_fire1_ref01", "weapon_fire1_ref00"]);
        Assert.Equal(["weapon_fire1_ref00", "weapon_fire1_ref01"], result.Clips);
        Assert.Equal(["weapon_fire1_ref00"], result.States["fire"]);
        Assert.Equal("fire1", Assert.Single(result.Bindings).Alias);
    }

    private static NativeAnimationEntry Entry(string clip, string alias) =>
        new(clip, alias, true, "none", "play_once", 0, []);
}
