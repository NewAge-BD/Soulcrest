using Soulcrest.Ocr;
using Xunit;

namespace Soulcrest.Ocr.Tests;

public sealed class GameLanguageDetectorTests
{
    [Theory]
    [InlineData("Soul: Magic Gravi (Bound) x1", "en")]
    [InlineData("Die Sammlung und deine Stufe", "de")]
    [InlineData("Magic Gravi 25/75 MAX", null)]
    [InlineData("Soul Soul Soul", null)]
    [InlineData("Soul Bound Seele Gebunden", null)]
    [InlineData("", null)]
    [InlineData("Bindung abgeschlossen", "de")]
    [InlineData("Bindung nicht abgeschlossen", "de")]
    [InlineData("Pet-Kenntnis", "de")]
    [InlineData("Sammlungsfortschritt 174/200", "de")]
    [InlineData("Pet Insight", "en")]
    [InlineData("Collection Status", "en")]
    [InlineData("Pet-Kenntnis Collection Status", null)]
    public void RequiresMultipleUnambiguousLanguageHints(string text, string? expected) =>
        Assert.Equal(expected, GameLanguageDetector.Detect([text]));
}
