using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>The same pixel renderer serves the live layered window, previews and offline visual QA.</summary>
internal static class LootOverlayRenderer
{
    internal const int Width = 400, DropsTop = 242, RowHeight = 29, FooterHeight = 29, RareDropHeight = 92;
    internal static readonly Color Blue = Color.FromArgb(79, 162, 246);
    internal static readonly Color Green = Color.FromArgb(89, 220, 147);
    internal static readonly Color Gold = Color.FromArgb(245, 199, 89);
    internal static Rectangle LockBounds => new(Width - 31, 8, 20, 20);
    internal static Rectangle DailyBar => new(16, 79, Width - 32, 24);
    internal static Rectangle DailyKinaIconBounds => new(DailyBar.Left + 5, DailyBar.Top + 4, 16, 16);
    internal static RectangleF DailyKinaNumberBounds => new(DailyBar.Left + 27, DailyBar.Top, DailyBar.Width - 32, DailyBar.Height);
    internal static Rectangle SessionKinaIconBounds => new(16, 151, 24, 24);
    internal static RectangleF SessionKinaNumberBounds => new(48, 147, 180, 33);
    internal static RectangleF SessionRateLabelBounds => new(236, 133, Width - 252, 18);
    internal static RectangleF SessionRateNumberBounds => new(236, 151, Width - 252, 27);
    internal static Rectangle DropIconBounds(int row) => new(16, DropsTop + row * RowHeight + 2, 24, 24);

    /// <summary>Only a fresh running session can explain a known part of today's confirmed total.</summary>
    internal static RectangleF SessionSegmentBounds(LootOverlaySnapshot snapshot)
    {
        if (!snapshot.SessionActive || !snapshot.TrackingAvailable || !snapshot.DailyConfirmed
            || snapshot.DailyLimitReached || snapshot.DailyMonsterKina is not { } total) return RectangleF.Empty;
        var daily = Math.Clamp(total, 0, LootOverlaySnapshot.DailyLimit);
        var contribution = Math.Clamp(snapshot.SessionKina, 0, daily);
        if (contribution == 0) return RectangleF.Empty;
        var beforeSession = daily - contribution;
        var bounds = DailyBar;
        var unit = (bounds.Width - 2) / (double)LootOverlaySnapshot.DailyLimit;
        return new(bounds.Left + 1 + (float)(beforeSession * unit), bounds.Top + 1,
            (float)(contribution * unit), bounds.Height - 2);
    }

    internal static Size Measure(LootOverlaySnapshot snapshot) => new(Width,
        DropsTop + RowsHeight(snapshot) + FooterHeight
        + RareNotices(snapshot).Count * RareDropHeight);

    private static int RowSize(LootOverlayDrop drop)
    {
        var size = RowHeight;
        return size;
    }
    private static int RowsHeight(LootOverlaySnapshot snapshot) => snapshot.Drops.Count == 0 ? RowHeight : snapshot.Drops.Sum(RowSize);

    internal static IReadOnlyList<RareDropNotice> RareNotices(LootOverlaySnapshot snapshot) => snapshot.RareDrops.Count > 0
        ? snapshot.RareDrops : snapshot.RareDrop is { } notice ? [notice] : [];

    internal static LootItemRarity DropRarity(LootOverlayDrop drop) => drop.Rarity == LootItemRarity.Unknown
        ? MonsterLootItemCatalog.Default.DropRarity(drop.Key) : drop.Rarity;

    internal static double NormalizedScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, .6, 2) : 1;
    internal static double NormalizedBackgroundOpacity(double opacity) => double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1;
    internal static string RateLabel(double? value) => value is { } rate && double.IsFinite(rate) && rate >= 0
        ? rate.ToString("N0", CultureInfo.GetCultureInfo(UiText.Language == "de" ? "de-DE" : "en-US")) : "—";

    internal static Bitmap RenderBitmap(LootOverlaySnapshot snapshot, string themeId, double scale = 1,
        double animationSeconds = 0, double? completionSeconds = null, bool locked = true, LootOverlayIconCache? icons = null,
        double backgroundOpacity = 1)
    {
        scale = NormalizedScale(scale);
        var size = Measure(snapshot);
        var image = new Bitmap(Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(size.Height * scale)), PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform((float)scale, (float)scale);
            Paint(graphics, snapshot, OverlayThemeDefinition.Resolve(themeId), animationSeconds, completionSeconds, locked, icons,
                backgroundOpacity);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    /// <summary>Composes the two actual production surfaces (panel and separate lock) for placement and visual QA.</summary>
    internal static Bitmap RenderPreview(LootOverlaySnapshot snapshot, string themeId, double scale = 1,
        double animationSeconds = 0, double? completionSeconds = null, bool locked = true, LootOverlayIconCache? icons = null,
        double backgroundOpacity = 1)
    {
        scale = NormalizedScale(scale);
        var image = RenderBitmap(snapshot, themeId, scale, animationSeconds, completionSeconds, locked, icons, backgroundOpacity);
        try
        {
            var bounds = LockBounds;
            var physical = Rectangle.Round(new RectangleF((float)(bounds.X * scale), (float)(bounds.Y * scale),
                (float)(bounds.Width * scale), (float)(bounds.Height * scale)));
            using var glyph = OverlayLockButton.RenderThemedGlyph(physical.Size, OverlayThemeDefinition.Resolve(themeId), locked, false, false);
            using var graphics = Graphics.FromImage(image);
            graphics.DrawImageUnscaled(glyph, physical.Location);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    internal static void Paint(Graphics graphics, LootOverlaySnapshot snapshot, OverlayThemeDefinition theme,
        double animationSeconds = 0, double? completionSeconds = null, bool locked = true, LootOverlayIconCache? icons = null,
        double backgroundOpacity = 1)
    {
        using var ownIcons = icons is null ? new LootOverlayIconCache() : null;
        var iconCache = icons ?? ownIcons!;
        var size = Measure(snapshot);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        // Only the theme's surface fill is attenuated. Text, icons, progress and rarity effects retain their own alpha.
        theme.Panel(graphics, new(Point.Empty, size), NormalizedBackgroundOpacity(backgroundOpacity));
        using var heading = theme.Heading(15);
        using var body = theme.Body(12, FontStyle.Bold);
        using var small = theme.Body(10.5f);
        using var amount = theme.Body(26, FontStyle.Bold);
        using var rateAmount = theme.Body(19, FontStyle.Bold);
        using var primary = new SolidBrush(theme.Primary);
        using var secondary = new SolidBrush(theme.Secondary);
        using var blue = new SolidBrush(Blue);
        using var green = new SolidBrush(Green);
        using var gold = new SolidBrush(Gold);
        using var edge = new Pen(Color.FromArgb(90, theme.Border));
        using var text = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat(text) { Alignment = StringAlignment.Far };
        using var center = new StringFormat(text) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        theme.Diamond(graphics, new(16, 17), 4, theme.Gold);
        graphics.DrawString(UiText.T("Beute"), heading, primary, new RectangleF(29, 6, Width - 70, 24), text);
        graphics.DrawLine(edge, 15, 35, Width - 15, 35);
        graphics.DrawString(UiText.T("Monster-Kina heute"), body, primary, new RectangleF(16, 45, Width - 32, 19), text);
        graphics.DrawString(UiText.T("Ungebundene Kina"), small, secondary, new RectangleF(16, 64, Width - 32, 15), text);

        PaintDailyBar(graphics, snapshot, body, center, snapshot.AnimationsEnabled ? animationSeconds : 0, iconCache);
        var dailyStatus = snapshot.DailyMonsterKina is not null && !snapshot.DailyConfirmed ? "Gespeicherter Tagesstand"
            : snapshot.DailyLimitReached ? "Tageslimit erreicht"
            : snapshot.DailyMonsterKina is null ? "Tagesstand unbekannt" : "";
        if (dailyStatus.Length > 0)
            graphics.DrawString(UiText.T(dailyStatus), small, snapshot.DailyLimitReached ? gold : secondary,
                new RectangleF(16, 107, Width - 32, 17), text);
        graphics.DrawString(UiText.T("Kina dieser Session"), small, secondary, new RectangleF(16, 133, Width - 32, 18), text);
        iconCache.Draw(graphics, snapshot.KinaIconPath, SessionKinaIconBounds);
        graphics.DrawString(Number(snapshot.SessionKina), amount, green, SessionKinaNumberBounds, text);
        graphics.DrawString(UiText.T("Kina/h"), small, secondary, SessionRateLabelBounds, right);
        graphics.DrawString(RateLabel(snapshot.SessionKinaPerHour), rateAmount, green, SessionRateNumberBounds, right);
        graphics.DrawString(UiText.T("Weitere Beute"), small, secondary, new RectangleF(16, 221, Width - 32, 18), text);
        graphics.DrawLine(edge, 16, 240, Width - 16, 240);

        var y = DropsTop;
        if (snapshot.Drops.Count == 0)
            graphics.DrawString(UiText.T("Noch keine Beute"), small, secondary, new RectangleF(16, y + 7, Width - 32, 19), text);
        for (var row = 0; row < snapshot.Drops.Count; row++)
        {
            var drop = snapshot.Drops[row];
            var rarity = DropRarity(drop);
            var rowSize = RowSize(drop);
            LootRarityVisuals.PaintBackground(graphics, new RectangleF(13, y + 1, Width - 26, rowSize - 2),
                rarity, animationSeconds, snapshot.SessionActive && snapshot.AnimationsEnabled);
            iconCache.Draw(graphics, drop.IconPath, new(16, y + 2, 24, 24));
            using var nameInk = new SolidBrush(LootRarityVisuals.NameColor(rarity) ?? theme.Primary);
            graphics.DrawString(drop.Name, body, nameInk, new RectangleF(48, y + 6, Width - 157, 20), text);
            graphics.DrawString("×" + Number(drop.Count), body, drop.Kind == LootOverlayDropKind.SoulCrystal ? blue : primary,
                new RectangleF(Width - 106, y + 6, 89, 20), right);
            y += rowSize;
        }
        y = DropsTop + RowsHeight(snapshot);
        graphics.DrawLine(edge, 16, y + 3, Width - 16, y + 3);
        var status = !locked ? "Schloss: sperren · Kopf ziehen" : !snapshot.TrackingAvailable ? "Monsterloot nicht verfügbar"
            : !snapshot.SessionActive ? "Keine laufende Session" : snapshot.LimitedKinaSession ? "Limited Kina Session" : "Schloss klicken zum Entsperren";
        graphics.DrawString(UiText.T(status), small, secondary, new RectangleF(16, y + 9, Width - 32, 17), text);
        var noticeY = y + FooterHeight;
        foreach (var notice in RareNotices(snapshot))
        {
            PaintRareDrop(graphics, notice, noticeY, snapshot.AnimationsEnabled, animationSeconds, theme, iconCache);
            noticeY += RareDropHeight;
        }
        if (snapshot.AnimationsEnabled && snapshot.DailyLimitReached && completionSeconds is >= 0 and < LootOverlayAnimation.CompletionDuration)
            PaintCompletion(graphics, completionSeconds.Value);
    }

    private static void PaintRareDrop(Graphics graphics, RareDropNotice notice, int y, bool animated, double seconds,
        OverlayThemeDefinition theme, LootOverlayIconCache icons)
    {
        var bounds = new RectangleF(13, y + 1, Width - 26, RareDropHeight - 10);
        LootRarityVisuals.PaintBackground(graphics, bounds, notice.Rarity, seconds, animated);
        var accent = LootRarityVisuals.Accent(notice.Rarity);
        using var border = new Pen(Color.FromArgb(110, accent));
        graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        icons.Draw(graphics, notice.IconPath, new(23, y + 24, 42, 42));
        using var label = theme.Body(9.5f, FontStyle.Bold);
        using var title = theme.Body(12, FontStyle.Bold);
        using var small = theme.Body(10);
        using var light = new SolidBrush(accent);
        using var text = new SolidBrush(theme.Primary);
        using var muted = new SolidBrush(theme.Secondary);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString(UiText.T("Besonderer Fund"), label, light, new RectangleF(76, y + 10, Width - 97, 17), format);
        graphics.DrawString(notice.Name + " ×" + Number(notice.Quantity), title, text, new RectangleF(76, y + 28, Width - 97, 23), format);
    }

    private static void PaintDailyBar(Graphics graphics, LootOverlaySnapshot snapshot, Font font, StringFormat center, double seconds,
        LootOverlayIconCache icons)
    {
        var bounds = DailyBar;
        using var track = new SolidBrush(Color.FromArgb(190, 11, 20, 30));
        using var border = new Pen(Color.FromArgb(125, snapshot.DailyLimitReached ? Gold : Blue));
        graphics.FillRectangle(track, bounds);
        if (snapshot.DailyMonsterKina is { } total)
        {
            var fraction = Math.Clamp(total / (double)LootOverlaySnapshot.DailyLimit, 0, 1);
            var fillWidth = (float)((bounds.Width - 2) * fraction);
            if (fillWidth > 0)
            {
                using var fill = new LinearGradientBrush(bounds,
                    snapshot.DailyLimitReached ? Color.FromArgb(160, 111, 34) : Color.FromArgb(25, 82, 148),
                    snapshot.DailyLimitReached ? Gold : Blue, LinearGradientMode.Horizontal);
                graphics.FillRectangle(fill, bounds.X + 1, bounds.Y + 1, fillWidth, bounds.Height - 2);
            }
            if (SessionSegmentBounds(snapshot) is { Width: > 0 } session) PaintSessionSegment(graphics, session, seconds);
        }
        else
        {
            // Unknown remains a patterned, unfilled track; it is never presented as zero progress.
            using var hatch = new HatchBrush(HatchStyle.ForwardDiagonal, Color.FromArgb(45, Blue), Color.Transparent);
            graphics.FillRectangle(hatch, bounds);
        }
        graphics.DrawRectangle(border, bounds.X + .5f, bounds.Y + .5f, bounds.Width - 1, bounds.Height - 1);
        icons.Draw(graphics, snapshot.KinaIconPath, DailyKinaIconBounds);
        var label = (snapshot.DailyMonsterKina is { } value ? Number(value) : "?")
            + " / " + Number(LootOverlaySnapshot.DailyLimit);
        using var shadow = new SolidBrush(Color.FromArgb(220, 2, 8, 14));
        using var ink = new SolidBrush(snapshot.DailyLimitReached ? Color.FromArgb(255, 249, 219) : Color.White);
        var labelBounds = DailyKinaNumberBounds;
        graphics.DrawString(label, font, shadow, new RectangleF(labelBounds.X + 1, labelBounds.Y + 1, labelBounds.Width, labelBounds.Height), center);
        graphics.DrawString(label, font, ink, labelBounds, center);
    }

    private static void PaintSessionSegment(Graphics graphics, RectangleF bounds, double seconds)
    {
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds, CombineMode.Intersect);
            using var fill = new LinearGradientBrush(bounds, Color.FromArgb(35, 123, 77), Green, LinearGradientMode.Horizontal);
            graphics.FillRectangle(fill, bounds);
            var pulse = .13 + .09 * Math.Sin(seconds * 3);
            using var pulseFill = new SolidBrush(Color.FromArgb((int)(pulse * 255), Green));
            graphics.FillRectangle(pulseFill, bounds);
            var cycle = ((seconds / 2.4) % 1 + 1) % 1;
            var beamWidth = Math.Clamp(bounds.Width * .4f, 8, 36);
            var x = bounds.Left - beamWidth + (bounds.Width + beamWidth * 2) * (float)cycle;
            using var sweep = new LinearGradientBrush(new RectangleF(x, bounds.Top, beamWidth, bounds.Height), Color.Transparent, Green, 0f);
            sweep.InterpolationColors = new ColorBlend
            {
                Positions = [0, .5f, 1], Colors = [Color.FromArgb(0, Green), Color.FromArgb(220, 183, 255, 210), Color.FromArgb(0, Green)]
            };
            graphics.FillRectangle(sweep, x, bounds.Top, beamWidth, bounds.Height);
        }
        finally { graphics.Restore(state); }
    }

    private static void PaintCompletion(Graphics graphics, double seconds)
    {
        var progress = seconds / LootOverlayAnimation.CompletionDuration;
        var alpha = (int)(185 * (1 - progress));
        var bounds = DailyBar;
        var sweepX = bounds.Left + (bounds.Width + 90) * (float)Math.Min(1, progress * 1.5) - 90;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds);
            // Keep the celebration inside its track: subtitles and the daily-status line stay unobscured.
            using var ring = new Pen(Color.FromArgb(alpha, 255, 239, 174), 2.5f);
            graphics.DrawRectangle(ring, bounds.X + 1.5f, bounds.Y + 1.5f, bounds.Width - 3, bounds.Height - 3);
            using var flare = new LinearGradientBrush(new RectangleF(sweepX, bounds.Y, 90, bounds.Height),
                Color.FromArgb(0, Gold), Color.FromArgb(alpha, 255, 255, 227), LinearGradientMode.Horizontal);
            flare.InterpolationColors = new ColorBlend
            {
                Positions = [0, .5f, 1], Colors = [Color.FromArgb(0, Gold), Color.FromArgb(alpha, 255, 255, 227), Color.FromArgb(0, Gold)]
            };
            graphics.FillRectangle(flare, sweepX, bounds.Y, 90, bounds.Height);
        }
        finally { graphics.Restore(state); }
    }

    private static string Number(long value) => Math.Max(0, value).ToString("N0",
        CultureInfo.GetCultureInfo(UiText.Language == "de" ? "de-DE" : "en-US"));
}

/// <summary>Only a newly observed limit transition celebrates; completed totals remain golden indefinitely.</summary>
internal sealed class LootOverlayAnimation
{
    internal const double CompletionDuration = 2.4;
    private bool _observed;
    private bool _complete;
    private double? _completedAt;
    private string? _scope;
    private long? _dailyTotal;
    private bool _dailyConfirmed;

    internal void Observe(LootOverlaySnapshot snapshot, double seconds)
    {
        var scopeChanged = !StringComparer.Ordinal.Equals(_scope, snapshot.CompletionScopeKey);
        if (_observed && !scopeChanged && !_complete && _dailyTotal is >= 0 and < LootOverlaySnapshot.DailyLimit
            && _dailyConfirmed && snapshot.DailyConfirmed && snapshot.DailyLimitReached) _completedAt = seconds;
        if (!snapshot.DailyLimitReached || scopeChanged || !snapshot.DailyConfirmed) _completedAt = null;
        _complete = snapshot.DailyLimitReached;
        _scope = snapshot.CompletionScopeKey;
        _dailyTotal = snapshot.DailyMonsterKina;
        _dailyConfirmed = snapshot.DailyConfirmed;
        _observed = true;
    }

    internal double? CompletionSeconds(double seconds) => _completedAt is { } start && seconds - start < CompletionDuration
        ? Math.Max(0, seconds - start) : null;
    internal bool IsAnimating(LootOverlaySnapshot snapshot, double seconds) =>
        snapshot.AnimationsEnabled && (LootOverlayRenderer.SessionSegmentBounds(snapshot).Width > 0
            || CompletionSeconds(seconds) is not null || LootOverlayRenderer.RareNotices(snapshot).Count > 0
            || snapshot.SessionActive && snapshot.Drops.Any(drop => MonsterLootItemCatalog.IsExceptional(LootOverlayRenderer.DropRarity(drop))));
}
