using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Media;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Independent boss schedule and alert panel. Mouse input and moving are enabled only while Alt is held.</summary>
public sealed class BossOverlayForm : Form
{
    internal const int PanelWidth = 400;
    internal const int HeaderHeight = 48;
    internal const int AlertStride = 74;
    internal const int HeroStride = 90;
    internal const int RowStride = 44;
    private const int FooterHeight = 48;
    private static readonly Color Surface = Color.FromArgb(17, 24, 32);
    private static readonly Color Card = Color.FromArgb(25, 35, 46);
    private static readonly Color Border = Color.FromArgb(44, 59, 73);
    private static readonly Color Accent = Color.FromArgb(248, 113, 113);
    private static readonly Color Muted = Color.FromArgb(154, 173, 189);
    private static readonly Color Amber = Color.FromArgb(250, 204, 111);
    private readonly BossRushService _bosses;
    private readonly SettingsService _settings;
    private readonly ProgressService _progress;
    private readonly Dictionary<string, Image?> _icons = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private IReadOnlyList<BossRushEntry> _rows = [];
    private IReadOnlyList<BossNotice> _alerts = [];
    private Point? _drag;
    private readonly OverlayLockButton _lockButton;
    private bool _manualInteraction;
    private bool _locked = true, _inRecordings, _showList, _suppressed;
    private long _lastSoundId;

    public BossOverlayForm(BossRushService bosses, SettingsService settings, ProgressService progress)
    {
        _bosses = bosses;
        _settings = settings;
        _progress = progress;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Surface;
        Opacity = 0.94;
        DoubleBuffered = true;
        _lockButton = new OverlayLockButton(this);
        _lockButton.Toggled += ToggleManualInteraction;
        _lockButton.SetVisibleInRecordings(_settings.Current.OverlaysInRecordings);
        _timer.Tick += (_, _) => RefreshPanel();
        _timer.Start();
    }

    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed) return;
        _suppressed = suppressed;
        if (suppressed)
        {
            FinishDrag();
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
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE
                | NativeMethods.WS_EX_TOPMOST;
            if (_locked) cp.ExStyle |= NativeMethods.WS_EX_TRANSPARENT;
            else cp.ExStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetCaptureVisibility(Handle, _settings.Current.OverlaysInRecordings);
        _inRecordings = _settings.Current.OverlaysInRecordings;
        ApplyInteractionStyle();
    }

    /// <summary>Temporary interaction state; legacy lock preferences have no effect.</summary>
    internal bool ManualInteractionEnabled => _manualInteraction;

    internal void ToggleManualInteraction()
    {
        _manualInteraction = !_manualInteraction;
        _lockButton.SetLocked(!_manualInteraction);
        SetInteractionEnabled(OverlayInteraction.IsAltHeld);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) return;
        _manualInteraction = false;
        _lockButton?.SetLocked(true);
        SetInteractionEnabled(false);
    }

    private bool InteractionAllowed => !_suppressed && (_manualInteraction || OverlayInteraction.IsAltHeld);

    public void SetInteractionEnabled(bool enabled)
    {
        enabled = (enabled || _manualInteraction) && !_suppressed;
        if (_locked == !enabled) return;
        _locked = !enabled;
        if (_locked) FinishDrag();
        ApplyInteractionStyle();
        Cursor = _locked ? Cursors.Default : Cursors.SizeAll;
        Invalidate();
    }

    private void ApplyInteractionStyle()
    {
        if (!IsHandleCreated) return;
        NativeMethods.SetClickThrough(Handle, _locked);
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
        using var outline = Rounded(new RectangleF(0, 0, Width, Height), 12);
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }

    private void RefreshPanel()
    {
        if (_suppressed || !IsHandleCreated || IsDisposed || SmokeTest.Enabled) return;
        var settings = _settings.Current;
        _showList = ScheduleEnabled(settings);
        _rows = _showList ? _bosses.OverlaySpawns : [];
        _alerts = _bosses.Alerts;
        var newest = _alerts.LastOrDefault()?.Id ?? _lastSoundId;
        if (newest > _lastSoundId && settings.BossAlertSound) SystemSounds.Exclamation.Play();
        _lastSoundId = newest;
        if (!PanelVisible(settings, _alerts.Count)) { FinishDrag(); Hide(); return; }
        if (_inRecordings != settings.OverlaysInRecordings)
        {
            _inRecordings = settings.OverlaysInRecordings;
            NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
        _lockButton?.SetVisibleInRecordings(_inRecordings);
        }
        var scale = Math.Clamp(settings.BossOverlayScale, .6, 2);
        Size = new Size((int)(PanelWidth * scale), (int)(LogicalHeight(_rows.Count, _alerts.Count, _showList) * scale));
        if (_drag is null) Location = new Point(settings.BossOverlayX, settings.BossOverlayY);
        if (!Visible) Show();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var settings = _settings.Current;
        e.Graphics.ScaleTransform((float)Math.Clamp(settings.BossOverlayScale, .6, 2), (float)Math.Clamp(settings.BossOverlayScale, .6, 2));
        PaintPanel(e.Graphics, _rows, _alerts, _showList, _locked, settings.LootNameLanguage, BossIcon);
    }

    internal static bool ScheduleEnabled(AppSettings settings) => settings.BossRushEnabled && settings.BossOverlayEnabled;

    internal static bool PanelVisible(AppSettings settings, int alerts) => settings.BossOverlayVisible
        && (ScheduleEnabled(settings) || (settings.BossAlertsEnabled && alerts > 0));

    internal static string HotkeyHint(bool locked) => UiText.T(locked
        ? "Schloss klicken zum Entsperren" : "Schloss: sperren · Kopf ziehen");

    internal static int LogicalHeight(int rows, int alerts, bool list) => HeaderHeight + Math.Max(0, alerts) * AlertStride
        + (list ? rows > 0 ? HeroStride + (rows - 1) * RowStride : 72 : 0) + FooterHeight;

    internal static void PaintPanel(Graphics g, IReadOnlyList<BossRushEntry> rows, IReadOnlyList<BossNotice> alerts,
        bool list, bool locked, string language, Func<BossPlace, Image?> icon)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var title = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var timer = new Font("Segoe UI", 23, FontStyle.Bold, GraphicsUnit.Pixel);
        using var rowTimer = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.FromArgb(235, 242, 248));
        using var dim = new SolidBrush(Muted);
        using var red = new SolidBrush(Accent);
        using var amber = new SolidBrush(Amber);
        using var outline = new Pen(Border);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat { Alignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var centered = new StringFormat { Alignment = StringAlignment.Center };
        using var wrapped = new StringFormat { Trimming = StringTrimming.EllipsisWord };
        var height = LogicalHeight(rows.Count, alerts.Count, list);
        FillRound(new RectangleF(0, 0, PanelWidth, height), 12, Surface);
        using (var edge = Rounded(new RectangleF(.5f, .5f, PanelWidth - 1, height - 1), 12)) g.DrawPath(outline, edge);

        FillRound(new RectangleF(14, 15, 3, 17), 1.5f, Accent);
        g.DrawString("BOSS RUSH", title, white, 25, 14);
        g.DrawString(UiText.T(list ? "Nächste Spawns" : "Boss-Alerts"), small, dim, new RectangleF(157, 17, 205, 17), right);

        var y = HeaderHeight;
        foreach (var alert in alerts)
        {
            var box = new RectangleF(10, y, PanelWidth - 20, AlertStride - 8);
            FillRound(box, 9, Color.FromArgb(51, 30, 39));
            using (var path = Rounded(box, 9))
            using (var pen = new Pen(Color.FromArgb(104, 53, 65))) g.DrawPath(pen, path);
            FillRound(new RectangleF(10, y + 12, 3, AlertStride - 32), 1.5f, Accent);
            DrawIcon(alert.Boss, 21, y + 13, 38);
            g.DrawString(alert.Boss.Name(language), body, white, new RectangleF(71, y + 10, 306, 21), format);
            var status = alert.Spawned ? UiText.T("Bereits gespawnt")
                : alert.Remaining > TimeSpan.Zero ? UiText.F("Spawn in {0}", BossRushService.Countdown(alert.Remaining))
                : UiText.T("Spawn unbestätigt");
            g.DrawString(status, body, red, new RectangleF(71, y + 35, 306, 21), format);
            y += AlertStride;
        }

        if (list)
        {
            if (rows.Count == 0)
            {
                FillRound(new RectangleF(10, y, PanelWidth - 20, 64), 9, Card);
                g.DrawString(UiText.T("Keine angekündigten Spawns. Bossliste öffnen."), small, dim,
                    new RectangleF(24, y + 17, PanelWidth - 48, 38), wrapped);
                y += 72;
            }
            else
            {
                var next = rows[0];
                FillRound(new RectangleF(10, y, PanelWidth - 20, HeroStride - 8), 9, Card);
                using (var path = Rounded(new RectangleF(10, y, PanelWidth - 20, HeroStride - 8), 9)) g.DrawPath(outline, path);
                DrawIcon(next.Place.Boss, 22, y + 24, 44);
                g.DrawString(UiText.T("Nächster Spawn"), small, dim, 80, y + 9);
                g.DrawString(next.Place.Boss.Name(language), body, white, new RectangleF(80, y + 26, 294, 20), format);
                if (next.Remaining > TimeSpan.Zero)
                    g.DrawString(BossRushService.Countdown(next.Remaining), timer, red, new RectangleF(78, y + 45, 286, 32), format);
                else
                    g.DrawString(UiText.T("Spawn unbestätigt"), body, amber, new RectangleF(80, y + 52, 294, 22), format);
                y += HeroStride;

                for (var i = 1; i < rows.Count; i++)
                {
                    var row = rows[i];
                    FillRound(new RectangleF(10, y, PanelWidth - 20, RowStride - 4), 7, Card);
                    g.DrawString((i + 1).ToString(), small, dim, new RectangleF(17, y + 12, 17, 17), right);
                    DrawIcon(row.Place.Boss, 42, y + 5, 30);
                    g.DrawString(row.Place.Boss.Name(language), body, white, new RectangleF(82, y + 10, 198, 21), format);
                    g.DrawString(row.Remaining > TimeSpan.Zero ? BossRushService.Countdown(row.Remaining) : UiText.T("Unbestätigt"),
                        rowTimer, row.Remaining > TimeSpan.Zero ? red : amber, new RectangleF(281, y + 10, 95, 21), right);
                    y += RowStride;
                }
            }
        }

        if (!locked)
        {
            DrawGrip(20, y + 8, Muted);
            g.DrawString(UiText.T("verschieben"), small, dim,
                new RectangleF(36, y + 5, 344, 20), format);
        }
        else if (list && rows.Count > 0)
        {
            var cached = rows.Any(r => r.Cached);
            var pending = rows.Any(r => r.Remaining <= TimeSpan.Zero);
            using var status = new SolidBrush(cached || pending ? Amber : Muted);
            g.FillEllipse(status, 17, y + 11, 4, 4);
            g.DrawString(UiText.T(cached ? "Gespeicherter Stand · Countdowns laufen weiter"
                : pending ? "Spawn unbestätigt" : "Countdowns laufen weiter"), small, status,
                new RectangleF(29, y + 5, 350, 20), format);
        }

        g.DrawString(HotkeyHint(locked), small, dim, new RectangleF(17, y + 26, PanelWidth - 34, 18), format);

        void FillRound(RectangleF bounds, float radius, Color color)
        {
            using var path = Rounded(bounds, radius);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }

        void DrawIcon(BossPlace boss, int left, int top, int size)
        {
            var bounds = new RectangleF(left, top, size, size);
            using var clip = Rounded(bounds, 6);
            var state = g.Save();
            g.SetClip(clip, CombineMode.Intersect);
            if (icon(boss) is { } image) g.DrawImage(image, bounds);
            else
            {
                using var fallback = new SolidBrush(Color.FromArgb(48, 40, 48));
                g.FillRectangle(fallback, bounds);
                g.DrawString("◆", title, red, new RectangleF(left, top + (size - 20) / 2f, size, 22),
                    centered);
            }
            g.Restore(state);
            g.DrawPath(outline, clip);
        }

        void DrawGrip(int left, int top, Color color)
        {
            using var brush = new SolidBrush(color);
            for (var col = 0; col < 2; col++)
                for (var row = 0; row < 3; row++) g.FillEllipse(brush, left + col * 4, top + row * 4, 2, 2);
        }
    }

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
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

    private Image? BossIcon(BossPlace boss)
    {
        if (boss.Icon is not { } icon || _progress.MapDataDirectory is not { } data) return null;
        if (_icons.TryGetValue(icon, out var cached)) return cached;
        Image? image = null;
        try
        {
            var path = Path.Combine(data, icon);
            if (File.Exists(path)) { using var stream = File.OpenRead(path); using var loaded = Image.FromStream(stream); image = new Bitmap(loaded); }
        }
        catch (Exception e) when (e is IOException or ArgumentException or System.Runtime.InteropServices.ExternalException) { LogFile.Error("Boss icon", e); }
        _icons[icon] = image;
        return image;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!InteractionAllowed) { SetInteractionEnabled(false); return; }
        if (_locked || e.Button != MouseButtons.Left) return;
        _drag = e.Location;
        Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!InteractionAllowed) { SetInteractionEnabled(false); return; }
        if (_drag is { } from) Location = new Point(Location.X + e.X - from.X, Location.Y + e.Y - from.Y);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!InteractionAllowed) { SetInteractionEnabled(false); return; }
        FinishDrag();
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) FinishDrag();
    }
    private void FinishDrag()
    {
        if (_drag is null) return;
        _drag = null;
        Capture = false;
        _settings.Update(s => { s.BossOverlayX = Location.X; s.BossOverlayY = Location.Y; });
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _lockButton.Toggled -= ToggleManualInteraction; _lockButton.Dispose(); _timer.Stop(); _timer.Dispose(); foreach (var image in _icons.Values) image?.Dispose(); }
        base.Dispose(disposing);
    }
}
