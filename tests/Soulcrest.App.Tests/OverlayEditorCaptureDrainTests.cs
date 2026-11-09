using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorCaptureDrainTests
{
    [Theory]
    [InlineData("success", false)]
    [InlineData("fault", false)]
    [InlineData("cancel", false)]
    [InlineData("success", true)]
    [InlineData("fault", true)]
    [InlineData("cancel", true)]
    public async Task PreviousCaptureCompletionNeverFailsTheNewRequest(string outcome, bool alreadyCompleted)
    {
        var previous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (alreadyCompleted) Complete(previous, outcome);

        var drain = OverlayEditorBitmapRenderer.WaitForPreviousCaptureAsync(previous.Task, CancellationToken.None);
        if (!alreadyCompleted)
        {
            Assert.False(drain.IsCompleted);
            Complete(previous, outcome);
        }

        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(previous.Task.IsCompleted);
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("cancel")]
    public async Task CurrentCancellationDoesNotCompleteOrBypassTheNativeCapture(string lateOutcome)
    {
        var previous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var current = new CancellationTokenSource();
        var cancelledDrain = OverlayEditorBitmapRenderer.WaitForPreviousCaptureAsync(previous.Task, current.Token);
        Assert.False(cancelledDrain.IsCompleted);

        current.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledDrain.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(current.Token, error.CancellationToken);
        Assert.False(previous.Task.IsCompleted);

        // A later valid caller still has to wait for the same native operation. Cancellation
        // of its predecessor cannot permit overlapping captures or present its late frame.
        var nextDrain = OverlayEditorBitmapRenderer.WaitForPreviousCaptureAsync(previous.Task, CancellationToken.None);
        Assert.False(nextDrain.IsCompleted);
        Complete(previous, lateOutcome);
        await nextDrain.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnAlreadyCompletedCaptureDoesNotSuppressCurrentCancellation()
    {
        using var current = new CancellationTokenSource();
        current.Cancel();
        var drain = OverlayEditorBitmapRenderer.WaitForPreviousCaptureAsync(Task.CompletedTask, current.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
        Assert.Equal(current.Token, error.CancellationToken);
    }

    private static void Complete(TaskCompletionSource source, string outcome)
    {
        if (outcome == "fault") source.SetException(new ArgumentException("Obsolete native capture failed."));
        else if (outcome == "cancel") source.SetCanceled();
        else source.SetResult();
    }
}
