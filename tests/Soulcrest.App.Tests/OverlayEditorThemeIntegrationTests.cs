using System.Text.Json;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorThemeIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private const string Profile = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly string[] Themes =
        ["aion2-stylized", "aion2-neutral", "obsidian", "aurora", "runic", "soulcrest-legacy"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingTheGlobalThemeAffectsBothCaptureModesWithoutChangingFactsOrAcquiringPrices(bool examples)
    {
        // AppSettings and the injected preference reader remain in RAM; this test creates no settings files.
        var preferences = new AppSettings { OverlayTheme = Themes[0] };
        var state = State();
        var originalState = JsonSerializer.Serialize(state);
        var sessionReads = 0;
        var marketReads = 0;
        var marketLeases = 0;
        using var source = new OverlayEditorPresentationSource(() => { sessionReads++; return state; }, () => null,
            (session, _) =>
            {
                marketReads++;
                return new() { SessionId = session.Id, SourceProfileId = Profile, Available = true, NetKina = 400 };
            }, () => { marketLeases++; throw new InvalidOperationException("Capture must never acquire prices."); },
            new Clock(), theme: () => preferences.OverlayTheme);
        var original = source.Capture(examples, "en");
        foreach (var theme in Themes)
        {
            preferences.OverlayTheme = theme;
            var actual = source.Capture(examples, "en");
            Assert.Equal(theme, actual.ThemeId);
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(actual with { ThemeId = original.ThemeId }));
        }
        Assert.Equal(0, marketLeases);
        Assert.Equal(examples ? 0 : 7, sessionReads);
        Assert.Equal(examples ? 0 : 7, marketReads);
        Assert.Equal(originalState, JsonSerializer.Serialize(state));
    }

    public static IEnumerable<object?[]> UnrecognizedSelections()
    {
        foreach (var examples in new[] { false, true })
            foreach (var selected in new string?[] { null, "", "unknown-theme", "grindcrest", "aion2" })
                yield return [examples, selected];
    }

    [Theory]
    [MemberData(nameof(UnrecognizedSelections))]
    public void AliasesAndUnknownSelectionsNormalizeToTheExistingDefaultInBothModes(bool examples, string? selected)
    {
        using var source = new OverlayEditorPresentationSource(State, () => null, (_, _) => new(),
            () => throw new InvalidOperationException("Capture must never acquire prices."), new Clock(), theme: () => selected!);
        Assert.Equal("aion2-stylized", source.Capture(examples, "en").ThemeId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingThemeReaderUsesTheSameDefaultAsGlobalSettings(bool examples)
    {
        using var source = new OverlayEditorPresentationSource(State, () => null, (_, _) => new(),
            () => throw new InvalidOperationException("Capture must never acquire prices."), new Clock());
        Assert.Equal("aion2-stylized", source.Capture(examples, "en").ThemeId);
    }

    [Fact]
    public void ThemedExamplesNeverReadAnyLiveSourceOrAcquireAMarketLease()
    {
        using var source = new OverlayEditorPresentationSource(
            () => throw new InvalidOperationException("No live session read is allowed."),
            () => throw new InvalidOperationException("No live notice read is allowed."),
            (_, _) => throw new InvalidOperationException("No live market read is allowed."),
            () => throw new InvalidOperationException("No market lease is allowed."), new Clock(),
            theme: () => "aurora");
        var example = source.Capture(examples: true, language: "de");
        Assert.Equal("aurora", example.ThemeId);
        Assert.Equal("de", example.UiLanguage);
        Assert.NotEmpty(example.Metrics);
        Assert.NotEmpty(example.Drops);
    }

    private static UnifiedLootOverlayState State() => new(new LootTrackerSession
    {
        Id = "synthetic-theme-session", Character = new(Profile, null), Status = LootTrackerSessionStatus.Live,
        StartedAt = Now.AddMinutes(-30), Elapsed = TimeSpan.FromMinutes(30), KinaUnbound = 200,
        Loot = [new("Item:510116011", "Synthetic item", LootTrackerLootKind.Item, 3)],
    }, true, new(null, null, null, 0), null, false, Now);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
