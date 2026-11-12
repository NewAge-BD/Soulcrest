using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>The approved nine-row sidebar, shared by the native HUD and offline placement previews.</summary>
internal static class GenusInsightOverlayRenderer
{
    internal const int BaseWidth = 340, BaseHeight = 510;
    internal static double NormalizeScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, .6, 2) : 1;

    internal static Bitmap Render(GenusInsightSnapshot snapshot, OverlayThemeDefinition? theme = null,
        double scale = 1, string language = "en", LootOverlayIconCache? icons = null)
    {
        scale = NormalizeScale(scale);
        var image = new Bitmap((int)Math.Round(BaseWidth * scale), (int)Math.Round(BaseHeight * scale), PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            Paint(graphics, new(Point.Empty, image.Size), snapshot, theme, language, icons);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    internal static void Paint(Graphics graphics, Rectangle bounds, GenusInsightSnapshot snapshot,
        OverlayThemeDefinition? theme = null, string language = "en", LootOverlayIconCache? icons = null)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        theme ??= OverlayThemeDefinition.Aion2;
        using var localIcons = icons is null ? new LootOverlayIconCache() : null;
        icons ??= localIcons!;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds, CombineMode.Intersect);
            graphics.TranslateTransform(bounds.Left, bounds.Top);
            graphics.ScaleTransform(bounds.Width / (float)BaseWidth, bounds.Height / (float)BaseHeight);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var surface = new SolidBrush(Color.FromArgb(theme.IsLegacy ? 255 : 240, theme.Surface));
            using var border = new Pen(theme.Border);
            using var ornament = new Pen(Color.FromArgb(200, theme.Gold));
            graphics.FillRectangle(surface, 0, 0, BaseWidth, BaseHeight);
            graphics.DrawRectangle(border, .5f, .5f, BaseWidth - 1, BaseHeight - 1);
            graphics.DrawLines(ornament, new PointF[] { new(4, 17), new(4, 4), new(17, 4) });
            graphics.DrawLines(ornament, new PointF[] { new(BaseWidth - 17, BaseHeight - 4), new(BaseWidth - 4, BaseHeight - 4), new(BaseWidth - 4, BaseHeight - 17) });

            using var heading = theme.Heading(20);
            using var body = theme.Body(12);
            using var value = theme.Body(14, FontStyle.Bold);
            using var small = theme.Body(9);
            using var tiny = theme.Body(7, FontStyle.Bold);
            using var number = theme.Body(10);
            using var primary = new SolidBrush(theme.Primary);
            using var muted = new SolidBrush(theme.Secondary);
            using var gold = new SolidBrush(theme.Gold);
            var genus = snapshot.ViewedBoard?.Genus ?? snapshot.SelectedGenus;
            if (GenusInsightPresentation.KnownGenus(genus))
                icons.Draw(graphics, Path.Combine(AppContext.BaseDirectory, "wwwroot",
                    GenusInsightPresentation.IconPath(genus!).Replace('/', Path.DirectorySeparatorChar)), new(14, 19, 35, 38));
            Draw(graphics, "GENUS INSIGHT", small, gold, new(59, 14, 225, 13));
            Draw(graphics, GenusInsightPresentation.GenusLabel(genus ?? ""), heading, primary, new(59, 27, 225, 26));
            var level = snapshot.ViewedBoard?.Level?.ToString(Culture(language)) ?? "—";
            var preset = snapshot.ViewedPreset?.ToString(Culture(language)) ?? "—";
            Draw(graphics, Text(language, "Level ", "Level ") + level + " · Preset " + preset,
                small, muted, new(59, 56, 219, 14));
            using (var badge = new SolidBrush(Color.FromArgb(18, theme.Gold))) graphics.FillRectangle(badge, 297, 14, 28, 27);
            graphics.DrawRectangle(border, 297, 14, 28, 27);
            Draw(graphics, preset, body, gold, new(297, 18, 28, 19), StringAlignment.Center);

            const float rowHeight = 41.333333f, rowStride = rowHeight + 4;
            for (byte slotNumber = 1; slotNumber <= 9; slotNumber++)
            {
                var matches = snapshot.ViewedBoard?.Slots.Where(slot => slot.Slot == slotNumber).ToArray() ?? [];
                var slot = matches.Length == 1 ? matches[0] : new GenusInsightSlotView { Slot = slotNumber };
                var y = 74 + (slotNumber - 1) * rowStride;
                var known = Qualified(slot);
                var color = known ? ColorTranslator.FromHtml(GenusInsightPresentation.RankColor(slot.Rank)) : theme.Secondary;
                using var rank = new SolidBrush(color);
                if (slot.IsFavorite)
                {
                    using var favoriteFill = new SolidBrush(Color.FromArgb(20, theme.Gold));
                    using var favoriteBorder = new Pen(Color.FromArgb(112, theme.Gold));
                    graphics.FillRectangle(favoriteFill, 14, y, 312, rowHeight);
                    graphics.DrawRectangle(favoriteBorder, 14.5f, y + .5f, 311, rowHeight - 1);
                }
                Draw(graphics, slotNumber.ToString("00", CultureInfo.InvariantCulture), number, muted, new(21, y + 3, 20, 16));
                var label = language == "de" ? slot.LabelDe : slot.LabelEn;
                Draw(graphics, label, body, rank, new(47, y + 1, 202, 19));
                Draw(graphics, ValueText(slot, language), value, rank, new(249, y, 52, 21), StringAlignment.Far);
                if (slot.IsFavorite) Draw(graphics, "★", body, gold, new(305, y + 1, 16, 19), StringAlignment.Center);
                using (var track = new SolidBrush(Color.FromArgb(32, theme.Secondary))) graphics.FillRectangle(track, 47, y + 23, 272, 3);
                if (known && slot.RollFraction is { } fraction && double.IsFinite(fraction) && fraction is >= 0 and <= 1)
                {
                    var position = 47 + (float)fraction * 272;
                    using var fill = new SolidBrush(Color.FromArgb(190, color));
                    using var marker = new Pen(theme.Primary, 2);
                    graphics.FillRectangle(fill, 47, y + 23, (float)fraction * 272, 3);
                    graphics.DrawLine(marker, position, y + 20, position, y + 29);
                }
                Draw(graphics, RangeText(slot.Minimum, slot.Unit, language), small, muted, new(47, y + 28, 90, 12));
                Draw(graphics, RangeText(slot.Maximum, slot.Unit, language), small, muted, new(229, y + 28, 90, 12), StringAlignment.Far);
                if (ShowMax(slot)) Draw(graphics, "MAX ROLL!", tiny, gold, new(137, y + 29, 92, 11), StringAlignment.Center);
            }
            var observed = snapshot.ObservedAt?.ToLocalTime().ToString("HH:mm:ss", Culture(language)) ?? "—";
            Draw(graphics, Text(language, "Stand: ", "Updated: ") + observed, small, muted, new(14, 483, 185, 13));
            Draw(graphics, Text(language, "★ Wunschstat", "★ Favorite"), small, gold, new(205, 483, 121, 13), StringAlignment.Far);
        }
        finally { graphics.Restore(state); }
    }

    private static bool Qualified(GenusInsightSlotView slot) => slot.IsCurrent && slot.Rank is >= 1 and <= 5
        && slot.Value is { } value && slot.Minimum is { } minimum && slot.Maximum is { } maximum
        && maximum >= minimum && value >= minimum && value <= maximum && slot.Unit is "Points" or "Percent";
    internal static bool ShowMax(GenusInsightSlotView slot) => Qualified(slot) && slot.IsMaxRoll && slot.Value == slot.Maximum;
    internal static string ValueText(GenusInsightSlotView slot, string language = "en") => !Qualified(slot) ? "—"
        : slot.Unit == "Percent" ? decimal.Round(slot.Value!.Value, 1, MidpointRounding.ToEven).ToString("0.#", Culture(language)) + " %"
        : slot.Value!.Value.ToString("0.##", Culture(language));
    internal static string RangeText(decimal? value, string? unit, string language = "en") => value is { } number && unit is "Points" or "Percent"
        ? number.ToString("0.############################", Culture(language)) + (unit == "Percent" ? " %" : "") : "—";
    private static CultureInfo Culture(string language) => CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
    private static string Text(string language, string de, string en) => language == "de" ? de : en;
    private static void Draw(Graphics graphics, string text, Font font, Brush brush, RectangleF bounds,
        StringAlignment alignment = StringAlignment.Near)
    {
        using var format = new StringFormat { Alignment = alignment, Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString(text, font, brush, bounds, format);
    }
}
