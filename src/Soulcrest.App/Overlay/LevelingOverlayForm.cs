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
    private bool _altInteractionRequested, _blockAltUntilRelease;
    private bool _locked = true, _inRecordings, _suppressed;
    private bool _placementHidden, _foregroundSuppressed;
    private OverlayThemeDefinition _theme = OverlayThemeDefinition.Legacy;
    private double _renderScale = 1;
    private bool _rendering;
    private bool PresentationHidden => _placementHidden || _foregroundSuppressed;

    public LevelingOverlayForm(MapTargetsService targets, ExplorationService exploration, SettingsService settings)
    {
        _targets = targets;
        _exploration = exploration;
        _settings = settings;
        _theme = OverlayThemeDefinition.Resolve(settings.Current.OverlayTheme);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Surface;
        Opacity = .97;
        DoubleBuffered = true;
        _lockButton = new OverlayLockButton(this, LockBounds);
        _lockButton.SetTheme(_theme);
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
            ResetInteraction();
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
        LayeredOverlayPresenter.Configure(this, !_theme.IsLegacy, .97);
    }

    internal bool ManualInteractionEnabled => _manualInteraction;

    internal void ToggleManualInteraction() => SetManualInteraction(_locked);

    // A hotkey can itself contain Alt: toggle the persistent choice, not its temporary Alt unlock.
    internal void ToggleManualInteractionFromHotkey() => SetManualInteraction(!_manualInteraction);

    private void SetManualInteraction(bool enabled)
    {
        // An explicit lock wins over an already active Alt signal until its next release.
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

    private bool InteractionAllowed => !_suppressed && !PresentationHidden && (_manualInteraction || (!_blockAltUntilRelease && OverlayInteraction.IsAltHeld));

    /// <summary>Temporary interaction state; releasing Alt cancels any pending button action.</summary>
    public void SetInteractionEnabled(bool enabled)
    {
        _altInteractionRequested = enabled;
        if (!enabled) _blockAltUntilRelease = false;
        enabled = (_manualInteraction || (enabled && !_blockAltUntilRelease)) && !_suppressed && !PresentationHidden;
        if (_lockButton is { } button && button.Locked != !enabled) button.SetLocked(!enabled);
        if (_locked == !enabled) return;
        _locked = !enabled;
        if (_locked) ResetInteraction();
        ApplyInteractionStyle();
        Cursor = Cursors.Default;
        InvalidatePanel();
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
        UpdateWindowShape();
    }

    private void UpdateWindowShape()
    {
        if (!_theme.IsLegacy)
        {
            var old = Region; Region = null; old?.Dispose();
            return;
        }
        using var outline = Rounded(new RectangleF(0, 0, Width, Height), 14);
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }

    private void RefreshPanel()
    {
        if (_suppressed || PresentationHidden || !IsHandleCreated || IsDisposed || SmokeTest.Enabled) return;
        var settings = _settings.Current;
        ApplyRequestedTheme();
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
        var logicalSize = Measure(snapshot!.Window.Count, _theme).Size;
        _renderScale = scale;
        if (!_theme.IsLegacy)
        {
            var desired = _drag is null ? new Point(settings.LevelingOverlayX, settings.LevelingOverlayY) : Location;
            var area = Screen.FromPoint(desired).WorkingArea;
            _renderScale = Math.Min(scale, Math.Min(area.Width / (double)logicalSize.Width, area.Height / (double)logicalSize.Height));
        }
        Size = new Size(Math.Max(1, (int)Math.Ceiling(logicalSize.Width * _renderScale)),
            Math.Max(1, (int)Math.Ceiling(logicalSize.Height * _renderScale)));
        if (_drag is null)
        {
            var desired = new Point(settings.LevelingOverlayX, settings.LevelingOverlayY);
            var area = Screen.FromRectangle(new Rectangle(desired, Size)).WorkingArea;
            // A display-only clamp must not rewrite an off-screen saved layout on refresh/placement exit.
            // Native gestures and the explicit placement action remain the persistence boundaries.
            Location = ClampLocation(desired, Size, area);
        }
        if (!Visible) Show();
        _lockButton?.RefreshTarget();
        InvalidatePanel();
    }

    private void ApplyRequestedTheme()
    {
        if (_drag is not null || _pressed != LevelingOverlayAction.None) return;
        var requested = OverlayThemeDefinition.Resolve(_settings.Current.OverlayTheme);
        if (requested.Id == _theme.Id) return;
        _theme = requested;
        LayeredOverlayPresenter.Configure(this, !_theme.IsLegacy, .97);
        UpdateWindowShape();
        _lockButton.SetTheme(_theme);
        _hover = LevelingOverlayAction.None;
    }

    private Rectangle LockBounds()
    {
        if (_theme.IsLegacy)
        {
            var size = Math.Max(1, (int)Math.Round(28 * DeviceDpi / 96d));
            var inset = Math.Max(0, (int)Math.Round(8 * DeviceDpi / 96d));
            return new(Math.Max(0, ClientSize.Width - size - inset), inset, size, size);
        }
        var logical = Measure(_snapshot?.Window.Count ?? 0, _theme).LockBounds;
        return Rectangle.Round(new RectangleF((float)(logical.X * _renderScale), (float)(logical.Y * _renderScale),
            (float)(logical.Width * _renderScale), (float)(logical.Height * _renderScale)));
    }

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
                PaintPanel(graphics, snapshot, _locked, _hover, _pressed, _theme);
            }
            LayeredOverlayPresenter.Present(this, bitmap);
        }
        finally { _rendering = false; }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { if (_theme.IsLegacy) base.OnPaintBackground(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_theme.IsLegacy) return;
        base.OnPaint(e);
        if (_snapshot is not { } snapshot) return;
        var scale = (float)Scale(_settings.Current.LevelingOverlayScale);
        e.Graphics.ScaleTransform(scale, scale);
        PaintPanel(e.Graphics, snapshot, _locked, _hover, _pressed, _theme);
    }

    internal static double Scale(double value) => double.IsFinite(value) ? Math.Clamp(value, .7, 1.6) : 1;
    internal static bool PanelVisible(AppSettings settings, LevelingOverlaySnapshot? snapshot) =>
        settings.LevelingOverlayEnabled && snapshot is not null;
    internal static int LogicalHeight(int rows, OverlayThemeDefinition? theme = null) => Measure(rows, theme ?? OverlayThemeDefinition.Legacy).Size.Height;
    internal static int ControlsTop(int rows) => WindowTop + Math.Clamp(rows, 0, 6) * RowStride + 12;
    internal sealed record LevelingPanelLayout(Size Size, RectangleF DragBounds, RectangleF LockBounds,
        int HeroTop, int RowsTop, int RowHeight, int ControlsTop);

    internal static LevelingPanelLayout Measure(int rows, OverlayThemeDefinition theme)
    {
        rows = Math.Clamp(rows, 0, 6);
        return theme.IsLegacy
            ? new(new(PanelWidth, WindowTop + rows * RowStride + 80), new(0, 0, PanelWidth, HeaderHeight),
                new(324, 8, 28, 28), HeaderHeight, WindowTop, RowStride, ControlsTop(rows))
            : new(new(PanelWidth, 174 + rows * 32 + 66), new(0, 0, PanelWidth, 68),
                new(330, 8, 22, 22), 70, 174, 32, 174 + rows * 32 + 8);
    }
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

    internal static RectangleF ButtonBounds(LevelingOverlayAction action, int rows, OverlayThemeDefinition? theme = null) => (theme ?? OverlayThemeDefinition.Legacy).IsLegacy ? action switch
    {
        LevelingOverlayAction.Back => new(16, ControlsTop(rows), 92, 34),
        LevelingOverlayAction.Next => new(116, ControlsTop(rows), 92, 34),
        LevelingOverlayAction.Cancel => new(216, ControlsTop(rows), 128, 34),
        _ => RectangleF.Empty
    } : action switch
    {
        LevelingOverlayAction.Back => new(12, Measure(rows, theme!).ControlsTop, 94, 30),
        LevelingOverlayAction.Next => new(112, Measure(rows, theme!).ControlsTop, 112, 30),
        LevelingOverlayAction.Cancel => new(230, Measure(rows, theme!).ControlsTop, 118, 30),
        _ => RectangleF.Empty
    };

    internal static LevelingOverlayAction HitTest(PointF point, bool locked, LevelingOverlaySnapshot? snapshot, OverlayThemeDefinition? theme = null)
    {
        if (locked || snapshot is null) return LevelingOverlayAction.None;
        foreach (var action in new[] { LevelingOverlayAction.Back, LevelingOverlayAction.Next, LevelingOverlayAction.Cancel })
            if (ButtonBounds(action, snapshot.Window.Count, theme).Contains(point)
                && (action != LevelingOverlayAction.Back || snapshot.Progress.CanGoBack)
                && (action != LevelingOverlayAction.Next || snapshot.Progress.CanGoNext)) return action;
        return Measure(snapshot.Window.Count, theme ?? OverlayThemeDefinition.Legacy).DragBounds.Contains(point)
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

    internal static Color ObjectiveColor(MapTarget target, OverlayThemeDefinition? theme = null) => target.Color switch
    {
        LevelingRouteStyle.MainQuest => Color.FromArgb(250, 204, 21),
        LevelingRouteStyle.Teleport => Accent,
        LevelingRouteStyle.RegionalQuest => Color.FromArgb(74, 222, 128),
        _ => theme is { IsLegacy: false } ? theme.Primary : Color.White
    };
    internal static Color Mix(Color foreground, Color background, double amount) => Color.FromArgb(
        (int)Math.Round(background.R + (foreground.R - background.R) * amount),
        (int)Math.Round(background.G + (foreground.G - background.G) * amount),
        (int)Math.Round(background.B + (foreground.B - background.B) * amount));

    internal static void PaintPanel(Graphics g, LevelingOverlaySnapshot snapshot, bool locked,
        LevelingOverlayAction hover = LevelingOverlayAction.None, LevelingOverlayAction pressed = LevelingOverlayAction.None,
        OverlayThemeDefinition? theme = null)
    {
        if (theme is { IsLegacy: false }) { PaintAion2(g, snapshot, locked, hover, pressed, theme); return; }
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

    private static void PaintAion2(Graphics g, LevelingOverlaySnapshot snapshot, bool locked,
        LevelingOverlayAction hover, LevelingOverlayAction pressed, OverlayThemeDefinition theme)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var layout = Measure(snapshot.Window.Count, theme);
        theme.Panel(g, new Rectangle(Point.Empty, layout.Size));
        using var title = theme.Heading(17);
        using var heading = theme.Heading(15);
        using var body = theme.Body(12);
        using var small = theme.Body(10);
        using var bold = theme.Body(12, FontStyle.Bold);
        using var white = new SolidBrush(theme.Primary);
        using var muted = new SolidBrush(theme.Secondary);
        using var gold = new SolidBrush(theme.Gold);
        using var rule = new Pen(theme.Border);
        using var single = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat(single) { Alignment = StringAlignment.Far };
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var wrapped = new StringFormat { Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.LineLimit };
        theme.Diamond(g, new(16, 17), 3, theme.Gold);
        g.DrawString(UiText.T("Leveling Routes"), heading, gold, new RectangleF(27, 6, 295, 23), single);
        g.DrawString(snapshot.CharacterName, small, muted, new RectangleF(12, 29, 324, 16), single);
        g.DrawString(snapshot.RouteName, title, white, new RectangleF(12, 44, 336, 24), single);
        g.DrawLine(rule, 12, layout.HeroTop - 1, 348, layout.HeroTop - 1);

        var progress = snapshot.Progress;
        var fraction = progress.Total <= 0 ? 1 : Math.Clamp((double)progress.Completed / progress.Total, 0, 1);
        var objectiveColor = snapshot.Current is { } current ? ObjectiveColor(current, theme) : theme.Mint;
        using var objective = new SolidBrush(objectiveColor);
        using var hero = new SolidBrush(Color.FromArgb(95, Mix(Color.Black, theme.Surface, .45)));
        g.FillRectangle(hero, 8, layout.HeroTop + 3, 344, 94);
        theme.Diamond(g, new(19, layout.HeroTop + 17), 4, objectiveColor);
        g.DrawString(UiText.F("Station {0} von {1}", Math.Min(progress.Completed + 1, progress.Total), progress.Total),
            small, muted, new RectangleF(31, layout.HeroTop + 8, 240, 18), single);
        g.DrawString($"{Math.Round(fraction * 100)}%", bold, objective, new RectangleF(275, layout.HeroTop + 6, 61, 21), right);
        g.DrawString(snapshot.Current is { } heroTarget ? TargetLabel(heroTarget) : UiText.T("Route abgeschlossen"),
            heading, objective, new RectangleF(18, layout.HeroTop + 31, 318, 44), wrapped);
        using (var trough = new SolidBrush(Color.FromArgb(150, Mix(Color.Black, theme.Surface, .7)))) g.FillRectangle(trough, 18, layout.HeroTop + 81, 318, 3);
        if (fraction > 0) g.FillRectangle(objective, 18, layout.HeroTop + 81, Math.Max(1, (float)(318 * fraction)), 3);

        for (var index = 0; index < Math.Min(6, snapshot.Window.Count); index++)
        {
            var target = snapshot.Window[index];
            var y = layout.RowsTop + index * layout.RowHeight;
            var active = target.Id == snapshot.Current?.Id;
            var color = ObjectiveColor(target, theme);
            if (active)
            {
                using var shade = new SolidBrush(Color.FromArgb(110, Mix(theme.Primary, theme.Surface, .06)));
                g.FillRectangle(shade, 8, y, 344, layout.RowHeight - 2);
            }
            theme.Diamond(g, new(18, y + 14), active ? 4 : 3,
                target.Completed ? Color.FromArgb(105, color) : color);
            using var label = new SolidBrush(target.Completed ? Color.FromArgb(120, theme.Secondary) : active ? color : theme.Primary);
            g.DrawString(((target.StopIndex ?? index) + 1).ToString(), small, label, new RectangleF(28, y + 7, 25, 18), right);
            g.DrawString(TargetLabel(target), active ? bold : body, label, new RectangleF(64, y + 6, 270, 20), single);
            if (target.Completed) g.DrawLine(rule, 64, y + 16, Math.Min(330, 64 + g.MeasureString(TargetLabel(target), body).Width), y + 16);
        }

        DrawButton(LevelingOverlayAction.Back, UiText.T("Zurück"), progress.CanGoBack, false);
        DrawButton(LevelingOverlayAction.Next, UiText.T("Weiter"), progress.CanGoNext, true);
        DrawButton(LevelingOverlayAction.Cancel, UiText.T("Abbrechen"), true, false);
        g.DrawString(HotkeyHint(locked), small, muted, new RectangleF(12, layout.ControlsTop + 36, 336, 18), single);

        void DrawButton(LevelingOverlayAction action, string label, bool enabled, bool primary)
        {
            var bounds = ButtonBounds(action, snapshot.Window.Count, theme);
            var active = enabled && !locked;
            using var plate = new SolidBrush(active && pressed == action ? Color.FromArgb(220, Mix(theme.Gold, theme.Surface, .2))
                : active && hover == action ? Color.FromArgb(220, Mix(theme.Gold, theme.Surface, .12)) : Color.FromArgb(160, theme.Surface));
            using var edge = new Pen(primary && enabled ? theme.Gold : theme.Border);
            using var ink = new SolidBrush(!enabled ? Color.FromArgb(110, theme.Secondary) : primary ? theme.Gold : theme.Primary);
            g.FillRectangle(plate, bounds);
            g.DrawRectangle(edge, bounds.X + .5f, bounds.Y + .5f, bounds.Width - 1, bounds.Height - 1);
            g.DrawString(label, body, ink, bounds, center);
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
        var scale = (float)(_theme.IsLegacy ? Scale(_settings.Current.LevelingOverlayScale) : _renderScale);
        return new PointF(point.X / scale, point.Y / scale);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!InteractionAllowed) { SetInteractionEnabled(OverlayInteraction.IsAltHeld); return; }
        if (e.Button != MouseButtons.Left) return;
        var action = HitTest(LogicalPoint(e.Location), _locked, _snapshot, _theme);
        if (action == LevelingOverlayAction.Drag) _drag = e.Location;
        else if (action != LevelingOverlayAction.None)
        {
            _pressed = action;
            _pressedRouteId = _snapshot?.RouteId;
            _pressedCharacterId = _snapshot?.CharacterId;
            _pressedProgress = _snapshot?.Progress;
        }
        if (action != LevelingOverlayAction.None) Capture = true;
        InvalidatePanel();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!InteractionAllowed) { SetInteractionEnabled(OverlayInteraction.IsAltHeld); return; }
        if (_drag is { } from)
            Location = new Point(Location.X + e.X - from.X, Location.Y + e.Y - from.Y);
        _hover = HitTest(LogicalPoint(e.Location), _locked, _snapshot, _theme);
        Cursor = _hover == LevelingOverlayAction.Drag ? Cursors.SizeAll
            : _hover != LevelingOverlayAction.None ? Cursors.Hand : Cursors.Default;
        InvalidatePanel();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = LevelingOverlayAction.None;
        InvalidatePanel();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!InteractionAllowed) { SetInteractionEnabled(OverlayInteraction.IsAltHeld); return; }
        if (e.Button != MouseButtons.Left) return;
        var action = _pressed;
        var routeId = _pressedRouteId;
        var characterId = _pressedCharacterId;
        var progress = _pressedProgress;
        var released = HitTest(LogicalPoint(e.Location), _locked, _snapshot, _theme);
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
