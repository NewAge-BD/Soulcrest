using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>A compact calendar HUD with its own placement, lock and normal HUD visibility policy.</summary>
public sealed class EventTimelineOverlayForm : Form
{
    internal const int PanelWidth = 720, AxisHeight = 24, LaneStride = 24, BottomInset = 8;
    internal const int TrackLeft = 144, TrackWidth = PanelWidth - TrackLeft - 12;
    private static readonly Color Surface = Color.FromArgb(17, 24, 32), Border = Color.FromArgb(44, 59, 73),
        Muted = Color.FromArgb(154, 173, 189), White = Color.FromArgb(235, 242, 248);
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private readonly AlertsService _alerts;
    private readonly BossRushService _bosses;
    private readonly SettingsService _settings;
    private readonly ProgressService? _progress;
    private readonly Dictionary<string, Image?> _icons = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly OverlayLockButton _lockButton;
    private readonly ToolTip _tooltip = new() { InitialDelay = 250, ReshowDelay = 100, AutoPopDelay = 7000, ShowAlways = true };
    private IReadOnlyList<TimelineHit> _hits = [];
    private EventTimelineSnapshot? _snapshot;
    private double _renderScale = 1;
    private int _logicalWidth = PanelWidth;
    private Point? _drag;
    private (Point Pointer, Size Original)? _resize;
    private string? _tooltipText;
    private bool _manualInteraction, _altInteractionRequested, _blockAltUntilRelease;
    private bool _locked = true, _inRecordings, _suppressed;
    private bool _placementHidden, _foregroundSuppressed;
    private OverlayThemeDefinition _theme = OverlayThemeDefinition.Legacy;
    private bool _rendering;
    private bool PresentationHidden => _placementHidden || _foregroundSuppressed;

    public EventTimelineOverlayForm(AlertsService alerts, BossRushService bosses, SettingsService settings, ProgressService? progress = null)
    {
        _alerts = alerts;
        _bosses = bosses;
        _settings = settings;
        _theme = OverlayThemeDefinition.Resolve(settings.Current.OverlayTheme);
        _progress = progress;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Surface;
        Opacity = .94;
        DoubleBuffered = true;
        // The lock occupies the unused left side of the axis, never a time label or event.
        _lockButton = new OverlayLockButton(this, LockBounds);
        _lockButton.SetTheme(_theme);
        _lockButton.Toggled += ToggleManualInteraction;
        _lockButton.SetVisibleInRecordings(settings.Current.OverlaysInRecordings);
        _timer.Tick += (_, _) => RefreshPanel();
        _timer.Start();
    }

    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed) return;
        _suppressed = suppressed;
        if (suppressed)
        {
            FinishGesture();
            Hide();
            SetInteractionEnabled(false);
        }
        else RefreshPanel();
    }

    /// <summary>Hides live windows for placement without changing settings or executing pending actions.</summary>
    public void SetPlacementHidden(bool hidden)
    {
        if (_placementHidden == hidden) return;
        _placementHidden = hidden;
        ApplyPresentationVisibility();
    }

    /// <summary>Temporarily hides presentation while retaining live content deadlines and user preferences.</summary>
    public void SetForegroundSuppressed(bool suppressed)
    {
        if (_foregroundSuppressed == suppressed) return;
        _foregroundSuppressed = suppressed;
        ApplyPresentationVisibility();
    }

    private void ApplyPresentationVisibility()
    {
        if (PresentationHidden)
        {
            _drag = null;
            _resize = null;
            SetTooltip(null);
            _manualInteraction = false;
            SetInteractionEnabled(false);
            if (IsHandleCreated) Capture = false;
            Hide();
            _lockButton.Hide();
        }
        else
        {
            // A modifier held while leaving placement must be released before native input resumes.
            _manualInteraction = false;
            SetInteractionEnabled(false);
            RefreshPanel();
        }
        _blockAltUntilRelease = true;
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(value && !_suppressed && !PresentationHidden);
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
        LayeredOverlayPresenter.Configure(this, !_theme.IsLegacy, .94);
        _inRecordings = _settings.Current.OverlaysInRecordings;
        NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
        ApplyInteractionStyle();
    }

    internal bool ManualInteractionEnabled => _manualInteraction;
    internal void ToggleManualInteraction() => SetManualInteraction(_locked);
    internal void ToggleManualInteractionFromHotkey() => SetManualInteraction(!_manualInteraction);
    private void SetManualInteraction(bool enabled)
    {
        _manualInteraction = enabled && !PresentationHidden;
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

    private bool InteractionAllowed => !_suppressed && !PresentationHidden
        && (_manualInteraction || (!_blockAltUntilRelease && OverlayInteraction.IsAltHeld));

    public void SetInteractionEnabled(bool enabled)
    {
        _altInteractionRequested = enabled;
        if (!enabled) _blockAltUntilRelease = false;
        enabled = (_manualInteraction || (enabled && !_blockAltUntilRelease)) && !_suppressed && !PresentationHidden;
        if (_lockButton is { } button && button.Locked != !enabled) button.SetLocked(!enabled);
        if (_locked == !enabled) return;
        _locked = !enabled;
        if (_locked) { FinishGesture(); SetTooltip(null); }
        ApplyInteractionStyle();
        Cursor = _locked ? Cursors.Default : Cursors.SizeAll;
        InvalidatePanel();
    }

    private void ApplyInteractionStyle()
    {
        if (IsHandleCreated) NativeMethods.SetClickThrough(Handle, _locked);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WM_MOUSEACTIVATE) { message.Result = NativeMethods.MA_NOACTIVATE; return; }
        base.WndProc(ref message);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        UpdateWindowShape();
    }

    private void UpdateWindowShape()
    {
        if (!_theme.IsLegacy)
        {
            var old = Region; Region = null; old?.Dispose();
            return;
        }
        using var outline = AlertsOverlayForm.Rounded(new RectangleF(0, 0, Width, Height), 8);
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }

    private void RefreshPanel()
    {
        if (_suppressed || PresentationHidden || !IsHandleCreated || IsDisposed || SmokeTest.Enabled) return;
        var settings = _settings.Current;
        ApplyRequestedTheme();
        if (!settings.EventTimelineOverlayEnabled) { FinishGesture(); Hide(); return; }
        if (_resize is not null) return;
        _snapshot = EventTimelinePresentation.Create(_alerts.EventTimers, _bosses.Schedule,
            settings.BossAlertIds, _alerts.Now, settings.EventTimelineHours, settings.NameLanguage);
        if (_inRecordings != settings.OverlaysInRecordings)
        {
            _inRecordings = settings.OverlaysInRecordings;
            NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
            _lockButton.SetVisibleInRecordings(_inRecordings);
        }
        _logicalWidth = Math.Clamp(settings.EventTimelineOverlayWidth, 360, 1600);
        var location = _drag is null ? new Point(settings.EventTimelineOverlayX, settings.EventTimelineOverlayY) : Location;
        var fitted = FitBounds(Screen.FromPoint(location).WorkingArea, location,
            Measure(_snapshot, _logicalWidth, _theme).Size, settings.EventTimelineOverlayScale);
        _renderScale = fitted.Width / (double)_logicalWidth;
        Size = fitted.Size;
        if (_drag is null) Location = fitted.Location;
        if (!Visible) Show();
        _lockButton.RefreshTarget();
        InvalidatePanel();
    }

    private void ApplyRequestedTheme()
    {
        if (_drag is not null || _resize is not null) return;
        var requested = OverlayThemeDefinition.Resolve(_settings.Current.OverlayTheme);
        if (requested.Id == _theme.Id) return;
        _theme = requested;
        LayeredOverlayPresenter.Configure(this, !_theme.IsLegacy, .94);
        UpdateWindowShape();
        _lockButton.SetTheme(_theme);
        _hits = [];
        SetTooltip(null);
    }

    private Rectangle LockBounds() => _theme.IsLegacy ? new(6, 2, 20, 20)
        : Rectangle.Round(new RectangleF((float)(8 * _renderScale), (float)(3 * _renderScale),
            (float)(20 * _renderScale), (float)(20 * _renderScale)));

    private void InvalidatePanel()
    {
        if (_theme.IsLegacy) { Invalidate(); return; }
        if (_rendering || !IsHandleCreated || IsDisposed || PresentationHidden || _snapshot is not { } snapshot || Width <= 0 || Height <= 0) return;
        _rendering = true;
        try
        {
            using var bitmap = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform((float)_renderScale, (float)_renderScale);
                _hits = PaintPanel(graphics, snapshot, _locked, _logicalWidth, BossIcon, _theme);
            }
            LayeredOverlayPresenter.Present(this, bitmap);
        }
        finally { _rendering = false; }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { if (_theme.IsLegacy) base.OnPaintBackground(e); }

    internal static Rectangle FitBounds(Rectangle workingArea, Point position, Size logicalSize, double requestedScale)
    {
        var scale = double.IsFinite(requestedScale) ? Math.Clamp(requestedScale, .6, 2) : 1;
        scale = Math.Min(scale, Math.Min(workingArea.Width / (double)logicalSize.Width, workingArea.Height / (double)logicalSize.Height));
        var size = new Size(Math.Max(1, (int)Math.Round(logicalSize.Width * scale)), Math.Max(1, (int)Math.Round(logicalSize.Height * scale)));
        return new(new Point(Math.Clamp(position.X, workingArea.Left, workingArea.Right - size.Width),
            Math.Clamp(position.Y, workingArea.Top, workingArea.Bottom - size.Height)), size);
    }
    internal static int LogicalHeight(EventTimelineSnapshot snapshot, OverlayThemeDefinition? theme = null) =>
        Measure(snapshot, PanelWidth, theme ?? OverlayThemeDefinition.Legacy).Size.Height;
    internal static int TrackStart(int width, OverlayThemeDefinition? theme = null) =>
        (theme ?? OverlayThemeDefinition.Legacy).IsLegacy ? Math.Clamp(width / 5, 98, TrackLeft) : Math.Clamp(width / 4, 112, 170);
    internal sealed record TimelinePanelLayout(Size Size, int AxisHeight, int LaneHeight, int BottomInset, int TrackStart, int TrackWidth);
    internal static TimelinePanelLayout Measure(EventTimelineSnapshot snapshot, int width, OverlayThemeDefinition theme) => theme.IsLegacy
        ? new(new(width, AxisHeight + snapshot.Lanes.Count * LaneStride + BottomInset), AxisHeight, LaneStride, BottomInset,
            TrackStart(width), width - TrackStart(width) - 12)
        : new(new(width, 28 + snapshot.Lanes.Count * 28 + 10), 28, 28, 10,
            TrackStart(width, theme), width - TrackStart(width, theme) - 12);
    internal static (int Width, double Scale) ResizeDimensions(Size original, Point delta, int logicalHeight)
    {
        var scale = Math.Clamp((original.Height + delta.Y) / (double)logicalHeight, .6, 2);
        return (Math.Clamp((int)Math.Round((original.Width + delta.X) / scale), 360, 1600), scale);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_theme.IsLegacy) return;
        if (_snapshot is not { } snapshot) return;
        e.Graphics.ScaleTransform((float)_renderScale, (float)_renderScale);
        _hits = PaintPanel(e.Graphics, snapshot, _locked, _logicalWidth, BossIcon, _theme);
    }

    internal sealed record TimelineHit(RectangleF Bounds, string Tooltip);

    internal static IReadOnlyList<TimelineHit> PaintPanel(Graphics g, EventTimelineSnapshot snapshot, bool locked,
        int width = PanelWidth, Func<EventTimelineOccurrence, Image?>? icon = null, OverlayThemeDefinition? theme = null)
    {
        if (theme is { IsLegacy: false }) return PaintAion2(g, snapshot, locked, width, icon, theme);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var body = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        using var white = new SolidBrush(White);
        using var dim = new SolidBrush(Muted);
        using var edge = new Pen(Border);
        using var grid = new Pen(Color.FromArgb(34, 47, 60));
        using var line = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat { Alignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var height = LogicalHeight(snapshot);
        var left = TrackStart(width);
        var track = width - left - 12;
        FillRound(new RectangleF(0, 0, width, height), 8, Surface);
        using (var outline = AlertsOverlayForm.Rounded(new RectangleF(.5f, .5f, width - 1, height - 1), 8)) g.DrawPath(edge, outline);
        var ticks = track < 400 ? 2 : 4;
        for (var hour = 0; hour <= snapshot.Hours; hour++)
        {
            var x = left + hour * track / snapshot.Hours;
            g.DrawLine(grid, x, AxisHeight - 3, x, height - BottomInset);
        }
        for (var index = 0; index <= ticks; index++)
        {
            var x = left + index * track / (float)ticks;
            var at = snapshot.From.AddHours(snapshot.Hours * index / ticks);
            var label = Clock(at, snapshot.Hours > 12);
            var bounds = new RectangleF(x - 48, 3, 96, 15);
            if (index == 0) bounds = new(x, 3, 96, 15);
            else if (index == ticks) bounds = new(x - 96, 3, 96, 15);
            g.DrawString(label, small, dim, bounds, index == 0 ? line : index == ticks ? right : center);
            g.DrawLine(grid, x, AxisHeight - 3, x, height - BottomInset);
        }

        List<TimelineHit> hits = [];
        var y = AxisHeight;
        foreach (var lane in snapshot.Lanes)
        {
            var color = ColorTranslator.FromHtml(lane.Color);
            using var accent = new SolidBrush(color);
            g.FillEllipse(accent, 10, y + 9, 4, 4);
            g.DrawString(lane.Key == "invasion" ? UiText.T("Invasion · Siege") : lane.Title, body, white, new RectangleF(20, y + 4, left - 28, 16), line);
            hits.Add(new(new RectangleF(8, y, left - 12, LaneStride), lane.Title));
            g.DrawLine(grid, left, y + LaneStride - 2, left + track, y + LaneStride - 2);
            if (lane.Bosses && lane.Occurrences.Count == 0)
                g.DrawString(UiText.T(snapshot.CapturedBossCount == 0 ? "Noch keine Boss-Timer erfasst" : "Keine Boss-Termine in diesem Zeitraum"), small, dim,
                    new RectangleF(left + 6, y + 4, track - 12, 16), line);
            if (lane.Bosses)
            {
                // Dense or coincident spawns stay on one row; a count badge preserves every hover target.
                foreach (var group in BossGroups(lane.Occurrences, snapshot, left, track))
                {
                    var first = group.Items[0];
                    var bounds = new RectangleF(group.X - 9, y + 2, 18, 18);
                    var state = g.Save();
                    using var clip = AlertsOverlayForm.Rounded(bounds, 4);
                    g.SetClip(clip, CombineMode.Intersect);
                    if (icon?.Invoke(first) is { } image) g.DrawImage(image, bounds);
                    else
                    {
                        using var fill = new SolidBrush(first.Provisional ? Color.FromArgb(68, 77, 88) : ColorTranslator.FromHtml(first.Color));
                        g.FillRectangle(fill, bounds);
                        g.DrawString("◆", body, white, bounds, center);
                    }
                    if (first.Provisional) { using var shade = new SolidBrush(Color.FromArgb(115, 17, 24, 32)); g.FillRectangle(shade, bounds); }
                    g.Restore(state);
                    using var outline = new Pen(first.Provisional ? Muted : ColorTranslator.FromHtml(first.Color))
                        { DashStyle = first.Provisional ? DashStyle.Dot : DashStyle.Solid };
                    g.DrawPath(outline, clip);
                    if (group.Items.Count > 1)
                    {
                        var badge = new RectangleF(bounds.Right - 7, bounds.Y - 1, 13, 12);
                        FillRound(badge, 4, Color.FromArgb(55, 65, 81));
                        g.DrawString(group.Items.Count.ToString(), small, white, badge, center);
                    }
                    foreach (var occurrence in group.Items) hits.Add(new(new RectangleF(bounds.X - 2, y, 25, 22), Tooltip(occurrence)));
                }
            }
            else foreach (var occurrence in lane.Occurrences)
            {
                var startX = left + (float)snapshot.Fraction(occurrence.StartsAt) * track;
                var endX = left + (float)snapshot.Fraction(occurrence.EndsAt) * track;
                var bounds = new RectangleF(startX, y + 7, Math.Min(Math.Max(3, endX - startX), left + track - startX), 8);
                FillRound(bounds, 3, Color.FromArgb(205, ColorTranslator.FromHtml(occurrence.Color)));
                if (occurrence.PortalEndsAt is { } portal && portal > snapshot.Now)
                {
                    var portalX = left + (float)snapshot.Fraction(portal) * track;
                    FillRound(new RectangleF(startX, y + 7, Math.Min(Math.Max(3, portalX - startX), left + track - startX), 8), 3, Color.FromArgb(94, 234, 212));
                }
                hits.Add(new(new RectangleF(bounds.X, y + 2, Math.Max(3, bounds.Width), 19), Tooltip(occurrence)));
            }
            y += LaneStride;
        }
        // Draw resets above event bars so coincident daily/weekly boundaries remain visible.
        foreach (var reset in snapshot.Resets)
        {
            var weekly = reset.Key.StartsWith("weekly-reset:", StringComparison.Ordinal);
            var x = left + (float)snapshot.Fraction(reset.StartsAt) * track + (weekly ? 1.5f : -1.5f);
            x = Math.Clamp(x, left, left + track);
            using var resetLine = new Pen(ColorTranslator.FromHtml(reset.Color), weekly ? 1.7f : 1.2f)
                { DashStyle = weekly ? DashStyle.Dash : DashStyle.Dot };
            g.DrawLine(resetLine, x, AxisHeight - 3, x, height - BottomInset);
            hits.Add(new(new RectangleF(Math.Max(left, x - 5), AxisHeight - 3, Math.Min(10, left + track - Math.Max(left, x - 5)), height - AxisHeight - BottomInset + 3), Tooltip(reset)));
        }
        var nowX = left + (float)snapshot.Fraction(snapshot.Now) * track;
        using (var now = new Pen(Color.FromArgb(167, 139, 250), 1.5f)) g.DrawLine(now, nowX, AxisHeight - 3, nowX, height - BottomInset);
        if (!locked)
        {
            using var grip = new Pen(Muted);
            for (var index = 0; index < 3; index++) g.DrawLine(grip, width - 4 - index * 4, height - 4, width - 4, height - 4 - index * 4);
        }
        return hits;

        void FillRound(RectangleF bounds, float radius, Color color)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            using var path = AlertsOverlayForm.Rounded(bounds, radius);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
    }

    private static IReadOnlyList<TimelineHit> PaintAion2(Graphics g, EventTimelineSnapshot snapshot, bool locked,
        int width, Func<EventTimelineOccurrence, Image?>? icon, OverlayThemeDefinition theme)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var layout = Measure(snapshot, width, theme);
        theme.Panel(g, new Rectangle(Point.Empty, layout.Size));
        using var body = theme.Body(10, FontStyle.Bold);
        using var small = theme.Body(10);
        using var primary = new SolidBrush(theme.Primary);
        using var muted = new SolidBrush(theme.Secondary);
        using var grid = new Pen(Color.FromArgb(75, theme.Border));
        using var separator = new Pen(theme.Border);
        using var line = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat(line) { Alignment = StringAlignment.Far };
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var left = layout.TrackStart;
        var track = layout.TrackWidth;
        var bottom = layout.Size.Height - layout.BottomInset;
        var ticks = track < 400 ? 2 : 4;
        for (var hour = 0; hour <= snapshot.Hours; hour++)
        {
            var x = left + hour * track / snapshot.Hours;
            g.DrawLine(grid, x, layout.AxisHeight - 4, x, bottom);
        }
        for (var index = 0; index <= ticks; index++)
        {
            var x = left + index * track / (float)ticks;
            var bounds = index == 0 ? new RectangleF(x, 5, 96, 16)
                : index == ticks ? new RectangleF(x - 96, 5, 96, 16) : new RectangleF(x - 48, 5, 96, 16);
            g.DrawString(Clock(snapshot.From.AddHours(snapshot.Hours * index / ticks), snapshot.Hours > 12),
                small, muted, bounds, index == 0 ? line : index == ticks ? right : center);
        }
        g.DrawLine(separator, 8, layout.AxisHeight - 2, width - 9, layout.AxisHeight - 2);
        List<TimelineHit> hits = [];
        var y = layout.AxisHeight;
        foreach (var lane in snapshot.Lanes)
        {
            theme.Diamond(g, new(13, y + 13), 2.5f, ColorTranslator.FromHtml(lane.Color));
            g.DrawString(lane.Key == "invasion" ? UiText.T("Invasion · Siege") : lane.Title,
                body, primary, new RectangleF(23, y + 6, left - 31, 17), line);
            hits.Add(new(new RectangleF(8, y, left - 12, layout.LaneHeight), lane.Title));
            g.DrawLine(grid, left, y + layout.LaneHeight - 2, left + track, y + layout.LaneHeight - 2);
            if (lane.Bosses && lane.Occurrences.Count == 0)
                g.DrawString(UiText.T(snapshot.CapturedBossCount == 0 ? "Noch keine Boss-Timer erfasst" : "Keine Boss-Termine in diesem Zeitraum"),
                    small, muted, new RectangleF(left + 6, y + 6, track - 12, 17), line);
            if (lane.Bosses)
            {
                foreach (var group in BossGroups(lane.Occurrences, snapshot, left, track))
                {
                    var first = group.Items[0];
                    var bounds = new RectangleF(group.X - 9, y + 4, 18, 18);
                    var state = g.Save();
                    g.SetClip(bounds, CombineMode.Intersect);
                    if (icon?.Invoke(first) is { } image) g.DrawImage(image, bounds);
                    else
                    {
                        using var backing = new SolidBrush(first.Provisional ? Color.FromArgb(110, 70, 72, 70) : ColorTranslator.FromHtml(first.Color));
                        g.FillRectangle(backing, bounds);
                        g.DrawString("◆", body, primary, bounds, center);
                    }
                    if (first.Provisional)
                    {
                        using var shade = new SolidBrush(Color.FromArgb(115, theme.Surface));
                        g.FillRectangle(shade, bounds);
                    }
                    g.Restore(state);
                    using var outline = new Pen(first.Provisional ? theme.Secondary : theme.Gold)
                        { DashStyle = first.Provisional ? DashStyle.Dot : DashStyle.Solid };
                    g.DrawRectangle(outline, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    if (group.Items.Count > 1)
                    {
                        var badge = new RectangleF(bounds.Right - 7, bounds.Y - 2, 13, 12);
                        using var back = new SolidBrush(Color.FromArgb(245, theme.Surface));
                        g.FillRectangle(back, badge);
                        g.DrawString(group.Items.Count.ToString(), small, primary, badge, center);
                    }
                    foreach (var occurrence in group.Items)
                        hits.Add(new(new RectangleF(bounds.X - 2, y + 2, 25, 24), Tooltip(occurrence)));
                }
            }
            else foreach (var occurrence in lane.Occurrences)
            {
                var startX = left + (float)snapshot.Fraction(occurrence.StartsAt) * track;
                var endX = left + (float)snapshot.Fraction(occurrence.EndsAt) * track;
                var bounds = new RectangleF(startX, y + 10, Math.Min(Math.Max(3, endX - startX), left + track - startX), 5);
                using var fill = new SolidBrush(Color.FromArgb(220, ColorTranslator.FromHtml(occurrence.Color)));
                if (bounds.Width > 0) g.FillRectangle(fill, bounds);
                if (occurrence.PortalEndsAt is { } portal && portal > snapshot.Now)
                {
                    var portalX = left + (float)snapshot.Fraction(portal) * track;
                    using var portalBrush = new SolidBrush(theme.Mint);
                    var portalWidth = Math.Min(Math.Max(3, portalX - startX), left + track - startX);
                    if (portalWidth > 0) g.FillRectangle(portalBrush, startX, y + 10, portalWidth, 5);
                }
                hits.Add(new(new RectangleF(bounds.X, y + 3, Math.Max(3, bounds.Width), 22), Tooltip(occurrence)));
            }
            y += layout.LaneHeight;
        }
        foreach (var reset in snapshot.Resets)
        {
            var weekly = reset.Key.StartsWith("weekly-reset:", StringComparison.Ordinal);
            var x = Math.Clamp(left + (float)snapshot.Fraction(reset.StartsAt) * track + (weekly ? 1.5f : -1.5f), left, left + track);
            using var resetLine = new Pen(ColorTranslator.FromHtml(reset.Color), weekly ? 1.5f : 1)
                { DashStyle = weekly ? DashStyle.Dash : DashStyle.Dot };
            g.DrawLine(resetLine, x, layout.AxisHeight - 4, x, bottom);
            hits.Add(new(new RectangleF(Math.Max(left, x - 5), layout.AxisHeight - 4,
                Math.Min(10, left + track - Math.Max(left, x - 5)), bottom - layout.AxisHeight + 4), Tooltip(reset)));
        }
        var nowX = left + (float)snapshot.Fraction(snapshot.Now) * track;
        using (var now = new Pen(theme.Gold, 1.2f)) g.DrawLine(now, nowX, layout.AxisHeight - 4, nowX, bottom);
        theme.Diamond(g, new(nowX, layout.AxisHeight - 3), 2.5f, theme.Gold);
        if (!locked)
        {
            using var grip = new Pen(theme.Secondary);
            for (var index = 0; index < 3; index++)
                g.DrawLine(grip, width - 4 - index * 4, layout.Size.Height - 4, width - 4, layout.Size.Height - 4 - index * 4);
        }
        return hits;
    }

    private sealed record BossGroup(float X, List<EventTimelineOccurrence> Items);
    private static IReadOnlyList<BossGroup> BossGroups(IReadOnlyList<EventTimelineOccurrence> occurrences, EventTimelineSnapshot snapshot, int left, int track)
    {
        List<BossGroup> groups = [];
        foreach (var item in occurrences)
        {
            var x = Math.Clamp(left + (float)snapshot.Fraction(item.StartsAt) * track, left + 10, left + track - 15);
            if (groups.Count > 0 && x - groups[^1].X < 24) groups[^1].Items.Add(item);
            else groups.Add(new(x, [item]));
        }
        return groups;
    }

    private Image? BossIcon(EventTimelineOccurrence boss)
    {
        if (boss.Icon is not { } icon || _progress?.MapDataDirectory is not { } data) return null;
        if (_icons.TryGetValue(icon, out var cached)) return cached;
        Image? image = null;
        try
        {
            var path = Path.Combine(data, icon);
            if (File.Exists(path)) { using var stream = File.OpenRead(path); using var loaded = Image.FromStream(stream); image = new Bitmap(loaded); }
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Runtime.InteropServices.ExternalException) { LogFile.Error("Timeline boss icon", error); }
        _icons[icon] = image;
        return image;
    }

    private static string Tooltip(EventTimelineOccurrence occurrence)
    {
        var status = occurrence.Provisional ? UiText.T("Unbestätigt") : occurrence.Spawned ? UiText.T("Bestätigter Spawn") : "";
        var time = Clock(occurrence.StartsAt, true);
        if (occurrence.EndsAt > occurrence.StartsAt) time += " – " + Clock(occurrence.EndsAt, true);
        if (occurrence.PortalEndsAt is { } portal) time += "\n" + UiText.T("Portal") + ": " + Clock(portal, true);
        return occurrence.Title + "\n" + time + " Europe/Berlin" + (status.Length == 0 ? "" : "\n" + status);
    }
    private static string Clock(DateTimeOffset at, bool date) => TimeZoneInfo.ConvertTime(at, Berlin).ToString(date ? "dd.MM HH:mm" : "HH:mm");
    private void SetTooltip(string? text)
    {
        if (_tooltipText == text) return;
        _tooltipText = text;
        _tooltip.SetToolTip(this, text);
    }
    private bool IsResizeGrip(Point point) => point.X >= Width - 18 && point.Y >= Height - 18;
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!InteractionAllowed)
        {
            SetInteractionEnabled(OverlayInteraction.IsAltHeld);
            // WinForms may capture before this handler receives a queued click after relocking.
            if (Capture) Capture = false;
            return;
        }
        if (_locked) { if (Capture) Capture = false; return; }
        if (e.Button != MouseButtons.Left) return;
        if (IsResizeGrip(e.Location)) _resize = (PointToScreen(e.Location), Size);
        else _drag = e.Location;
        SetTooltip(null);
        Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!InteractionAllowed) { SetInteractionEnabled(OverlayInteraction.IsAltHeld); return; }
        if (_resize is { } resize && _snapshot is { } snapshot)
        {
            var pointer = PointToScreen(e.Location);
            var dimensions = ResizeDimensions(resize.Original, new Point(pointer.X - resize.Pointer.X, pointer.Y - resize.Pointer.Y), LogicalHeight(snapshot, _theme));
            _logicalWidth = dimensions.Width;
            var fitted = FitBounds(Screen.FromPoint(Location).WorkingArea, Location, Measure(snapshot, _logicalWidth, _theme).Size, dimensions.Scale);
            _renderScale = fitted.Width / (double)_logicalWidth;
            Bounds = fitted;
            InvalidatePanel();
            return;
        }
        if (_drag is { } from) { Location = new Point(Location.X + e.X - from.X, Location.Y + e.Y - from.Y); return; }
        Cursor = IsResizeGrip(e.Location) ? Cursors.SizeNWSE : Cursors.SizeAll;
        var logical = new PointF((float)(e.X / _renderScale), (float)(e.Y / _renderScale));
        var hovered = _hits.Where(hit => hit.Bounds.Contains(logical)).Select(hit => hit.Tooltip).Distinct(StringComparer.Ordinal).ToArray();
        SetTooltip(hovered.Length == 0 ? null : string.Join("\n\n", hovered));
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetTooltip(null); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!InteractionAllowed) { SetInteractionEnabled(OverlayInteraction.IsAltHeld); return; }
        FinishGesture();
    }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) FinishGesture(); }
    private void FinishGesture()
    {
        var resized = _resize is not null;
        var changed = resized || _drag is not null;
        _resize = null;
        _drag = null;
        if (Capture) Capture = false;
        if (!changed) return;
        var area = Screen.FromPoint(Location).WorkingArea;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        _settings.Update(settings =>
        {
            settings.EventTimelineOverlayX = Location.X;
            settings.EventTimelineOverlayY = Location.Y;
            if (resized) { settings.EventTimelineOverlayWidth = _logicalWidth; settings.EventTimelineOverlayScale = Math.Clamp(_renderScale, .6, 2); }
        });
        RefreshPanel();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lockButton.Toggled -= ToggleManualInteraction;
            _lockButton.Dispose();
            _tooltip.Dispose();
            _timer.Stop();
            _timer.Dispose();
            foreach (var icon in _icons.Values) icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
