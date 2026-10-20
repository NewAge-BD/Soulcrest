using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

[Flags]
internal enum OverlayPlacementGrip { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Width = 16 }

internal sealed record OverlayPlacementPreview(string Id, string Title, Bitmap Image, OverlayLayout Layout);

/// <summary>One active, alpha-composited placement surface. Only example images receive pointer input.</summary>
public sealed class OverlayPlacementForm : Form
{
    internal const int GridSize = 16, DimAlpha = 166;
    private readonly SettingsService _settings;
    private readonly OverlayPlacementService _placement;
    private readonly OverlayPreviewRenderer _renderer;
    private readonly List<OverlayPlacementPreview> _previews = [];
    private readonly List<(Rectangle Bounds, Action Invoke)> _buttons = [];
    private readonly System.Windows.Forms.Timer _monitorTimer = new() { Interval = 500 };
    private AppSettings _renderSettings = new();
    private Rectangle _monitorBounds, _layoutBounds;
    private string? _gestureId;
    private Point _gesturePointer;
    private OverlayLayout? _gestureLayout;
    private Rectangle _gestureBounds;
    private OverlayPlacementGrip _grip;
    private bool _finishingGesture, _closing, _refreshing, _gestureChanged;

    internal bool PlacementClosing => _closing;

    public OverlayPlacementForm(SettingsService settings, OverlayPlacementService placement, OverlayPreviewRenderer renderer)
    {
        _settings = settings;
        _placement = placement;
        _renderer = renderer;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Text = "Soulcrest · Place Mode";
        _placement.Changed += PlacementChanged;
        _monitorTimer.Tick += (_, _) => _placement.RefreshMonitors();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
            parameters.ExStyle &= ~(NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE);
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetCaptureVisibility(Handle, _settings.Current.OverlaysInRecordings);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshState();
        if (_closing || IsDisposed) return;
        _monitorTimer.Start();
        Activate();
    }

    private void PlacementChanged()
    {
        if (_closing || IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(RefreshState); }
            catch (InvalidOperationException) when (_closing || IsDisposed || Disposing) { }
        }
        else RefreshState();
    }

    private void RefreshState()
    {
        if (_closing || IsDisposed || Disposing || _refreshing) return;
        if (!_placement.Active || _placement.Monitor is not { } monitor)
        {
            Close();
            return;
        }
        _refreshing = true;
        try
        {
            _monitorBounds = monitor.Bounds;
            _layoutBounds = monitor.WorkingArea;
            if (Bounds != monitor.Bounds) Bounds = monitor.Bounds;
            if (_gestureId is null)
            {
                DisposePreviews();
                _renderSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings.Current))!;
                foreach (var descriptor in OverlayRegistry.All.Where(item => item.CanPlace))
                {
                    var layout = OverlayRegistry.NormalizeLayout(descriptor.Id, OverlayRegistry.ReadLayout(_settings.Current, descriptor.Id));
                    var maximumWidth = MaximumLogicalWidth(_layoutBounds, layout.Scale);
                    var image = _renderer.Render(descriptor.Id, _renderSettings, maximumWidth);
                    layout = FitLayout(layout, image.Size, _layoutBounds, descriptor.MinScale);
                    _previews.Add(new(descriptor.Id, descriptor.Title, image, layout));
                }
            }
            NativeMethods.SetCaptureVisibility(Handle, _settings.Current.OverlaysInRecordings);
            PresentFrame();
        }
        catch (Exception error) when (IsPlacementError(error)) { Fail(error); }
        finally { _refreshing = false; }
    }

    private void PresentFrame()
    {
        if (!IsHandleCreated || _closing || Width <= 0 || Height <= 0) return;
        try
        {
            using var frame = RenderFrame(ClientSize, _previews, _monitorBounds.Location,
                _placement.SelectedOverlayId, _placement.GridVisible);
            using (var graphics = Graphics.FromImage(frame)) DrawControls(graphics);
            Present(frame);
        }
        catch (Exception error) when (IsPlacementError(error)) { Fail(error); }
    }

    /// <summary>Per-pixel alpha darkens only the background; opaque example pixels retain their own brightness.</summary>
    internal static Bitmap RenderFrame(Size size, IReadOnlyList<OverlayPlacementPreview> previews,
        Point monitorOrigin, string selectedId, bool gridVisible)
    {
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(DimAlpha, 0, 0, 0));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        if (gridVisible)
        {
            using var fine = new Pen(Color.FromArgb(28, 225, 230, 240));
            using var major = new Pen(Color.FromArgb(55, 225, 230, 240));
            for (var x = 0; x < size.Width; x += GridSize) graphics.DrawLine(x % 128 == 0 ? major : fine, x, 0, x, size.Height);
            for (var y = 0; y < size.Height; y += GridSize) graphics.DrawLine(y % 128 == 0 ? major : fine, 0, y, size.Width, y);
        }
        foreach (var preview in previews.OrderBy(preview => preview.Id == selectedId))
        {
            var bounds = PreviewBounds(preview, monitorOrigin);
            graphics.DrawImage(preview.Image, bounds);
            var selected = preview.Id == selectedId;
            using var edge = new Pen(selected ? Color.FromArgb(255, 214, 166, 255) : Color.FromArgb(180, 148, 163, 184), selected ? 2 : 1);
            graphics.DrawRectangle(edge, bounds.X, bounds.Y, Math.Max(1, bounds.Width - 1), Math.Max(1, bounds.Height - 1));
            using var font = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(selected ? Color.FromArgb(237, 223, 255) : Color.FromArgb(228, 233, 240));
            using var label = new SolidBrush(Color.FromArgb(245, 23, 28, 39));
            var text = UiText.T(preview.Title);
            var labelSize = graphics.MeasureString(text, font);
            var labelBounds = new RectangleF(bounds.Left, Math.Max(0, bounds.Top - 21), Math.Min(size.Width - bounds.Left, labelSize.Width + 16), 21);
            graphics.FillRectangle(label, labelBounds);
            graphics.DrawString(text, font, ink, labelBounds.X + 8, labelBounds.Y + 3);
            if (!selected) continue;
            using var handle = new SolidBrush(Color.FromArgb(255, 209, 174, 255));
            foreach (var point in CornerPoints(bounds)) graphics.FillRectangle(handle, point.X - 5, point.Y - 5, 10, 10);
            if (preview.Layout.Width is not null)
            {
                using var widthHandle = new SolidBrush(Color.FromArgb(255, 125, 211, 252));
                graphics.FillRectangle(widthHandle, bounds.Right - 5, bounds.Top + bounds.Height / 2 - 10, 10, 20);
            }
        }
        return bitmap;
    }

    private void DrawControls(Graphics graphics)
    {
        _buttons.Clear();
        var width = Math.Min(260, Math.Max(180, ClientSize.Width - 32));
        var left = Math.Max(16, ClientSize.Width - width - 16);
        var top = 16;
        var selected = _previews.FirstOrDefault(item => item.Id == _placement.SelectedOverlayId);
        var outside = selected is not null && SavedOutsideMonitor(selected);
        var contentTop = top + 44 + (outside ? 26 : 0);
        var panelHeight = contentTop - top + 6 * 34 + 42 + 40 + (selected?.Layout.Width is null ? 0 : 40) + (outside ? 40 : 0) + 40;
        using var panel = new SolidBrush(Color.FromArgb(253, 17, 24, 35));
        using var edge = new Pen(Color.FromArgb(110, 167, 139, 250));
        graphics.FillRectangle(panel, left, top, width, panelHeight);
        graphics.DrawRectangle(edge, left, top, width, panelHeight);
        TextAt(graphics, "Place Mode", new(left + 14, top + 10, width - 28, 26), 17, true);
        if (outside) TextAt(graphics, UiText.T("Außerhalb des gewählten Monitors"), new(left + 14, top + 38, width - 28, 22), 10);
        var y = contentTop;
        foreach (var descriptor in OverlayRegistry.All.Where(item => item.CanPlace))
        {
            var id = descriptor.Id;
            Button(graphics, new(left + 10, y, width - 20, 30), UiText.T(descriptor.Title)
                + (_previews.FirstOrDefault(item => item.Id == id) is { } preview && SavedOutsideMonitor(preview) ? "  ↗" : ""),
                () => _placement.SelectedOverlayId = id, id == _placement.SelectedOverlayId);
            y += 34;
        }
        Button(graphics, new(left + 10, y + 4, (width - 26) / 2, 30), UiText.T("Raster"),
            () => _placement.GridVisible = !_placement.GridVisible, _placement.GridVisible);
        Button(graphics, new(left + 16 + (width - 26) / 2, y + 4, (width - 26) / 2, 30), UiText.T("Einrasten"),
            () => _placement.SnapToGrid = !_placement.SnapToGrid, _placement.SnapToGrid);
        y += 42;
        if (selected is not null)
        {
            TextAt(graphics, UiText.T("Größe") + "  " + Math.Round(selected.Layout.Scale * 100) + " %",
                new(left + 14, y + 5, width - 90, 24));
            Button(graphics, new(left + width - 78, y, 30, 30), "−", () => ChangeScale(-.05));
            Button(graphics, new(left + width - 42, y, 30, 30), "+", () => ChangeScale(.05));
            y += 40;
            if (selected.Layout.Width is { } contentWidth)
            {
                TextAt(graphics, UiText.T("Breite") + "  " + contentWidth + " px", new(left + 14, y + 5, width - 90, 24));
                Button(graphics, new(left + width - 78, y, 30, 30), "−", () => ChangeWidth(-16));
                Button(graphics, new(left + width - 42, y, 30, 30), "+", () => ChangeWidth(16));
                y += 40;
            }
        }
        if (outside)
        {
            Button(graphics, new(left + 10, y, width - 20, 30), UiText.T("Auf Monitor holen"), BringToMonitor);
            y += 40;
        }
        Button(graphics, new(left + 10, y, width - 20, 30), UiText.T("Fertig") + " · Esc", EndPlacement);
        var hintWidth = Math.Min(730, ClientSize.Width - 32);
        var hintY = Math.Max(0, ClientSize.Height - 53);
        graphics.FillRectangle(panel, 16, hintY, hintWidth, 37);
        TextAt(graphics, UiText.T("Ziehen zum Verschieben · Ecken zum Skalieren · Änderungen werden sofort gespeichert."),
            new(27, hintY + 10, hintWidth - 22, 24), 12);
    }

    private bool SavedOutsideMonitor(OverlayPlacementPreview preview)
    {
        var saved = OverlayRegistry.ReadLayout(_settings.Current, preview.Id);
        return !_monitorBounds.Contains(new Rectangle(new Point(saved.X, saved.Y), ScaledSize(preview.Image.Size, saved.Scale)));
    }

    private static void TextAt(Graphics graphics, string text, Rectangle bounds, float size = 12, bool bold = false)
    {
        using var font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(237, 240, 246));
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private void Button(Graphics graphics, Rectangle bounds, string text, Action action, bool active = false)
    {
        using var fill = new SolidBrush(active ? Color.FromArgb(255, 70, 50, 100) : Color.FromArgb(255, 34, 44, 59));
        using var edge = new Pen(active ? Color.FromArgb(167, 139, 250) : Color.FromArgb(67, 82, 102));
        graphics.FillRectangle(fill, bounds);
        graphics.DrawRectangle(edge, bounds);
        TextAt(graphics, text, new(bounds.X + 9, bounds.Y + 7, bounds.Width - 12, bounds.Height - 5));
        _buttons.Add((bounds, action));
    }

    private void BringToMonitor()
    {
        if (_previews.FirstOrDefault(item => item.Id == _placement.SelectedOverlayId) is not { } selected) return;
        // Explicit relocation saves the shown position, retaining independently chosen width and scale.
        var saved = OverlayRegistry.ReadLayout(_settings.Current, selected.Id);
        _placement.UpdateLayout(selected.Id, RelocateLayout(saved, selected.Layout));
    }

    internal static OverlayLayout RelocateLayout(OverlayLayout saved, OverlayLayout displayed) =>
        saved with { X = displayed.X, Y = displayed.Y };

    private void ChangeScale(double delta)
    {
        if (_previews.FirstOrDefault(item => item.Id == _placement.SelectedOverlayId) is not { } selected) return;
        var layout = OverlayRegistry.NormalizeLayout(selected.Id, selected.Layout with { Scale = selected.Layout.Scale + delta });
        var imageSize = selected.Image.Size;
        if (selected.Id == "alerts")
        {
            using var wrapped = _renderer.Render(selected.Id, _renderSettings, MaximumLogicalWidth(_layoutBounds, layout.Scale));
            imageSize = wrapped.Size;
        }
        var fitted = FitLayout(layout, imageSize, _layoutBounds, OverlayRegistry.Get(selected.Id).MinScale);
        if (fitted == selected.Layout) return;
        _placement.UpdateLayout(selected.Id, fitted);
    }

    private void ChangeWidth(int delta)
    {
        if (_previews.FirstOrDefault(item => item.Id == _placement.SelectedOverlayId) is not { Layout.Width: { } width } selected) return;
        var layout = OverlayRegistry.NormalizeLayout(selected.Id, selected.Layout with { Width = width + delta });
        if (layout.Width == width) return;
        _placement.UpdateLayout(selected.Id, layout);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _closing) return;
        var button = _buttons.LastOrDefault(button => button.Bounds.Contains(e.Location));
        if (button.Invoke is not null)
        {
            try { button.Invoke(); }
            catch (Exception error) when (IsPlacementError(error)) { Fail(error); }
            return;
        }
        var selected = _previews.FirstOrDefault(item => item.Id == _placement.SelectedOverlayId);
        var selectedGrip = selected is null ? OverlayPlacementGrip.None : GripAt(PreviewBounds(selected, _monitorBounds.Location), e.Location, selected.Layout.Width is not null);
        var hit = selectedGrip != OverlayPlacementGrip.None ? selected : _previews
            .OrderBy(item => item.Id == _placement.SelectedOverlayId).LastOrDefault(item => PreviewBounds(item, _monitorBounds.Location).Contains(e.Location));
        if (hit is null) return;
        _placement.SelectedOverlayId = hit.Id;
        if (_closing || IsDisposed) return;
        selected = _previews.First(item => item.Id == hit.Id);
        _gestureId = selected.Id;
        _gestureChanged = false;
        _gesturePointer = PointToScreen(e.Location);
        _gestureLayout = selected.Layout;
        _gestureBounds = new Rectangle(new Point(selected.Layout.X, selected.Layout.Y), ScaledSize(selected.Image.Size, selected.Layout.Scale));
        _grip = GripAt(PreviewBounds(selected, _monitorBounds.Location), e.Location, selected.Layout.Width is not null);
        Capture = true;
        PresentFrame();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        try { MovePointer(e); }
        catch (Exception error) when (IsPlacementError(error)) { Fail(error); }
    }

    private void MovePointer(MouseEventArgs e)
    {
        if (_gestureId is null || _gestureLayout is null)
        {
            var selected = _previews.FirstOrDefault(item => item.Id == _placement.SelectedOverlayId);
            var grip = selected is null ? OverlayPlacementGrip.None : GripAt(PreviewBounds(selected, _monitorBounds.Location), e.Location, selected.Layout.Width is not null);
            Cursor = CursorFor(grip);
            return;
        }
        var index = _previews.FindIndex(item => item.Id == _gestureId);
        if (index < 0) return;
        var item = _previews[index];
        var pointer = PointToScreen(e.Location);
        var delta = new Point(pointer.X - _gesturePointer.X, pointer.Y - _gesturePointer.Y);
        if (!_gestureChanged && delta == Point.Empty) return;
        OverlayLayout layout;
        if (_grip == OverlayPlacementGrip.Width)
        {
            var width = (_gestureLayout.Width ?? item.Image.Width) + (int)Math.Round(delta.X / _gestureLayout.Scale);
            if (_placement.SnapToGrid) width = Snap(width, 0);
            layout = OverlayRegistry.NormalizeLayout(item.Id, _gestureLayout with { Width = width });
            OverlayRegistry.WriteLayout(_renderSettings, item.Id, layout);
            var image = _renderer.Render(item.Id, _renderSettings);
            item.Image.Dispose();
            item = item with { Image = image };
        }
        else if (_grip == OverlayPlacementGrip.None)
            layout = DragLayout(_gestureLayout, delta, _monitorBounds, _placement.SnapToGrid);
        else
            layout = ResizeLayout(_gestureLayout, _gestureBounds, delta, _grip, OverlayRegistry.Get(item.Id), _monitorBounds, _placement.SnapToGrid);
        if (item.Id == "alerts" && _grip != OverlayPlacementGrip.None)
        {
            // Scaling changes the logical wrapping width before screen fitting, just like the live strip.
            var image = _renderer.Render(item.Id, _renderSettings, MaximumLogicalWidth(_layoutBounds, layout.Scale));
            item.Image.Dispose();
            item = item with { Image = image };
            var size = ScaledSize(image.Size, layout.Scale);
            if (_grip.HasFlag(OverlayPlacementGrip.Left)) layout = layout with { X = _gestureBounds.Right - size.Width };
            if (_grip.HasFlag(OverlayPlacementGrip.Top)) layout = layout with { Y = _gestureBounds.Bottom - size.Height };
        }
        layout = FitLayout(layout, item.Image.Size, _layoutBounds, OverlayRegistry.Get(item.Id).MinScale);
        _gestureChanged = ShouldSaveGesture(_gestureLayout, layout);
        _previews[index] = item with { Layout = layout };
        PresentFrame();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) FinishGesture();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) FinishGesture();
    }

    private void FinishGesture()
    {
        if (_gestureId is null || _finishingGesture) return;
        _finishingGesture = true;
        try
        {
            var item = _previews.FirstOrDefault(preview => preview.Id == _gestureId);
            var changed = _gestureChanged;
            _gestureChanged = false;
            _gestureId = null;
            _gestureLayout = null;
            _grip = OverlayPlacementGrip.None;
            Capture = false;
            if (changed && item is not null) _placement.UpdateLayout(item.Id, item.Layout);
        }
        catch (Exception error) when (IsPlacementError(error)) { Fail(error); }
        finally { _finishingGesture = false; }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape) { EndPlacement(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private void EndPlacement()
    {
        FinishGesture();
        _placement.Stop();
        if (!IsDisposed && !_closing) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closing)
        {
            _closing = true;
            _monitorTimer.Stop();
            FinishGesture();
            _placement.Stop();
        }
        base.OnFormClosing(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        base.OnDpiChanged(e);
        FinishGesture();
        _placement.RefreshMonitors();
        if (!_closing) RefreshState();
    }

    private static bool IsPlacementError(Exception error) => error is IOException or UnauthorizedAccessException
        or ExternalException or ArgumentException or InvalidOperationException or OutOfMemoryException;

    private void Fail(Exception error)
    {
        _gestureId = null;
        _gestureLayout = null;
        _gestureChanged = false;
        if (IsHandleCreated) Capture = false;
        _placement.Fail(UiText.T("Place Mode konnte nicht angezeigt oder gespeichert werden.") + " " + error.Message);
        if (!_closing && !IsDisposed) Close();
    }

    internal static bool ShouldSaveGesture(OverlayLayout original, OverlayLayout current) => original != current;

    internal static int Snap(int value, int origin) => origin + (int)Math.Round((value - origin) / (double)GridSize,
        MidpointRounding.AwayFromZero) * GridSize;

    internal static OverlayLayout DragLayout(OverlayLayout original, Point delta, Rectangle monitor, bool snap)
    {
        var x = original.X + delta.X;
        var y = original.Y + delta.Y;
        return original with { X = snap ? Snap(x, monitor.Left) : x, Y = snap ? Snap(y, monitor.Top) : y };
    }

    internal static OverlayLayout ResizeLayout(OverlayLayout original, Rectangle bounds, Point delta,
        OverlayPlacementGrip grip, OverlayDescriptor descriptor, Rectangle monitor, bool snap)
    {
        var movingX = (grip.HasFlag(OverlayPlacementGrip.Left) ? bounds.Left : bounds.Right) + delta.X;
        var movingY = (grip.HasFlag(OverlayPlacementGrip.Top) ? bounds.Top : bounds.Bottom) + delta.Y;
        if (snap) { movingX = Snap(movingX, monitor.Left); movingY = Snap(movingY, monitor.Top); }
        var width = grip.HasFlag(OverlayPlacementGrip.Left) ? bounds.Right - movingX : movingX - bounds.Left;
        var height = grip.HasFlag(OverlayPlacementGrip.Top) ? bounds.Bottom - movingY : movingY - bounds.Top;
        var byWidth = width / (double)Math.Max(1, bounds.Width);
        var byHeight = height / (double)Math.Max(1, bounds.Height);
        var factor = Math.Abs(byWidth - 1) >= Math.Abs(byHeight - 1) ? byWidth : byHeight;
        var scale = Math.Clamp(original.Scale * factor, descriptor.MinScale, descriptor.MaxScale);
        var newWidth = (int)Math.Ceiling(bounds.Width / original.Scale * scale);
        var newHeight = (int)Math.Ceiling(bounds.Height / original.Scale * scale);
        return original with
        {
            X = grip.HasFlag(OverlayPlacementGrip.Left) ? bounds.Right - newWidth : bounds.Left,
            Y = grip.HasFlag(OverlayPlacementGrip.Top) ? bounds.Bottom - newHeight : bounds.Top,
            Scale = scale
        };
    }

    internal static int MaximumLogicalWidth(Rectangle workingArea, double scale) =>
        Math.Max(160, (int)Math.Floor(AlertsOverlayForm.StripArea(workingArea, scale).Width / scale));

    internal static OverlayLayout FitLayout(OverlayLayout layout, Size logicalSize, Rectangle monitor, double minimumScale = .01)
    {
        var scale = Math.Min(layout.Scale, Math.Min(monitor.Width / (double)Math.Max(1, logicalSize.Width),
            monitor.Height / (double)Math.Max(1, logicalSize.Height)));
        // On a screen smaller than a panel's minimum, keep the configured limit. Selection stays
        // reachable through the list; fitting presentation must never create an unsavable scale.
        scale = Math.Max(minimumScale, scale);
        var size = ScaledSize(logicalSize, scale);
        return layout with
        {
            X = Math.Clamp(layout.X, monitor.Left, Math.Max(monitor.Left, monitor.Right - size.Width)),
            Y = Math.Clamp(layout.Y, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - size.Height)),
            Scale = scale
        };
    }

    private static Size ScaledSize(Size size, double scale) => new(Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
        Math.Max(1, (int)Math.Ceiling(size.Height * scale)));

    private static Rectangle PreviewBounds(OverlayPlacementPreview preview, Point origin) =>
        new(new Point(preview.Layout.X - origin.X, preview.Layout.Y - origin.Y), ScaledSize(preview.Image.Size, preview.Layout.Scale));

    private static Point[] CornerPoints(Rectangle bounds) =>
        [bounds.Location, new(bounds.Right, bounds.Top), new(bounds.Left, bounds.Bottom), new(bounds.Right, bounds.Bottom)];

    internal static OverlayPlacementGrip GripAt(Rectangle bounds, Point point, bool widthEnabled)
    {
        const int tolerance = 9;
        var horizontal = Math.Abs(point.X - bounds.Left) <= tolerance ? OverlayPlacementGrip.Left
            : Math.Abs(point.X - bounds.Right) <= tolerance ? OverlayPlacementGrip.Right : OverlayPlacementGrip.None;
        var vertical = Math.Abs(point.Y - bounds.Top) <= tolerance ? OverlayPlacementGrip.Top
            : Math.Abs(point.Y - bounds.Bottom) <= tolerance ? OverlayPlacementGrip.Bottom : OverlayPlacementGrip.None;
        if (horizontal != OverlayPlacementGrip.None && vertical != OverlayPlacementGrip.None) return horizontal | vertical;
        return widthEnabled && Math.Abs(point.X - bounds.Right) <= tolerance && Math.Abs(point.Y - bounds.Top - bounds.Height / 2) <= 13
            ? OverlayPlacementGrip.Width : OverlayPlacementGrip.None;
    }

    private static Cursor CursorFor(OverlayPlacementGrip grip) => grip switch
    {
        OverlayPlacementGrip.Left | OverlayPlacementGrip.Top or OverlayPlacementGrip.Right | OverlayPlacementGrip.Bottom => Cursors.SizeNWSE,
        OverlayPlacementGrip.Left | OverlayPlacementGrip.Bottom or OverlayPlacementGrip.Right | OverlayPlacementGrip.Top => Cursors.SizeNESW,
        OverlayPlacementGrip.Width => Cursors.SizeWE,
        _ => Cursors.SizeAll
    };

    private void Present(Bitmap frame)
    {
        var header = new NativeMethods.BitmapInfoHeader
        { Size = 40, Width = frame.Width, Height = -frame.Height, Planes = 1, BitCount = 32 };
        var dib = NativeMethods.CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        if (dib == 0) throw new ExternalException("Placement surface unavailable.");
        try
        {
            var data = frame.LockBits(new Rectangle(Point.Empty, frame.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                // Both bitmaps are top-down premultiplied ARGB, one physical-pixel coordinate system.
                var row = new byte[frame.Width * 4];
                for (var y = 0; y < frame.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    Marshal.Copy(row, 0, bits + y * row.Length, row.Length);
                }
            }
            finally { frame.UnlockBits(data); }
            var screen = NativeMethods.GetDC(0);
            var memory = NativeMethods.CreateCompatibleDC(screen);
            var old = NativeMethods.SelectObject(memory, dib);
            try
            {
                var target = new NativeMethods.NativePoint(Left, Top);
                var source = new NativeMethods.NativePoint(0, 0);
                var size = new NativeMethods.NativeSize(frame.Width, frame.Height);
                var blend = new NativeMethods.BlendFunction
                { BlendOp = NativeMethods.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = NativeMethods.AC_SRC_ALPHA };
                if (!NativeMethods.UpdateLayeredWindow(Handle, screen, ref target, ref size, memory, ref source, 0, ref blend, NativeMethods.ULW_ALPHA))
                    throw new ExternalException($"Placement surface update failed (Win32 {Marshal.GetLastWin32Error()}).");
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

    private void DisposePreviews()
    {
        foreach (var preview in _previews) preview.Image.Dispose();
        _previews.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _placement.Changed -= PlacementChanged;
            _monitorTimer.Stop();
            _monitorTimer.Dispose();
            DisposePreviews();
        }
        base.Dispose(disposing);
    }
}
