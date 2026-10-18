using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Soulcrest.Ocr.Shop;

namespace Soulcrest.App.Overlay;

/// <summary>Passive, always click-through soul counters integrated into the merchant card feet.</summary>
public sealed class MerchantOverlayForm : Form
{
    private readonly MerchantService _merchant;
    private readonly SettingsService _settings;
    private readonly GameCaptureService _capture;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Windows.Forms.Timer _visibility = new() { Interval = 100 };
    private Task? _worker;
    private int _queued;
    private Picture? _latestPicture;
    private volatile bool _closed;
    private MerchantSnapshot? _shownSnapshot;
    private Rectangle _shownBounds;
    private long _shownFrameAt;
    private IReadOnlyList<Badge> _shownBadges = [];
    private bool _inRecordings;
    internal static MerchantOverlayHealth Health { get; } = new();

    internal sealed record Badge(Rectangle Bounds, string Counter, bool IsFresh, bool IsMax, string FarmRegions = "",
        Rectangle PriceBounds = default, string PetCategory = "", string FarmSpawns = "",
        Rectangle CategoryBounds = default);
    private sealed record Picture(MerchantSnapshot Snapshot, Rectangle GameBounds, long FrameAt,
        IReadOnlyList<Badge> Badges);

    public MerchantOverlayForm(MerchantService merchant, SettingsService settings, GameCaptureService capture)
    {
        _merchant = merchant;
        _settings = settings;
        _capture = capture;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        _visibility.Tick += (_, _) => CheckVisibility();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT |
                NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
            return cp;
        }
    }

    public void Start()
    {
        if (_closed || _worker is not null) return;
        _ = Handle; // Only the UI thread creates this window; the worker never touches controls.
        _visibility.Start();
        _worker = Task.Run(() => Run(_stop.Token));
    }

    private async Task Run(CancellationToken token)
    {
        var stability = new MerchantLayoutStability();
        while (!token.IsCancellationRequested)
        {
            Picture? picture = null;
            var retainPicture = false;
            try
            {
                var snapshot = _merchant.Snapshot;
                var bounds = MonitorCapture.ForegroundGameWindowBounds();
                if (!_settings.Current.MerchantOverlayEnabled || snapshot is null || snapshot.Offers.Count != 10 || bounds is null)
                {
                    stability.Reset();
                    Health.Observe(!_settings.Current.MerchantOverlayEnabled ? "disabled" : snapshot is null ? "no-offers"
                        : snapshot.Offers.Count != 10 ? "offer-count" : "not-foreground", 0);
                }
                else
                {
                    // Read only the game's WGC surface. There is deliberately no screen/monitor fallback.
                    var scale = Math.Min(1.0, 1920.0 / bounds.Value.Width);
                    using var image = _capture.TryGrabGameWindowOnly(bounds.Value, scale);
                    var frameAt = GameCaptureService.GrabbedFrameAt;
                    if (image is null)
                    {
                        // A transient busy/missing WGC read is not a new image proving the shop is gone.
                        // Keep the previous picture only within its original one-second freshness limit.
                        retainPicture = stability.RetainAfterMissingCapture(snapshot, bounds.Value, Fresh(stability.ConfirmedFrameAt));
                        Health.Observe(retainPicture ? "capture-held" : "capture-missing", stability.ConfirmedFrameAt);
                    }
                    else if (!GameCaptureService.GrabbedGameWindowOnly || !Fresh(frameAt)
                        || !MerchantLayoutStability.MatchesCaptureSize(bounds.Value.Size, scale, new Size(image.Width, image.Height)))
                    {
                        stability.Reset();
                        Health.Observe(!GameCaptureService.GrabbedGameWindowOnly ? "not-window-capture" : !Fresh(frameAt)
                            ? "frame-stale" : "frame-size-mismatch", frameAt);
                    }
                    else
                    {
                        var preferred = stability.StableAnchors.Select(anchor => anchor with
                        {
                            PriceBounds = new OpenCvSharp.Rect((int)Math.Round(anchor.PriceBounds.X * scale),
                                (int)Math.Round(anchor.PriceBounds.Y * scale), (int)Math.Round(anchor.PriceBounds.Width * scale),
                                (int)Math.Round(anchor.PriceBounds.Height * scale)), Scale = anchor.Scale * scale
                        }).ToArray();
                        var detected = MerchantCardLocator.Find(image, preferred);
                        var anchors = detected.Select(anchor => anchor with
                        {
                            PriceBounds = new OpenCvSharp.Rect((int)Math.Round(anchor.PriceBounds.X / scale),
                                (int)Math.Round(anchor.PriceBounds.Y / scale), (int)Math.Round(anchor.PriceBounds.Width / scale),
                                (int)Math.Round(anchor.PriceBounds.Height / scale)), Scale = anchor.Scale / scale
                        }).ToArray();
                        var ready = stability.Update(snapshot, bounds.Value, frameAt, anchors);
                        Health.Observe(anchors.Length != 10 ? "layout-missing" : !ready ? "confirming-layout" : "layout-ready", frameAt);
                        if (ready && ReferenceEquals(snapshot, _merchant.Snapshot)
                            && MonitorCapture.ForegroundGameWindowBounds() == bounds)
                        {
                            var rows = _merchant.Rows;
                            if (ReferenceEquals(snapshot, _merchant.Snapshot))
                            {
                                var badges = BuildBadges(bounds.Value.Size, stability.StableAnchors, rows);
                                if (badges.Count > 0) picture = new(snapshot, bounds.Value, frameAt, badges);
                                else Health.Observe("no-pet-offers", frameAt);
                            }
                        }
                    }
                }
            }
            catch (Exception error) when (CaptureReadback.IsBusy(error))
            {
                var snapshot = _merchant.Snapshot;
                var bounds = MonitorCapture.ForegroundGameWindowBounds();
                retainPicture = _settings.Current.MerchantOverlayEnabled && snapshot is not null && bounds is not null
                    && stability.RetainAfterMissingCapture(snapshot, bounds.Value, Fresh(stability.ConfirmedFrameAt));
                if (!retainPicture) stability.Reset();
                Health.Observe(retainPicture ? "capture-held" : "capture-missing", stability.ConfirmedFrameAt);
            }
            catch (Exception error) when (error is InvalidOperationException or ExternalException or ArgumentException or
                OpenCvSharp.OpenCVException or IOException)
            {
                stability.Reset();
                Health.Observe("capture-error", 0);
                Trace.TraceWarning("Merchant overlay capture failed: {0}: {1}", error.GetType().Name, error.Message);
            }
            if (!retainPicture) Queue(picture);
            try { await Task.Delay(200, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static IReadOnlyList<Badge> BuildBadges(Size gameSize, IReadOnlyList<MerchantCardAnchor> anchors,
        IReadOnlyList<MerchantCounterRow> rows)
    {
        if (anchors.Count != 10 || anchors.Select(a => a.SlotIndex).Distinct().Count() != 10) return [];
        var badges = new List<Badge>();
        foreach (var row in rows)
        {
            var anchor = anchors.FirstOrDefault(a => a.SlotIndex == row.SlotIndex);
            if (anchor is null) return [];
            // User request 2026-10-13: continue the native hanging card below its price, rather
            // than add a dashboard badge. Anchor.Scale already includes UI scale and physical
            // capture resolution; neither monitor DPI nor game-window dimensions scale it again.
            if (!double.IsFinite(anchor.Scale) || anchor.Scale <= 0) return [];
            var hasRegions = !string.IsNullOrWhiteSpace(row.FarmRegions);
            var width = Math.Max(1, (int)Math.Round(212 * anchor.Scale));
            var footHeight = Math.Max(1, (int)Math.Round((hasRegions ? 52 : 36) * anchor.Scale));
            var gap = Math.Max(1, (int)Math.Round(anchor.Scale));
            var rectangle = new Rectangle(anchor.PriceBounds.X + (anchor.PriceBounds.Width - width) / 2,
                anchor.PriceBounds.Top, width, anchor.PriceBounds.Height + gap + footHeight);
            // User request 2026-10-18: the genus belongs centrally above the native pet name,
            // in the empty strip below its selection seal. All dimensions use the one anchor scale.
            var category = Rectangle.Empty;
            if (!string.IsNullOrWhiteSpace(row.PetCategory))
            {
                var categoryWidth = Math.Max(1, (int)Math.Round(96 * anchor.Scale));
                category = new Rectangle(anchor.PriceBounds.X + (anchor.PriceBounds.Width - categoryWidth) / 2,
                    anchor.PriceBounds.Top - (int)Math.Round(228 * anchor.Scale),
                    categoryWidth, Math.Max(1, (int)Math.Round(20 * anchor.Scale)));
            }
            var gameArea = new Rectangle(Point.Empty, gameSize);
            if (!gameArea.Contains(rectangle) || !category.IsEmpty && !gameArea.Contains(category)) return [];
            badges.Add(new(rectangle, row.Counter, row.IsFresh, row.IsMax, row.FarmRegions,
                new Rectangle(anchor.PriceBounds.X, anchor.PriceBounds.Y, anchor.PriceBounds.Width, anchor.PriceBounds.Height),
                row.PetCategory, row.FarmSpawns, category));
        }
        return badges.AsReadOnly();
    }

    internal static Rectangle SurfaceBounds(Badge badge) => badge.CategoryBounds.IsEmpty
        ? badge.Bounds : Rectangle.Union(badge.Bounds, badge.CategoryBounds);

    private static bool Fresh(long frameAt) => frameAt > 0 && frameAt <= Stopwatch.GetTimestamp() &&
        Stopwatch.GetElapsedTime(frameAt) <= TimeSpan.FromSeconds(1);

    private void Queue(Picture? picture)
    {
        Volatile.Write(ref _latestPicture, picture);
        if (_closed || Interlocked.Exchange(ref _queued, 1) != 0) return;
        try
        {
            BeginInvoke(() =>
            {
                try { if (!_closed && !IsDisposed) Present(Volatile.Read(ref _latestPicture)); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException)
                {
                    Health.Present("presentation-error");
                    Hide();
                    _shownSnapshot = null;
                    _shownBadges = [];
                    Trace.TraceWarning("Merchant overlay presentation failed: {0}", error.Message);
                }
                finally { Volatile.Write(ref _queued, 0); }
            });
        }
        catch (InvalidOperationException) { Volatile.Write(ref _queued, 0); }
    }

    private void CheckVisibility()
    {
        if (_shownSnapshot is null) return;
        if (_closed || Volatile.Read(ref _latestPicture) is null || !_settings.Current.MerchantOverlayEnabled || !Fresh(_shownFrameAt) ||
            !ReferenceEquals(_shownSnapshot, _merchant.Snapshot) ||
            MonitorCapture.ForegroundGameWindowBounds() != _shownBounds)
        {
            Health.Present("hidden");
            Hide();
            _shownSnapshot = null;
            _shownBadges = [];
        }
    }

    private void Present(Picture? picture)
    {
        if (picture is null || !ReferenceEquals(picture, Volatile.Read(ref _latestPicture)) ||
            !_settings.Current.MerchantOverlayEnabled || !Fresh(picture.FrameAt) ||
            !ReferenceEquals(picture.Snapshot, _merchant.Snapshot) ||
            MonitorCapture.ForegroundGameWindowBounds() != picture.GameBounds)
        {
            Health.Present("hidden");
            Hide();
            _shownSnapshot = null;
            _shownBadges = [];
            return;
        }
        _shownFrameAt = picture.FrameAt;
        if (_settings.Current.OverlaysInRecordings != _inRecordings)
        {
            _inRecordings = _settings.Current.OverlaysInRecordings;
            NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
        }
        if (ReferenceEquals(_shownSnapshot, picture.Snapshot) && _shownBounds == picture.GameBounds &&
            _shownBadges.SequenceEqual(picture.Badges) && Visible) return;

        var area = picture.Badges.Select(SurfaceBounds).Aggregate(Rectangle.Union);
        var header = new NativeMethods.BitmapInfoHeader
        { Size = 40, Width = area.Width, Height = -area.Height, Planes = 1, BitCount = 32 };
        var dib = NativeMethods.CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        if (dib == 0) throw new ExternalException("Merchant overlay surface unavailable.");
        try
        {
            using (var surface = new Bitmap(area.Width, area.Height, area.Width * 4, PixelFormat.Format32bppPArgb, bits))
            using (var graphics = Graphics.FromImage(surface))
            {
                // The native name/artwork between header and footer remains fully transparent.
                graphics.Clear(Color.Transparent);
                DrawBadges(graphics, area.Location, picture.Badges);
            }
            var screen = NativeMethods.GetDC(0);
            var memory = NativeMethods.CreateCompatibleDC(screen);
            var old = NativeMethods.SelectObject(memory, dib);
            try
            {
                var size = new NativeMethods.NativeSize(area.Width, area.Height);
                var source = new NativeMethods.NativePoint(0, 0);
                var target = new NativeMethods.NativePoint(picture.GameBounds.X + area.X, picture.GameBounds.Y + area.Y);
                var blend = new NativeMethods.BlendFunction
                { BlendOp = NativeMethods.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = NativeMethods.AC_SRC_ALPHA };
                if (!NativeMethods.UpdateLayeredWindow(Handle, screen, ref target, ref size, memory, ref source, 0, ref blend, NativeMethods.ULW_ALPHA))
                    throw new ExternalException($"Merchant surface update failed (Win32 {Marshal.GetLastWin32Error()}).");
            }
            finally
            {
                NativeMethods.SelectObject(memory, old);
                NativeMethods.DeleteDC(memory);
                NativeMethods.ReleaseDC(0, screen);
            }
        }
        finally { NativeMethods.DeleteObject(dib); }
        Health.Present("shown");
        _shownSnapshot = picture.Snapshot;
        _shownBounds = picture.GameBounds;
        _shownBadges = picture.Badges;
        if (!Visible) Show();
    }

    internal static void DrawBadges(Graphics graphics, Point origin, IReadOnlyList<Badge> badges)
    {
        var originalState = graphics.Save();
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // ClearType's opaque/subpixel fringe is unsuitable for a premultiplied-alpha window.
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter
        };
        foreach (var badge in badges)
        {
            var rect = new Rectangle(badge.Bounds.X - origin.X, badge.Bounds.Y - origin.Y,
                badge.Bounds.Width, badge.Bounds.Height);
            var scale = rect.Width / 212f;
            var hasRegions = !string.IsNullOrWhiteSpace(badge.FarmRegions);
            DrawCategory(graphics, origin, badge, format, scale);
            var farmText = string.IsNullOrWhiteSpace(badge.FarmSpawns) ? badge.FarmRegions : badge.FarmSpawns;
            // User request 2026-10-13: prioritize pets not yet unlocked and the farm
            // category Special (no source on the three maps), independently of shop tab.
            var needsUnlock = badge.Counter == "0/5";
            var special = badge.FarmRegions == "Special";
            var isMax = badge.IsMax;
            var state = graphics.Save();
            graphics.SetClip(rect, CombineMode.Intersect);
            var contentTop = (float)rect.Top;
            if (!badge.PriceBounds.IsEmpty)
            {
                var price = new RectangleF(badge.PriceBounds.X - origin.X, badge.PriceBounds.Y - origin.Y,
                    badge.PriceBounds.Width, badge.PriceBounds.Height);
                contentTop = price.Bottom + Math.Max(1, (int)Math.Round(scale));
                // Preserve the native price capsule, including antialiasing and
                // the discount arrow. Only the card material around/below it is replaced.
                price.Inflate(2 * scale + 1, 2 * scale + 1);
                using var protectedPrice = new GraphicsPath();
                var diameter = price.Height;
                protectedPrice.AddArc(price.Left, price.Top, diameter, diameter, 90, 180);
                protectedPrice.AddArc(price.Right - diameter, price.Top, diameter, diameter, 270, 180);
                protectedPrice.CloseFigure();
                graphics.SetClip(protectedPrice, CombineMode.Exclude);
            }

            // BuildBadges reserves 36 px for a counter alone or 52 px with the farm line.
            // Derive geometry from that physical space, so adding/removing header metadata or
            // using a legacy farm-label fallback never shifts the counter or the contour.
            var hasFarmFoot = rect.Bottom - contentTop > 44 * scale;

            // The old sloping edge and ornament sit inside this extension. Near-opaque neutral
            // charcoal conceals them; an open top joins the original sides without a badge seam.
            var left = rect.Left + .5f * scale;
            var right = rect.Right - .5f * scale;
            // The wider metadata line needs a straight shoulder before the short native tip;
            // the outside bounds and price capsule remain unchanged.
            var shoulder = rect.Bottom - (hasFarmFoot ? 18 : 24) * scale;
            var tip = new PointF(rect.Left + rect.Width / 2f, rect.Bottom - scale);
            PointF[] outline = [new(left, rect.Top), new(left, shoulder), tip,
                new(right, shoulder), new(right, rect.Top)];
            using var shape = new GraphicsPath();
            shape.AddPolygon(outline);
            // User request 2026-10-13: Special and MAX must catch the eye. Match the
            // shop's coloured item material while blending into its neutral price surround.
            var materialEnd = special ? Color.FromArgb(252, 75, 53, 26)
                : isMax ? badge.IsFresh ? Color.FromArgb(252, 30, 66, 41) : Color.FromArgb(252, 38, 48, 36)
                : Color.FromArgb(252, 44, 44, 41);
            using var background = new LinearGradientBrush(rect,
                Color.FromArgb(255, 54, 54, 52), materialEnd, 90f);
            graphics.FillPath(background, shape);
            var edgeColor = special ? Color.FromArgb(255, 217, 182, 106)
                : isMax ? badge.IsFresh ? Color.FromArgb(245, 160, 208, 153) : Color.FromArgb(220, 135, 160, 121)
                : needsUnlock && badge.IsFresh ? Color.FromArgb(210, 177, 160, 117)
                : needsUnlock ? Color.FromArgb(190, 155, 142, 111) : Color.FromArgb(200, 139, 132, 108);
            using var edge = new Pen(edgeColor, Math.Max(.5f, (special || isMax ? 1.3f : 1f) * scale));
            graphics.DrawLines(edge, outline);

            // Standalone MAX and the decorated Special line have distinct green/amber
            // accents. Stored maximum levels remain less saturated than fresh confirmation.
            var color = isMax ? badge.IsFresh ? Color.FromArgb(196, 242, 184) : Color.FromArgb(178, 197, 162)
                : needsUnlock ? badge.IsFresh ? Color.FromArgb(238, 211, 151) : Color.FromArgb(200, 182, 143)
                : !badge.IsFresh ? Color.FromArgb(191, 179, 151) : Color.FromArgb(224, 221, 209);
            var counter = badge.Counter.Equals("max", StringComparison.OrdinalIgnoreCase)
                ? "MAX" : badge.Counter.Replace("/", " / ", StringComparison.Ordinal);
            // User refinement 2026-10-18: souls are centered beneath the untouched price again.
            var counterArea = new RectangleF(rect.Left + 8 * scale, contentTop + (hasFarmFoot ? 0 : scale),
                rect.Width - 16 * scale, 22 * scale);
            var counterText = isMax ? "MAX" : UiText.T("Souls") + "  " + counter;
            using var font = FitFont(graphics, counterText, counterArea, (isMax ? 19 : 16) * scale,
                isMax || needsUnlock && badge.IsFresh ? FontStyle.Bold : FontStyle.Regular);
            DrawShadowedText(graphics, counterText, font, color, counterArea, format, scale);
            if (hasRegions)
            {
                var regionArea = new RectangleF(rect.Left + 8 * scale, contentTop + 21 * scale,
                    rect.Width - 16 * scale, 16 * scale);
                using var regionFont = FitFont(graphics, farmText, regionArea, (special ? 14 : 12) * scale,
                    special ? FontStyle.Bold : FontStyle.Regular);
                // Long three-map counts use the straight portion above the sloping card tip.
                // Keep the same font/footprint rather than let the last glyphs cross its contour.
                var regionWidth = graphics.MeasureString(farmText, regionFont,
                    int.MaxValue, StringFormat.GenericTypographic).Width;
                if (regionWidth > 176 * scale) regionArea.Offset(0, -3 * scale);
                var regionColor = special ? Color.FromArgb(255, 219, 143) : Color.FromArgb(181, 179, 165);
                DrawShadowedText(graphics, farmText, regionFont, regionColor,
                    regionArea, format, scale);
                if (special)
                {
                    var textWidth = graphics.MeasureString(farmText, regionFont,
                        int.MaxValue, StringFormat.GenericTypographic).Width;
                    var centerX = regionArea.Left + regionArea.Width / 2;
                    var centerY = regionArea.Top + regionArea.Height / 2;
                    var offset = textWidth / 2 + 9 * scale;
                    using var ornament = new SolidBrush(regionColor);
                    DrawDiamond(graphics, ornament, centerX - offset, centerY, 2.5f * scale);
                    DrawDiamond(graphics, ornament, centerX + offset, centerY, 2.5f * scale);
                }
            }
            graphics.Restore(state);
        }
        graphics.Restore(originalState);
    }

    private static void DrawCategory(Graphics graphics, Point origin, Badge badge, StringFormat format, float scale)
    {
        if (badge.CategoryBounds.IsEmpty || string.IsNullOrWhiteSpace(badge.PetCategory)) return;
        var area = new RectangleF(badge.CategoryBounds.X - origin.X, badge.CategoryBounds.Y - origin.Y,
            badge.CategoryBounds.Width, badge.CategoryBounds.Height);
        var state = graphics.Save();
        graphics.SetClip(area, CombineMode.Intersect);
        using var font = FitFont(graphics, badge.PetCategory, area, 13 * scale);
        var color = badge.PetCategory switch
        {
            "Cogni" => Color.FromArgb(174, 198, 219),
            "Fera" => Color.FromArgb(214, 175, 160),
            "Natura" => Color.FromArgb(180, 204, 163),
            "Varian" => Color.FromArgb(217, 197, 147),
            _ => Color.FromArgb(181, 179, 165),
        };
        DrawShadowedText(graphics, badge.PetCategory, font, color, area, format, scale);
        graphics.Restore(state);
    }

    private static void DrawDiamond(Graphics graphics, Brush ink, float x, float y, float radius)
    {
        PointF[] points = [new(x, y - radius), new(x + radius, y), new(x, y + radius), new(x - radius, y)];
        graphics.FillPolygon(ink, points);
    }

    private static Font FitFont(Graphics graphics, string text, RectangleF area, float size, FontStyle style = FontStyle.Regular)
    {
        using var measured = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
        var width = graphics.MeasureString(text, measured, int.MaxValue, StringFormat.GenericTypographic).Width;
        return new Font("Segoe UI", size * Math.Min(1f, area.Width / Math.Max(1, width)),
            style, GraphicsUnit.Pixel);
    }

    private static void DrawShadowedText(Graphics graphics, string text, Font font, Color color,
        RectangleF area, StringFormat format, float scale)
    {
        using var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        using var ink = new SolidBrush(color);
        var shadowArea = area;
        shadowArea.Offset(0, scale);
        graphics.DrawString(text, font, shadow, shadowArea, format);
        graphics.DrawString(text, font, ink, area, format);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _inRecordings = _settings.Current.OverlaysInRecordings;
        NativeMethods.SetCaptureVisibility(Handle, _inRecordings);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            _visibility.Dispose();
            _stop.Cancel();
            if (_worker is { } worker) _ = worker.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
            else _stop.Dispose();
        }
        base.Dispose(disposing);
    }
}
