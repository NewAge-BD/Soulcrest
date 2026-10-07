using System.Runtime.InteropServices;
using Soulcrest.App.Capture;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CaptureReadbackTests
{
    [Fact]
    public void BusyFramesDoNotReachTheConsumerAndTheNextGoodFrameDoes()
    {
        var calls = 0;
        var consumed = 0;
        var picture = new object();
        object Capture()
        {
            calls++;
            if (calls <= 3) throw new COMException("GPU busy", CaptureReadback.WasStillDrawing);
            return picture;
        }
        for (var i = 0; i < 4; i++)
        {
            var frame = CaptureReadback.TryCapture(Capture);
            if (frame is null) continue;
            Assert.Same(picture, frame);
            consumed++;
        }
        Assert.Equal(4, calls);
        Assert.Equal(1, consumed);
    }

    [Theory]
    [InlineData(unchecked((int)0x887A0005))] // device removed
    [InlineData(unchecked((int)0x887A0007))] // device reset
    [InlineData(unchecked((int)0x80070005))] // access denied
    public void PermanentGraphicsErrorsRemainVisible(int code)
    {
        var error = new COMException("fatal", code);
        Assert.Same(error, Assert.Throws<COMException>(() => CaptureReadback.TryCapture<object>(() => throw error)));
    }

    [Fact]
    public void CancellationIsNotSwallowed()
    {
        Assert.Throws<OperationCanceledException>(() => CaptureReadback.TryCapture<object>(() => throw new OperationCanceledException()));
    }
}
