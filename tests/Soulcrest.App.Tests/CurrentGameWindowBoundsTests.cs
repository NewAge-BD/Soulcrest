using System.Drawing;
using System.Reflection;
using Soulcrest.App.Capture;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CurrentGameWindowBoundsTests
{
    private static readonly Rectangle InitialBounds = new(100, 80, 1920, 1080);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFreshBoundsFromTheBoundWindow(bool requireForeground)
    {
        var native = new Metadata();
        Assert.Equal(InitialBounds, Read(native, requireForeground));
        native.Area = new(-1300, 200, 2560, 1440);
        Assert.Equal(native.Area, Read(native, requireForeground));
        Assert.Equal(2, native.BoundsReads);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("hidden")]
    [InlineData("minimized")]
    [InlineData("reused")]
    [InlineData("missing-pid")]
    [InlineData("background")]
    public void InvalidBoundWindowNeverReachesBoundsRead(string transition)
    {
        var native = new Metadata();
        Change(native, transition);
        Assert.Null(Read(native));
        Assert.Equal(0, native.BoundsReads);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("hidden")]
    [InlineData("minimized")]
    [InlineData("reused")]
    [InlineData("missing-pid")]
    [InlineData("background")]
    public void TransitionDuringDwmReadRejectsTheObservation(string transition)
    {
        var native = new Metadata();
        native.OnBounds = () => Change(native, transition);
        Assert.Null(Read(native));
        Assert.Equal(1, native.BoundsReads);
    }

    [Fact]
    public void AlwaysModeAllowsBackgroundButDoesNotBypassOtherChecks()
    {
        var native = new Metadata { ForegroundWindow = 999 };
        Assert.Equal(InitialBounds, Read(native, requireForeground: false));
        native.Minimized = true;
        Assert.Null(Read(native, requireForeground: false));
    }

    [Fact]
    public void ZeroWindowOrUnconfirmedPidNeverReadsNativeMetadata()
    {
        var native = new Metadata();
        Assert.Null(CapturedGameWindowMetadata.Read(0, 42, true, native));
        Assert.Null(CapturedGameWindowMetadata.Read(123, 0, true, native));
        Assert.Equal(0, native.IdentityReads);
        Assert.Equal(0, native.BoundsReads);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void EmptyOrInvalidBoundsAreUnavailable(int width, int height)
    {
        var native = new Metadata { Area = new(0, 0, width, height) };
        Assert.Null(Read(native));
    }

    [Fact]
    public void DwmFailureIsUnavailable()
    {
        Assert.Null(Read(new Metadata { Area = null }));
    }

    [Fact]
    public void MissingExistingSessionDoesNotStartAReader()
    {
        var observation = new CurrentGameWindowBounds(() => null, () => false);
        Assert.Null(observation.Read(true));
    }

    [Fact]
    public void StableSessionForwardsForegroundRequirementAndFreshBounds()
    {
        var source = new Source();
        var observation = new CurrentGameWindowBounds(() => source, () => false);
        Assert.Equal(InitialBounds, observation.Read(false));
        Assert.False(source.RequiredForeground);
        source.Area = new(200, 300, 1280, 720);
        Assert.Equal(source.Area, observation.Read(true));
        Assert.True(source.RequiredForeground);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplacementOrRemovalDuringReadRejectsOldSession(bool replacement)
    {
        var source = new Source();
        IGameWindowBoundsSource? current = source;
        source.OnRead = () => current = replacement ? new Source() : null;
        var observation = new CurrentGameWindowBounds(() => current, () => false);
        Assert.Null(observation.Read(true));
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public void AlreadyDisposedServiceNeverReadsTheSource()
    {
        var source = new Source();
        var observation = new CurrentGameWindowBounds(() => source, () => true);
        Assert.Null(observation.Read(true));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void ServiceDisposalDuringReadRejectsTheObservation()
    {
        var disposed = false;
        var source = new Source { OnRead = () => disposed = true };
        var observation = new CurrentGameWindowBounds(() => source, () => disposed);
        Assert.Null(observation.Read(true));
    }

    [Fact]
    public async Task UnstartedServiceReadsNeitherPixelsNorCaptureLockOrThreadMetadata()
    {
        using var capture = new GameCaptureService();
        var gate = typeof(GameCaptureService).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(capture)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var driver = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(); } });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Task.Run(() =>
            {
                var frameAt = GameCaptureService.GrabbedFrameAt;
                var windowOnly = GameCaptureService.GrabbedGameWindowOnly;
                var timing = GameCaptureService.GrabbedTiming;
                var sentinel = new CaptureTiming(true, 1, 2, 3, 4, 5, CaptureSessionId: 42);
                try
                {
                    GameCaptureService.GrabbedFrameAt = 1234;
                    GameCaptureService.GrabbedGameWindowOnly = true;
                    GameCaptureService.GrabbedTiming = sentinel;
                    Assert.Null(capture.TryGetCurrentGameWindowBounds(true));
                    Assert.Null(capture.TryGetCurrentGameWindowBounds(false));
                    Assert.Equal(1234, GameCaptureService.GrabbedFrameAt);
                    Assert.True(GameCaptureService.GrabbedGameWindowOnly);
                    Assert.Equal(sentinel, GameCaptureService.GrabbedTiming);
                    Assert.Equal(0, capture.Diagnostics.Grabs);
                    Assert.Equal("GDI", capture.Method);
                }
                finally
                {
                    GameCaptureService.GrabbedFrameAt = frameAt;
                    GameCaptureService.GrabbedGameWindowOnly = windowOnly;
                    GameCaptureService.GrabbedTiming = timing;
                }
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); await driver; }
        capture.Dispose();
        Assert.Null(capture.TryGetCurrentGameWindowBounds(true));
    }

    private static Rectangle? Read(Metadata native, bool requireForeground = true) =>
        CapturedGameWindowMetadata.Read(123, 42, requireForeground, native);

    private static void Change(Metadata native, string transition)
    {
        switch (transition)
        {
            case "closed": native.Exists = false; break;
            case "hidden": native.Visible = false; break;
            case "minimized": native.Minimized = true; break;
            case "reused": native.Pid = 43; break;
            case "missing-pid": native.Pid = 0; break;
            case "background": native.ForegroundWindow = 999; break;
            default: throw new ArgumentOutOfRangeException(nameof(transition));
        }
    }

    private sealed class Metadata : IGameWindowMetadata
    {
        internal bool Exists = true, Visible = true, Minimized;
        internal uint Pid = 42;
        internal Rectangle? Area = InitialBounds;
        internal int IdentityReads, BoundsReads;
        internal Action? OnBounds;
        public nint ForegroundWindow { get; set; } = 123;
        public bool IsWindow(nint window) { Assert.Equal((nint)123, window); IdentityReads++; return Exists; }
        public bool IsVisible(nint window) => Visible;
        public bool IsMinimized(nint window) => Minimized;
        public uint ProcessId(nint window) => Pid;
        public Rectangle? Bounds(nint window) { BoundsReads++; OnBounds?.Invoke(); return Area; }
    }

    private sealed class Source : IGameWindowBoundsSource
    {
        internal Rectangle? Area = InitialBounds;
        internal bool RequiredForeground;
        internal int Reads;
        internal Action? OnRead;
        public Rectangle? TryGetCurrentGameWindowBounds(bool requireForeground)
        { Reads++; RequiredForeground = requireForeground; OnRead?.Invoke(); return Area; }
    }
}
