using System.Reflection;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>The Patchnotes tab shows docs/PATCHNOTES.md or PATCHNOTES.en.md in the interface language (user requests 2026-10-07).</summary>
public sealed class PatchNotesTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void TheNewestEntryIsTheVersionOfThisBuild(string language)
    {
        var version = typeof(PatchNotes).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        var releases = PatchNotes.For(language);
        Assert.NotEmpty(releases);
        Assert.Equal(version, releases[0].Version);
        Assert.All(releases, r => Assert.NotEmpty(r.Entries));
        Assert.Equal(releases.Count, releases.Select(r => r.Version).Distinct().Count());
    }

    [Fact]
    public void BothLanguagesListTheSameVersionsInTheTab()
    {
        // Every version the tab shows (from 0.1.28) has German and English notes with the same kinds of entries.
        static Version[] Shown(string language) =>
            [.. PatchNotes.For(language).Select(r => Version.Parse(r.Version)).Where(v => v >= PatchNotes.FirstShown)];
        Assert.Equal(Shown("de"), Shown("en"));
        Assert.Equal(PatchNotes.FirstShown, Shown("en")[^1]);
        var kinds = new Dictionary<string, string> { ["Neu"] = "New", ["Verbessert"] = "Improved", ["Behoben"] = "Fixed", ["Geändert"] = "Changed", ["Entfernt"] = "Removed" };
        foreach (var german in PatchNotes.For("de").Where(r => Version.Parse(r.Version) >= PatchNotes.FirstShown))
        {
            var english = PatchNotes.For("en").Single(r => r.Version == german.Version);
            Assert.Equal(german.Entries.Select(e => kinds[e.Kind]).Order(), english.Entries.Select(e => e.Kind).Order());
        }
    }

    [Fact]
    public void TheUpdateNoticeTakesTheSectionOfItsLanguage()
    {
        const string body = "## 0.2.1 – 2026-10-08\n\n- **Neu:** Eins.\n\n## 0.2.1 – 2026-10-08\n\n- **New:** One.\n";
        Assert.Equal("Eins.", PatchNotes.OfRelease(body, "de")!.Entries[0].Text);
        Assert.Equal("One.", PatchNotes.OfRelease(body, "en")!.Entries[0].Text);
        Assert.Equal("Eins.", PatchNotes.OfRelease("## 0.2.0 – 2026-10-07\n\n- **Neu:** Eins.\n", "en")!.Entries[0].Text); // older release: German only
    }

    [Fact]
    public void EntriesKeepKindContinuationLinesAndMarkup()
    {
        var releases = PatchNotes.Parse("""
            # Soulcrest – Patchnotes

            Intro.

            ## 0.1.2 – 2026-10-07

            - **Neu:** Fehler landen in `logs\errors.log`
              und im **Diagnosepaket**.
            - Ohne Art.

            ## 0.1.1 – 2026-10-06

            - **Behoben:** Eins.
            """);
        Assert.Equal(["0.1.2", "0.1.1"], releases.Select(r => r.Version));
        Assert.Equal("2026-10-07", releases[0].Date);
        Assert.Equal(new PatchNotes.Entry("Neu", "Fehler landen in `logs\\errors.log` und im **Diagnosepaket**."), releases[0].Entries[0]);
        Assert.Equal(new PatchNotes.Entry("", "Ohne Art."), releases[0].Entries[1]);
        Assert.Equal(
            [new("Fehler landen in ", false, false), new("logs\\errors.log", false, true), new(" und im ", false, false), new("Diagnosepaket", true, false), new(".", false, false)],
            PatchNotes.Spans(releases[0].Entries[0].Text));
    }
}
