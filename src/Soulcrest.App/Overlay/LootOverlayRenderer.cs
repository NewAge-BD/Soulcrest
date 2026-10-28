using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>The same pixel renderer serves the live layered window, previews and offline visual QA.</summary>
internal static class LootOverlayRenderer
{
    internal const int Width = 480, DropsTop = 220, RowHeight = 27, FooterHeight = 27, RareDropHeight = 92;
    internal static readonly Color Blue = Color.FromArgb(79, 162, 246);
    internal static readonly Color Green = Color.FromArgb(89, 220, 147);
    internal static readonly Color Gold = Color.FromArgb(245, 199, 89);
    internal static Rectangle LockBounds => new(Width - 31, 8, 20, 20);
    internal static Rectangle DailyBar => new(16, 60, Width - 32, 24);
    internal static Rectangle DailyKinaIconBounds => new(DailyBar.Left + 5, DailyBar.Top + 4, 16, 16);
    internal static RectangleF DailyKinaNumberBounds => new(DailyBar.Left + 27, DailyBar.Top, DailyBar.Width - 32, DailyBar.Height);
    internal static Rectangle SessionKinaIconBounds => new(16, 129, 24, 24);
    internal static RectangleF SessionKinaNumberBounds => new(48, 124, 180, 34);
    internal static RectangleF SessionRateLabelBounds => new(256, 107, Width - 272, 18);
    internal static RectangleF SessionRateNumberBounds => new(256, 124, Width - 272, 34);
    internal static Rectangle DropIconBounds(int row) => new(16, DropsTop + row * RowHeight + 2, 22, 22);
    internal static Rectangle DropIconBoundsFor(LootOverlaySnapshot snapshot, int row) =>
        new(16, DropsTopFor(snapshot) + row * RowHeight + 2, 22, 22);
    internal static bool ShowPveBlock(LootOverlaySnapshot snapshot) => snapshot.HasPveActivity
        && snapshot.SessionKind is not (LootTrackerSessionKind.Abyss or LootTrackerSessionKind.Lifeskill);
    internal static bool ShowLifeskillBlock(LootOverlaySnapshot snapshot) => snapshot.HasLifeskillActivity
        || snapshot.SessionKind == LootTrackerSessionKind.Lifeskill;
    internal static bool ShowOpenWorldPointsBlock(LootOverlaySnapshot snapshot) => snapshot.SessionActive
        && snapshot.SessionKind == LootTrackerSessionKind.Mixed && snapshot.HasAbyssPointActivity;
    internal static int OpenWorldPointsTopFor(LootOverlaySnapshot snapshot) => 38 + (ShowPveBlock(snapshot) ? 156 : 0);
    internal static RectangleF OpenWorldPointsNumberBoundsFor(LootOverlaySnapshot snapshot) =>
        new(48, OpenWorldPointsTopFor(snapshot) + 24, 180, 34);
    internal static RectangleF OpenWorldPointsRateBoundsFor(LootOverlaySnapshot snapshot) =>
        new(256, OpenWorldPointsTopFor(snapshot) + 24, Width - 272, 34);
    internal static int LifeskillTopFor(LootOverlaySnapshot snapshot) => 38 +
        (snapshot.SessionKind == LootTrackerSessionKind.Abyss ? 194 : ShowPveBlock(snapshot) ? 156 : 0)
        + (ShowOpenWorldPointsBlock(snapshot) ? 76 : 0);
    internal static RectangleF HarvestNumberBoundsFor(LootOverlaySnapshot snapshot) => new(16, LifeskillTopFor(snapshot) + 24, 212, 34);
    internal static RectangleF HarvestRateBoundsFor(LootOverlaySnapshot snapshot) => new(256, LifeskillTopFor(snapshot) + 24, Width - 272, 34);
    internal static int DropsTopFor(LootOverlaySnapshot snapshot) => LifeskillTopFor(snapshot)
        + (ShowLifeskillBlock(snapshot) ? 100 : 0) + 26 + SummaryLineHeight(snapshot);
    private static int SummaryLineHeight(LootOverlaySnapshot snapshot)
    {
        return 0;
    }

    /// <summary>Only a fresh running session can explain a known part of today's confirmed total.</summary>
    internal static RectangleF SessionSegmentBounds(LootOverlaySnapshot snapshot)
    {
        if (!ShowPveBlock(snapshot) || !snapshot.SessionKinaKnown || !snapshot.SessionActive || !snapshot.TrackingAvailable || !snapshot.DailyConfirmed
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
        DropsTopFor(snapshot) + RowsHeight(snapshot) + FooterHeight
        + RareNotices(snapshot).Count * RareDropHeight);

    private static int RowsHeight(LootOverlaySnapshot snapshot) => Math.Max(1, snapshot.Drops.Count) * RowHeight;

    internal static IReadOnlyList<RareDropNotice> RareNotices(LootOverlaySnapshot snapshot)
    {
        IReadOnlyList<RareDropNotice> notices = snapshot.RareDrops.Count > 0 ? snapshot.RareDrops
            : snapshot.RareDrop is { } notice ? [notice] : [];
        return notices.Where(notice => MonsterLootItemCatalog.IsExceptional(notice.Rarity)).ToArray();
    }

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
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        theme.Panel(graphics, new(Point.Empty, Measure(snapshot)), NormalizedBackgroundOpacity(backgroundOpacity));
        using var heading = theme.Heading(14);
        using var body = theme.Body(10.5f, FontStyle.Bold);
        using var small = theme.Body(9.5f);
        using var amount = theme.Body(24, FontStyle.Bold);
        using var rateAmount = theme.Body(20, FontStyle.Bold);
        using var compactAmount = theme.Body(16, FontStyle.Bold);
        using var primary = new SolidBrush(theme.Primary);
        using var secondary = new SolidBrush(theme.Secondary);
        using var edge = new Pen(Color.FromArgb(90, theme.Border));
        using var rowGround = new SolidBrush(Color.FromArgb(50, 0, 0, 0));
        using var text = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat(text) { Alignment = StringAlignment.Far };
        using var center = new StringFormat(text) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var abyss = snapshot.SessionKind == LootTrackerSessionKind.Abyss;
        var currency = abyss || ShowPveBlock(snapshot);
        var dropsTop = DropsTopFor(snapshot);
        theme.Diamond(graphics, new(16, 17), 4, theme.Gold);
        graphics.DrawString(UiText.T("Beute") + (abyss ? " · Abyss" : ""), heading, primary,
            new RectangleF(29, 6, 245, 22), text);
        var elapsed = snapshot.SessionElapsed < TimeSpan.Zero ? TimeSpan.Zero : snapshot.SessionElapsed;
        var duration = ((long)elapsed.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + elapsed.ToString(@"mm\:ss");
        graphics.DrawString(duration, body, secondary, new RectangleF(285, 8, Width - 332, 20), right);
        graphics.DrawLine(edge, 15, 31, Width - 15, 31);

        if (currency)
        {
            graphics.DrawString(UiText.T(abyss ? "Kumulativ gefarmte AP" : "Monster-Kina heute"), body, primary,
                new RectangleF(16, 39, Width - 32, 18), text);
            if (abyss) PaintAbyssBar(graphics, snapshot, body, center);
            else PaintDailyBar(graphics, snapshot, body, center, snapshot.AnimationsEnabled ? animationSeconds : 0, iconCache);
            var status = abyss ? snapshot.FarmedAbyssPoints is null ? "AP-Farmstand unbekannt"
                : !snapshot.AbyssFarmConfirmed ? "Gespeicherter AP-Farmstand"
                : snapshot.AbyssPointCap is > 0 && snapshot.FarmedAbyssPoints >= snapshot.AbyssPointCap ? "AP-Cap erreicht" : ""
                : snapshot.DailyMonsterKina is not null && !snapshot.DailyConfirmed ? "Gespeicherter Tagesstand"
                : snapshot.DailyLimitReached ? "Tageslimit erreicht"
                : snapshot.DailyMonsterKina is null ? "Tagesstand unbekannt" : "";
            if (status.Length > 0)
                graphics.DrawString(UiText.T(status), small, secondary, new RectangleF(16, 88, Width - 32, 17), text);
            graphics.DrawString(UiText.T(abyss ? "Abyss Points dieser Session" : "Kina dieser Session"), small, secondary,
                new RectangleF(16, 107, 220, 18), text);
            iconCache.Draw(graphics, abyss ? snapshot.AbyssPointIconPath : snapshot.KinaIconPath, SessionKinaIconBounds);
            PaintCounter(graphics, abyss ? CountLabel(snapshot.SessionAbyssPoints)
                : snapshot.SessionKinaKnown ? Number(snapshot.SessionKina) : "—", amount, Green, SessionKinaNumberBounds, text);
            graphics.DrawString(UiText.T(abyss ? snapshot.AbyssPointRatePeriod == AbyssPointRatePeriod.Minute ? "AP/min" : "AP/h" : "Kina/h"),
                small, secondary, SessionRateLabelBounds, right);
            PaintCounter(graphics, RateLabel(abyss ? snapshot.SessionAbyssPointsRate : snapshot.SessionKinaPerHour),
                rateAmount, Green, SessionRateNumberBounds, right);
            graphics.DrawLine(edge, 240, 109, 240, 158);
            if (abyss)
            {
                graphics.DrawString(UiText.T("Kina dieser Session"), small, secondary, new RectangleF(16, 166, 215, 17), text);
                graphics.DrawString(UiText.T("Kina/h"), small, secondary, new RectangleF(256, 166, Width - 272, 17), right);
                iconCache.Draw(graphics, snapshot.KinaIconPath, new(16, 185, 18, 18));
                PaintCounter(graphics, snapshot.SessionKinaKnown ? Number(snapshot.SessionKina) : "—", compactAmount,
                    theme.Primary, new(41, 183, 187, 26), text);
                PaintCounter(graphics, RateLabel(snapshot.SessionKinaPerHour), compactAmount, theme.Primary,
                    new(256, 183, Width - 272, 26), right);
            }
        }
        if (ShowOpenWorldPointsBlock(snapshot))
        {
            var top = OpenWorldPointsTopFor(snapshot);
            if (currency) graphics.DrawLine(edge, 16, top, Width - 16, top);
            graphics.DrawString(UiText.T("Abyss Points dieser Session"), small, secondary,
                new RectangleF(16, top + 5, 220, 18), text);
            graphics.DrawString(UiText.T(snapshot.AbyssPointRatePeriod == AbyssPointRatePeriod.Minute ? "AP/min" : "AP/h"),
                small, secondary, new RectangleF(256, top + 5, Width - 272, 18), right);
            iconCache.Draw(graphics, snapshot.AbyssPointIconPath, new(16, top + 29, 24, 24));
            PaintCounter(graphics, CountLabel(snapshot.SessionAbyssPoints), amount, Green,
                OpenWorldPointsNumberBoundsFor(snapshot), text);
            PaintCounter(graphics, RateLabel(snapshot.SessionAbyssPointsRate), rateAmount, Green,
                OpenWorldPointsRateBoundsFor(snapshot), right);
            graphics.DrawLine(edge, 240, top + 7, 240, top + 58);
        }
        if (ShowLifeskillBlock(snapshot))
        {
            var top = LifeskillTopFor(snapshot);
            if (currency || ShowOpenWorldPointsBlock(snapshot)) graphics.DrawLine(edge, 16, top, Width - 16, top);
            graphics.DrawString(UiText.T("Erfolgreiche Ernten"), small, secondary, new RectangleF(16, top + 5, 220, 18), text);
            graphics.DrawString(UiText.T("Ernten/h"), small, secondary, new RectangleF(256, top + 5, Width - 272, 18), right);
            PaintCounter(graphics, CountLabel(snapshot.HarvestCount), amount, Green, HarvestNumberBoundsFor(snapshot), text);
            PaintCounter(graphics, RateLabel(snapshot.HarvestsPerHour), rateAmount, Green, HarvestRateBoundsFor(snapshot), right);
            graphics.DrawLine(edge, 240, top + 7, 240, top + 58);
            graphics.DrawString(UiText.T("Fehlversuche") + ": " + CountLabel(snapshot.FailedHarvestCount), small, secondary,
                new RectangleF(16, top + 64, 220, 19), text);
            graphics.DrawString(UiText.T("Itemmenge") + ": " + CountLabel(snapshot.ItemCount), small, secondary,
                new RectangleF(256, top + 64, Width - 272, 19), right);
        }
        graphics.DrawString(UiText.T("Weitere Beute"), small, secondary, new RectangleF(16, dropsTop - 21, Width - 32, 18), text);
        graphics.DrawLine(edge, 16, dropsTop - 2, Width - 16, dropsTop - 2);
        if (snapshot.Drops.Count == 0)
            graphics.DrawString(UiText.T("Noch keine Beute"), small, secondary, new RectangleF(16, dropsTop + 5, Width - 32, 19), text);
        for (var row = 0; row < snapshot.Drops.Count; row++)
        {
            var drop = snapshot.Drops[row];
            var rarity = DropRarity(drop);
            var y = dropsTop + row * RowHeight;
            var rowBounds = new RectangleF(13, y + 1, Width - 26, RowHeight - 2);
            graphics.FillRectangle(rowGround, rowBounds);
            LootRarityVisuals.PaintBackground(graphics, rowBounds, rarity, animationSeconds,
                snapshot.SessionActive && snapshot.AnimationsEnabled);
            iconCache.Draw(graphics, drop.IconPath, DropIconBoundsFor(snapshot, row));
            using var nameInk = new SolidBrush(LootRarityVisuals.NameColor(rarity) ?? theme.Primary);
            graphics.DrawString(drop.Name, body, nameInk, new RectangleF(46, y + 5, Width - 171, 20), text);
            PaintCounter(graphics, "×" + Number(drop.Count), body,
                drop.Kind == LootOverlayDropKind.SoulCrystal ? Blue : theme.Primary, new RectangleF(Width - 116, y + 4, 99, 21), right);
        }
        var footer = dropsTop + RowsHeight(snapshot);
        graphics.DrawLine(edge, 16, footer + 3, Width - 16, footer + 3);
        var footerText = !locked ? "Schloss: sperren · Kopf ziehen" : !snapshot.TrackingAvailable ? "Beuteaufzeichnung nicht verfügbar"
            : !snapshot.SessionActive ? "Keine laufende Session" : snapshot.LimitedKinaSession ? "Limited Kina Session"
            : "Schloss klicken zum Entsperren";
        graphics.DrawString(UiText.T(footerText), small, secondary, new RectangleF(16, footer + 8, Width - 32, 17), text);
        var noticeY = footer + FooterHeight;
        foreach (var notice in RareNotices(snapshot))
        {
            PaintRareDrop(graphics, notice, noticeY, snapshot.AnimationsEnabled, animationSeconds, theme, iconCache);
            noticeY += RareDropHeight;
        }
        if (snapshot.AnimationsEnabled && snapshot.DailyLimitReached && completionSeconds is >= 0 and < LootOverlayAnimation.CompletionDuration)
            PaintCompletion(graphics, completionSeconds.Value);
    }

    private static string CountLabel(long? value) => value is >= 0 ? Number(value.Value) : "—";

    private static void PaintCounter(Graphics graphics, string value, Font preferred, Color color, RectangleF bounds, StringFormat format)
    {
        var size = preferred.Size;
        if (graphics.MeasureString(value, preferred).Width > bounds.Width - 6)
            for (size -= .5f; size > 10; size -= .5f)
            {
                using var fitted = new Font(preferred.FontFamily, size, preferred.Style, GraphicsUnit.Pixel);
                if (graphics.MeasureString(value, fitted).Width <= bounds.Width - 6) break;
            }
        var saved = graphics.Save();
        try
        {
            graphics.SetClip(bounds, CombineMode.Intersect);
            using var path = new GraphicsPath();
            path.AddString(value, preferred.FontFamily, (int)preferred.Style, size, bounds, format);
            using var outline = new Pen(Color.FromArgb(color.A, 3, 6, 10), 3) { LineJoin = LineJoin.Round };
            using var ink = new SolidBrush(color);
            graphics.DrawPath(outline, path);
            graphics.FillPath(ink, path);
        }
        finally { graphics.Restore(saved); }
    }

    private static void PaintAbyssBar(Graphics graphics, LootOverlaySnapshot snapshot, Font font, StringFormat center)
    {
        var bounds = DailyBar;
        using var track = new SolidBrush(Color.FromArgb(190, 11, 20, 30));
        using var fill = new SolidBrush(Blue);
        using var border = new Pen(Color.FromArgb(125, Blue));
        using var ink = new SolidBrush(Color.White);
        graphics.FillRectangle(track, bounds);
        if (snapshot.AbyssPointCap is > 0 and var cap && snapshot.FarmedAbyssPoints is >= 0 and var farmed)
            graphics.FillRectangle(fill, bounds.X + 1, bounds.Y + 1, (float)((bounds.Width - 2) * Math.Clamp(farmed / (double)cap, 0, 1)), bounds.Height - 2);
        graphics.DrawRectangle(border, bounds);
        PaintCounter(graphics, (snapshot.FarmedAbyssPoints is { } total ? Number(total) : "—") + " / "
            + (snapshot.AbyssPointCap is { } limit ? Number(limit) : "—"), font, Color.White, bounds, center);
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
        PaintCounter(graphics, label, font, ink.Color, labelBounds, center);
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
            || snapshot.SessionActive && snapshot.Drops.Any(drop => LootRarityVisuals.HasAnimatedBackground(LootOverlayRenderer.DropRarity(drop))));
}
