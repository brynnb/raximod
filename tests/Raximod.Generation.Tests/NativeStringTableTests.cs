using System.Text;
using Raximod.EngineAssets.Databases;
using Xunit;

namespace Raximod.Generation.Tests;

public sealed class NativeStringTableTests
{
    [Fact]
    public void PreservesOrderDuplicatesWhitespaceAndLatin1()
    {
        byte[] source = Encoding.Latin1.GetBytes(
            "# comment\r\n@award_title=Premi©  \r\n@empty=\r\n@award_title=Premi©  \r\n");

        NativeStringTable table = NativeStringTable.Parse(source);

        Assert.Equal(3, table.Entries.Count);
        Assert.Equal("Premi©  ", table.Entries[0].Value);
        Assert.Equal("", table.Entries[1].Value);
        Assert.Equal([2, 4], table.Find("@award_title").Select(entry => entry.LineNumber));
        Assert.Contains(table.Diagnostics, diagnostic => diagnostic.Code == "duplicate-key");
        Assert.Equal("Premi©  ", table.ResolveRequired("@award_title", "test").Value);
    }

    [Fact]
    public void ReportsMalformedLinesAndRejectsAmbiguousResolution()
    {
        NativeStringTable table = NativeStringTable.Parse(Encoding.Latin1.GetBytes(
            "@broken line\n@same=first\n@same=second\n"));

        Assert.Contains(table.Diagnostics, diagnostic => diagnostic.Code == "malformed-line" && diagnostic.LineNumber == 1);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            table.ResolveRequired("@same", "award 'example'"));
        Assert.Contains("ambiguous localization key", error.Message);
    }
}
