using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

internal sealed record LevelingOverlaySnapshot(string RouteId, string RouteName, string CharacterId,
    string CharacterName, RouteProgress Progress, MapTarget? Current, IReadOnlyList<MapTarget> Window);

internal enum LevelingOverlayAction { None, Drag, Back, Next, Cancel }

/// <summary>Independent leveling HUD with a directly clickable lock and temporary Alt interaction.</summary>
public sealed class LevelingOverlayForm : Form
{
    internal const int PanelWidth = 360;
    internal const int HeaderHeight = 88;
    internal const int WindowTop = 208;
    internal const int RowStride = 25;
    private static readonly Color Surface = Color.FromArgb(15, 22, 31);
    private static readonly Color Card = Color.FromArgb(23, 33, 45);
    private static readonly Color Border = Color.FromArgb(47, 61, 78);
    private static readonly Color White = Color.FromArgb(235, 241, 248);
    private static readonly Color Muted = Color.FromArgb(143, 161, 181);
    private static readonly Color Accent = Color.FromArgb(167, 139, 250);
    private readonly MapTargetsService _targets;
    private readonly ExplorationService _exploration;
    private readonly SettingsService _settings;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private LevelingOverlaySnapshot? _snapshot;
    private Point? _drag;
    private LevelingOverlayAction _pressed, _hover;
    private string? _pressedRouteId, _pressedCharacterId;
    private RouteProgress? _pressedProgress;
    private readonly OverlayLockButton _lockButton;
    private bool _manualInteraction;
    private bool _locked = true, _inRecordings, _suppressed;

    public LevelingOverlayForm(MapTargetsService targets, ExplorationService exploration, SettingsService settings)
    {
        _targets = targets;
        _exploration = exploration;
        _settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Surface;
        Opacity = .97;
        DoubleBuffered = true;
        _lockButton = new OverlayLockButton(this);
        _lockButton.Toggled += ToggleManualInteraction;
        _lockButton.SetVisibleInRecordings(_settings.Current.OverlaysInRecordings);
        _timer.Tick += (_, _) => RefreshPanel();
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Temporarily hides the HUD while preserving the route, character and saved layout.</summary>
    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed) return;
        _suppressed = suppressed;
        if (suppressed)
        {
            ResetInteraction();
            SetInteractionEnabled(false);
            Hide();
        }
        else RefreshPanel();
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(value && !_suppressed);

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
        _lockButton?.SetVisibleInRecordings(_inRecordings);
        ApplyInteractionStyle();
    }

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

    /// <summary>Temporary interaction state; releasing Alt cancels any pending button action.</summary>
    public void SetInteractionEnabled(bool enabled)
    {
        enabled = (enabled || _manualInteraction) && !_suppressed;
        if (_locked == !enabled) return;
        _locked = !enabled;
        if (_locked) ResetInteraction();
        ApplyInteractionStyle();
        Cursor = Cursors.Default;
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
        using var outline = Rounded(new RectangleF(0, 0, Width, Height), 14);
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }

    private void RefreshPanel()
    {
        if (_suppressed || !IsHandleCreated || IsDisposed || SmokeTest.Enabled) return;
        var settings = _settings.Current;
        var id = _targets.ActiveLevelingRouteId;
        var characterId = _exploration.ActiveId;
        var snapshot = BuildSnapshot(_targets.Routes.FirstOrDefault(r => r.Id == id),
            id is null ? null : _targets.GetRouteProgress(id), characterId,
            _exploration.Characters.FirstOrDefault(c => c.Id == characterId).Name ?? "",
            _targets.VisibleTargets);
        if (_snapshot?.RouteId != snapshot?.RouteId || _snapshot?.CharacterId != snapshot?.CharacterId)
            ResetInteraction();
        _snapshot = snapshot;
        if (!PanelVisible(settings, snapshot))
        {
            ResetInteraction();
            Hide();
            return;
        }
        if (_inRecordings != settings.OverlaysInRecordings)
        {
            _inRecordings = settings.OverlaysInRecordings;
            NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
        _lockButton?.SetVisibleInRecordings(_inRecordings);
        }
        var scale = Scale(settings.LevelingOverlayScale);
        Size = new Size((int)Math.Ceiling(PanelWidth * scale),
            (int)Math.Ceiling(LogicalHeight(snapshot!.Window.Count) * scale));
        if (_drag is null)
        {
            var desired = new Point(settings.LevelingOverlayX, settings.LevelingOverlayY);
            var area = Screen.FromRectangle(new Rectangle(desired, Size)).WorkingArea;
            Location = ClampLocation(desired, Size, area);
            if (Location != desired) SavePosition();
        }
        if (!Visible) Show();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_snapshot is not { } snapshot) return;
        var scale = (float)Scale(_settings.Current.LevelingOverlayScale);
        e.Graphics.ScaleTransform(scale, scale);
        PaintPanel(e.Graphics, snapshot, _locked, _hover, _pressed);
    }

    internal static double Scale(double value) => double.IsFinite(value) ? Math.Clamp(value, .7, 1.6) : 1;
    internal static bool PanelVisible(AppSettings settings, LevelingOverlaySnapshot? snapshot) =>
        settings.LevelingOverlayEnabled && snapshot is not null;
    internal static int LogicalHeight(int rows) => WindowTop + Math.Clamp(rows, 0, 6) * RowStride + 80;
    internal static int ControlsTop(int rows) => WindowTop + Math.Clamp(rows, 0, 6) * RowStride + 12;
    internal static string HotkeyHint(bool locked) => UiText.T(locked
        ? "Schloss klicken zum Entsperren" : "Schloss: sperren · Kopf ziehen");
    internal static string TargetLabel(MapTarget target) => string.IsNullOrWhiteSpace(target.Name)
        ? UiText.F("Station {0}", (target.StopIndex ?? 0) + 1) : target.Name;

    internal static LevelingOverlaySnapshot? BuildSnapshot(SavedRoute? route, RouteProgress? progress,
        string characterId, string characterName, IReadOnlyList<MapTarget> targets)
    {
        if (route is not { IsLeveling: true } || progress is null) return null;
        var stops = targets.Where(t => t.Leveling && t.RouteId == route.Id
            && (t.CharacterId is null || t.CharacterId == characterId)).OrderBy(t => t.StopIndex).ToArray();
        var window = stops.Where(t => t.Completed).TakeLast(3).Concat(stops.Where(t => !t.Completed).Take(3)).ToArray();
        return new(route.Id, route.Name, characterId, characterName, progress,
            window.FirstOrDefault(t => !t.Completed), window);
    }

    internal static Point ClampLocation(Point desired, Size panel, Rectangle workingArea) => new(
        Math.Clamp(desired.X, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - panel.Width)),
        Math.Clamp(desired.Y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - panel.Height)));

    internal static RectangleF ButtonBounds(LevelingOverlayAction action, int rows) => action switch
    {
        LevelingOverlayAction.Back => new(16, ControlsTop(rows), 92, 34),
        LevelingOverlayAction.Next => new(116, ControlsTop(rows), 92, 34),
        LevelingOverlayAction.Cancel => new(216, ControlsTop(rows), 128, 34),
        _ => RectangleF.Empty
    };

    internal static LevelingOverlayAction HitTest(PointF point, bool locked, LevelingOverlaySnapshot? snapshot)
    {
        if (locked || snapshot is null) return LevelingOverlayAction.None;
        foreach (var action in new[] { LevelingOverlayAction.Back, LevelingOverlayAction.Next, LevelingOverlayAction.Cancel })
            if (ButtonBounds(action, snapshot.Window.Count).Contains(point)
                && (action != LevelingOverlayAction.Back || snapshot.Progress.CanGoBack)
                && (action != LevelingOverlayAction.Next || snapshot.Progress.CanGoNext)) return action;
        return new RectangleF(0, 0, PanelWidth, HeaderHeight).Contains(point)
            ? LevelingOverlayAction.Drag : LevelingOverlayAction.None;
    }

    /// <summary>Clicks begun before a character/route switch cannot advance the newly selected profile.</summary>
    internal static bool CanApplyAction(string? pressedRouteId, string? pressedCharacterId,
        string? activeRouteId, string activeCharacterId) => pressedRouteId is not null
        && pressedRouteId == activeRouteId && pressedCharacterId == activeCharacterId;

    internal static bool CanApplyAction(string? pressedRouteId, string? pressedCharacterId,
        string? activeRouteId, string activeCharacterId, RouteProgress? pressedProgress, RouteProgress? activeProgress) =>
        CanApplyAction(pressedRouteId, pressedCharacterId, activeRouteId, activeCharacterId)
        && pressedProgress is not null && pressedProgress == activeProgress;

    internal static Color ObjectiveColor(MapTarget target) => target.Color switch
    {
        LevelingRouteStyle.MainQuest => Color.FromArgb(250, 204, 21),
        LevelingRouteStyle.Teleport => Accent,
        LevelingRouteStyle.RegionalQuest => Color.FromArgb(74, 222, 128),
        _ => Color.White
    };
    internal static Color Mix(Color foreground, Color background, double amount) => Color.FromArgb(
        (int)Math.Round(background.R + (foreground.R - background.R) * amount),
        (int)Math.Round(background.G + (foreground.G - background.G) * amount),
        (int)Math.Round(background.B + (foreground.B - background.B) * amount));

    internal static void PaintPanel(Graphics g, LevelingOverlaySnapshot snapshot, bool locked,
        LevelingOverlayAction hover = LevelingOverlayAction.None, LevelingOverlayAction pressed = LevelingOverlayAction.None)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var title = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        using var routeFont = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
        using var bold = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var white = new SolidBrush(White);
        using var muted = new SolidBrush(Muted);
        using var accent = new SolidBrush(Accent);
        using var line = new Pen(Border);
        using var single = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat { Alignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var wrapped = new StringFormat { Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.LineLimit };
        var height = LogicalHeight(snapshot.Window.Count);
        FillRound(new RectangleF(0, 0, PanelWidth, height), 14, Surface);
        using (var edge = Rounded(new RectangleF(.5f, .5f, PanelWidth - 1, height - 1), 14)) g.DrawPath(line, edge);
        FillRound(new RectangleF(16, 18, 3, 13), 1.5f, Accent);
        g.DrawString(UiText.T("Leveling Routes").ToUpperInvariant(), title, white, 27, 16);
        g.FillEllipse(accent, 18, 45, 5, 5);
        g.DrawString(snapshot.CharacterName, small, muted, new RectangleF(31, 39, 313, 19), single);
        g.DrawString(snapshot.RouteName, routeFont, white, new RectangleF(16, 59, 328, 26), single);

        var objectiveColor = snapshot.Current is { } current ? ObjectiveColor(current) : Color.FromArgb(74, 222, 128);
        var hero = new RectangleF(12, HeaderHeight, PanelWidth - 24, 110);
        FillRound(hero, 11, Card);
        FillRound(new RectangleF(12, HeaderHeight + 14, 3, 78), 1.5f, objectiveColor);
        var progress = snapshot.Progress;
        var fraction = progress.Total <= 0 ? 1 : Math.Clamp((double)progress.Completed / progress.Total, 0, 1);
        var station = UiText.F("Station {0} von {1}", Math.Min(progress.Completed + 1, progress.Total), progress.Total);
        g.DrawString(station, small, muted, new RectangleF(26, HeaderHeight + 13, 230, 19), single);
        using (var brush = new SolidBrush(objectiveColor))
            g.DrawString($"{Math.Round(fraction * 100)}%", bold, brush, new RectangleF(264, HeaderHeight + 11, 68, 21), right);
        FillRound(new RectangleF(26, HeaderHeight + 38, 306, 4), 2, Border);
        if (fraction > 0) FillRound(new RectangleF(26, HeaderHeight + 38, Math.Max(4, (float)(306 * fraction)), 4), 2, objectiveColor);
        g.DrawString(snapshot.Current is { } currentTarget ? TargetLabel(currentTarget) : UiText.T("Route abgeschlossen"),
            bold, white, new RectangleF(26, HeaderHeight + 53, 304, 45), wrapped);

        for (var i = 0; i < snapshot.Window.Count; i++)
        {
            var target = snapshot.Window[i];
            var y = WindowTop + i * RowStride;
            var color = ObjectiveColor(target);
            var isCurrent = target.Id == snapshot.Current?.Id;
            if (isCurrent) FillRound(new RectangleF(12, y, PanelWidth - 24, RowStride - 1), 6, Mix(color, Surface, .08));
            using var dot = new SolidBrush(target.Completed ? Mix(color, Surface, .28) : color);
            using var label = new SolidBrush(target.Completed ? Mix(White, Surface, .28) : isCurrent ? White : Muted);
            g.FillEllipse(dot, 21, y + 9, 8, 8);
            g.DrawString(((target.StopIndex ?? i) + 1).ToString(), small, label, new RectangleF(35, y + 4, 24, 18), right);
            g.DrawString(TargetLabel(target), isCurrent ? bold : body, label,
                new RectangleF(68, y + 3, 256, 20), single);
            if (isCurrent)
            {
                using var arrow = new Pen(color, 1.5f);
                g.DrawLines(arrow, [new PointF(332, y + 9), new PointF(336, y + 13), new PointF(332, y + 17)]);
            }
        }

        DrawButton(LevelingOverlayAction.Back, UiText.T("Zurück"), progress.CanGoBack, false);
        DrawButton(LevelingOverlayAction.Next, UiText.T("Weiter"), progress.CanGoNext, true);
        DrawButton(LevelingOverlayAction.Cancel, UiText.T("Abbrechen"), true, false);
        g.DrawString(HotkeyHint(locked), small, muted,
            new RectangleF(16, ControlsTop(snapshot.Window.Count) + 46, PanelWidth - 32, 19), single);

        void FillRound(RectangleF bounds, float radius, Color color)
        {
            using var path = Rounded(bounds, radius);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }

        void DrawButton(LevelingOverlayAction action, string label, bool enabled, bool primary)
        {
            var bounds = ButtonBounds(action, snapshot.Window.Count);
            var active = enabled && !locked;
            var fill = primary && enabled ? Color.FromArgb(46, 38, 70) : Card;
            if (active && hover == action) fill = Mix(Accent, fill, .15);
            if (active && pressed == action) fill = Mix(Accent, fill, .25);
            FillRound(bounds, 7, fill);
            using var edge = new Pen(active && primary ? Color.FromArgb(101, 80, 147) : Border);
            using (var path = Rounded(bounds, 7)) g.DrawPath(edge, path);
            using var ink = new SolidBrush(enabled ? primary ? Color.FromArgb(214, 197, 255) : White : Mix(Muted, fill, .42));
            g.DrawString(label, bold, ink, bounds, centered);
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

    private PointF LogicalPoint(Point point)
    {
        var scale = (float)Scale(_settings.Current.LevelingOverlayScale);
        return new PointF(point.X / scale, point.Y / scale);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!InteractionAllowed) { SetInteractionEnabled(false); return; }
        if (e.Button != MouseButtons.Left) return;
        var action = HitTest(LogicalPoint(e.Location), _locked, _snapshot);
        if (action == LevelingOverlayAction.Drag) _drag = e.Location;
        else if (action != LevelingOverlayAction.None)
        {
            _pressed = action;
            _pressedRouteId = _snapshot?.RouteId;
            _pressedCharacterId = _snapshot?.CharacterId;
            _pressedProgress = _snapshot?.Progress;
        }
        if (action != LevelingOverlayAction.None) Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!InteractionAllowed) { SetInteractionEnabled(false); return; }
        if (_drag is { } from)
            Location = new Point(Location.X + e.X - from.X, Location.Y + e.Y - from.Y);
        _hover = HitTest(LogicalPoint(e.Location), _locked, _snapshot);
        Cursor = _hover == LevelingOverlayAction.Drag ? Cursors.SizeAll
            : _hover != LevelingOverlayAction.None ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = LevelingOverlayAction.None;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!InteractionAllowed) { SetInteractionEnabled(false); return; }
        if (e.Button != MouseButtons.Left) return;
        var action = _pressed;
        var routeId = _pressedRouteId;
        var characterId = _pressedCharacterId;
        var progress = _pressedProgress;
        var released = HitTest(LogicalPoint(e.Location), _locked, _snapshot);
        ResetInteraction();
        if (action == released && CanApplyAction(routeId, characterId, _targets.ActiveLevelingRouteId, _exploration.ActiveId,
            progress, routeId is null ? null : _targets.GetRouteProgress(routeId)))
        {
            switch (action)
            {
                case LevelingOverlayAction.Back: _targets.RewindRoute(routeId!); break;
                case LevelingOverlayAction.Next: _targets.AdvanceRoute(routeId!); break;
                case LevelingOverlayAction.Cancel: _targets.StopRoute(routeId!); break;
            }
        }
        RefreshPanel();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) ResetInteraction();
    }

    private void ResetInteraction()
    {
        _pressed = _hover = LevelingOverlayAction.None;
        _pressedRouteId = _pressedCharacterId = null;
        _pressedProgress = null;
        if (_drag is not null)
        {
            _drag = null;
            Location = ClampLocation(Location, Size, Screen.FromRectangle(Bounds).WorkingArea);
            SavePosition();
        }
        if (Capture) Capture = false;
    }

    private void SavePosition() => _settings.Update(s =>
    {
        s.LevelingOverlayX = Location.X;
        s.LevelingOverlayY = Location.Y;
    });

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lockButton.Toggled -= ToggleManualInteraction;
            _lockButton.Dispose();
            _timer.Stop();
            _timer.Dispose();
            ResetInteraction();
        }
        base.Dispose(disposing);
    }
}
