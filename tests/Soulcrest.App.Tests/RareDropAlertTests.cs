using System.Collections.Concurrent;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>No test opens a Windows audio player or emits sound.</summary>
public sealed class RareDropAlertTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
    private static AcceptedFarmLootDrop Drop(string key = "Item:210230006", long quantity = 1) =>
        new("session", "profile", Started, key, "Example rare item", quantity);

    [Fact]
    public void HiddenEventsAreDiscardedAndShowingNeverReplaysThem()
    {
        var time = new Clock();
        using var service = new RareDropAlertService(() => new(SoundEnabled: false),
            () => throw new InvalidOperationException("No playback"), time);
        service.Accept(Drop());
        Assert.Null(service.Current);
        service.SetPresentationVisible(true);
        Assert.Null(service.Current);
        service.Accept(Drop());
        Assert.NotNull(service.Current);
        service.SetPresentationVisible(false);
        service.SetPresentationVisible(true);
        Assert.Null(service.Current);
    }

    [Fact]
    public void EveryAcceptedReceiptIsRealEvenIfValuesAndTimestampMatchAndNoticesExpireMonotonically()
    {
        var time = new Clock();
        var changes = 0;
        using var service = new RareDropAlertService(() => new(SoundEnabled: false),
            () => throw new InvalidOperationException("No playback"), time);
        service.Changed += () => changes++;
        service.SetPresentationVisible(true);
        service.Accept(Drop());
        var first = service.Current;
        service.Accept(Drop());
        var second = service.Current;
        Assert.NotNull(first); Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, changes);
        Assert.Equal(2, service.CurrentNotices.Count);
        time.Advance(TimeSpan.FromSeconds(3.999));
        time.UtcNow = time.UtcNow.AddHours(-2);
        Assert.NotNull(service.Current);
        time.Advance(TimeSpan.FromSeconds(.001));
        Assert.Null(service.Current);
        // Merely reading restored/snapshot state cannot create another receipt.
        Assert.Null(service.Current); Assert.Equal(2, changes);
    }

    [Fact]
    public void BurstKeepsAtMostThreeConcurrentNoticesAndNeverQueuesExpiredEntries()
    {
        var time = new Clock();
        using var service = new RareDropAlertService(() => new(SoundEnabled: false),
            () => throw new InvalidOperationException("No playback"), time);
        service.SetPresentationVisible(true);
        for (var receipt = 1; receipt <= 8; receipt++) service.Accept(Drop(quantity: receipt));
        Assert.Equal([6L, 7L, 8L], service.CurrentNotices.Select(notice => notice.Quantity).ToArray());
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(service.CurrentNotices);
        time.UtcNow = time.UtcNow.AddDays(-1);
        Assert.Empty(service.CurrentNotices);
    }

    [Theory]
    [InlineData("Item:1999999999")]
    [InlineData("Item:210350009")]
    [InlineData("SoulCrystal:2")]
    [InlineData("PetSoul:1")]
    [InlineData("Spectral Greaves")]
    public void OrdinaryUnknownAndNonItemReceiptsNeverAlert(string key)
    {
        using var service = new RareDropAlertService(() => new(),
            () => throw new InvalidOperationException("No playback"), new Clock());
        service.SetPresentationVisible(true);
        service.Accept(Drop(key));
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task RareAudioHasItsOwnSessionAndMuteDisposesOnlyItsPlayback()
    {
        var audio = new ConcurrentQueue<FakeAudio>();
        var options = new RareDropAlertOptions(Volume: .4);
        using var service = new RareDropAlertService(() => options, () =>
        { var session = new FakeAudio(); audio.Enqueue(session); return session; }, new Clock());
        service.SetPresentationVisible(true);
        service.Accept(Drop());
        await WaitFor(() => audio.TryPeek(out var candidate) && candidate.Started);
        Assert.True(audio.TryPeek(out var first));
        Assert.Equal(.4, first!.Volume);
        service.Accept(Drop(quantity: 2));
        await WaitFor(() => audio.Count == 2 && audio.Last().Started);
        Assert.True(first.Disposed);
        options = options with { SoundEnabled = false };
        service.ApplySettings();
        Assert.True(audio.Last().Disposed);
        Assert.NotNull(service.Current);
        service.Accept(Drop());
        Assert.Equal(2, audio.Count);
        service.Dispose();
        Assert.Null(service.Current);
        service.Accept(Drop());
        Assert.Equal(2, audio.Count);
    }

    [Fact]
    public void DisablingAlertsClearsTheNoticeAndNeverRetainsPendingItems()
    {
        var options = new RareDropAlertOptions(SoundEnabled: false);
        using var service = new RareDropAlertService(() => options,
            () => throw new InvalidOperationException("No playback"), new Clock());
        service.SetPresentationVisible(true); service.Accept(Drop());
        options = options with { Enabled = false }; service.ApplySettings();
        Assert.Null(service.Current);
        service.Accept(Drop());
        options = options with { Enabled = true }; service.ApplySettings();
        Assert.Null(service.Current);
    }


    [Fact]
    public void SelectedResonantCueIsEmbeddedStereoPcmWithSoftEnds()
    {
        var wave = RareDropSound.CreateWave();
        using var reader = new BinaryReader(new MemoryStream(wave));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4)));
        reader.BaseStream.Position = 20;
        Assert.Equal(1, reader.ReadInt16()); Assert.Equal(2, reader.ReadInt16());
        Assert.Equal(RareDropSound.SampleRate, reader.ReadInt32());
        reader.BaseStream.Position = 44;
        var samples = new List<short>();
        while (reader.BaseStream.Position < reader.BaseStream.Length) samples.Add(reader.ReadInt16());
        Assert.Equal((int)Math.Round(RareDropSound.Duration * RareDropSound.SampleRate * 2), samples.Count);
        Assert.All(samples, sample => Assert.InRange(sample, -25000, 25000));
        Assert.Equal(0, samples[0]); Assert.InRange(Math.Abs((int)samples[^1]), 0, 30);
        Assert.Contains(samples.Chunk(2), pair => pair[0] != pair[1]);
        Assert.Equal(.6, RareDropAlertService.NormalizeVolume(double.NaN));
        Assert.Equal(1, RareDropAlertService.NormalizeVolume(5));
    }

    [Theory]
    [InlineData(LootItemRarity.Unique)]
    [InlineData(LootItemRarity.Epic)]
    [InlineData(LootItemRarity.Mythic)]
    [InlineData(LootItemRarity.Special)]
    public void ExceptionalBackgroundsAnimateOnlyForActiveEnabledSessions(LootItemRarity rarity)
    {
        var snapshot = new LootOverlaySnapshot("session", true, 0, 0,
            [new("synthetic", "Example special item", 1) { Rarity = rarity }]);
        using var first = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", animationSeconds: .3);
        using var later = LootOverlayRenderer.RenderBitmap(snapshot, "aion2-stylized", animationSeconds: 2.3);
        Assert.NotEqual(Pixels(first), Pixels(later));
        Assert.True(new LootOverlayAnimation().IsAnimating(snapshot, 0));
        foreach (var idle in new[] { snapshot with { SessionActive = false }, snapshot with { AnimationsEnabled = false } })
        {
            using var stopped = LootOverlayRenderer.RenderBitmap(idle, "aion2-stylized", animationSeconds: .3);
            using var stoppedLater = LootOverlayRenderer.RenderBitmap(idle, "aion2-stylized", animationSeconds: 2.3);
            Assert.Equal(Pixels(stopped), Pixels(stoppedLater));
            Assert.False(new LootOverlayAnimation().IsAnimating(idle, 0));
        }
    }

    private static byte[] Pixels(System.Drawing.Bitmap bitmap)
    {
        using var stream = new MemoryStream(); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); return stream.ToArray();
    }
    private static async Task WaitFor(Func<bool> condition)
    {
        for (var retry = 0; retry < 100 && !condition(); retry++) await Task.Delay(10);
        Assert.True(condition());
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset UtcNow = Started;
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        internal void Advance(TimeSpan duration) { _timestamp += duration.Ticks; UtcNow += duration; }
    }
    private sealed class FakeAudio : IRareDropAudioSession
    {
        internal volatile bool Started, Disposed;
        internal double Volume;
        public async Task PlayAsync(byte[] wave, double volume, CancellationToken cancellation)
        {
            Volume = volume; Started = true; await Task.Delay(Timeout.Infinite, cancellation);
        }
        public void Dispose() => Disposed = true;
    }
}

[Collection("Settings file")]
public sealed class RareDropRenderExploration
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void RenderProductionRareLootOverlay()
    {
        var output = TestPrerequisites.RequiredValue("SOULCREST_LOOT_OVERLAY_PNG_DIRECTORY");
        Directory.CreateDirectory(output);
        var originalLanguage = UiText.Language;
        try
        {
            foreach (var language in new[] { "de", "en" })
            {
                UiText.Language = language;
                const string key = "Item:210230006";
                var name = MonsterLootItemCatalog.Default.Name(210230006, language);
                var snapshot = OverlayPreviewRenderer.FarmLootExample(language);
                snapshot = snapshot with
                {
                    Drops = snapshot.Drops.Concat(new LootOverlayDrop[]
                    {
                        new(key, name, 1) { Rarity = LootItemRarity.Unique, IconPath = MonsterLootIconCatalog.Default.DropAsset(key) }
                    }).ToArray(),
                    RareDrop = new("preview", name, 1, LootItemRarity.Unique,
                        MonsterLootIconCatalog.Default.DropAsset(key), DateTimeOffset.UnixEpoch) { ElapsedSeconds = 1 }
                };
                using var image = LootOverlayRenderer.RenderPreview(snapshot, "aion2-stylized", 1.8, animationSeconds: 1.3);
                image.Save(Path.Combine(output, "loot-rare-" + language + ".png"), ImageFormat.Png);

                int[] qualityItems = [610530001, 610530005, 610530008, 610530010];
                var quality = new LootOverlaySnapshot("quality-preview", true, 0, 0,
                    qualityItems.Select(id => new LootOverlayDrop("Item:" + id,
                        MonsterLootItemCatalog.Default.Name(id, language), 1)
                        { IconPath = MonsterLootIconCatalog.Default.ItemAsset(id) }).ToArray())
                    { KinaIconPath = MonsterLootIconCatalog.Default.ItemAsset(930100031) };
                using var qualityImage = LootOverlayRenderer.RenderPreview(quality, "aion2-stylized", 1.8, animationSeconds: 1.3);
                qualityImage.Save(Path.Combine(output, "loot-quality-" + language + ".png"), ImageFormat.Png);
                var live = OverlayPreviewRenderer.FarmLootExample(language);
                live = live with { Drops = LootOverlayDropPresentation.GroupCommonGear(live.Drops.Concat([
                    new LootOverlayDrop("Item:110160001", MonsterLootItemCatalog.Default.Name(110160001, language), 5)
                        { IconPath = MonsterLootIconCatalog.Default.ItemAsset(110160001) },
                    new LootOverlayDrop("Item:110160008", MonsterLootItemCatalog.Default.Name(110160008, language), 2)
                        { IconPath = MonsterLootIconCatalog.Default.ItemAsset(110160008) }
                ]).ToArray()) };
                using var liveImage = LootOverlayRenderer.RenderPreview(live, "aion2-stylized", 1.8,
                    animationSeconds: 1.3, backgroundOpacity: .6);
                liveImage.Save(Path.Combine(output, "loot-session-" + language + ".png"), ImageFormat.Png);
            }
        }
        finally { UiText.Language = originalLanguage; }
    }
}
