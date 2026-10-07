using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>
/// Frames the cards of the pet window that the scan could not finish (user request 2026-10-03): orange =
/// value unreadable, yellow = pet unknown, both "click it". Shown only while a scan runs and the page
/// stands still, so the frames never lag behind a scrolling list. Per-pixel alpha, click-through and
/// always excluded from screen capture: the scan must never see its own frames.
/// </summary>
public sealed class ScanMarkerOverlayForm : Form
{
    private static readonly Color ValueMissing = Color.FromArgb(249, 115, 22);
    private static readonly Color PetUnknown = Color.FromArgb(250, 204, 21);
    private readonly PetScanService _scan;
    private bool _pending;
    private string _shown = "";

    public ScanMarkerOverlayForm(PetScanService scan)
    {
        _scan = scan;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        _scan.Changed += Schedule;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE
                | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetCaptureVisibility(Handle, false);
    }

    /// <summary>Scan events come from the scan thread: redraw once on the UI thread.</summary>
    private void Schedule()
    {
        if (_pending || IsDisposed || !IsHandleCreated)
            return;
        _pending = true;
        BeginInvoke(() =>
        {
            _pending = false;
            Render();
        });
    }

    private void Render()
    {
        var page = _scan.Page;
        var marks = _scan.Running && page is { Stable: true } ? page.Marks : [];
        var region = _scan.CaptureRegion;
        var state = string.Join(";", marks.Select(m => $"{m.Bounds}{m.ValueMissing}")) + region;
        if (state == _shown)
            return;
        _shown = state;
        if (marks.Count == 0)
        {
            if (Visible)
                Hide();
            return;
        }
        var area = marks.Select(m => m.Bounds).Aggregate(Rectangle.Union);
        area.Inflate(12, 12);
        using var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            using var font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel);
            foreach (var mark in marks)
            {
                var color = mark.ValueMissing ? ValueMissing : PetUnknown;
                var box = mark.Bounds;
                box.Offset(-area.X, -area.Y);
                box.Inflate(4, 4);
                using var glow = new Pen(Color.FromArgb(90, color), 7);
                using var line = new Pen(color, 3);
                graphics.DrawRectangle(glow, box);
                graphics.DrawRectangle(line, box);
                // Label at the top of the card, where the art matters least for reading.
                var label = mark.ValueMissing ? "Wert? anklicken" : "Pet? anklicken";
                var size = graphics.MeasureString(label, font);
                var tag = new RectangleF(box.X + 4, box.Y + 4, size.Width + 8, size.Height + 2);
                using var back = new SolidBrush(Color.FromArgb(220, 16, 20, 28));
                using var ink = new SolidBrush(color);
                graphics.FillRectangle(back, tag);
                graphics.DrawString(label, font, ink, tag.X + 4, tag.Y + 1);
            }
        }
        if (!Visible)
            Show();
        Present(bitmap, new Point(region.X + area.X, region.Y + area.Y));
    }

    private void Present(Bitmap bitmap, Point location)
    {
        var screen = NativeMethods.GetDC(0);
        var memory = NativeMethods.CreateCompatibleDC(screen);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var old = NativeMethods.SelectObject(memory, hBitmap);
        try
        {
            var size = new NativeMethods.NativeSize(bitmap.Width, bitmap.Height);
            var source = new NativeMethods.NativePoint(0, 0);
            var target = new NativeMethods.NativePoint(location.X, location.Y);
            var blend = new NativeMethods.BlendFunction
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };
            NativeMethods.UpdateLayeredWindow(Handle, screen, ref target, ref size, memory, ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.SelectObject(memory, old);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memory);
            NativeMethods.ReleaseDC(0, screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _scan.Changed -= Schedule;
        base.Dispose(disposing);
    }
}
