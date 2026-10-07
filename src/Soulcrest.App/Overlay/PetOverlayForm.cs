using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Soulcrest.App.Services;

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
    private const int ToastHeight = 44;
    // A pet row: round portrait, name above a progress bar with the count inside (user request 2026-10-04).
    private const int RowHeight = 44;
    private readonly Dictionary<string, Image?> _icons = [];
    private readonly ProgressService _progress;
    private readonly TrackerService _tracker;
    private readonly SettingsService _settings;
    private readonly PetScanService _scan;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private bool _locked = true;
    private Point? _dragStart;
    // Scan mode: shown automatically while the pet window is scanned, moved off the card grid.
    private bool _scanMode;
    private bool _wasVisibleBeforeScan;
    private Point _locationBeforeScan;

    public PetOverlayForm(ProgressService progress, TrackerService tracker, SettingsService settings, PetScanService scan)
    {
        _progress = progress;
        _tracker = tracker;
        _settings = settings;
        _scan = scan;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(settings.Current.OverlayX, settings.Current.OverlayY);
        Size = new Size(360, 120);
        BackColor = Color.FromArgb(16, 20, 28);
        Opacity = 0.88;
        DoubleBuffered = true;
        _timer.Tick += (_, _) => RefreshLayout();
        _timer.Start();
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
        var style = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE);
        style = _locked ? style | NativeMethods.WS_EX_TRANSPARENT : style & ~(nint)NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, style);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!_locked && e.Button == MouseButtons.Left)
            _dragStart = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is { } start)
            Location = new Point(Location.X + e.X - start.X, Location.Y + e.Y - start.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragStart is null)
            return;
        _dragStart = null;
        _settings.Update(s =>
        {
            s.OverlayX = Location.X;
            s.OverlayY = Location.Y;
        });
    }

    private void RefreshLayout()
    {
        if (_scan.Running != _scanMode)
            SwitchScanMode(_scan.Running);
        if (_scanMode)
        {
            var missingLines = Math.Min(6, _scan.Page?.Missing.Count ?? 0);
            var scanHeight = 34 + 44 + missingLines * 20 + 30;
            if (Height != scanHeight)
                Height = scanHeight;
            Invalidate();
            return;
        }
        var focus = _progress.FocusPets().Take(8).Count();
        var toasts = RecentToasts().Count;
        var height = 34 + (focus == 0 ? 26 : focus * RowHeight) + toasts * (ToastHeight + 2) + 26;
        if (Height != height)
            Height = height;
        Invalidate();
    }

    /// <summary>Enters/leaves scan mode: show the overlay beside the card grid (not over it), restore afterwards.</summary>
    private void SwitchScanMode(bool scanning)
    {
        _scanMode = scanning;
        if (scanning)
        {
            _wasVisibleBeforeScan = Visible;
            _locationBeforeScan = Location;
            var area = _scan.CaptureRegion;
            // Right of the grid, below the auto-loot hint: an empty area of the pet window.
            Location = new Point(area.X + (int)(area.Width * 0.255), area.Y + (int)(area.Height * 0.62));
            Width = 380;
            if (!Visible)
                Show();
        }
        else
        {
            Location = _locationBeforeScan;
            Width = 360;
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
            g.FillRectangle(back, 6, y, Width - 12, 38);
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
        g.DrawString(UiText.T($"Gelesen gesamt: {page?.TotalPets ?? _scan.Entries.Count} Pets{collection}"), small, dim, 10, Height - 22);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var title = new Font("Segoe UI Semibold", 10f);
        using var body = new Font("Segoe UI", 9.5f);
        using var small = new Font("Segoe UI", 8.5f);
        using var accent = new SolidBrush(Color.FromArgb(94, 234, 212));
        using var dim = new SolidBrush(Color.FromArgb(150, 160, 175));
        using var white = new SolidBrush(Color.FromArgb(235, 240, 245));
        using var border = new Pen(_locked ? Color.FromArgb(60, 70, 90) : Color.FromArgb(250, 204, 21), _locked ? 1 : 2);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        if (_scanMode)
        {
            PaintScanMode(g, title, body, small);
            return;
        }

        g.DrawString(UiText.T("Soulcrest"), title, accent, 10, 7);
        var state = _tracker.Running ? "● Netzwerk" : "○ Erfassung aus";
        g.DrawString(UiText.T(_locked ? state : "entsperrt – ziehen, Strg+Alt+L sperrt"), small, _tracker.Running ? accent : dim, 90, 9);

        var y = 34;
        var lang = _settings.Current.NameLanguage;
        var focus = _progress.FocusPets().Take(8).ToList();
        if (focus.Count == 0)
        {
            g.DrawString(UiText.T("Keine Fokus-Pets – in der Pet-Liste ★ setzen"), small, dim, 10, y + 4);
            y += 26;
        }
        foreach (var pet in focus)
        {
            DrawPetRow(g, pet.Id, y, 255, null);
            y += RowHeight;
        }

        foreach (var toast in RecentToasts())
        {
            // Fades out over the last 10 seconds of its minute.
            var left = ToastDuration - (DateTimeOffset.Now - toast.Last);
            var alpha = (int)Math.Clamp(left.TotalSeconds / 10 * 255, 60, 255);
            using var toastBack = new SolidBrush(Color.FromArgb(alpha * 40 / 255, 94, 234, 212));
            g.FillRectangle(toastBack, 4, y, Width - 8, ToastHeight);
            DrawPetRow(g, toast.PetId, y, alpha, $"+{toast.Quantity}");
            y += ToastHeight + 2;
        }
        g.DrawString(UiText.T("Strg+Alt+P ein/aus · Strg+Alt+L verschieben"), small, dim, 10, Height - 20);
    }

    /// <summary>
    /// One pet: round portrait (genus-coloured ring) on the left, then the name with the level and
    /// <paramref name="badge"/> (e.g. "+2") above a progress bar; inside the bar the souls of this level.
    /// </summary>
    private void DrawPetRow(Graphics g, string petId, int y, int alpha, string? badge)
    {
        var pet = _progress.Catalog.Find(petId);
        var souls = _progress.Souls(petId);
        var thresholds = _progress.Thresholds;
        var isMax = thresholds.IsMax(souls);
        var level = thresholds.Level(souls);
        var needed = thresholds.NeededForNextLevel(souls);
        var inLevel = thresholds.SoulsInLevel(souls);
        var genusColor = GenusColor(pet?.Genus ?? "");
        Color A(Color c) => Color.FromArgb(alpha * c.A / 255, c);

        const int size = 34;
        var icon = new Rectangle(8, y + (RowHeight - size) / 2, size, size);
        using (var back = new SolidBrush(A(Color.FromArgb(30, 36, 48))))
            g.FillEllipse(back, icon);
        if (PetIcon(petId) is { } image)
        {
            var state = g.Save();
            using var clip = new GraphicsPath();
            clip.AddEllipse(icon);
            g.SetClip(clip);
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha / 255f });
            g.DrawImage(image, icon, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            g.Restore(state);
        }
        using (var ring = new Pen(A(genusColor), 2))
            g.DrawEllipse(ring, icon);

        var x = icon.Right + 8;
        var right = Width - 10;
        using var nameFont = new Font("Segoe UI Semibold", 9.5f);
        using var smallFont = new Font("Segoe UI", 8f);
        using var nameBrush = new SolidBrush(A(Color.FromArgb(235, 240, 245)));
        using var dimBrush = new SolidBrush(A(Color.FromArgb(150, 160, 175)));
        using var accentBrush = new SolidBrush(A(Color.FromArgb(94, 234, 212)));
        var levelText = isMax ? "MAX" : level == 0 ? UiText.T("gesperrt") : UiText.F("Stufe {0}", level);
        var levelSize = g.MeasureString(levelText, smallFont);
        g.DrawString(UiText.T(levelText), smallFont, isMax ? accentBrush : dimBrush, right - levelSize.Width, y + 4);
        var nameRight = right - levelSize.Width - 4;
        if (badge is not null)
        {
            var badgeSize = g.MeasureString(badge, nameFont);
            g.DrawString(badge, nameFont, accentBrush, nameRight - badgeSize.Width, y + 3);
            nameRight -= badgeSize.Width + 2;
        }
        var name = pet?.DisplayName(_settings.Current.NameLanguage) ?? petId;
        using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(name, nameFont, nameBrush, new RectangleF(x, y + 3, Math.Max(10, nameRight - x), nameFont.Height), format);

        var bar = new Rectangle(x, y + 22, right - x, 16);
        using (var trough = new SolidBrush(A(Color.FromArgb(45, 52, 66))))
            g.FillRectangle(trough, bar);
        var fill = isMax || needed == 0 ? 1.0 : Math.Clamp((double)inLevel / needed, 0, 1);
        if (fill > 0)
        {
            using var fillBrush = new LinearGradientBrush(bar, A(Color.FromArgb(200, genusColor)), A(genusColor), LinearGradientMode.Horizontal);
            g.FillRectangle(fillBrush, bar.X, bar.Y, Math.Max(1, (int)(bar.Width * fill)), bar.Height);
        }
        using (var frame = new Pen(A(Color.FromArgb(70, 80, 100))))
            g.DrawRectangle(frame, bar);
        var count = isMax ? "MAX" : $"{inLevel} / {needed}";
        using var countFont = new Font("Segoe UI Semibold", 8.5f);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var shadow = new SolidBrush(A(Color.FromArgb(200, 0, 0, 0)));
        var textRect = new RectangleF(bar.X, bar.Y, bar.Width, bar.Height);
        textRect.Offset(1, 1);
        g.DrawString(count, countFont, shadow, textRect, center);
        textRect.Offset(-1, -1);
        g.DrawString(count, countFont, nameBrush, textRect, center);
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
            _timer.Dispose();
        base.Dispose(disposing);
    }
}
