using System.Runtime.InteropServices;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorDocumentReadinessTests
{
    private const string Local = "https://0.0.0.1/";

    [Fact]
    public async Task ALocalDocumentMustLoadBeforeNavigationCanBeUsedForScripts()
    {
        var readiness = new OverlayEditorDocumentReadiness();
        readiness.NavigationStarting(7, Local);
        Assert.False(readiness.Ready.IsCompleted);
        readiness.NavigationCompleted(7, success: true);
        Assert.False(readiness.Ready.IsCompleted);
        readiness.DocumentLoaded(7, Local);
        await readiness.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(readiness.Ready.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ALocalDomEventCanProveReadinessWhenTheStartEventWasMissed()
    {
        var readiness = new OverlayEditorDocumentReadiness();
        readiness.NavigationCompleted(8, success: true);
        Assert.False(readiness.Ready.IsCompleted);
        readiness.DocumentLoaded(8, Local + "overlay-placement-render.html");
        await readiness.Ready.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("about:blank")]
    [InlineData("https://example.com/")]
    [InlineData("https://0.0.0.1.example.com/")]
    [InlineData("https://0.0.0.1:9443/")]
    [InlineData("http://0.0.0.1/")]
    public async Task NonlocalOrBlankEventsCannotPrimeOrCompleteTheGate(string? address)
    {
        var readiness = new OverlayEditorDocumentReadiness();
        readiness.NavigationStarting(3, address);
        readiness.NavigationCompleted(3, success: false);
        readiness.DocumentLoaded(3, address);
        Assert.False(readiness.Ready.IsCompleted);
        readiness.DocumentLoaded(4, Local);
        await readiness.Ready.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EarlierNavigationEventsCannotFailOrCompleteTheCurrentLocalNavigation()
    {
        var readiness = new OverlayEditorDocumentReadiness();
        readiness.NavigationStarting(2, Local);
        readiness.NavigationStarting(3, Local);
        readiness.NavigationCompleted(2, success: false);
        readiness.DocumentLoaded(2, Local);
        readiness.DocumentLoaded(3, "about:blank");
        readiness.NavigationStarting(4, "https://example.com/");
        readiness.DocumentLoaded(4, "https://example.com/");
        readiness.NavigationCompleted(4, success: false);
        Assert.False(readiness.Ready.IsCompleted);
        readiness.DocumentLoaded(3, Local);
        await readiness.Ready.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FailureOfTheCurrentLocalNavigationFaultsWithoutExposingItsAddress()
    {
        var readiness = new OverlayEditorDocumentReadiness();
        var privateAddress = Local + "?private-value=example";
        readiness.NavigationStarting(11, privateAddress);
        readiness.NavigationCompleted(11, success: false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => readiness.Ready);
        Assert.Equal("The local editor rendering document did not load.", error.Message);
        Assert.DoesNotContain(privateAddress, error.ToString());
        readiness.DocumentLoaded(11, privateAddress);
        readiness.NavigationStarting(12, Local);
        readiness.DocumentLoaded(12, Local);
        readiness.Cancel();
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => readiness.Ready));
    }

    [Fact]
    public async Task CancellationIsTerminalAndLateBrowserEventsCannotMakeTheHostReady()
    {
        var readiness = new OverlayEditorDocumentReadiness();
        readiness.NavigationStarting(11, Local);
        readiness.Cancel();
        readiness.NavigationCompleted(11, success: false);
        readiness.DocumentLoaded(11, Local);
        readiness.NavigationStarting(12, Local);
        readiness.DocumentLoaded(12, Local);
        readiness.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness.Ready);
        Assert.True(readiness.Ready.IsCanceled);
    }

    [Fact]
    public async Task SuccessIsTerminalAndLateFailuresCancellationOrNavigationDoNotReplaceIt()
    {
        var readiness = new OverlayEditorDocumentReadiness();
        readiness.NavigationStarting(9, Local);
        readiness.DocumentLoaded(9, Local);
        readiness.NavigationCompleted(9, success: false);
        readiness.NavigationStarting(10, Local);
        readiness.NavigationCompleted(10, success: false);
        readiness.Cancel();
        await readiness.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(readiness.Ready.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ACancelledHostDoesNotChangeTheReadinessOfItsReplacement()
    {
        var abandoned = new OverlayEditorDocumentReadiness();
        var replacement = new OverlayEditorDocumentReadiness();
        abandoned.NavigationStarting(1, Local);
        abandoned.Cancel();
        replacement.NavigationStarting(1, Local);
        abandoned.DocumentLoaded(1, Local);
        Assert.False(replacement.Ready.IsCompleted);
        replacement.DocumentLoaded(1, Local);
        await replacement.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.Ready);
    }

    [Fact]
    public async Task NativeReadinessFailurePreservesItsExceptionAndResultCode()
    {
        var readiness = new OverlayEditorDocumentReadiness();
        const int resultCode = unchecked((int)0x8007139F);
        var failure = new COMException("The native document is unavailable.", resultCode);
        readiness.Failure(failure);
        var actual = await Assert.ThrowsAsync<COMException>(() => readiness.Ready);
        Assert.Same(failure, actual);
        Assert.Equal(resultCode, actual.HResult);
        readiness.NavigationStarting(5, Local);
        readiness.DocumentLoaded(5, Local);
        readiness.Cancel();
        readiness.Failure(new InvalidOperationException("Late failure."));
        Assert.Same(failure, await Assert.ThrowsAsync<COMException>(() => readiness.Ready));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateNativeFailureCannotReplaceSuccessfulOrCancelledReadiness(bool success)
    {
        var readiness = new OverlayEditorDocumentReadiness();
        if (success) readiness.DocumentLoaded(1, Local);
        else readiness.Cancel();
        readiness.Failure(new COMException("Late native failure.", unchecked((int)0x8007139F)));
        if (success)
        {
            await readiness.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(readiness.Ready.IsCompletedSuccessfully);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness.Ready);
            Assert.True(readiness.Ready.IsCanceled);
        }
    }
}
