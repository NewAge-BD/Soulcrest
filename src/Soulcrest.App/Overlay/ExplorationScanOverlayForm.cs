using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>
/// Click-through scan assistance; follows the detected game window. In screen recordings only with
/// "Overlays in Aufnahmen sichtbar" (user request 2026-10-07) and while the game window alone is captured,
/// which never contains this overlay; a monitor or GDI capture would let the scan read it.
/// </summary>
public sealed class ExplorationScanOverlayForm : Form
{
    private readonly ExplorationScanService _scan;
    private readonly SettingsService _settings;
    private readonly Capture.GameCaptureService _capture;
    private bool _inRecordings;

    // In recordings only when chosen and the scan cannot see the overlay (game window capture).
    private bool WantedInRecordings => _settings.Current.OverlaysInRecordings && _capture.CapturesGameWindowOnly;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private DateTime _visibleUntil;
    private ExplorationScanFeedback? _shown;
    private string _shownStatus = "";
    public ExplorationScanOverlayForm(ExplorationScanService scan, SettingsService settings, Capture.GameCaptureService capture)
    {
        _scan = scan;
        _settings = settings;
        _capture = capture;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual;
        _timer.Tick += (_, _) => Render(); _timer.Start();
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
        _inRecordings = WantedInRecordings;
        NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
    }
    private void Render()
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (WantedInRecordings != _inRecordings)
        {
            _inRecordings = WantedInRecordings;
            NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
        }
        if (_scan.Running) _visibleUntil = DateTime.UtcNow.AddSeconds(5);
        var feedback = _scan.Feedback;
        if (feedback is null || (!_scan.Running && DateTime.UtcNow > _visibleUntil)) { if (Visible) Hide(); return; }
        var state = _scan.Status + _scan.Running + UiText.Language;
        if (ReferenceEquals(_shown, feedback) && _shownStatus == state && Visible) return;
        _shown = feedback; _shownStatus = state;
        using var bitmap = CreateImage(feedback, _scan.ScanKind, _scan.Running ? feedback.Message : _scan.Status, _scan.Recognized, _scan.Running, out var area);
        if (!Visible) Show();
        Present(bitmap, new Point(feedback.Window.X + area.X, feedback.Window.Y + area.Y));
    }

    internal static Bitmap CreateImage(ExplorationScanFeedback feedback, string? kind, string status, int total, bool running, out Rectangle area)
    {
        var list = feedback.List ?? new Rectangle(20, 150, 400, 600);
        var panel = new Rectangle(Math.Min(list.Right + 20, Math.Max(0, feedback.Window.Width - 440)), Math.Max(10, list.Top), 420, 130);
        area = Rectangle.Union(list, panel); area.Inflate(8, 8);
        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.TranslateTransform(-area.X, -area.Y);
            using var outline = new Pen(Color.FromArgb(94, 234, 212), 2);
            if (feedback.List is not null) g.DrawRectangle(outline, list);
            if (running)
                foreach (var mark in feedback.Marks)
                {
                    using var pen = new Pen(mark.Recognized ? Color.LimeGreen : Color.Orange, 2);
                    var box = mark.Bounds; box.Inflate(3, 3); g.DrawRectangle(pen, box);
                }
            using var back = new SolidBrush(Color.FromArgb(235, 16, 20, 28));
            using var ink = new SolidBrush(Color.White);
            using var accent = new SolidBrush(feedback.Ready ? Color.LimeGreen : Color.FromArgb(94,234,212));
            using var title = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var body = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            g.FillRectangle(back, panel); g.DrawRectangle(outline, panel);
            g.DrawString("Soulcrest · " + kind switch { "dungeon" => "Sealed Dungeons", "stronghold" => "Stronghold", "kibelisk" => "Kibelisk", _ => "Exploration" }, title, accent, panel.X + 10, panel.Y + 8);
            g.DrawString(UiText.T(status), body, ink, new RectangleF(panel.X+10, panel.Y+35, panel.Width-20, 58));
            g.DrawString(UiText.F("Sichtbar: {0}/{1} · Insgesamt erkannt: {2}", feedback.Read, feedback.Total, total), body, ink, panel.X+10, panel.Y+103);
        }
        return bitmap;
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
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
