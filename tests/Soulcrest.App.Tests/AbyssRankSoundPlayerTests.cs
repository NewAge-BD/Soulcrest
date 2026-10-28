using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AbyssRankSoundPlayerTests
{
    private static readonly long Low = AbyssRankCatalog.Default.Stages[3].Threshold;
    private static readonly long High = AbyssRankCatalog.Default.Stages[4].Threshold;

    [Fact]
    public async Task SameStageDownInvalidMuteAndZeroVolumeDoNotCreateAudio()
    {
        var calls = 0;
        var options = new AbyssRankSoundOptions();
        using var player = new AbyssRankSoundPlayer(() => options,
            () => { calls++; return new FakeSession(); }, (_, _, _) => [1]);
        Assert.False(await player.PlayAsync(Low, Low + 1, false));
        Assert.False(await player.PlayAsync(High, Low, false));
        Assert.False(await player.PlayAsync(-1, High, false));
        options = new(false);
        Assert.False(await player.PlayAsync(Low, High, false));
        options = new(true, 0);
        Assert.False(await player.PlayAsync(Low, High, false));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OneDedicatedSessionReceivesReducedCueAndNormalizedVolume()
    {
        var session = new FakeSession();
        var reducedCue = false;
        using var player = new AbyssRankSoundPlayer(() => new(true, 2), () => session,
            (from, to, reduced) => { Assert.Equal(Low, from); Assert.Equal(High, to); reducedCue = reduced; return [7, 8]; });
        var play = player.PlayAsync(Low, High, true);
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reducedCue);
        Assert.Equal(1, session.Volume);
        Assert.Equal(new byte[] { 7, 8 }, session.Wave);
        session.End.TrySetResult();
        Assert.True(await play);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task StopDuringSynthesisCannotStartStalePlayback()
    {
        using var synthesizing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var player = new AbyssRankSoundPlayer(() => new(), () => { calls++; return new FakeSession(); },
            (_, _, _) => { synthesizing.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); return [1]; });
        var play = player.PlayAsync(Low, High, false);
        Assert.True(synthesizing.Wait(TimeSpan.FromSeconds(5)));
        player.Stop();
        release.Set();
        Assert.False(await play);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NewRequestReplacesPlaybackAndMuteCancelsTheLatest()
    {
        var options = new AbyssRankSoundOptions();
        var first = new FakeSession();
        var second = new FakeSession();
        var sessions = new Queue<FakeSession>([first, second]);
        using var player = new AbyssRankSoundPlayer(() => options, () => sessions.Dequeue(), (_, _, _) => [1]);
        var older = player.PlayAsync(Low, High, false);
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var latest = player.PlayAsync(Low, High, true);
        Assert.False(await older);
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.Disposed);
        options = new(false);
        player.ApplySettings();
        Assert.False(await latest);
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task UnrelatedSettingsDoNotStopTailButDisposeDoes()
    {
        var session = new FakeSession();
        var player = new AbyssRankSoundPlayer(() => new(), () => session, (_, _, _) => [1]);
        var play = player.PreviewAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        player.ApplySettings();
        Assert.False(session.Disposed);
        player.Dispose(); player.Dispose(); player.Stop();
        Assert.False(await play);
        Assert.True(session.Disposed);
        Assert.False(await player.PreviewAsync());
    }

    [Fact]
    public async Task DisposedPlayerIgnoresLateSettingsAndPlaybackWithoutReadingDisposedDependencies()
    {
        var reads = 0;
        var rejectReads = false;
        using var player = new AbyssRankSoundPlayer(() =>
        {
            reads++;
            if (rejectReads) throw new ObjectDisposedException("Synthetic options provider");
            return new();
        }, () => throw new InvalidOperationException("Disposed player must not create an audio session"),
            (_, _, _) => throw new InvalidOperationException("Disposed player must not synthesize audio"));
        Assert.Equal(1, reads);
        player.Dispose(); rejectReads = true;
        player.ApplySettings();
        Assert.False(await player.PlayAsync(Low, High, false));
        Assert.False(await player.PreviewAsync());
        player.Start(Low, High, false);
        Assert.Equal(1, reads);
    }

    private sealed class FakeSession : IRareDropAudioSession
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource End = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? Wave;
        public double Volume;
        public bool Disposed;
        public async Task PlayAsync(byte[] wave, double volume, CancellationToken cancellation)
        {
            Wave = wave; Volume = volume; Started.TrySetResult();
            await End.Task.WaitAsync(cancellation);
        }
        public void Dispose() { Disposed = true; End.TrySetCanceled(); }
    }
}
