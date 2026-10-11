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
        if (!_settings.Current.ExplorationScanOverlayEnabled) { if (Visible) Hide(); return; }
        if (WantedInRecordings != _inRecordings)
        {
            _inRecordings = WantedInRecordings;
            NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
        }
        if (_scan.Running) _visibleUntil = DateTime.UtcNow.AddSeconds(5);
        var feedback = _scan.Feedback;
        if (feedback is null || (!_scan.Running && DateTime.UtcNow > _visibleUntil)) { if (Visible) Hide(); return; }
        var kind = _scan.DetectedKind ?? _scan.ScanKind;
        var state = _scan.Status + _scan.Running + UiText.Language + kind;
        if (ReferenceEquals(_shown, feedback) && _shownStatus == state && Visible) return;
        _shown = feedback; _shownStatus = state;
        using var bitmap = CreateImage(feedback, kind, _scan.Running ? feedback.Message : _scan.Status, _scan.Recognized, _scan.Running, out var area);
        if (!Visible) Show();
        Present(bitmap, new Point(feedback.Window.X + area.X, feedback.Window.Y + area.Y));
    }

    internal static Bitmap CreateImage(ExplorationScanFeedback feedback, string? kind, string status, int total, bool running, out Rectangle area)
    {
        var window = new Rectangle(Point.Empty, new Size(Math.Max(1, feedback.Window.Width), Math.Max(1, feedback.Window.Height)));
        var list = Rectangle.Intersect(feedback.List ?? ExplorationLayout.ForWindow(window.Size).Bounds, window);
        var panel = PanelBounds(window.Size, list);
        area = Rectangle.Union(list, panel); area.Inflate(8, 8); area.Intersect(window);
        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.TranslateTransform(-area.X, -area.Y);
            g.SetClip(window);
            using var outline = new Pen(Color.FromArgb(94, 234, 212), 1.5f);
            if (feedback.List is not null && !list.IsEmpty) g.DrawRectangle(outline, list);
            if (running)
                foreach (var mark in feedback.Marks)
                {
                    var box = Rectangle.Intersect(mark.Bounds, list);
                    if (box.IsEmpty) continue;
                    box.Inflate(3, 3); box.Intersect(list);
                    using var pen = new Pen(mark.Recognized ? Color.FromArgb(104, 222, 161) : Color.FromArgb(246, 190, 91), 2)
                    { DashStyle = mark.Recognized ? DashStyle.Solid : DashStyle.Dash };
                    g.DrawRectangle(pen, box);
                }
            using var back = new SolidBrush(Color.FromArgb(245, 22, 28, 38));
            using var ink = new SolidBrush(Color.FromArgb(239, 243, 249));
            using var muted = new SolidBrush(Color.FromArgb(171, 184, 201));
            using var accent = new SolidBrush(feedback.Ready ? Color.FromArgb(104, 222, 161) : Color.FromArgb(94, 234, 212));
            using var border = new Pen(Color.FromArgb(81, 101, 124), 1);
            using var title = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var body = new Font("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
            using var number = new Font("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.LineLimit };
            using var shape = RoundedRectangle(panel, 12);
            g.FillPath(back, shape); g.DrawPath(border, shape);
            var x = panel.X + 16; var width = panel.Width - 32;
            g.DrawString(CategoryTitle(kind), title, ink, new RectangleF(x, panel.Y + 14, width, 24), format);
            g.DrawString(PhaseLabel(feedback.Ready, running), small, accent, new RectangleF(x, panel.Y + 40, width, 20), format);
            g.DrawString(UiText.T(status), body, ink, new RectangleF(x, panel.Y + 65, width, 55), format);
            using var separator = new Pen(Color.FromArgb(58, 73, 92), 1);
            g.DrawLine(separator, x, panel.Y + 129, panel.Right - 16, panel.Y + 129);
            var column = width / 2;
            g.DrawString(UiText.T("Sichtbare Zeilen geprüft"), small, muted, new RectangleF(x, panel.Y + 140, column - 8, 19), format);
            g.DrawString(VisibleCount(feedback), number, ink, new RectangleF(x, panel.Y + 158, column - 8, 30), format);
            g.DrawString(UiText.T("Einträge übernommen"), small, muted, new RectangleF(x + column, panel.Y + 140, column, 19), format);
            g.DrawString(Math.Max(0, total).ToString(System.Globalization.CultureInfo.InvariantCulture), number, ink,
                new RectangleF(x + column, panel.Y + 158, column, 30), format);
            using var track = new SolidBrush(Color.FromArgb(54, 67, 84));
            g.FillRectangle(track, x, panel.Y + 194, width, 4);
            if (feedback.Total > 0)
                g.FillRectangle(accent, x, panel.Y + 194, width * Math.Clamp((float)feedback.Read / feedback.Total, 0, 1), 4);
            DrawLegend(g, x, panel.Y + 209, small, muted, width);
            g.DrawString(UiText.T(kind == "kibelisk"
                    ? "Geprüft heißt gelesen. Der Bindestatus bestimmt den Fortschritt."
                    : "Geprüft heißt gelesen. Nur Haken oder bestätigte 100 % zählen als abgeschlossen."),
                small, muted, new RectangleF(x, panel.Y + 234, width, 36), format);
        }
        return bitmap;
    }

    internal static string CategoryTitle(string? kind) => "Soulcrest · " + (kind switch
    {
        "dungeon" => "Sealed Dungeons",
        "stronghold" => "Strongholds",
        "kibelisk" => "Kibelisks",
        _ => UiText.T("Erkundungsscan")
    });

    internal static string PhaseLabel(bool ready, bool running) => UiText.T(!running ? "Scan beendet"
        : ready ? "Bereit zum Weiterscrollen" : "Namen werden geprüft");

    internal static string VisibleCount(ExplorationScanFeedback feedback) =>
        UiText.F("{0} / {1}", Math.Clamp(feedback.Read, 0, Math.Max(0, feedback.Total)), Math.Max(0, feedback.Total));

    internal static Rectangle PanelBounds(Size window, Rectangle list)
    {
        var width = Math.Min(420, Math.Max(1, window.Width - 24));
        var height = Math.Min(282, Math.Max(1, window.Height - 24));
        var x = Math.Min(list.Right + 20, Math.Max(0, window.Width - width - 12));
        var bottom = Math.Max(0, window.Height - height - 12);
        var y = Math.Clamp(list.Top, Math.Min(12, bottom), bottom);
        return new Rectangle(x, y, width, height);
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath(); var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }

    private static void DrawLegend(Graphics g, int x, int y, Font font, Brush ink, int width)
    {
        using var green = new SolidBrush(Color.FromArgb(104, 222, 161));
        using var amber = new SolidBrush(Color.FromArgb(246, 190, 91));
        g.FillEllipse(green, x, y + 4, 7, 7);
        g.DrawString(UiText.T("Geprüft"), font, ink, x + 13, y);
        var second = x + width / 2;
        g.FillEllipse(amber, second, y + 4, 7, 7);
        g.DrawString(UiText.T("Noch prüfen"), font, ink, second + 13, y);
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
