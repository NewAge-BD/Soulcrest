using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorRareNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static AcceptedFarmLootDrop Drop(long quantity = 1, string key = "Item:210230006") =>
        new("session", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Now, key, "Example rare item", quantity);

    [Fact]
    public void HiddenLegacyHudDoesNotSuppressIndependentVisualReceiptOrActivateAudio()
    {
        var audio = 0; var clock = new Clock();
        using var source = new RareDropAlertService(() => new(Enabled: false, SoundEnabled: true),
            () => { audio++; throw new InvalidOperationException("No audio expected"); }, clock);
        source.Accept(Drop());
        var notice = source.LatestVisualNotice;
        Assert.NotNull(notice);
        Assert.Equal("session", notice.SessionId);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", notice.ProfileId);
        Assert.Null(source.Current);
        Assert.Empty(source.CurrentNotices);
        source.SetPresentationVisible(true);
        Assert.Null(source.Current);
        source.SetPresentationVisible(false);
        Assert.Equal(notice.Id, source.LatestVisualNotice?.Id);
        Assert.Equal(0, audio);
    }

    [Fact]
    public void IndependentProjectionKeepsOnlyTheLatestReceiptAndExpiresAfterFourMonotonicSeconds()
    {
        var clock = new Clock();
        using var source = new RareDropAlertService(() => new(SoundEnabled: false), () => throw new InvalidOperationException(), clock);
        source.Accept(Drop()); var first = source.LatestVisualNotice;
        clock.Advance(TimeSpan.FromSeconds(1)); source.Accept(Drop(2));
        Assert.NotEqual(first!.Id, source.LatestVisualNotice?.Id);
        Assert.Equal(2, source.LatestVisualNotice?.Quantity);
        clock.Advance(TimeSpan.FromSeconds(3.999)); clock.UtcNow = clock.UtcNow.AddDays(-1);
        Assert.NotNull(source.LatestVisualNotice);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(source.LatestVisualNotice);
        source.SetPresentationVisible(true);
        Assert.Null(source.Current);
        Assert.Null(source.LatestVisualNotice);
    }

    [Fact]
    public void PerRarityOptOutStillAppliesAndReenablingDoesNotReplayAnIgnoredReceipt()
    {
        var options = new RareDropAlertOptions(SoundEnabled: false)
        {
            RarityAlerts = new Dictionary<LootItemRarity, DropRarityAlertSettings> { [LootItemRarity.Unique] = new(Enabled: false) },
        };
        using var source = new RareDropAlertService(() => options, () => throw new InvalidOperationException(), new Clock());
        source.Accept(Drop()); Assert.Null(source.LatestVisualNotice);
        options = options with { RarityAlerts = null }; source.ApplySettings();
        Assert.Null(source.LatestVisualNotice);
        source.Accept(Drop()); Assert.NotNull(source.LatestVisualNotice);
        options = options with { RarityAlerts = new Dictionary<LootItemRarity, DropRarityAlertSettings> { [LootItemRarity.Unique] = new(Enabled: false) } };
        source.ApplySettings(); Assert.Null(source.LatestVisualNotice);
        options = options with { RarityAlerts = null }; source.ApplySettings(); Assert.Null(source.LatestVisualNotice);
    }

    [Theory]
    [InlineData("Item:210350009")]
    [InlineData("Item:1999999999")]
    [InlineData("SoulCrystal:2")]
    public void OrdinaryUnknownAndNonItemDropsDoNotExpandTheRarityScope(string key)
    {
        using var source = new RareDropAlertService(() => new(), () => throw new InvalidOperationException(), new Clock());
        source.Accept(Drop(key: key)); Assert.Null(source.LatestVisualNotice); Assert.Null(source.Current);
    }

    private sealed class Clock : TimeProvider
    {
        private long _stamp;
        internal DateTimeOffset UtcNow { get; set; } = Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override long GetTimestamp() => _stamp;
        internal void Advance(TimeSpan duration) { UtcNow += duration; _stamp += duration.Ticks; }
    }
}
