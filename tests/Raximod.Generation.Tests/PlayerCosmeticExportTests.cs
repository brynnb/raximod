using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class PlayerCosmeticExportTests
{
    private static string[] Corpus() =>
        (from kind in new[] { "hat", "beret", "shades", "earpiece" }
         from gender in new[] { "f", "m" }
         from faction in new[] { "nc", "tr", "vs" }
         select $"{kind}_{gender}_{faction}")
        .Concat("abcde".Select(face => $"female_headhat_{face}" )).ToArray();

    [Fact]
    public void DiscoversTheCompleteCorpusDeterministicallyWithoutWorldObjects()
    {
        var records = Corpus().Append("amp_cap").Append("vanguard_hatch").ToArray();
        var first = PlayerCosmeticExport.Discover(records);
        var second = PlayerCosmeticExport.Discover(records.Reverse());
        Assert.Equal(first, second);
        Assert.Equal(29, first.Count);
        Assert.Equal(5, first.Count(entry => entry.Kind == "hat-head"));
        Assert.Equal(6, first.Count(entry => entry.Kind == "shades"));
    }

    [Fact]
    public void MissingOrAmbiguousVariantsFailInsteadOfUsingAnotherFaction()
    {
        Assert.Throws<InvalidDataException>(() => PlayerCosmeticExport.Discover(Corpus().Skip(1)));
        Assert.Throws<InvalidDataException>(() => PlayerCosmeticExport.Discover(Corpus().Append("HAT_F_NC")));
    }
}
