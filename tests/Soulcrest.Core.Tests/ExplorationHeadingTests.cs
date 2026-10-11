using Soulcrest.Core.Text;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class ExplorationHeadingTests
{
    [Theory]
    [InlineData("Versiegelter Dunge...", "dungeon", "de")]
    [InlineData(" VERSIEGELTER DUNGE… ", "dungeon", "de")]
    [InlineData("Versiegelter Dunge..", "dungeon", "de")]
    [InlineData("Versiegelter Dunge.«", "dungeon", "de")]
    [InlineData("Versiegelter Dunge.„", "dungeon", "de")]
    [InlineData("Versiegelter Dungeon", "dungeon", "de")]
    [InlineData("Versiegelte Dungeons", "dungeon", "de")]
    [InlineData("Versiegelter-Dungeon", "dungeon", "de")]
    [InlineData("Sealed Dungeon", "dungeon", "en")]
    [InlineData("Sealed Dungeons", "dungeon", "en")]
    [InlineData("Garnison", "stronghold", "de")]
    [InlineData("Garnisonen", "stronghold", "de")]
    [InlineData("Stronghold", "stronghold", "en")]
    [InlineData("Strongholds", "stronghold", "en")]
    public void KnownCategoryLabelsAndTheReportedTruncationShareOneMeaning(string text, string kind, string language)
    {
        var heading = ExplorationHeading.Read(text);
        Assert.NotNull(heading);
        Assert.Equal(kind, heading.Value.Kind);
        Assert.Equal(language, heading.Value.Language);
    }

    [Theory]
    [InlineData("Versiegelter Dunge")]
    [InlineData("Versiegelter Dunge.")]
    [InlineData("Versiegelter D...")]
    [InlineData("Versiegelter Dung...")]
    [InlineData("Versiegelte Dunge...")]
    [InlineData("Versiegelter Dunce...")]
    [InlineData("Versiegelter Dunge.‹")]
    [InlineData("Versiegelter Dunge.»")]
    [InlineData("Versiegelter Dunge.“")]
    [InlineData("Versiegelter Dunge«")]
    [InlineData("Versiegelter Dunge„")]
    [InlineData("Versiegelter Dung.«")]
    [InlineData("Sealed Dunge.„")]
    [InlineData("Versiegelter Dunge.« Eingang")]
    [InlineData("Versiegelter Dunge.„ 100 %")]
    [InlineData("Sealed Dunge...")]
    [InlineData("Garnis...")]
    [InlineData("Versiegelter Dunge... 100 %")]
    [InlineData("Other Versiegelter Dunge...")]
    [InlineData("Versiegelter Dunge... Eingang")]
    [InlineData("(O) Versiegelter Dunge...")]
    [InlineData("Kibelisk")]
    [InlineData("")]
    public void OtherPrefixesAndUnrelatedTextCannotBecomeACategory(string text) =>
        Assert.Null(ExplorationHeading.Read(text));
}
