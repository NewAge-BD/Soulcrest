using System.Drawing;

namespace Soulcrest.App.OverlayEditorKit;

/// <summary>Renders only the supplied editor content; never acquires live data or captures another window.</summary>
public interface IOverlayEditorBitmapRenderer : IDisposable
{
    Task<Bitmap> RenderAsync(OverlayInstance window, OverlaySnapshot snapshot, CancellationToken cancellationToken = default);
}
