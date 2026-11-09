using System.Drawing;

namespace Soulcrest.App.OverlayEditorKit;

public interface IOverlayEditorLiveRenderer
{
    IOverlayEditorLiveRenderSession CreateSession();
}

/// <summary>One stable, UI-thread-owned browser surface per visible editor window.</summary>
public interface IOverlayEditorLiveRenderSession : IDisposable
{
    Task<Bitmap> UpdateAndCaptureAsync(OverlayInstance window, OverlaySnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed class OverlayEditorLiveRenderer(IServiceProvider services) : IOverlayEditorLiveRenderer
{
    public IOverlayEditorLiveRenderSession CreateSession() => new Session(services);

    private sealed class Session(IServiceProvider services) : IOverlayEditorLiveRenderSession
    {
        private readonly OverlayEditorBitmapRenderer _renderer = new(services, pauseAnimations: false);
        public Task<Bitmap> UpdateAndCaptureAsync(OverlayInstance window, OverlaySnapshot snapshot, CancellationToken cancellationToken = default) =>
            _renderer.RenderAsync(window, snapshot, cancellationToken);
        public void Dispose() => _renderer.Dispose();
    }
}
