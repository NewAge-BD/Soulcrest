using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Production rank tracker with point thresholds and signed current-session metrics.</summary>
internal static class AbyssRankOverlayRenderer
{
    internal const int Width = 480, Height = 178;
    internal const int MaximumKillFeedEntries = 10, KillFeedRowHeight = 27, KillFeedTop = Height + 30;
    internal const int KillFeedIconSize = 22, KillFeedOpponentLeft = 96;
    internal static Rectangle LockBounds => new(Width - 31, 8, 20, 20);
    internal static Rectangle CurrentIconBounds => new(8, 52, 48, 48);
    internal static Rectangle NextIconBounds => new(Width - 56, 52, 48, 48);
    internal static Rectangle ProgressBounds => new(58, 64, Width - 116, 24);
    internal static RectangleF RemainingBounds => new(ProgressBounds.X, 46, ProgressBounds.Width, 18);
    internal static RectangleF SessionNumberBounds => new(16, 124, Width / 2 - 28, 30);
    internal static RectangleF SessionRateBounds => new(Width / 2 + 12, 124, Width / 2 - 28, 30);
    internal static Color GainColor => Color.FromArgb(89, 220, 147);
    internal static Color LossColor => Color.FromArgb(245, 128, 128);
    internal static double NormalizedScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, .6, 2) : 1;
    internal static IReadOnlyList<AbyssKillFeedEntry> KillFeedEntries(AbyssRankOverlaySnapshot snapshot) =>
        !snapshot.CharacterBound || !snapshot.KillFeedEnabled ? [] : snapshot.KillFeed
            .Where(entry => entry is not null && (entry.Kind switch
            {
                AbyssKillFeedKind.Kill => snapshot.ShowKills,
                AbyssKillFeedKind.Death => snapshot.ShowDeaths,
                AbyssKillFeedKind.Assist => snapshot.ShowAssists,
                _ => false
            }))
            .OrderByDescending(entry => entry.At).ThenByDescending(entry => entry.Sequence)
            .Take(MaximumKillFeedEntries).ToArray();
    internal static Size Measure(AbyssRankOverlaySnapshot snapshot)
    {
        var count = KillFeedEntries(snapshot).Count;
        return new(Width, count == 0 ? Height : KillFeedTop + count * KillFeedRowHeight + 9);
    }

    internal static Bitmap RenderBitmap(AbyssRankOverlaySnapshot snapshot, string themeId, double scale = 1,
        bool locked = true, LootOverlayIconCache? icons = null, string? language = null,
        AbyssRankMotionFrame? frame = null, AbyssRankMorphCache? morphs = null,
        double killFeedSeconds = 0, bool animateKillFeed = false)
    {
        scale = NormalizedScale(scale);
        var size = Measure(snapshot);
        var image = new Bitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform((float)scale, (float)scale);
            Paint(graphics, snapshot, OverlayThemeDefinition.Resolve(themeId), locked, icons, language ?? UiText.Language, frame, morphs,
                killFeedSeconds, animateKillFeed);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    internal static Bitmap RenderPreview(AbyssRankOverlaySnapshot snapshot, string themeId, double scale = 1,
        bool locked = true, LootOverlayIconCache? icons = null, string? language = null,
        AbyssRankMotionFrame? frame = null, AbyssRankMorphCache? morphs = null,
        double killFeedSeconds = 0, bool animateKillFeed = false)
    {
        var image = RenderBitmap(snapshot, themeId, scale, locked, icons, language, frame, morphs, killFeedSeconds, animateKillFeed);
        try
        {
            scale = NormalizedScale(scale);
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

    private static void Paint(Graphics graphics, AbyssRankOverlaySnapshot snapshot, OverlayThemeDefinition theme,
        bool locked, LootOverlayIconCache? icons, string language, AbyssRankMotionFrame? frame, AbyssRankMorphCache? morphs,
        double killFeedSeconds, bool animateKillFeed)
    {
        using var ownIcons = icons is null ? new LootOverlayIconCache() : null;
        var cache = icons ?? ownIcons!;
        var progress = AbyssRankPresentation.Project(snapshot.CharacterBound ? snapshot.RankPoints : null);
        if (!snapshot.CharacterBound || !progress.HasPoints || !ValidFrame(frame)) frame = null;
        var stages = AbyssRankCatalog.Default.Stages;
        var currentStage = frame is null ? progress.Current : stages[frame.DisplayCurrentStageIndex];
        var nextStage = frame is null ? progress.Next : frame.DisplayNextStageIndex is { } nextIndex ? stages[nextIndex] : null;
        var maximum = frame is null ? progress.IsMaximum : frame.DisplayCurrentStageIndex == stages.Count - 1;
        using var labelsInk = new SolidBrush(WithOpacity(theme.Primary, frame?.LabelOpacity ?? 1));
        using var nextLabelInk = new SolidBrush(WithOpacity(theme.Primary, frame?.NextOpacity ?? 1));
        using var remainingInk = new SolidBrush(WithOpacity(Color.White, frame?.RemainingOpacity ?? 1));
        using var totalInk = new SolidBrush(WithOpacity(Color.White, frame?.TotalOpacity ?? 1));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        theme.Panel(graphics, new(Point.Empty, Measure(snapshot)));
        using var heading = theme.Heading(14);
        using var body = theme.Body(10.5f, FontStyle.Bold);
        using var small = theme.Body(9.5f);
        using var totalPointsFont = theme.Body(18, FontStyle.Bold);
        using var remainingPointsFont = theme.Body(13.5f, FontStyle.Bold);
        using var amount = theme.Body(24, FontStyle.Bold);
        using var rateAmount = theme.Body(20, FontStyle.Bold);
        using var primary = new SolidBrush(theme.Primary);
        using var secondary = new SolidBrush(theme.Secondary);
        using var gold = new SolidBrush(theme.Gold);
        using var green = new SolidBrush(GainColor);
        using var red = new SolidBrush(LossColor);
        using var edge = new Pen(Color.FromArgb(90, theme.Border));
        using var background = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        using var text = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var center = new StringFormat(text) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var right = new StringFormat(text) { Alignment = StringAlignment.Far };
        var culture = CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
        string Number(long value) => value.ToString("N0", culture);

        theme.Diamond(graphics, new(16, 17), 4, theme.Gold);
        graphics.DrawString(UiText.T("Abyss Rank Tracker"), heading, primary, new RectangleF(29, 6, Width - 70, 22), text);
        graphics.DrawLine(edge, 15, 31, Width - 15, 31);
        graphics.DrawString(currentStage?.Name(language) ?? "—", body, labelsInk, new RectangleF(14, 33, Width / 2 - 24, 16), text);
        graphics.DrawString(nextStage?.Name(language) ?? (maximum ? UiText.T("Höchste Punkte-Stufe") : "—"),
            body, nextLabelInk, new RectangleF(Width / 2 + 10, 33, Width / 2 - 24, 16), right);
        if (progress.HasPoints && frame is null)
        {
            cache.Draw(graphics, progress.Current?.IconAsset, CurrentIconBounds);
            cache.Draw(graphics, progress.Next?.IconAsset, NextIconBounds);
        }
        graphics.FillRectangle(background, ProgressBounds);
        if (progress.HasPoints)
        {
            var fill = new RectangleF(ProgressBounds.X + 1, ProgressBounds.Y + 1,
                (float)((ProgressBounds.Width - 2) * Math.Clamp(frame?.BarProgress ?? progress.Progress, 0, 1)), ProgressBounds.Height - 2);
            if (fill.Width > 0) graphics.FillRectangle(gold, fill);
        }
        graphics.DrawRectangle(edge, ProgressBounds);
        var displayedProgress = frame is null ? progress : AbyssRankPresentation.Project(frame.TotalPoints);
        var pointsToNext = displayedProgress.PointsToNext;
        var remaining = pointsToNext is { } points ? UiText.F("Noch {0} Rangpunkte", Number(points))
            : displayedProgress.IsMaximum ? UiText.T("Höchste Punkte-Stufe") : UiText.T("Rangpunkte unbekannt");
        PaintPoints(graphics, remaining, remainingPointsFont, remainingInk, RemainingBounds, center);
        var totalPoints = displayedProgress.TotalPoints;
        var total = totalPoints is not { } known ? UiText.T("Rangpunkte unbekannt")
            : displayedProgress.Next is { } target
                ? $"{known.ToString(CultureInfo.InvariantCulture)}/{target.Threshold.ToString(CultureInfo.InvariantCulture)}"
                : $"{known.ToString(CultureInfo.InvariantCulture)}/—";
        PaintPoints(graphics, total, totalPointsFont, totalInk, ProgressBounds, center);
        if (frame is not null) PaintMotion(graphics, frame, theme, cache, morphs);
        graphics.DrawLine(edge, 16, 104, Width - 16, 104);
        graphics.DrawString(UiText.T("Rangpunkte dieser Session"), small, secondary, new RectangleF(SessionNumberBounds.X, 108, SessionNumberBounds.Width, 16), text);
        graphics.DrawString(UiText.T("Rangpunkte pro Stunde"), small, secondary, new RectangleF(SessionRateBounds.X, 108, SessionRateBounds.Width, 16), right);
        var currentSession = snapshot.CharacterBound && snapshot.SessionActive && !string.IsNullOrWhiteSpace(snapshot.SessionId);
        var net = currentSession ? snapshot.SessionNetRankPoints : null;
        var rate = currentSession ? snapshot.SessionRankPointsPerHour : null;
        var sessionLabel = net is { } pointsNet ? Number(pointsNet) : "—";
        var rateLabel = SignedRateLabel(rate, culture);
        PaintAmount(graphics, sessionLabel, amount, net is null ? secondary : net < 0 ? red : green, SessionNumberBounds, text);
        PaintAmount(graphics, rateLabel, rateAmount, rate is not { } knownRate || !double.IsFinite(knownRate)
            ? secondary : knownRate < 0 ? red : green, SessionRateBounds, right);
        graphics.DrawLine(edge, 16, 157, Width - 16, 157);
        var status = !locked ? "Schloss: sperren · Kopf ziehen" : !snapshot.CharacterBound ? "Warte auf die eigene Charaktererkennung. Bitte einen Ladebildschirm auslösen."
            : progress.HasPoints && !snapshot.Confirmed ? "Gespeicherter Rangpunktestand" : "Punkte-Stufe, kein bestätigter Klassenrang";
        graphics.DrawString(UiText.T(status), small, secondary, new RectangleF(16, 162, Width - 32, 13), text);
        PaintKillFeed(graphics, snapshot, theme, cache, body, small, text, right, culture, killFeedSeconds, animateKillFeed);
    }

    internal static string SignedRateLabel(double? value, CultureInfo culture) => value is { } rate && double.IsFinite(rate)
        ? (Math.Round(rate) == 0 ? 0 : rate).ToString("N0", culture) : "—";

    private static Color WithOpacity(Color color, double opacity) =>
        Color.FromArgb((int)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color);

    private static bool ValidFrame(AbyssRankMotionFrame? frame)
    {
        if (frame is null) return false;
        var count = AbyssRankCatalog.Default.Stages.Count;
        return frame.DisplayCurrentStageIndex >= 0 && frame.DisplayCurrentStageIndex < count
            && (frame.DisplayNextStageIndex is null || frame.DisplayNextStageIndex >= 0 && frame.DisplayNextStageIndex < count)
            && frame.MotionFromStageIndex >= 0 && frame.MotionFromStageIndex < count
            && frame.MotionToStageIndex >= 0 && frame.MotionToStageIndex < count
            && Math.Abs(frame.MotionFromStageIndex - frame.MotionToStageIndex) <= 1
            && double.IsFinite(frame.CenterX) && double.IsFinite(frame.CenterY)
            && double.IsFinite(frame.IconScale) && frame.IconScale is > 0 and <= 2
            && new[] { frame.MorphProgress, frame.NextOpacity, frame.TotalOpacity, frame.RemainingOpacity,
                frame.LabelOpacity, frame.BarProgress, frame.LightStrength }.All(double.IsFinite);
    }

    private static void PaintMotion(Graphics graphics, AbyssRankMotionFrame frame, OverlayThemeDefinition theme,
        LootOverlayIconCache icons, AbyssRankMorphCache? morphs)
    {
        var stages = AbyssRankCatalog.Default.Stages;
        if (frame.DisplayNextStageIndex is { } next)
            DrawFadedIcon(graphics, icons, stages[next].IconAsset, NextIconBounds, frame.NextOpacity);
        var saved = graphics.Save();
        try
        {
            // The motion stays above the session separator; metrics and killfeed never dim or move.
            graphics.SetClip(new RectangleF(0, 45, Width, 59), CombineMode.Intersect);
            if (frame.LightStrength > 0)
            {
                var strength = Math.Clamp(frame.LightStrength, 0, 1);
                using var path = new GraphicsPath();
                path.AddEllipse((float)frame.CenterX - 35, (float)frame.CenterY - 26, 70, 52);
                // Preserve the small original metalwork against a fully filled gold progress bar.
                using (var shadow = new PathGradientBrush(path)
                {
                    CenterColor = Color.FromArgb((int)Math.Round(160 * strength), Color.Black),
                    SurroundColors = [Color.Transparent]
                }) graphics.FillPath(shadow, path);
                using var glow = new PathGradientBrush(path)
                {
                    CenterColor = Color.FromArgb((int)Math.Round(48 * strength), theme.Gold),
                    SurroundColors = [Color.Transparent]
                };
                graphics.FillPath(glow, path);
            }
            var size = (float)(48 * frame.IconScale);
            var bounds = new RectangleF((float)frame.CenterX - size / 2, (float)frame.CenterY - size / 2, size, size);
            var lower = Math.Min(frame.MotionFromStageIndex, frame.MotionToStageIndex);
            var upper = Math.Max(frame.MotionFromStageIndex, frame.MotionToStageIndex);
            if (lower == upper || frame.MorphProgress <= 0) icons.Draw(graphics, stages[lower].IconAsset, bounds);
            else if (frame.MorphProgress >= 1) icons.Draw(graphics, stages[upper].IconAsset, bounds);
            else if (morphs?.Draw(graphics, lower, upper, frame.MorphProgress, bounds, frame.LightStrength) != true)
                icons.Draw(graphics, stages[frame.MotionFromStageIndex].IconAsset, bounds);
        }
        finally { graphics.Restore(saved); }
    }

    private static void DrawFadedIcon(Graphics graphics, LootOverlayIconCache icons, string asset, Rectangle bounds, double opacity)
    {
        if (opacity <= 0) return;
        if (opacity >= 1) { icons.Draw(graphics, asset, bounds); return; }
        using var surface = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        using (var target = Graphics.FromImage(surface)) icons.Draw(target, asset, new RectangleF(0, 0, 64, 64));
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)Math.Clamp(opacity, 0, 1) });
        graphics.DrawImage(surface, bounds, 0, 0, 64, 64, GraphicsUnit.Pixel, attributes);
    }

    private static void PaintPoints(Graphics graphics, string value, Font preferred, SolidBrush ink, RectangleF bounds, StringFormat format)
    {
        var size = preferred.Size;
        if (graphics.MeasureString(value, preferred).Width > bounds.Width - 6)
            for (size -= .5f; size > 11; size -= .5f)
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
            // A full dark contour survives a bright filled bar; it shares the existing motion fade.
            using var outline = new Pen(Color.FromArgb(ink.Color.A, 3, 6, 10), 3) { LineJoin = LineJoin.Round };
            graphics.DrawPath(outline, path);
            graphics.FillPath(ink, path);
        }
        finally { graphics.Restore(saved); }
    }

    private static void PaintAmount(Graphics graphics, string value, Font preferred, Brush ink, RectangleF bounds, StringFormat format)
    {
        if (graphics.MeasureString(value, preferred).Width <= bounds.Width)
        {
            graphics.DrawString(value, preferred, ink, bounds, format);
            return;
        }
        // Use the same large coloured amounts as the Kina HUD; fit only extreme totals to the compact column.
        for (var size = preferred.Size - 1; size >= 11; size--)
        {
            using var fitted = new Font(preferred.FontFamily, size, preferred.Style, GraphicsUnit.Pixel);
            if (graphics.MeasureString(value, fitted).Width <= bounds.Width || size <= 11)
            {
                graphics.DrawString(value, fitted, ink, bounds, format);
                return;
            }
        }
        graphics.DrawString(value, preferred, ink, bounds, format);
    }

    private static void PaintKillFeed(Graphics graphics, AbyssRankOverlaySnapshot snapshot, OverlayThemeDefinition theme,
        LootOverlayIconCache icons, Font body, Font small, StringFormat text, StringFormat right, CultureInfo culture,
        double seconds, bool animated)
    {
        var entries = KillFeedEntries(snapshot);
        if (entries.Count == 0) return;
        using var edge = new Pen(Color.FromArgb(90, theme.Border));
        using var secondary = new SolidBrush(theme.Secondary);
        using var primary = new SolidBrush(theme.Primary);
        using var green = new SolidBrush(Color.FromArgb(89, 220, 147));
        using var red = new SolidBrush(Color.FromArgb(245, 128, 128));
        using var blue = new SolidBrush(Color.FromArgb(130, 180, 255));
        using var rowBackground = new SolidBrush(Color.FromArgb(100, 0, 0, 0));
        using var resultBackground = new SolidBrush(Color.FromArgb(240, 12, 15, 21));
        var iconBackground = Color.FromArgb(14, 17, 22);
        using var metricFont = theme.Body(9.5f, FontStyle.Bold);
        using var metricFormat = new StringFormat(right) { LineAlignment = StringAlignment.Center };
        using var nameFormat = new StringFormat(text) { LineAlignment = StringAlignment.Center };
        graphics.DrawLine(edge, 16, Height + 2, Width - 16, Height + 2);
        graphics.DrawString(UiText.T("Killfeed"), small, secondary, new RectangleF(17, Height + 8, Width - 34, 18), text);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var y = KillFeedTop + index * KillFeedRowHeight;
            var ink = entry.Kind switch { AbyssKillFeedKind.Kill => green, AbyssKillFeedKind.Assist => blue, _ => red };
            var rowBounds = new RectangleF(13, y + 1, Width - 26, KillFeedRowHeight - 2);
            graphics.FillRectangle(rowBackground, rowBounds);
            AbyssKillFeedRankVisuals.PaintBackground(graphics, rowBounds, entry.OpponentAbyssRankGrade, seconds, animated);
            // Result cues remain separate from rank colors, including a bright cyan commander background.
            graphics.FillRectangle(resultBackground, 17, y + 1, KillFeedOpponentLeft - 19, KillFeedRowHeight - 2);
            graphics.FillRectangle(ink, 13, y + 2, 3, KillFeedRowHeight - 4);
            graphics.FillEllipse(ink, 18, y + 11, 5, 5);
            var label = entry.Kind switch { AbyssKillFeedKind.Kill => "Kill", AbyssKillFeedKind.Assist => "Assist", _ => "Tod" };
            graphics.DrawString(UiText.T(label), body, ink,
                new RectangleF(31, y + 2, 60, 23), nameFormat);
            var opponentLeft = (float)KillFeedOpponentLeft;
            var iconY = y + (KillFeedRowHeight - KillFeedIconSize) / 2f;
            foreach (var asset in new[] { AbyssOpponentIconAssets.ClassAsset(entry.OpponentClassId),
                AbyssOpponentIconAssets.RankAsset(entry.OpponentAbyssRankGrade) })
            {
                if (asset is null) continue;
                var iconBounds = new RectangleF(opponentLeft, iconY, KillFeedIconSize, KillFeedIconSize);
                // Stable dark backing preserves the original class/rank artwork against every grade effect.
                if (!icons.Draw(graphics, asset, iconBounds, background: iconBackground)) continue;
                opponentLeft += KillFeedIconSize + 2;
            }
            if (opponentLeft > KillFeedOpponentLeft) opponentLeft += 2;
            const int opponentRight = Width - 137;
            graphics.DrawString(OpponentLabel(entry.OpponentName), body, primary,
                new RectangleF(opponentLeft, y + 2, opponentRight - opponentLeft, 23), nameFormat);
            var rankInk = entry.RankPointDelta is null ? secondary : entry.RankPointDelta < 0 ? red : green;
            if (entry.Kind == AbyssKillFeedKind.Death)
                PaintKillFeedMetric(graphics, KillFeedAmountLabel(entry.RankPointDelta, "RP", culture), body,
                    rankInk, new RectangleF(Width - 125, y, 108, KillFeedRowHeight), metricFormat);
            else
            {
                PaintKillFeedMetric(graphics, KillFeedAmountLabel(entry.AbyssPointGain, "AP", culture), metricFont,
                    entry.AbyssPointGain is null ? secondary : green,
                    new RectangleF(Width - 125, y + .5f, 108, 13), metricFormat);
                PaintKillFeedMetric(graphics, KillFeedAmountLabel(entry.RankPointDelta, "RP", culture), metricFont,
                    rankInk, new RectangleF(Width - 125, y + 13.5f, 108, 13), metricFormat);
            }
        }
    }

    internal static string KillFeedAmountLabel(long? value, string unit, CultureInfo culture) =>
        (value is { } amount ? amount.ToString("+#,0;-#,0;0", culture) : "—") + " " + unit;

    private static void PaintKillFeedMetric(Graphics graphics, string value, Font preferred, Brush ink,
        RectangleF bounds, StringFormat format)
    {
        var saved = graphics.Save();
        try
        {
            // The two metric lines cannot spill into each other or the opponent name.
            graphics.SetClip(bounds, CombineMode.Intersect);
            if (graphics.MeasureString(value, preferred).Width <= bounds.Width - 2)
            {
                graphics.DrawString(value, preferred, ink, bounds, format);
                return;
            }
            for (var size = preferred.Size - .5f; size >= 6; size -= .5f)
            {
                using var fitted = new Font(preferred.FontFamily, size, preferred.Style, GraphicsUnit.Pixel);
                if (graphics.MeasureString(value, fitted).Width > bounds.Width - 2 && size > 6) continue;
                graphics.DrawString(value, fitted, ink, bounds, format);
                return;
            }
        }
        finally { graphics.Restore(saved); }
    }

    internal static string OpponentLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return UiText.T("Unbekannter Gegner");
        var safe = new string(name.Where(character => !char.IsControl(character)
            && char.GetUnicodeCategory(character) != UnicodeCategory.Format).Take(96).ToArray()).Trim();
        return safe.Length == 0 ? UiText.T("Unbekannter Gegner") : safe;
    }
}
