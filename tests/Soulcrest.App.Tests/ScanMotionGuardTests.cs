using System.Drawing;
using System.Runtime.InteropServices;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ScanMotionGuardTests
{
    private static Bitmap Picture(Color colour)
    {
        var picture = new Bitmap(240, 180);
        using var graphics = Graphics.FromImage(picture);
        graphics.Clear(colour);
        return picture;
    }

    [Fact]
    public async Task MovementHidesLabelsWhileOcrIsPendingAndDiscardsItsLateResult()
    {
        using var source = Picture(Color.White);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guard = ScanMotionGuard.ReadAsync(source, [new Rectangle(0, 0, 240, 180)],
            () => Picture(Color.Black), _ => release.Task, () => hidden.SetResult(), CancellationToken.None);
        await hidden.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(guard.IsCompleted);
        release.SetResult("stale page");
        Assert.Null(await guard);
    }

    [Fact]
    public async Task SmallChangesAreComparedToTheOriginalFrameRatherThanTheLastPoll()
    {
        using var source = Picture(Color.White);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = 0;
        var guard = ScanMotionGuard.ReadAsync(source, [new Rectangle(0, 0, 240, 180)],
            () => { var value = ++captures == 1 ? 250 : 245; return Picture(Color.FromArgb(value, value, value)); },
            _ => release.Task, () => hidden.SetResult(), CancellationToken.None);
        try
        {
            await hidden.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(2, captures);
        }
        finally { release.TrySetResult("old page"); }
        Assert.Null(await guard);
    }

    [Fact]
    public async Task FinalCaptureAlsoRejectsMovementAfterFastOcr()
    {
        using var source = Picture(Color.White);
        var hidden = false;
        var result = await ScanMotionGuard.ReadAsync(source, [new Rectangle(0, 0, 240, 180)],
            () => Picture(Color.Black), _ => Task.FromResult("stale page"), () => hidden = true, CancellationToken.None);
        Assert.Null(result);
        Assert.True(hidden);
    }

    [Fact]
    public async Task BusyCaptureCannotPublishAnUnverifiedPage()
    {
        using var source = Picture(Color.White);
        var result = await ScanMotionGuard.ReadAsync(source, [new Rectangle(0, 0, 240, 180)],
            () => throw new COMException("busy", CaptureReadback.WasStillDrawing), _ => Task.FromResult("page"), () => { }, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task UnchangedGridAcceptsResultAndExternalCancellationStopsTheRead()
    {
        using var source = Picture(Color.White);
        var bounds = new[] { new Rectangle(0, 0, 240, 180) };
        Assert.Equal("page", await ScanMotionGuard.ReadAsync(source, bounds, () => Picture(Color.White),
            _ => Task.FromResult("page"), () => throw new InvalidOperationException("No movement."), CancellationToken.None));
        using var stop = new CancellationTokenSource();
        var guard = ScanMotionGuard.ReadAsync(source, bounds, () => Picture(Color.White), async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "page";
        }, () => throw new InvalidOperationException("Cancellation is not movement."), stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard);
    }
}
