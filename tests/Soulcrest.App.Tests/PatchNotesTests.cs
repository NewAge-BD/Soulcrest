using System.Reflection;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>The Patchnotes tab shows docs/PATCHNOTES.md as embedded in the app (user request 2026-10-07).</summary>
public sealed class PatchNotesTests
{
    [Fact]
    public void TheNewestEntryIsTheVersionOfThisBuild()
    {
        var version = typeof(PatchNotes).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        var releases = PatchNotes.All;
        Assert.NotEmpty(releases);
        Assert.Equal(version, releases[0].Version);
        Assert.All(releases, r => Assert.NotEmpty(r.Entries));
        Assert.Equal(releases.Count, releases.Select(r => r.Version).Distinct().Count());
        Assert.Equal("0.1.28", PatchNotes.Shown[^1].Version); // the tab starts with 0.1.28
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
