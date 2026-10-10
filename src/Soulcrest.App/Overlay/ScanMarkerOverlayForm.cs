using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>
/// Labels every card with its recognised value (user request 2026-10-08): green = read,
/// orange = value unreadable, yellow = pet unknown, red = conflicting values. Shown while the page
/// stands still, so the frames never lag behind a scrolling list. Per-pixel alpha, click-through; a small
/// owned input window allows Alt+left-click on a value to correct Soulcrest's result. In screen
/// recordings only with "Overlays in Aufnahmen sichtbar" (user request 2026-10-07) and while the game window
/// alone is captured: a monitor or GDI capture would let the scan read its own frames.
/// </summary>
public sealed class ScanMarkerOverlayForm : Form
{
    private static readonly Color ValueMissing = Color.FromArgb(249, 115, 22);
    private static readonly Color PetUnknown = Color.FromArgb(250, 204, 21);
    private static readonly Color ReadValue = Color.FromArgb(94, 234, 212);
    private static readonly Color ConflictingValue = Color.FromArgb(251, 113, 133);
    private readonly PetScanService _scan;
    private readonly SettingsService _settings;
    private readonly Capture.GameCaptureService _capture;
    private bool _inRecordings;
    private readonly System.Windows.Forms.Timer _interactionTimer = new() { Interval = 25 };
    private readonly ScanValueHotspotForm _hotspot;
    private IReadOnlyList<ScanMark> _renderedMarks = [];
    private ScanMark? _interactiveTarget;
    private bool _editing;

    // In recordings only when chosen and the scan cannot see the overlay (game window capture).
    private bool WantedInRecordings => _settings.Current.OverlaysInRecordings && _capture.CapturesGameWindowOnly;
    private bool _pending;
    private string _shown = "";

    public ScanMarkerOverlayForm(PetScanService scan, SettingsService settings, Capture.GameCaptureService capture)
    {
        _scan = scan;
        _settings = settings;
        _capture = capture;
        _hotspot = new ScanValueHotspotForm(OpenEditor, () => _interactiveTarget);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        _scan.Changed += Schedule;
        _settings.Changed += Schedule;
        _interactionTimer.Tick += (_, _) => UpdateInteraction();
        _interactionTimer.Start();
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
        if (!_settings.Current.PetScanOverlayEnabled) { SetInteractiveTarget(null); if (Visible) Hide(); _shown = ""; return; }
        if (_editing) return;
        if (WantedInRecordings != _inRecordings)
        {
            _inRecordings = WantedInRecordings;
            NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
        }
        var page = _scan.Page;
        var marks = _scan.Running && page is { Stable: true } ? page.Marks : [];
        var region = _scan.CaptureRegion;
        var state = string.Join(";", marks) + region + UiText.Language;
        _renderedMarks = marks;
        if (state == _shown)
            return;
        _shown = state;
        SetInteractiveTarget(null);
        if (marks.Count == 0)
        {
            if (Visible)
                Hide();
            return;
        }
        var area = marks.Select(m => m.Bounds).Aggregate(Rectangle.Union);
        area.Inflate(12, 12);
        using var bitmap = RenderMarks(marks, area);
        if (!Visible)
            Show();
        Present(bitmap, new Point(region.X + area.X, region.Y + area.Y));
    }

    internal static Bitmap RenderMarks(IReadOnlyList<ScanMark> marks, Rectangle area)
    {
        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            foreach (var mark in marks)
            {
                var color = mark.Conflict ? ConflictingValue : mark.ValueMissing ? ValueMissing : mark.PetUnknown || mark.PetUncertain ? PetUnknown : ReadValue;
                var box = mark.Bounds;
                box.Offset(-area.X, -area.Y);
                box.Inflate(4, 4);
                if (mark.Conflict || mark.ValueMissing || mark.PetUnknown || mark.PetUncertain)
                {
                    using var glow = new Pen(Color.FromArgb(90, color), 7);
                    using var line = new Pen(color, 3);
                    graphics.DrawRectangle(glow, box);
                    graphics.DrawRectangle(line, box);
                }
                using var font = new Font("Segoe UI", Math.Clamp(mark.Bounds.Width / 12f, 10f, 14f), FontStyle.Bold, GraphicsUnit.Pixel);
                using var detailFont = new Font("Segoe UI", Math.Clamp(mark.Bounds.Width / 14f, 9f, 12f), FontStyle.Regular, GraphicsUnit.Pixel);
                using var back = new SolidBrush(Color.FromArgb(235, 16, 20, 28));
                using var ink = new SolidBrush(color);
                using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                var detail = mark.Detail;
                var nameTag = new RectangleF(box.X + 4, box.Y + 4, mark.Bounds.Width, font.Height + 6 + (detail.Length > 0 ? detailFont.Height + 2 : 0));
                graphics.FillRectangle(back, nameTag);
                graphics.DrawString(mark.PetName.Length > 0 ? mark.PetName : UiText.T("Pet unbekannt"), font, ink,
                    new RectangleF(nameTag.X + 4, nameTag.Y + 2, nameTag.Width - 8, font.Height + 2), format);
                if (detail.Length > 0)
                    graphics.DrawString(detail, detailFont, ink,
                        new RectangleF(nameTag.X + 4, nameTag.Y + font.Height + 4, nameTag.Width - 8, detailFont.Height + 2), format);
                var valueTag = ValueTag(mark);
                valueTag.Offset(-area.X, -area.Y);
                graphics.FillRectangle(back, valueTag);
                format.Alignment = StringAlignment.Far;
                // Reserve space for the number first; a long translated source may be truncated.
                graphics.DrawString(mark.Value, font, ink,
                    new RectangleF(valueTag.X + 4, valueTag.Y + 2, valueTag.Width - 8, valueTag.Height - 4), format);
                var numberWidth = graphics.MeasureString(mark.Value, font).Width + 8;
                format.Alignment = StringAlignment.Near;
                graphics.DrawString(UiText.T(mark.Source), detailFont, ink,
                    new RectangleF(valueTag.X + 4, valueTag.Y + 3, Math.Max(0, valueTag.Width - numberWidth - 8), valueTag.Height - 4), format);
            }
        }
        return bitmap;
    }

    /// <summary>Immediately above the detected glyphs, right aligned; never cover the game's counter.</summary>
    internal static Rectangle ValueTag(ScanMark mark)
    {
        var card = mark.Bounds;
        var counter = mark.ProgressBounds ?? new Rectangle(card.X + card.Width / 2, card.Y + card.Height * 86 / 100, card.Width / 2, card.Height / 12);
        var height = (int)Math.Ceiling(Math.Clamp(card.Width / 12f, 10f, 14f) * 1.6f) + 4;
        var width = Math.Min(card.Width, Math.Max(100, card.Width * 85 / 100));
        var right = Math.Clamp(counter.Right + 4, card.Left + width, card.Right);
        return new Rectangle(right - width, counter.Top - height - 3, width, height);
    }

    internal static ScanMark? EditableAt(IReadOnlyList<ScanMark> marks, Point capturePoint, bool altHeld, bool stable) =>
        altHeld && stable ? marks.FirstOrDefault(m => m.EntryKey.Length > 0 && ValueTag(m).Contains(capturePoint)) : null;

    private void UpdateInteraction()
    {
        var page = _scan.Page;
        var point = Cursor.Position;
        var region = _scan.CaptureRegion;
        point.Offset(-region.X, -region.Y);
        var target = EditableAt(_renderedMarks, point, OverlayInteraction.IsAltHeld,
            Visible && !_editing && _scan.Running && page is { Stable: true } && ReferenceEquals(page.Marks, _renderedMarks));
        // Read only window metadata. Never intercept input intended for another application.
        if (target is not null && !UiResponsiveness.ForegroundProcess().Equals("AION2", StringComparison.OrdinalIgnoreCase)) target = null;
        SetInteractiveTarget(target);
    }

    private void SetInteractiveTarget(ScanMark? target)
    {
        if (_interactiveTarget == target) return;
        _hotspot.ResetInteraction();
        _interactiveTarget = target;
        if (target is null)
        {
            _hotspot.Hide();
            return;
        }
        // The visual layer stays click-through. Only this one value gets an input window while Alt
        // is held; names, borders, other cards and the original counter never become input surfaces.
        var bounds = ValueTag(target);
        bounds.Offset(_scan.CaptureRegion.Location);
        _hotspot.Bounds = bounds;
        if (!_hotspot.Visible) _hotspot.Show(this);
    }

    private void OpenEditor(ScanMark expected)
    {
        if (_editing) return;
        UpdateInteraction();
        if (_interactiveTarget is not { } mark || mark != expected) return;
        _editing = true;
        SetInteractiveTarget(null);
        try
        {
            using var editor = new ScanValueEditorForm(mark, text => _scan.CorrectValue(mark.EntryKey, text));
            editor.ShowDialog(this);
        }
        finally
        {
            _editing = false;
            _shown = "";
            Render();
        }
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
        {
            _scan.Changed -= Schedule;
            _settings.Changed -= Schedule;
            _interactionTimer.Dispose();
            _hotspot.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class ScanValueHotspotForm : Form
    {
        private readonly Action<ScanMark> _edit;
        private readonly Func<ScanMark?> _target;
        private ScanMark? _pressedTarget;

        internal ScanValueHotspotForm(Action<ScanMark> edit, Func<ScanMark?> target)
        {
            _edit = edit;
            _target = target;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            Opacity = .01;
            Cursor = Cursors.Hand;
        }

        internal void ResetInteraction()
        {
            _pressedTarget = null;
            Capture = false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ResetInteraction();
            if (e.Button != MouseButtons.Left || !OverlayInteraction.IsAltHeld) return;
            _pressedTarget = _target();
            Capture = _pressedTarget is not null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!OverlayInteraction.IsAltHeld) ResetInteraction();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var pressed = _pressedTarget;
            ResetInteraction();
            // A complete Alt-click must refer to the same still-displayed card on release.
            if (e.Button == MouseButtons.Left && OverlayInteraction.IsAltHeld
                && pressed is not null && pressed == _target() && ClientRectangle.Contains(e.Location))
                _edit(pressed);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) _pressedTarget = null;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) ResetInteraction();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.SetCaptureVisibility(Handle, false);
        }
    }
}
