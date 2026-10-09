using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;

namespace Soulcrest.App.Overlay;

/// <summary>
/// Compact in-game overlay: focus pets with souls/next target, toasts for new soul pickups,
/// tracker status. Click-through while locked, excluded from screen capture so the OCR never
/// reads it (display affinity, as Grindcrest's NativeOverlayForm).
/// </summary>
public sealed class PetOverlayForm : Form
{
    // A registered soul stays visible for a minute, with the pet's portrait (user request 2026-10-03).
    private static readonly TimeSpan ToastDuration = TimeSpan.FromMinutes(1);
    private const int RowHeight = 52;
    private static readonly Color PanelColor = Color.FromArgb(17, 24, 32);
    private static readonly Color CardColor = Color.FromArgb(25, 35, 46);
    private static readonly Color LineColor = Color.FromArgb(44, 59, 73);
    private static readonly Color AccentColor = Color.FromArgb(94, 234, 212);
    internal sealed record LootRow(string Name, string Genus, int Level, bool IsMax, int Current, int Needed,
        Image? Icon = null, int Quantity = 0, int Alpha = 255);
    private IReadOnlyList<LootRow> _focusRows = [];
    private IReadOnlyList<LootRow> _lootRows = [];
    private readonly Dictionary<string, Image?> _icons = [];
    private readonly ProgressService _progress;
    private readonly TrackerService _tracker;
    private readonly SettingsService _settings;
    private readonly PetScanService _scan;
    private readonly MapTargetsService _targets;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private bool _locked = true;
    private Point? _dragStart;
    // Size (user request 2026-10-07): everything is laid out for 360 px and drawn scaled; unlocked, any
    // corner can be dragged and the opposite one stays put.
    private const int BaseWidth = 360, ScanWidth = 380, CornerGrip = 16;
    private const double MinScale = 0.6, MaxScale = 2.5;
    private double _scale;
    private int _logicalHeight = LootLogicalHeight(0, 0);
    private Corner _resizing;
    private Point _resizeFrom;
    private Rectangle _resizeBounds;
    private double _resizeScale;

    [Flags]
    private enum Corner { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }
    // Scan mode: shown automatically while the pet window is scanned, moved off the card grid.
    private bool _scanMode;
    private bool _wasVisibleBeforeScan;
    private Point _locationBeforeScan;

    public PetOverlayForm(ProgressService progress, TrackerService tracker, SettingsService settings, PetScanService scan, MapTargetsService targets)
    {
        _progress = progress;
        _tracker = tracker;
        _settings = settings;
        _scan = scan;
        _targets = targets;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(settings.Current.OverlayX, settings.Current.OverlayY);
        _scale = Math.Clamp(settings.Current.OverlayScale, MinScale, MaxScale);
        ApplySize();
        BackColor = PanelColor;
        Opacity = 0.94;
        DoubleBuffered = true;
        _timer.Tick += (_, _) => RefreshLayout();
        _timer.Start();
        RefreshLayout();
    }

    public bool Locked => _locked;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetCaptureVisibility(Handle, _settings.Current.OverlaysInRecordings);
        ApplyLockState();
        _settings.Changed += ApplyCaptureVisibility;
    }

    private void ApplyCaptureVisibility()
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(() => { NativeMethods.SetCaptureVisibility(Handle, _settings.Current.OverlaysInRecordings); Invalidate(); });
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _settings.Changed -= ApplyCaptureVisibility;
        base.OnHandleDestroyed(e);
    }

    /// <summary>Unlocked: the overlay takes mouse input and can be dragged. Locked: click-through.</summary>
    public void ToggleLock()
    {
        _locked = !_locked;
        if (_locked) FinishDrag();
        ApplyLockState();
        Cursor = _locked ? Cursors.Default : Cursors.SizeAll;
        Invalidate();
    }

    private void ApplyLockState()
    {
        if (!IsHandleCreated) return;
        var style = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE);
        style = _locked ? style | NativeMethods.WS_EX_TRANSPARENT : style & ~(nint)NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, style);
    }

    private int LogicalWidth => _scanMode ? ScanWidth : BaseWidth;

    /// <summary>Window size from the layout size (360 × content height) and the scale.</summary>
    private void ApplySize()
    {
        var size = new Size((int)Math.Round(LogicalWidth * _scale), (int)Math.Round(_logicalHeight * _scale));
        if (Size != size)
            Size = size;
        UpdateWindowShape();
    }

    private void UpdateWindowShape()
    {
        using var path = RoundedRectangle(new RectangleF(0, 0, Width, Height), (float)(12 * _scale));
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    private void SetLogicalHeight(int height)
    {
        if (_logicalHeight == height)
            return;
        _logicalHeight = height;
        ApplySize();
    }

    private Corner CornerAt(Point p)
    {
        var corner = Corner.None;
        if (p.X < CornerGrip) corner |= Corner.Left;
        else if (p.X >= Width - CornerGrip) corner |= Corner.Right;
        if (p.Y < CornerGrip) corner |= Corner.Top;
        else if (p.Y >= Height - CornerGrip) corner |= Corner.Bottom;
        // Only the four corners resize; edges and the inside move the overlay.
        return (corner & (Corner.Left | Corner.Right)) != 0 && (corner & (Corner.Top | Corner.Bottom)) != 0 ? corner : Corner.None;
    }

    private static Cursor CursorFor(Corner corner) => corner switch
    {
        Corner.Left | Corner.Top or Corner.Right | Corner.Bottom => Cursors.SizeNWSE,
        Corner.Right | Corner.Top or Corner.Left | Corner.Bottom => Cursors.SizeNESW,
        _ => Cursors.SizeAll,
    };

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_locked || _scanMode || e.Button != MouseButtons.Left)
            return;
        _resizing = CornerAt(e.Location);
        if (_resizing == Corner.None)
            _dragStart = e.Location;
        else
        {
            _resizeFrom = Cursor.Position;
            _resizeBounds = Bounds;
            _resizeScale = _scale;
        }
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_resizing != Corner.None)
        {
            ResizeTo(Cursor.Position);
            return;
        }
        if (_dragStart is { } start)
        {
            Location = new Point(Location.X + e.X - start.X, Location.Y + e.Y - start.Y);
            return;
        }
        if (!_locked && !_scanMode)
            Cursor = CursorFor(CornerAt(e.Location));
    }

    /// <summary>Scales with the corner under the mouse; the opposite corner stays where it was.</summary>
    private void ResizeTo(Point mouse)
    {
        var dx = mouse.X - _resizeFrom.X;
        var dy = mouse.Y - _resizeFrom.Y;
        if (_resizing.HasFlag(Corner.Left)) dx = -dx;
        if (_resizing.HasFlag(Corner.Top)) dy = -dy;
        // The larger pull wins, so a diagonal or a straight drag both work.
        var byWidth = (_resizeBounds.Width + dx) / (double)_resizeBounds.Width;
        var byHeight = (_resizeBounds.Height + dy) / (double)_resizeBounds.Height;
        var factor = Math.Abs(byWidth - 1) >= Math.Abs(byHeight - 1) ? byWidth : byHeight;
        _scale = Math.Clamp(_resizeScale * factor, MinScale, MaxScale);
        var width = (int)Math.Round(LogicalWidth * _scale);
        var height = (int)Math.Round(_logicalHeight * _scale);
        var x = _resizing.HasFlag(Corner.Left) ? _resizeBounds.Right - width : _resizeBounds.X;
        var y = _resizing.HasFlag(Corner.Top) ? _resizeBounds.Bottom - height : _resizeBounds.Y;
        Bounds = new Rectangle(x, y, width, height);
        UpdateWindowShape();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        FinishDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) FinishDrag();
    }

    private void FinishDrag()
    {
        if (_dragStart is null && _resizing == Corner.None)
            return;
        _dragStart = null;
        _resizing = Corner.None;
        Capture = false;
        _settings.Update(s =>
        {
            s.OverlayX = Location.X;
            s.OverlayY = Location.Y;
            s.OverlayScale = Math.Round(_scale, 3);
        });
    }

    private void RefreshLayout()
    {
        if (_scan.Running != _scanMode)
            SwitchScanMode(_scan.Running);
        if (_scanMode)
        {
            var missingLines = Math.Min(6, _scan.Page?.Missing.Count ?? 0);
            SetLogicalHeight(34 + 44 + missingLines * 20 + 30);
            Invalidate();
            return;
        }
        var focus = SelectFocusTarget(_settings.Current, _targets.Targets, _targets.Progression);
        _focusRows = focus?.PetId is { } petId ? [BuildRow(petId, target: focus)] : [];
        _lootRows = RecentToasts().Select(toast =>
        {
            var left = ToastDuration - (DateTimeOffset.Now - toast.Last);
            var alpha = (int)Math.Clamp(left.TotalSeconds / 10 * 255, 60, 255);
            return BuildRow(toast.PetId, toast.Quantity, alpha);
        }).ToList();
        SetLogicalHeight(LootLogicalHeight(_focusRows.Count, _lootRows.Count));
        Invalidate();
    }

    /// <summary>Enters/leaves scan mode: show the overlay beside the card grid (not over it), restore afterwards.</summary>
    private void SwitchScanMode(bool scanning)
    {
        _scanMode = scanning;
        if (scanning)
        {
            FinishDrag();
            _wasVisibleBeforeScan = Visible;
            _locationBeforeScan = Location;
            var area = _scan.CaptureRegion;
            // Right of the grid, below the auto-loot hint: an empty area of the pet window.
            Location = new Point(area.X + (int)(area.Width * 0.255), area.Y + (int)(area.Height * 0.62));
            ApplySize();
            if (!Visible)
                Show();
        }
        else
        {
            Location = _locationBeforeScan;
            ApplySize();
            if (!_wasVisibleBeforeScan)
                Hide();
        }
    }

    private void PaintScanMode(Graphics g, Font title, Font body, Font small)
    {
        using var accent = new SolidBrush(Color.FromArgb(94, 234, 212));
        using var dim = new SolidBrush(Color.FromArgb(150, 160, 175));
        using var white = new SolidBrush(Color.FromArgb(235, 240, 245));
        using var warn = new SolidBrush(Color.FromArgb(250, 204, 21));
        using var ok = new SolidBrush(Color.FromArgb(34, 197, 94));
        using var big = new Font("Segoe UI Semibold", 12.5f);
        g.DrawString(UiText.T("Soulcrest · Pet-Scan"), title, accent, 10, 7);
        var page = _scan.Page;
        var y = 34;
        if (page is null || page.Cards == 0)
        {
            g.DrawString(UiText.T("Warte auf das Pet-Fenster …"), big, warn, 10, y + 6);
            y += 44;
        }
        else if (page.Ready)
        {
            using var back = new SolidBrush(Color.FromArgb(40, 34, 197, 94));
            g.FillRectangle(back, 6, y, LogicalWidth - 12, 38);
            g.DrawString(UiText.T($"✓ Alle {page.Cards} erkannt – weiterscrollen"), big, ok, 12, y + 7);
            y += 44;
        }
        else if (!page.Stable)
        {
            g.DrawString(UiText.T($"… Bild bewegt sich – kurz stillhalten ({page.Read}/{page.Cards})"), body, dim, 10, y + 10);
            y += 44;
        }
        else
        {
            g.DrawString(UiText.T($"{page.Read}/{page.Cards} erkannt – bitte anklicken:"), big, warn, 10, y + 6);
            y += 44;
            foreach (var line in page.Missing.Take(6))
            {
                g.DrawString("• " + UiText.T(line), body, white, 16, y);
                y += 20;
            }
        }
        var collection = page?.Collection is { } c ? UiText.F(" · Sammlung {0}/{1}", c.Owned, c.Total) : "";
        g.DrawString(UiText.T($"Gelesen gesamt: {page?.TotalPets ?? _scan.Entries.Count} Pets{collection}"), small, dim, 10, _logicalHeight - 22);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.ScaleTransform((float)_scale, (float)_scale); // laid out at 360 px, everything scales along
        if (_scanMode)
        {
            using var title = new Font("Segoe UI Semibold", 10f);
            using var body = new Font("Segoe UI", 9.5f);
            using var small = new Font("Segoe UI", 8.5f);
            using var border = new Pen(LineColor);
            using var frame = RoundedRectangle(new RectangleF(.5f, .5f, LogicalWidth - 1, _logicalHeight - 1), 12);
            g.DrawPath(border, frame);
            PaintScanMode(g, title, body, small);
            return;
        }
        PaintLootPanel(g, _focusRows, _lootRows, _tracker.Running, _locked);
    }

    /// <summary>
    /// Manual, currently active pet stops precede progression. A chained future stop waits until its
    /// predecessor is gone; independent targets keep their marking order, with the current map first.
    /// A shared display name alone is never enough to turn a non-pet mark into a focus pet.
    /// </summary>
    internal static MapTarget? SelectFocusTarget(AppSettings settings, IReadOnlyList<MapTarget> targets, MapTarget? progression)
    {
        var marked = targets.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var active = targets.Where(t => !string.IsNullOrWhiteSpace(t.PetId) && (t.After is null || !marked.Contains(t.After))).ToList();
        return active.FirstOrDefault(t => t.MapId == settings.LastMap) ?? active.FirstOrDefault()
            ?? (settings.ProgressionEnabled && !string.IsNullOrWhiteSpace(progression?.PetId) ? progression : null);
    }

    internal static int LootLogicalHeight(int focus, int drops) => focus == 0 && drops == 0 ? 54
        : 54 + (focus == 0 ? 0 : 24 + focus * RowHeight + 8) + (drops == 0 ? 0 : 24 + drops * RowHeight + 8) + 28;

    /// <summary>One layout for the live panel and offline previews; rows are a consistent progress snapshot.</summary>
    internal static void PaintLootPanel(Graphics g, IReadOnlyList<LootRow> focus, IReadOnlyList<LootRow> drops, bool running, bool locked)
    {
        var height = LootLogicalHeight(focus.Count, drops.Count);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var background = new SolidBrush(PanelColor);
        using var frame = RoundedRectangle(new RectangleF(.5f, .5f, BaseWidth - 1, height - 1), 12);
        using var border = new Pen(locked ? LineColor : Color.FromArgb(250, 204, 21));
        g.FillPath(background, frame);
        g.DrawPath(border, frame);
        using var title = new Font("Segoe UI Semibold", 14, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 11, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.FromArgb(235, 240, 245));
        using var dim = new SolidBrush(Color.FromArgb(161, 176, 190));
        using var accent = new SolidBrush(AccentColor);
        using var line = new Pen(LineColor);
        g.DrawString("Soulcrest", title, white, 14, 10);
        g.DrawString(UiText.T("Loot & Pet-Fortschritt"), small, dim, 14, 30);
        var state = UiText.T(running ? "Netzwerk" : "Erfassung aus");
        var stateWidth = g.MeasureString(state, small).Width + 28;
        var stateRect = new RectangleF(BaseWidth - 14 - stateWidth, 13, stateWidth, 24);
        using (var statePlate = new SolidBrush(running ? Color.FromArgb(24, 57, 58) : CardColor))
        using (var statePath = RoundedRectangle(stateRect, 12))
            g.FillPath(statePlate, statePath);
        g.FillEllipse(running ? accent : dim, stateRect.X + 9, stateRect.Y + 9, 5, 5);
        g.DrawString(state, small, running ? accent : dim, stateRect.X + 19, stateRect.Y + 5);
        if (focus.Count == 0 && drops.Count == 0)
        {
            if (!locked) DrawCornerGrips(g, BaseWidth, height);
            return;
        }
        g.DrawLine(line, 14, 53, BaseWidth - 14, 53);

        var y = 54;
        if (focus.Count > 0)
        {
            DrawSection(UiText.T("Fokus"), null);
            foreach (var row in focus)
            {
                DrawPetRow(g, row, y);
                y += RowHeight;
            }
            y += 8;
        }
        if (drops.Count > 0)
        {
            DrawSection(UiText.T("Letzte Beute"), $"+{drops.Sum(row => row.Quantity)}", highlight: true);
            foreach (var row in drops)
            {
                DrawPetRow(g, row, y);
                y += RowHeight;
            }
            y += 8;
        }
        g.DrawLine(line, 14, y, BaseWidth - 14, y);
        g.DrawString(UiText.T(locked ? "Strg+Alt+P ein/aus · Strg+Alt+L verschieben" : "Verschieben · Ecken skalieren · Strg+Alt+L sperrt"),
            small, dim, new RectangleF(14, y + 7, BaseWidth - 28, 18));
        if (!locked) DrawCornerGrips(g, BaseWidth, height);

        void DrawSection(string heading, string? metric, bool highlight = false)
        {
            g.DrawString(heading, small, dim, 14, y + 5);
            if (metric is not null)
            {
                var width = g.MeasureString(metric, small).Width;
                g.DrawString(metric, small, highlight ? accent : dim, BaseWidth - 14 - width, y + 5);
            }
            y += 24;
        }
    }

    /// <summary>Unlocked: yellow angles in the corners show where the overlay can be resized.</summary>
    private static void DrawCornerGrips(Graphics g, int width, int height)
    {
        using var pen = new Pen(Color.FromArgb(250, 204, 21), 3);
        const int length = 12;
        const int inset = 6;
        int right = width - 7, bottom = height - 7;
        g.DrawLines(pen, [new Point(inset, inset + length), new Point(inset, inset), new Point(inset + length, inset)]);
        g.DrawLines(pen, [new Point(right - length, inset), new Point(right, inset), new Point(right, inset + length)]);
        g.DrawLines(pen, [new Point(inset, bottom - length), new Point(inset, bottom), new Point(inset + length, bottom)]);
        g.DrawLines(pen, [new Point(right - length, bottom), new Point(right, bottom), new Point(right, bottom - length)]);
    }

    private LootRow BuildRow(string petId, int quantity = 0, int alpha = 255, MapTarget? target = null)
    {
        var pet = _progress.Catalog.Find(petId);
        var souls = _progress.Souls(petId);
        var thresholds = _progress.Thresholds;
        var monster = target is not null && _progress.MapDataDirectory is { } data
            ? MapPetMarkers.MonsterAt(data, target.MapId, petId, target.X, target.Y) : null;
        return new LootRow(RowName(_settings.Current, monster ?? MonsterName(petId, target?.MapId), pet, target?.Name ?? petId), pet?.Genus ?? "",
            thresholds.Level(souls), thresholds.IsMax(souls), thresholds.SoulsInLevel(souls),
            thresholds.NeededForNextLevel(souls), PetIcon(petId), quantity, alpha);
    }

    private static void DrawPetRow(Graphics g, LootRow row, int y)
    {
        Color A(Color color) => Color.FromArgb(row.Alpha * color.A / 255, color);
        var genus = GenusColor(row.Genus);
        using var card = new SolidBrush(A(CardColor));
        using var cardShape = RoundedRectangle(new RectangleF(10, y, BaseWidth - 20, RowHeight - 4), 8);
        g.FillPath(card, cardShape);
        using var edge = new Pen(A(row.Quantity > 0 ? Color.FromArgb(41, 89, 88) : LineColor));
        g.DrawPath(edge, cardShape);

        var icon = new Rectangle(18, y + 6, 36, 36);
        using (var back = new SolidBrush(A(PanelColor))) g.FillEllipse(back, icon);
        if (row.Icon is { } image)
        {
            var state = g.Save();
            using var clip = new GraphicsPath();
            clip.AddEllipse(icon);
            g.SetClip(clip);
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = row.Alpha / 255f });
            g.DrawImage(image, icon, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            g.Restore(state);
        }
        else
        {
            using var fallbackFont = new Font("Segoe UI", 16, GraphicsUnit.Pixel);
            using var fallbackBrush = new SolidBrush(A(genus));
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("◆", fallbackFont, fallbackBrush, icon, center);
        }
        using (var ring = new Pen(A(genus), 1.5f)) g.DrawEllipse(ring, icon);

        const int x = 64, right = BaseWidth - 20;
        using var nameFont = new Font("Segoe UI Semibold", 13, GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", 10.5f, GraphicsUnit.Pixel);
        using var countFont = new Font("Segoe UI Semibold", 11, GraphicsUnit.Pixel);
        using var white = new SolidBrush(A(Color.FromArgb(235, 240, 245)));
        using var dim = new SolidBrush(A(Color.FromArgb(161, 176, 190)));
        using var accent = new SolidBrush(A(AccentColor));
        var nameRight = (float)right;
        if (row.Quantity > 0)
        {
            var quantity = $"+{row.Quantity}";
            var width = g.MeasureString(quantity, countFont).Width + 12;
            var badge = new RectangleF(right - width, y + 5, width, 19);
            using var badgeBack = new SolidBrush(A(Color.FromArgb(24, 66, 64)));
            using var badgeShape = RoundedRectangle(badge, 6);
            g.FillPath(badgeBack, badgeShape);
            g.DrawString(quantity, countFont, accent, badge.X + 6, badge.Y + 2);
            nameRight = badge.X - 6;
        }
        using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(row.Name, nameFont, white, new RectangleF(x, y + 5, Math.Max(10, nameRight - x), 18), format);
        var levelText = row.IsMax ? "MAX" : row.Level == 0 ? UiText.T("gesperrt") : UiText.F("Stufe {0}", row.Level);
        g.DrawString(levelText, smallFont, row.IsMax ? accent : dim, x, y + 25);
        if (!row.IsMax)
        {
            var count = $"{row.Current} / {row.Needed}";
            var countWidth = g.MeasureString(count, countFont).Width;
            g.DrawString(count, countFont, white, right - countWidth, y + 24);
        }
        var bar = new RectangleF(x, y + 40, right - x, 4);
        using (var trough = new SolidBrush(A(Color.FromArgb(43, 55, 69))))
        using (var barShape = RoundedRectangle(bar, 2)) g.FillPath(trough, barShape);
        var fill = row.IsMax || row.Needed == 0 ? 1 : Math.Clamp((double)row.Current / row.Needed, 0, 1);
        if (fill > 0)
        {
            using var fillBrush = new SolidBrush(A(row.IsMax ? AccentColor : genus));
            using var fillShape = RoundedRectangle(new RectangleF(bar.X, bar.Y, Math.Max(1, (float)(bar.Width * fill)), bar.Height), 2);
            g.FillPath(fillBrush, fillShape);
        }
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// The monsters to hunt for a pet instead of its soul name (user request 2026-10-07: "Soft Breeze
    /// Spirit", not "Lesser Wind Spirit"): on the map shown last, else the faction's own map, else any map
    /// with the pet. The one with the most spawns there (Superior Wind Spirit: Whirlwind Spirit, 87, not the
    /// 6 Immortal Wind Spirit Guardians).
    /// </summary>
    internal static string RowName(AppSettings settings, MonsterName? monster, PetDefinition? pet, string petId) =>
        monster?.In(settings.LootNameLanguage) ?? pet?.DisplayName(settings.LootNameLanguage) ?? petId;

    private MonsterName? MonsterName(string petId, string? preferredMap = null)
    {
        if (_progress.MapDataDirectory is not { } mapdata)
            return null;
        var maps = new[] { preferredMap, _settings.Current.LastMap, Factions.HomeMap(_settings.Current.Faction) }.OfType<string>()
            .Concat(_progress.Maps.Select(m => m.Id)).Distinct(StringComparer.Ordinal);
        foreach (var map in maps)
        {
            var monsters = MapPetMarkers.MonstersOf(mapdata, map, petId);
            if (monsters.Count > 0)
                return monsters[0];
        }
        return null;
    }

    private sealed record Toast(string PetId, int Quantity, DateTimeOffset Last);

    /// <summary>Souls of the last minute, one row per pet (newest first, at most 5).</summary>
    private List<Toast> RecentToasts() => _progress.Recent()
        .Where(r => DateTimeOffset.Now - r.At < ToastDuration)
        .GroupBy(r => r.PetId)
        .Select(g => new Toast(g.Key, g.Sum(r => r.Quantity), g.Max(r => r.At)))
        .OrderByDescending(t => t.Last)
        .Take(5)
        .ToList();

    /// <summary>Portrait of a pet: map-data icon, else the portrait learned from the pet window.</summary>
    private Image? PetIcon(string petId)
    {
        if (_icons.TryGetValue(petId, out var cached))
            return cached;
        string? path = null;
        if (_progress.Catalog.Find(petId)?.Icon is { } icon && _progress.MapDataDirectory is { } mapdata)
            path = Path.Combine(mapdata, icon);
        var learned = Path.Combine(AppPaths.LearnedPortraitsDirectory, $"{petId}.png");
        if ((path is null || !File.Exists(path)) && File.Exists(learned))
            path = learned;
        Image? image = null;
        if (path is not null && File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            using var loaded = Image.FromStream(stream);
            image = new Bitmap(loaded); // detached from the file
        }
        _icons[petId] = image;
        return image;
    }

    internal static Color GenusColor(string genus) => genus switch
    {
        "cogni" => Color.FromArgb(59, 130, 246),
        "fera" => Color.FromArgb(239, 68, 68),
        "natura" => Color.FromArgb(34, 197, 94),
        "varian" => Color.FromArgb(234, 179, 8),
        _ => Color.FromArgb(103, 232, 249),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            foreach (var image in _icons.Values) image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
