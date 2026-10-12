using System.Collections.Concurrent;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AlertSoundPlayerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Soulcrest-alert-audio-" + Guid.NewGuid().ToString("N"));
    private string Mp3 => Path.Combine(_directory, "notification.MP3");
    private readonly ConcurrentQueue<string> _systemSounds = new();

    public AlertSoundPlayerTests()
    {
        Directory.CreateDirectory(_directory);
        // Sessions are substituted in every playback test; this is deliberately not an audio fixture.
        File.WriteAllText(Mp3, "fake MP3 for path validation");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task StandardToneUsesEmbeddedAudioSessionAndStopsWhenReplaced()
    {
        var standard = new FakeSession();
        using var player = new AlertSoundPlayer(() => throw new InvalidOperationException("MP3 should not open"),
            _systemSounds.Enqueue, createStandardSession: () => standard);
        Assert.True(await player.Play("standard"));
        Assert.Empty(_systemSounds);
        Assert.False(standard.Disposed);
        Assert.True(await player.Play("beep"));
        Assert.True(standard.Disposed);
        using var stream = typeof(AlertSoundPlayer).Assembly.GetManifestResourceStream("Soulcrest.AlertStandard.wav");
        Assert.NotNull(stream);
        using var reader = new BinaryReader(stream);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4)));
        Assert.InRange(stream.Length, 26000, 29000);
    }

    [Theory]
    [InlineData("asterisk", "asterisk")]
    [InlineData("beep", "beep")]
    [InlineData("hand", "hand")]
    [InlineData("question", "question")]
    [InlineData("exclamation", "exclamation")]
    [InlineData("unknown", "exclamation")]
    public async Task BuiltInSoundsNeverOpenTheCustomFile(string sound, string expected)
    {
        using var player = new AlertSoundPlayer(() => throw new InvalidOperationException("No custom decoder expected."), _systemSounds.Enqueue);
        Assert.True(await player.Play(sound, Mp3));
        Assert.Equal(new[] { expected }, _systemSounds);
        Assert.Null(player.Status);
    }

    [Fact]
    public async Task LocalMp3StartsInBackgroundAndRemainsAliveUntilStopped()
    {
        var session = new FakeSession();
        using var player = new AlertSoundPlayer(() => session, _systemSounds.Enqueue);
        Assert.True(await player.Play("custom", Mp3));
        Assert.Equal(Path.GetFullPath(Mp3), session.Path);
        Assert.False(session.Disposed);
        Assert.Empty(_systemSounds);
        player.Dispose();
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task MissingFileFallsBackWithoutOpeningTheDecoder()
    {
        using var player = new AlertSoundPlayer(() => throw new InvalidOperationException("No decoder expected."), _systemSounds.Enqueue);
        Assert.False(await player.Play("custom", Path.Combine(_directory, "missing.mp3")));
        Assert.Equal(new[] { "exclamation" }, _systemSounds);
        Assert.NotNull(player.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.test/audio.mp3")]
    [InlineData("\\\\server\\share\\audio.mp3")]
    [InlineData("relative.mp3")]
    [InlineData("C:\\sound.wav")]
    public async Task InvalidCustomPathsCannotReachTheDecoder(string? path)
    {
        using var player = new AlertSoundPlayer(() => throw new InvalidOperationException("No decoder expected."), _systemSounds.Enqueue);
        Assert.False(await player.Play("custom", path));
        Assert.Equal(new[] { "exclamation" }, _systemSounds);
        Assert.NotNull(player.Status);
    }

    [Fact]
    public async Task DecoderFailureStopsTheSessionAndUsesFallback()
    {
        var session = new FakeSession { StartupError = new IOException("Invalid MP3 data.") };
        using var player = new AlertSoundPlayer(() => session, _systemSounds.Enqueue);
        Assert.False(await player.Play("custom", Mp3));
        Assert.True(session.Disposed);
        Assert.Equal(new[] { "exclamation" }, _systemSounds);
        Assert.NotNull(player.Status);
    }

    [Fact]
    public async Task LateDecoderFailureUpdatesVisibleStatusAndFallsBackOnce()
    {
        var session = new FakeSession();
        using var player = new AlertSoundPlayer(() => session, _systemSounds.Enqueue);
        Assert.True(await player.Play("custom", Mp3));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.Changed += () => { if (player.Status is not null) changed.TrySetResult(); };
        session.Fail(new IOException("Decode failed after opening."));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Disposed);
        Assert.Equal(new[] { "exclamation" }, _systemSounds);
    }

    [Fact]
    public async Task ANewRequestCancelsPendingPlaybackAndStopsTheOldSession()
    {
        var session = new FakeSession { HoldStartup = true };
        using var player = new AlertSoundPlayer(() => session, _systemSounds.Enqueue);
        var first = player.Play("custom", Mp3);
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = player.Play("beep");
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(session.Disposed);
        Assert.Equal(new[] { "beep" }, _systemSounds);
        Assert.Null(player.Status);
    }

    [Fact]
    public async Task DisposeCancelsPendingStartupAndRejectsLaterRequests()
    {
        var session = new FakeSession { HoldStartup = true };
        using var player = new AlertSoundPlayer(() => session, _systemSounds.Enqueue);
        var pending = player.Play("custom", Mp3);
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        player.Dispose();
        Assert.True(session.Disposed);
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await player.Play("beep"));
        Assert.Empty(_systemSounds);
    }

    [Fact]
    public async Task FailureOfTheFallbackIsReportedWithoutCrashing()
    {
        using var player = new AlertSoundPlayer(() => new FakeSession(), _ => throw new InvalidOperationException("No output device."));
        Assert.False(await player.Play("custom", Path.Combine(_directory, "missing.mp3")));
        Assert.NotNull(player.Status);
    }

    [Fact]
    public async Task BrokenStatusSubscriberCannotPreventPlayback()
    {
        using var player = new AlertSoundPlayer(() => new FakeSession(), _systemSounds.Enqueue);
        player.Changed += () => throw new InvalidOperationException("Broken view.");
        Assert.True(await player.Play("beep"));
        Assert.Equal(new[] { "beep" }, _systemSounds);
    }

    [Fact]
    public async Task PickerReturnsOnlyTheChosenLocalPathAndDoesNotPlayIt()
    {
        using var player = new AlertSoundPlayer(() => throw new InvalidOperationException("No playback expected."), _systemSounds.Enqueue,
            () => Task.FromResult(new AlertSoundFileChoice(Mp3)));
        var choice = await player.ChooseMp3Async();
        Assert.Equal(Mp3, choice.Path);
        Assert.Null(choice.Error);
        Assert.Empty(_systemSounds);
    }

    [Fact]
    public async Task CancellingThePickerIsNotAnError()
    {
        using var player = new AlertSoundPlayer(() => new FakeSession(), _systemSounds.Enqueue,
            () => Task.FromResult(new AlertSoundFileChoice()));
        Assert.Equal(new AlertSoundFileChoice(), await player.ChooseMp3Async());
        Assert.Empty(_systemSounds);
    }

    [Fact]
    public async Task PickerFailureProducesUserFeedbackWithoutExposingTheException()
    {
        using var player = new AlertSoundPlayer(() => new FakeSession(), _systemSounds.Enqueue,
            () => Task.FromException<AlertSoundFileChoice>(new IOException("private filename")));
        var choice = await player.ChooseMp3Async();
        Assert.Null(choice.Path);
        Assert.NotNull(choice.Error);
        Assert.DoesNotContain("private filename", choice.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.test/audio.mp3")]
    [InlineData("\\\\server\\share\\audio.mp3")]
    [InlineData("relative.mp3")]
    public async Task PickerRejectsRemoteAndRelativePaths(string path)
    {
        using var player = new AlertSoundPlayer(() => new FakeSession(), _systemSounds.Enqueue,
            () => Task.FromResult(new AlertSoundFileChoice(path)));
        var choice = await player.ChooseMp3Async();
        Assert.Null(choice.Path);
        Assert.NotNull(choice.Error);
    }

    [Fact]
    public async Task ConcurrentPickersAreRejectedAndCancellationReleasesTheGuard()
    {
        var selection = new TaskCompletionSource<AlertSoundFileChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var player = new AlertSoundPlayer(() => new FakeSession(), _systemSounds.Enqueue, () => selection.Task);
        var first = player.ChooseMp3Async();
        Assert.NotNull((await player.ChooseMp3Async()).Error);
        selection.SetResult(new());
        Assert.Equal(new AlertSoundFileChoice(), await first);
        Assert.Equal(new AlertSoundFileChoice(), await player.ChooseMp3Async());
    }

    private sealed class FakeSession : IAlertAudioSession
    {
        public event Action<Exception>? Failed;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldStartup { get; init; }
        public Exception? StartupError { get; init; }
        public bool Disposed { get; private set; }
        public string? Path { get; private set; }
        public async Task StartAsync(string path, CancellationToken cancellation)
        {
            Path = path;
            Started.TrySetResult();
            if (StartupError is not null) throw StartupError;
            if (HoldStartup) await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        }
        public void Fail(Exception error) => Failed?.Invoke(error);
        public void Dispose() => Disposed = true;
    }
}
