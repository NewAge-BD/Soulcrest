using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Soulcrest.App.Overlay;

internal interface IOverlayEditorLiveSurface : IDisposable
{
    bool Visible { get; }
    event Action? GeometryInvalidated;
    void HideFrame();
    void ShowFrame(Bitmap image, Rectangle bounds, bool visibleInRecordings);
}

/// <summary>Own read-only layered surface. Placement is handled exclusively by the separate Place Mode.</summary>
internal sealed class OverlayEditorLiveWindow : Form, IOverlayEditorLiveSurface
{
    private readonly OverlayCaptureVisibility _captureVisibility = new();
    private bool _presentationAllowed;
    public event Action? GeometryInvalidated;

    internal OverlayEditorLiveWindow()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Soulcrest Overlay";
        LayeredOverlayPresenter.Configure(this, true, 1);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT
                | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
            return parameters;
        }
    }
    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(value && _presentationAllowed);

    public void HideFrame()
    {
        _presentationAllowed = false;
        if (!IsDisposed) Hide();
    }

    public void ShowFrame(Bitmap image, Rectangle bounds, bool visibleInRecordings)
    {
        if (IsDisposed || Disposing) return;
        _ = Handle;
        LayeredOverlayPresenter.Configure(this, true, 1);
        _captureVisibility.Apply(Handle, visibleInRecordings);
        Bounds = bounds;
        _presentationAllowed = true;
        if (image.Size == bounds.Size)
        {
            if (!LayeredOverlayPresenter.Present(this, image)) throw new InvalidOperationException("Das Overlay konnte nicht angezeigt werden.");
            if (!Visible) Show();
            return;
        }
        using var scaled = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, new Rectangle(Point.Empty, scaled.Size));
        }
        if (!LayeredOverlayPresenter.Present(this, scaled)) throw new InvalidOperationException("Das Overlay konnte nicht angezeigt werden.");
        if (!Visible) Show();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _captureVisibility.Reset();
        base.OnHandleDestroyed(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        base.OnDpiChanged(e);
        HideFrame();
        GeometryInvalidated?.Invoke();
    }
}
