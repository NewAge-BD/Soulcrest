using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Native timer strip and shared safe window interaction for the separate reminder banners.</summary>
public class AlertsOverlayForm : Form
{
    internal const int PanelWidth = 340, HeaderHeight = 22, RowStride = 44, FooterHeight = 6, MaximumRows = 6;
    internal const int MinimumWidth = 240, MaximumWidth = 1000;
    private static readonly Color Surface = Color.FromArgb(17, 24, 32);
    private static readonly Color Card = Color.FromArgb(25, 35, 46);
    private static readonly Color Border = Color.FromArgb(44, 59, 73);
    private static readonly Color Muted = Color.FromArgb(154, 173, 189);
    private readonly AlertsService _alerts;
    private readonly SettingsService _settings;
    private readonly bool _notification;
    private readonly TimerIconCache _icons;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly OverlayLockButton _lockButton;
    private readonly ToolTip _tooltip = new() { InitialDelay = 300, ReshowDelay = 100, ShowAlways = true };
    private IReadOnlyList<AlertDisplayRow> _rows = [];
    private TimerStripLayout _stripLayout = TimerStripRenderer.Layout([]);
    private Point? _drag;
    private ResizeGesture? _resize;
    private bool _manualInteraction, _altInteractionRequested, _blockAltUntilRelease;
    private bool _locked = true, _inRecordings, _suppressed;
    private int _logicalWidth = PanelWidth;
    private double _renderScale = 1;
    private double _requestedScale = 1;
    private string _tooltipText = "";

    public AlertsOverlayForm(AlertsService alerts, SettingsService settings, string? mapDataDirectory = null)
        : this(alerts, settings, notification: false, mapDataDirectory) { }

    protected AlertsOverlayForm(AlertsService alerts, SettingsService settings, bool notification, string? mapDataDirectory = null)
    {
        _alerts = alerts;
        _settings = settings;
        _notification = notification;
        _icons = new TimerIconCache(notification ? null : mapDataDirectory ?? AppPaths.FindMapData());
        if (notification) _timer.Interval = 200;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Surface;
        if (notification) Opacity = .95;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        _lockButton = new OverlayLockButton(this, LockBounds,
            notification ? OverlayLockButtonStyle.Default : OverlayLockButtonStyle.TimerStrip);
        _lockButton.Toggled += ToggleManualInteraction;
        _lockButton.SetVisibleInRecordings(settings.Current.OverlaysInRecordings);
        _timer.Tick += (_, _) => RefreshPanel();
        _timer.Start();
    }

    public void SetSuppressed(bool suppressed)
    {
        // User request 2026-10-12: timed reminders remain visible when the game HUD is hidden.
        if (_notification || _suppressed == suppressed) return;
        _suppressed = suppressed;
        if (suppressed)
        {
            FinishGesture();
            Capture = false;
            Hide();
            SetInteractionEnabled(false);
        }
        else RefreshPanel();
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(value && !_suppressed);
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW
                | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
            if (_locked) cp.ExStyle |= NativeMethods.WS_EX_TRANSPARENT;
            else cp.ExStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _inRecordings = _settings.Current.OverlaysInRecordings;
        NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
        ApplyInteractionStyle();
    }

    internal bool ManualInteractionEnabled => _manualInteraction;
    internal double OverlayScale => _renderScale;
    internal void ToggleManualInteraction() => SetManualInteraction(_locked);
    internal void ToggleManualInteractionFromHotkey() => SetManualInteraction(!_manualInteraction);

    private void SetManualInteraction(bool enabled)
    {
        _manualInteraction = enabled;
        var alt = _altInteractionRequested || OverlayInteraction.IsAltHeld;
        _blockAltUntilRelease = !enabled && alt;
        SetInteractionEnabled(alt);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) return;
        _manualInteraction = false;
        _lockButton?.SetLocked(true);
        SetInteractionEnabled(false);
    }

    private bool InteractionAllowed => !_suppressed
        && (_manualInteraction || (!_blockAltUntilRelease && OverlayInteraction.IsAltHeld));

    public void SetInteractionEnabled(bool enabled)
    {
        _altInteractionRequested = enabled;
        if (!enabled) _blockAltUntilRelease = false;
        enabled = (_manualInteraction || (enabled && !_blockAltUntilRelease)) && !_suppressed;
        if (_lockButton is { } button && button.Locked != !enabled) button.SetLocked(!enabled);
        if (_locked == !enabled)
        {
            // WinForms may capture a queued mouse-down before OnMouseDown rejects a relocked panel.
            if (_locked && IsHandleCreated) Capture = false;
            return;
        }
        _locked = !enabled;
        if (_locked)
        {
            FinishGesture();
            if (IsHandleCreated) Capture = false;
        }
        ApplyInteractionStyle();
        Cursor = _locked ? Cursors.Default : Cursors.SizeAll;
        RenderPanel();
    }

    private void ApplyInteractionStyle()
    {
        if (IsHandleCreated) NativeMethods.SetClickThrough(Handle, _locked);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            message.Result = NativeMethods.MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        if (!_notification)
        {
            var old = Region; Region = null; old?.Dispose();
            return;
        }
        using var outline = Rounded(new RectangleF(0, 0, Width, Height), 8);
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }

    internal void RefreshPanel()
    {
        if (_suppressed || !IsHandleCreated || IsDisposed || SmokeTest.Enabled) return;
        var settings = _settings.Current;
        var now = _alerts.Now;
        _rows = _notification
            ? _alerts.NotificationRows.Select(notice => new AlertDisplayRow(notice.Key, notice.Title,
                notice.Message, notice.Color, notice.At, notice.At <= now && !notice.AwaitingConfirmation,
                AwaitingConfirmation: notice.AwaitingConfirmation)).ToArray()
            : _alerts.TimerOverlayRows;
        if (_rows.Count == 0 || (!_notification && !settings.AlertsOverlayEnabled))
        {
            FinishGesture();
            Hide();
            return;
        }
        if (_inRecordings != settings.OverlaysInRecordings)
        {
            _inRecordings = settings.OverlaysInRecordings;
            NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
            _lockButton.SetVisibleInRecordings(_inRecordings);
        }
        if (_drag is null && _resize is null)
        {
            _logicalWidth = Math.Clamp(_notification ? settings.AlertNotificationsWidth : settings.AlertsOverlayWidth,
                MinimumWidth, MaximumWidth);
            var scale = _notification ? settings.AlertNotificationsScale : settings.AlertsOverlayScale;
            var location = _notification ? new Point(settings.AlertNotificationsX, settings.AlertNotificationsY)
                : new Point(settings.AlertsOverlayX, settings.AlertsOverlayY);
            ApplyBounds(location, _logicalWidth, scale);
        }
        RenderPanel();
        if (!Visible) Show();
        _lockButton.RefreshTarget();
    }

    internal static bool PanelVisible(AppSettings settings, int rows) => settings.AlertsOverlayEnabled && rows > 0;
    internal static int LogicalHeight(int rows) => HeaderHeight + Math.Clamp(rows, 0, MaximumRows) * RowStride + FooterHeight;

    internal static Rectangle FitBounds(Rectangle area, Point location, int logicalWidth, int logicalHeight, double scale)
    {
        logicalWidth = Math.Clamp(logicalWidth, MinimumWidth, MaximumWidth);
        logicalHeight = Math.Max(1, logicalHeight);
        scale = double.IsFinite(scale) ? Math.Clamp(scale, .6, 2) : 1;
        scale = Math.Min(scale, Math.Min(area.Width / (double)logicalWidth, area.Height / (double)logicalHeight));
        var size = new Size(Math.Min(area.Width, Math.Max(1, (int)Math.Ceiling(logicalWidth * scale))),
            Math.Min(area.Height, Math.Max(1, (int)Math.Ceiling(logicalHeight * scale))));
        return new Rectangle(new Point(Math.Clamp(location.X, area.Left, Math.Max(area.Left, area.Right - size.Width)),
            Math.Clamp(location.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height))), size);
    }

    private void ApplyBounds(Point location, int width, double scale)
    {
        var area = Screen.FromPoint(location).WorkingArea;
        if (!_notification)
        {
            _requestedScale = double.IsFinite(scale) ? Math.Clamp(scale, .6, 2) : 1;
            // Keep the independent unlock window on the same monitor, including a full-width strip.
            var available = StripArea(area, _requestedScale);
            _stripLayout = TimerStripRenderer.Layout(_rows, _settings.Current.TimerOverlayGap,
                Math.Max(1, (int)Math.Floor(available.Width / _requestedScale)));
            _logicalWidth = _stripLayout.Size.Width;
            var strip = FitStripBounds(available, location, _stripLayout.Size, _requestedScale);
            _renderScale = Math.Min(strip.Width / (double)_stripLayout.Size.Width, strip.Height / (double)_stripLayout.Size.Height);
            Bounds = strip;
            _lockButton.RefreshTarget();
            return;
        }
        var height = LogicalHeight(_rows.Count);
        var fitted = FitBounds(area, location, width, height, scale);
        _requestedScale = double.IsFinite(scale) ? Math.Clamp(scale, .6, 2) : 1;
        _renderScale = Math.Min(fitted.Width / (double)width, fitted.Height / (double)height);
        Bounds = fitted;
        _lockButton.RefreshTarget();
    }

    internal static Rectangle StripArea(Rectangle area, double scale) => new(area.Left, area.Top,
        Math.Max(1, area.Width - (int)Math.Ceiling(22 * scale)), area.Height);

    internal static double ResizeScale(double originalScale, int originalHeight, int desiredHeight) =>
        Math.Clamp(originalScale * Math.Max(1, desiredHeight) / Math.Max(1, originalHeight), .6, 2);

    internal static Rectangle FitStripBounds(Rectangle area, Point location, Size size, double scale)
    {
        scale = double.IsFinite(scale) ? Math.Clamp(scale, .6, 2) : 1;
        scale = Math.Min(scale, Math.Min(area.Width / (double)Math.Max(1, size.Width), area.Height / (double)Math.Max(1, size.Height)));
        var fitted = new Size(Math.Min(area.Width, Math.Max(1, (int)Math.Ceiling(size.Width * scale))),
            Math.Min(area.Height, Math.Max(1, (int)Math.Ceiling(size.Height * scale))));
        return new(new Point(Math.Clamp(location.X, area.Left, Math.Max(area.Left, area.Right - fitted.Width)),
            Math.Clamp(location.Y, area.Top, Math.Max(area.Top, area.Bottom - fitted.Height))), fitted);
    }

    private void RenderPanel()
    {
        if (_notification) { Invalidate(); return; }
        if (!IsHandleCreated || IsDisposed || _rows.Count == 0) return;
        var header = new NativeMethods.BitmapInfoHeader
        { Size = 40, Width = Width, Height = -Height, Planes = 1, BitCount = 32 };
        var dib = NativeMethods.CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        if (dib == 0) return;
        try
        {
            using (var surface = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, bits))
            using (var graphics = Graphics.FromImage(surface))
            {
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform((float)_renderScale, (float)_renderScale);
                TimerStripRenderer.Paint(graphics, _stripLayout, _alerts.Now,
                    _settings.Current.TimerOverlayShowSeconds, _locked, _icons.Load);
            }
            var screen = NativeMethods.GetDC(0);
            var memory = NativeMethods.CreateCompatibleDC(screen);
            var old = NativeMethods.SelectObject(memory, dib);
            try
            {
                var target = new NativeMethods.NativePoint(Left, Top);
                var source = new NativeMethods.NativePoint(0, 0);
                var size = new NativeMethods.NativeSize(Width, Height);
                var blend = new NativeMethods.BlendFunction
                { BlendOp = NativeMethods.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = NativeMethods.AC_SRC_ALPHA };
                if (!NativeMethods.UpdateLayeredWindow(Handle, screen, ref target, ref size, memory, ref source, 0, ref blend, NativeMethods.ULW_ALPHA))
                    Trace.TraceWarning("Timer strip presentation failed (Win32 {0}).", Marshal.GetLastWin32Error());
            }
            finally
            {
                NativeMethods.SelectObject(memory, old);
                NativeMethods.DeleteDC(memory);
                NativeMethods.ReleaseDC(0, screen);
            }
        }
        finally { NativeMethods.DeleteObject(dib); }
    }

    private Rectangle LockBounds()
    {
        var size = Math.Max(10, (int)Math.Round(18 * _renderScale));
        var inset = Math.Max(1, (int)Math.Round(3 * _renderScale));
        // The separate lock window sits outside the strip and never covers a countdown.
        return _notification ? new Rectangle(Math.Max(0, ClientSize.Width - size - inset), inset, size, size)
            : new Rectangle(ClientSize.Width + inset, 0, size, size);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_notification) return;
        e.Graphics.ScaleTransform((float)_renderScale, (float)_renderScale);
        PaintNotifications(e.Graphics, _rows, _alerts.Now, _locked, _logicalWidth);
    }

    internal static void PaintNotifications(Graphics g, IReadOnlyList<AlertDisplayRow> rows, DateTimeOffset now,
        bool locked, int width = PanelWidth)
    {
        width = Math.Clamp(width, MinimumWidth, MaximumWidth);
        var height = LogicalHeight(rows.Count);
        var saved = g.Save();
        g.SetClip(new RectangleF(0, 0, width, height), CombineMode.Intersect);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var heading = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        using var timer = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.FromArgb(235, 242, 248));
        using var dim = new SolidBrush(Muted);
        using var outline = new Pen(Border);
        using var line = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat { Alignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        FillRound(new RectangleF(0, 0, width, height), 8, Surface);
        using (var edge = Rounded(new RectangleF(.5f, .5f, width - 1, height - 1), 8)) g.DrawPath(outline, edge);
        g.DrawString(UiText.T("Benachrichtigung"), heading, dim, 11, 4);
        if (rows.Count > MaximumRows)
            g.DrawString(UiText.F("{0} weitere im Alerts-Tab", rows.Count - MaximumRows), small, dim,
                new RectangleF(100, 4, width - 130, 15), right);
        var y = HeaderHeight;
        foreach (var row in rows.Take(MaximumRows))
        {
            var color = ColorTranslator.FromHtml(row.Color);
            using var accent = new SolidBrush(color);
            FillRound(new RectangleF(5, y, width - 10, RowStride - 2), 5, Card);
            FillRound(new RectangleF(8, y + 6, 2, 30), 1, color);
            var countdownWidth = row.CountdownLabel is null ? 112 : 158;
            // The confirmation status uses the subtitle line so the boss stays identifiable even at minimum width.
            var titleWidth = row.AwaitingConfirmation ? width - 28 : Math.Max(28, width - countdownWidth - 28);
            g.DrawString(row.Title, body, white, new RectangleF(16, y + 4, titleWidth, 17), line);
            var countdown = CountdownText(row, now);
            if (!row.AwaitingConfirmation)
            {
                if (row.CountdownLabel is { } label) countdown = label + " " + countdown;
                g.DrawString(countdown, timer, row.Provisional ? dim : accent,
                    new RectangleF(width - countdownWidth - 12, y + 3, countdownWidth, 18), right);
            }
            g.DrawString(row.AwaitingConfirmation ? countdown
                    : row.Provisional ? UiText.T("Timer nicht aktuell · Spawn unbestätigt") : row.Message,
                small, dim, new RectangleF(16, y + 24, width - 78, 15), line);
            g.DrawString(row.At.ToLocalTime().ToString("HH:mm"), small, dim,
                new RectangleF(width - 58, y + 24, 45, 15), right);
            y += RowStride;
        }
        if (!locked)
        {
            using var grip = new Pen(Muted, 1);
            g.DrawLine(grip, width - 12, height - 3, width - 3, height - 12);
            g.DrawLine(grip, width - 7, height - 3, width - 3, height - 7);
        }
        g.Restore(saved);

        void FillRound(RectangleF bounds, float radius, Color color)
        {
            using var path = Rounded(bounds, radius);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
    }

    internal static string CountdownText(AlertDisplayRow row, DateTimeOffset now)
    {
        var remaining = row.At - now;
        return row.AwaitingConfirmation ? UiText.T("Warte auf Bestätigung")
            : row.Provisional && remaining <= TimeSpan.Zero ? UiText.T("Spawn unbestätigt")
            : (row.Started && row.CountdownLabel is null) || remaining <= TimeSpan.Zero ? UiText.T("Jetzt") : Countdown(remaining);
    }

    internal static string Countdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return UiText.T("Jetzt");
        return remaining.TotalDays >= 1
            ? UiText.F("{0} T. {1:00}:{2:00}:{3:00}", (int)remaining.TotalDays, remaining.Hours, remaining.Minutes, remaining.Seconds)
            : BossRushService.Countdown(remaining);
    }

    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private bool IsResizePoint(Point point)
    {
        var grip = Math.Max(12, (int)Math.Round(18 * _renderScale));
        return point.X >= Width - grip && point.Y >= Height - grip;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!InteractionAllowed) { SetInteractionEnabled(OverlayInteraction.IsAltHeld); return; }
        if (_locked || e.Button != MouseButtons.Left)
        {
            if (_locked) Capture = false;
            return;
        }
        if (IsResizePoint(e.Location)) _resize = new ResizeGesture(PointToScreen(e.Location), Size, _requestedScale);
        else _drag = e.Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!InteractionAllowed) { SetInteractionEnabled(OverlayInteraction.IsAltHeld); return; }
        if (_drag is { } from) Location = new Point(Location.X + e.X - from.X, Location.Y + e.Y - from.Y);
        else if (_resize is { } resize)
        {
            var pointer = PointToScreen(e.Location);
            var desiredHeight = Math.Max(1, resize.Size.Height + pointer.Y - resize.Pointer.Y);
            var scale = _notification ? Math.Clamp(desiredHeight / (double)LogicalHeight(_rows.Count), .6, 2)
                : ResizeScale(resize.Scale, resize.Size.Height, desiredHeight);
            var desiredWidth = resize.Size.Width + pointer.X - resize.Pointer.X;
            if (_notification) _logicalWidth = Math.Clamp((int)Math.Round(desiredWidth / scale), MinimumWidth, MaximumWidth);
            ApplyBounds(Location, _logicalWidth, scale);
            RenderPanel();
        }
        else
        {
            Cursor = IsResizePoint(e.Location) ? Cursors.SizeNWSE : Cursors.SizeAll;
            var rowIndex = (int)(e.Y / _renderScale - HeaderHeight) / RowStride;
            var hit = _notification
                ? e.Y / _renderScale >= HeaderHeight && rowIndex >= 0 && rowIndex < Math.Min(_rows.Count, MaximumRows) ? _rows[rowIndex] : null
                : _stripLayout.Cells.FirstOrDefault(cell => cell.Bounds.Contains((int)(e.X / _renderScale), (int)(e.Y / _renderScale)))?.Row;
            var text = hit is not null ? RowTooltip(hit, _alerts.Now) : BossOverlayForm.HotkeyHint(_locked);
            if (text != _tooltipText) { _tooltipText = text; _tooltip.SetToolTip(this, text); }
        }
    }

    internal static string RowTooltip(AlertDisplayRow row, DateTimeOffset now) => string.Join(Environment.NewLine,
        new[] { row.Title, row.Message, row.Provisional ? UiText.T("Timer nicht aktuell · Spawn unbestätigt") : "",
            string.Join(" ", new[] { row.CountdownLabel, CountdownText(row, now) }.Where(part => !string.IsNullOrEmpty(part))),
            UiText.F("Lokal: {0}", row.At.ToLocalTime().ToString("g")) }.Where(part => !string.IsNullOrEmpty(part)));

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!InteractionAllowed) { SetInteractionEnabled(OverlayInteraction.IsAltHeld); return; }
        FinishGesture();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) FinishGesture();
    }

    private void FinishGesture()
    {
        if (_drag is null && _resize is null) return;
        var resized = _resize is not null;
        _drag = null;
        _resize = null;
        Capture = false;
        var area = Screen.FromPoint(Location).WorkingArea;
        if (!_notification) area = StripArea(area, _renderScale);
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        _settings.Update(s =>
        {
            if (_notification)
            {
                s.AlertNotificationsX = Left; s.AlertNotificationsY = Top;
                if (resized) { s.AlertNotificationsWidth = _logicalWidth; s.AlertNotificationsScale = _requestedScale; }
            }
            else
            {
                s.AlertsOverlayX = Left; s.AlertsOverlayY = Top;
                if (resized) s.AlertsOverlayScale = _requestedScale;
            }
        });
    }

    private sealed record ResizeGesture(Point Pointer, Size Size, double Scale);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lockButton.Toggled -= ToggleManualInteraction;
            _lockButton.Dispose();
            _tooltip.Dispose();
            _icons.Dispose();
            _timer.Stop();
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
