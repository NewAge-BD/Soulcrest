using Soulcrest.Core.Text;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class ExplorationHeadingLanguageTests
{
    [Theory]
    [InlineData("Versiegelter Dunge... 0 %", "de")]
    [InlineData("Versiegelter Dunge... 31 %", "de")]
    [InlineData("Versiegelter Dunge… 100%", "de")]
    [InlineData("Versiegelter Dunge.« 31 %", "de")]
    [InlineData("Versiegelter Dunge.„ 31 %", "de")]
    [InlineData("Versiegelter Dungeon 99 %", "de")]
    [InlineData("Versiegelter-Dungeon 31 %", "de")]
    [InlineData("Versiegelte Dungeons IOO %", "de")]
    [InlineData("Garnisonen 100 %", "de")]
    [InlineData("Sealed Dungeon 31 %", "en")]
    [InlineData("Sealed Dungeons 100%", "en")]
    [InlineData("Stronghold 0 %", "en")]
    [InlineData("Strongholds 99 %", "en")]
    [InlineData("(O) Versiegelter Dunge...", "de")]
    [InlineData("(O) Garnison", "de")]
    [InlineData("(O) Sealed Dungeon", "en")]
    [InlineData("(O) Strongholds", "en")]
    [InlineData(" (O) Versiegelter Dunge... 31 % ", "de")]
    [InlineData("(O) Versiegelter Dunge.« 31 %", "de")]
    [InlineData("(O) Versiegelter Dunge.„ 31 %", "de")]
    [InlineData("(O) Garnisonen 100 %", "de")]
    [InlineData("(O) Sealed Dungeon 31 %", "en")]
    [InlineData("(O) Strongholds 100 %", "en")]
    public void KnownIconAndPercentageCanIdentifyOnlyTheLanguage(string text, string language)
    {
        Assert.Equal(language, ExplorationHeading.ReadLanguageHint(text));
        // The action parser still needs its isolated heading and spatial evidence.
        Assert.Null(ExplorationHeading.Read(text));
    }

    [Theory]
    [InlineData("Map: Sealed Dungeon 31 %")]
    [InlineData("Karte: Versiegelter Dunge... 31 %")]
    [InlineData("Quest: Sealed Dungeon 31 %")]
    [InlineData("Quest: Versiegelter Dunge... 31 %")]
    [InlineData("Other Sealed Dungeon 31 %")]
    [InlineData("Sealed Dungeon 31 % Complete")]
    [InlineData("(0) Sealed Dungeon 31 %")]
    [InlineData("(O)Sealed Dungeon 31 %")]
    [InlineData("(O) (O) Sealed Dungeon 31 %")]
    [InlineData("Sealed Dungeon (O) 31 %")]
    [InlineData("Sealed Dungeon %")]
    [InlineData("Sealed Dungeon %!")]
    [InlineData("Sealed Dungeon 101 %")]
    [InlineData("Sealed Dungeon 1000 %")]
    [InlineData("Sealed Dungeon -1 %")]
    [InlineData("Sealed Dungeon +1 %")]
    [InlineData("Sealed Dungeon - 1 %")]
    [InlineData("Sealed Dungeon + 1 %")]
    [InlineData("Sealed Dungeon : 31 %")]
    [InlineData("Sealed Dungeon 31.5 %")]
    [InlineData("Sealed Dungeon 31,5 %")]
    [InlineData("Sealed Dungeon 31 %%")]
    [InlineData("Sealed Dungeon (31 %)")]
    [InlineData("Sealed Dungeon 31 % 99 %")]
    [InlineData("Sealed Dungeon Stronghold 31 %")]
    [InlineData("Versiegelter Dunge... Sealed Dungeon 31 %")]
    [InlineData("Versiegelter Dunge 31 %")]
    [InlineData("Versiegelter D... 31 %")]
    [InlineData("Versiegelter Dunge.‹ 31 %")]
    [InlineData("Versiegelter Dunge.» 31 %")]
    [InlineData("Versiegelter Dunge.“ 31 %")]
    [InlineData("Versiegelter Dunge« 31 %")]
    [InlineData("Versiegelter Dung.« 31 %")]
    [InlineData("Sealed Dunge.„ 31 %")]
    [InlineData("Sealed Dungeon.« 31 %")]
    [InlineData("Versiegelter Dungeon.„ 31 %")]
    [InlineData("Stronghold.«")]
    [InlineData("Garnison.„")]
    [InlineData("Versiegelter Dunge.« Complete 31 %")]
    [InlineData("Versiegelter Dunge.„ 31 % Unentdeckt")]
    [InlineData("Kibelisk 31 %")]
    public void TheWholeLineMustBeOneKnownHeadingWithOnlyTheSupportedDecorations(string text) =>
        Assert.Null(ExplorationHeading.ReadLanguageHint(text));
}
