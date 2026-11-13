using System.Runtime.InteropServices;
using System.Text.Json;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorRenderDiagnosticsTests
{
    [Fact]
    public void PlaceModeFailuresRetainTheUnderlyingCodeAndStageWithoutPrivateExceptionData()
    {
        var health = new OverlayEditorRenderDiagnostics();
        const int code = unchecked((int)0x80004005);
        health.Failure(OverlayEditorRenderStage.Capture, new InvalidOperationException("private-path",
            new COMException("private-identity", code)));
        var failed = health.Capture();
        Assert.True(failed.LastAttemptFailed);
        Assert.Equal(OverlayEditorRenderStage.Capture, failed.FailureStage);
        Assert.Equal(nameof(COMException), failed.ExceptionType);
        Assert.Equal(code, failed.HResult);
        Assert.Equal(1, failed.Failed);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(failed));
        health.Success();
        Assert.False(health.Capture().LastAttemptFailed);
        Assert.Equal(1, health.Capture().Succeeded);
        Assert.True(failed.LastAttemptFailed); // immutable passive snapshots
    }
}
