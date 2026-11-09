using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Soulcrest.App.Components;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Shared production paint path for the native tracker and its anonymous previews.</summary>
internal static class AchievementOverlayRenderer
{
    internal const int BaseWidth = 360, HeaderHeight = 36, RowHeight = 104, FooterHeight = 20;
    internal static int HeightFor(int rows) => HeaderHeight + Math.Max(0, rows) * RowHeight + FooterHeight;
    internal static int ViewportHeight(int rows, double scale, int workingHeight) => Math.Max(1,
        Math.Min((int)Math.Round(HeightFor(rows) * NormalizeScale(scale)), (int)Math.Floor(Math.Max(1, workingHeight) * .8)));
    internal static double NormalizeScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, .6, 2.5) : 1;

    internal static Bitmap Render(IReadOnlyList<AchievementTrackerRow> rows, OverlayThemeDefinition? theme = null,
        double scale = 1, int? maximumHeight = null, int scrollOffset = 0, string language = "en", LootOverlayIconCache? icons = null)
    {
        scale = NormalizeScale(scale);
        var height = Math.Max(1, (int)Math.Round(HeightFor(rows.Count) * scale));
        if (maximumHeight is { } maximum) height = Math.Max(1, Math.Min(height, maximum));
        var image = new Bitmap(Math.Max(1, (int)Math.Round(BaseWidth * scale)), height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            Paint(graphics, new(Point.Empty, image.Size), rows, theme, scrollOffset, language, icons);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    internal static void Paint(Graphics graphics, Rectangle bounds, IReadOnlyList<AchievementTrackerRow> rows,
        OverlayThemeDefinition? theme = null, int scrollOffset = 0, string language = "en", LootOverlayIconCache? icons = null)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        theme ??= OverlayThemeDefinition.Aion2;
        using var localIcons = icons is null ? new LootOverlayIconCache() : null;
        icons ??= localIcons!;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds, CombineMode.Intersect);
            graphics.TranslateTransform(bounds.X, bounds.Y);
            var scale = bounds.Width / (float)BaseWidth;
            graphics.ScaleTransform(scale, scale);
            var height = bounds.Height / scale;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            theme.Panel(graphics, new(0, 0, BaseWidth, (int)Math.Ceiling(height)));
            using var heading = theme.Heading(15);
            using var body = theme.Body(10);
            using var gold = new SolidBrush(theme.Gold);
            using var muted = new SolidBrush(theme.Secondary);
            using var edge = new Pen(theme.Border);
            theme.Diamond(graphics, new(13, 17), 4, theme.Gold);
            Draw(graphics, Text(language, "Achievement-Fortschritt", "Achievement progress"), heading, gold, new(25, 7, 296, 22));
            graphics.DrawLine(edge, 12, HeaderHeight - 1, BaseWidth - 12, HeaderHeight - 1);
            var viewport = Math.Max(0, height - HeaderHeight - FooterHeight);
            var maximumScroll = Math.Max(0, rows.Count * RowHeight - (int)Math.Floor(viewport));
            scrollOffset = Math.Clamp(scrollOffset, 0, maximumScroll);
            var content = graphics.Save();
            graphics.SetClip(new RectangleF(7, HeaderHeight, BaseWidth - 14, viewport), CombineMode.Intersect);
            for (var i = 0; i < rows.Count; i++)
            {
                var y = HeaderHeight + i * RowHeight - scrollOffset;
                if (y + RowHeight <= HeaderHeight || y >= height - FooterHeight) continue;
                PaintRow(graphics, rows[i], y, theme, language, icons);
            }
            graphics.Restore(content);
            var footer = rows.Count == 1 ? Text(language, "1 Achievement", "1 achievement")
                : Text(language, $"{rows.Count} Achievements", $"{rows.Count} achievements");
            Draw(graphics, footer, body, muted, new(13, height - FooterHeight + 3, 155, 14));
            if (maximumScroll > 0)
            {
                using var progress = new SolidBrush(Color.FromArgb(180, theme.Gold));
                var thumb = Math.Max(20, viewport * viewport / Math.Max(1, rows.Count * RowHeight));
                graphics.FillRectangle(progress, BaseWidth - 6, HeaderHeight + (viewport - thumb) * scrollOffset / maximumScroll, 2, thumb);
                using var right = new StringFormat { Alignment = StringAlignment.Far };
                graphics.DrawString(Text(language, "Alt + Mausrad", "Alt + mouse wheel"), body, muted,
                    new RectangleF(165, height - FooterHeight + 3, 181, 14), right);
            }
        }
        finally { graphics.Restore(state); }
    }

    private static void PaintRow(Graphics graphics, AchievementTrackerRow row, int y, OverlayThemeDefinition theme,
        string language, LootOverlayIconCache icons)
    {
        var bounds = new RectangleF(9, y + 5, BaseWidth - 18, RowHeight - 8);
        using var background = new SolidBrush(Color.FromArgb(theme.IsLegacy ? 155 : 58, 10, 16, 23));
        using var edge = new Pen(Color.FromArgb(row.IsCompleted ? 145 : 72, row.IsCompleted ? theme.Gold : theme.Border));
        graphics.FillRectangle(background, bounds);
        graphics.DrawRectangle(edge, bounds.X + .5f, bounds.Y + .5f, bounds.Width - 1, bounds.Height - 1);
        if (row.IsAnimating) Celebration(graphics, bounds, row.AnimationProgress, theme.Gold);
        if (row.IsFavorite) FavoriteMarker(graphics, bounds, y);
        using var nameFont = theme.Body(13, FontStyle.Bold);
        using var conditionFont = theme.Body(11);
        using var small = theme.Body(10.5f);
        using var primary = new SolidBrush(row.IsCompleted ? theme.Gold : theme.Primary);
        using var secondary = new SolidBrush(theme.Secondary);
        using var valueInk = new SolidBrush(row.IsCompleted ? theme.Gold : theme.Mint);
        var icon = new RectangleF(17, y + 12, 20, 20);
        if (!icons.Draw(graphics, Asset(row.IconPath), icon)) theme.Diamond(graphics, new(27, y + 21), 6, row.IsCompleted ? theme.Gold : theme.Mint);
        var name = language == "de" ? row.NameDe : row.NameEn;
        if (string.IsNullOrWhiteSpace(name)) name = language == "de" ? row.NameEn : row.NameDe;
        Draw(graphics, name, nameFont, primary, new(44, y + 10, row.IsFavorite ? 272 : 288, 22));
        var condition = Condition(row, language);
        using (var format = new StringFormat { Trimming = StringTrimming.EllipsisWord })
            graphics.DrawString(condition, conditionFont, secondary, new RectangleF(17, y + 35, 318, 29), format);
        var bar = new RectangleF(17, y + 68, 318, 4);
        using (var rail = new SolidBrush(Color.FromArgb(145, 8, 12, 17))) graphics.FillRectangle(rail, bar);
        if (row.ProgressFraction is { } fraction)
        {
            using var fill = new SolidBrush(row.IsCompleted ? theme.Gold : theme.Mint);
            graphics.FillRectangle(fill, bar.X, bar.Y, (float)(bar.Width * fraction), bar.Height);
        }
        var rewardCount = row.Rewards.IsDefault ? 0 : Math.Min(3, row.Rewards.Length);
        var counterWidth = rewardCount == 0 ? 190 : Math.Min(190, BaseWidth - 21 - rewardCount * 48 - 8 - 17);
        if (row.IsHistorical)
        {
            // Keep provenance and the known numbers legible even beside three rewards.
            using var sourceFont = theme.Body(9);
            Draw(graphics, HistoryLabel(language), sourceFont, secondary, new(17, y + 73, counterWidth, 11));
            Draw(graphics, CounterValue(row, language), small, valueInk, new(17, y + 84, counterWidth, 15));
        }
        else Draw(graphics, Counter(row, language), small, valueInk, new(17, y + 79, counterWidth, 16));
        for (var index = 0; index < rewardCount; index++)
        {
            var reward = row.Rewards[index];
            var x = BaseWidth - 21 - (rewardCount - index) * 48;
            var target = new RectangleF(x, y + 77, 18, 18);
            if (!icons.Draw(graphics, Asset(reward.IconPath), target)) theme.Diamond(graphics, new(x + 9, y + 86), 4, theme.Secondary);
            Draw(graphics, reward.Quantity is { } quantity ? Compact(quantity, language) : "?", small, secondary, new(x + 20, y + 79, 29, 15));
        }
    }

    private static void FavoriteMarker(Graphics graphics, RectangleF bounds, int y)
    {
        // A small, theme-independent warm marker distinguishes a persistent pin from recent activity.
        var warm = Color.FromArgb(226, 195, 127);
        using var accent = new Pen(Color.FromArgb(85, warm), 1);
        graphics.DrawLine(accent, bounds.Left + 1.5f, bounds.Top + 10, bounds.Left + 1.5f, bounds.Bottom - 10);
        var points = new PointF[10];
        for (var i = 0; i < points.Length; i++)
        {
            var angle = -Math.PI / 2 + i * Math.PI / 5;
            var radius = i % 2 == 0 ? 5.5 : 2.5;
            points[i] = new(336 + (float)(Math.Cos(angle) * radius), y + 21 + (float)(Math.Sin(angle) * radius));
        }
        using var ink = new SolidBrush(Color.FromArgb(210, warm));
        graphics.FillPolygon(ink, points);
    }

    internal static string Condition(AchievementTrackerRow row, string language)
    {
        var condition = language == "de" ? row.ConditionDe : row.ConditionEn;
        if (string.IsNullOrWhiteSpace(condition)) condition = language == "de" ? row.ConditionEn : row.ConditionDe;
        var titleGoal = language == "de" ? row.TitleNameDe ?? row.TitleNameEn : row.TitleNameEn ?? row.TitleNameDe;
        return !row.IsTitlePlaceholder && !string.IsNullOrWhiteSpace(titleGoal)
            ? Text(language, "Titelziel: ", "Title goal: ") + titleGoal + "\n" + condition : condition;
    }

    internal static string Counter(AchievementTrackerRow row, string language)
    {
        return (row.IsHistorical ? HistoryLabel(language) + " " : "") + CounterValue(row, language);
    }

    private static string HistoryLabel(string language) => Text(language, "Gespeichert:", "Saved:");

    private static string CounterValue(AchievementTrackerRow row, string language)
    {
        if (!row.IsHistorical && row.State == AchievementTrackerRowState.AwaitingClaim) return Text(language, "Belohnung abholen", "Reward available");
        if (!row.IsHistorical && row.State == AchievementTrackerRowState.Celebrating && row.CelebratedStageId is null)
            return Text(language, "Abgeschlossen!", "Completed!");
        var prefix = !row.IsHistorical && row.State == AchievementTrackerRowState.Celebrating ? "✓ " : "";
        if (!row.HasQualifiedGoal) return row.RawValue is { } raw
            ? prefix + string.Format(CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US"),
                Text(language, "Gemeldeter Wert: {0}", "Reported value: {0}"), raw)
            : prefix + Text(language, "Fortschritt unbekannt", "Progress unknown");
        if (row.Unit == CombatAchievementProgressUnit.Minutes)
            return prefix + CombatAchievementsPresentation.ProgressDisplay(row.PresentationCurrent, row.Current, row.Unit, language)
                + " / " + CombatAchievementsPresentation.ProgressDisplay(row.PresentationTarget, row.Target, row.Unit, language);
        var culture = CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
        var current = row.PresentationCurrent!.Value.ToString("0.##", culture);
        var target = row.PresentationTarget!.Value.ToString("0.##", culture);
        var suffix = row.Unit switch
        {
            CombatAchievementProgressUnit.Minutes => " min", CombatAchievementProgressUnit.Seconds => " s",
            CombatAchievementProgressUnit.Level => Text(language, " Stufe", " level"), _ => "",
        };
        return prefix + current + " / " + target + suffix;
    }

    private static void Celebration(Graphics graphics, RectangleF bounds, double progress, Color gold)
    {
        progress = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 1;
        var elapsed = progress * 60;
        var opacity = Math.Clamp(Math.Min(elapsed / .25, (60 - elapsed) / 1.2), 0, 1);
        var pulse = Math.Pow(Math.Sin(elapsed / 3 * Math.PI * 2), 2);
        Color A(int alpha) => Color.FromArgb((int)Math.Round(alpha * opacity), gold);
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds, CombineMode.Intersect);
            using var tint = new LinearGradientBrush(bounds, A(64), A(6), LinearGradientMode.Horizontal);
            graphics.FillRectangle(tint, bounds);
            var phase = elapsed / 2.2 % 1;
            var sweep = new RectangleF(bounds.Left - 110 + (float)(phase * (bounds.Width + 220)), bounds.Top, 110, bounds.Height);
            using var light = new LinearGradientBrush(sweep, Color.Transparent, A(56), LinearGradientMode.Horizontal);
            light.SetBlendTriangularShape(.5f);
            graphics.FillRectangle(light, sweep);
            using var border = new Pen(A((int)(140 + 70 * pulse)), 1.3f);
            graphics.DrawRectangle(border, bounds.X + .75f, bounds.Y + .75f, bounds.Width - 1.5f, bounds.Height - 1.5f);
            using var rail = new Pen(A(230), 2);
            graphics.DrawLine(rail, bounds.Left + 1, bounds.Top + 10, bounds.Left + 1, bounds.Bottom - 10);
        }
        finally { graphics.Restore(state); }
    }

    private static string Compact(long value, string language) => value >= 1000
        ? (value / 1000m).ToString("0.#", CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US")) + "k"
        : value.ToString(CultureInfo.InvariantCulture);
    private static string Text(string language, string de, string en) => language == "de" ? de : en;
    private static void Draw(Graphics graphics, string text, Font font, Brush brush, RectangleF bounds)
    {
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static string? Asset(string? asset)
    {
        if (string.IsNullOrWhiteSpace(asset)) return null;
        if (Path.IsPathFullyQualified(asset)) return asset;
        var parts = asset.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':'))) return null;
        return Path.Combine(AppContext.BaseDirectory, "wwwroot", Path.Combine(parts));
    }
}
