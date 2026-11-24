using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>The approved nine-row sidebar, shared by the native HUD and offline placement previews.</summary>
internal static class GenusInsightOverlayRenderer
{
    internal const int BaseWidth = 340, BaseHeight = 510;
    private static readonly Color[] RankColors = Enumerable.Range(1, GenusInsightOverlaySpectrum.RankCount)
        .Select(rank => ColorTranslator.FromHtml(GenusInsightPresentation.RankColor((byte)rank))).ToArray();
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
            using var rangeLabel = theme.Body(8.5f, FontStyle.Bold);
            using var number = theme.Body(10);
            using var primary = new SolidBrush(theme.Primary);
            using var muted = new SolidBrush(theme.Secondary);
            using var gold = new SolidBrush(theme.Gold);
            using var rowBrush = new SolidBrush(theme.Secondary);
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

            const float trackLeft = 47, trackWidth = 272, labelPitch = 7, slotsHeight = 408;
            var culture = Culture(language);
            using var rangeFormat = StringFormat.GenericTypographic;
            rangeFormat.Alignment = StringAlignment.Center;
            var rows = Enumerable.Range(1, 9).Select(slotNumber =>
            {
                var matches = snapshot.ViewedBoard?.Slots.Where(slot => slot.Slot == slotNumber).ToArray() ?? [];
                var slot = matches.Length == 1 ? matches[0] : new GenusInsightSlotView { Slot = (byte)slotNumber };
                var spectrum = new GenusInsightOverlaySpectrum(slot);
                var labels = spectrum.Labels(culture, trackWidth,
                    text => graphics.MeasureString(text, rangeLabel, PointF.Empty, rangeFormat).Width);
                var extraHeight = Math.Max(0, GenusInsightOverlaySpectrum.LabelRows(labels) - 2) * labelPitch;
                return (Slot: slot, Spectrum: spectrum, Labels: labels, ExtraHeight: extraHeight);
            }).ToArray();

            var slotsState = graphics.Save();
            graphics.TranslateTransform(0, 72);
            graphics.ScaleTransform(1, slotsHeight / (slotsHeight + rows.Sum(row => row.ExtraHeight)));
            var y = 0f;
            foreach (var (slot, spectrum, labels, extraHeight) in rows)
            {
                var rowHeight = 45f + extraHeight;
                var known = GenusInsightOverlaySpectrum.Confirmed(slot);
                rowBrush.Color = known ? RankColors[slot.Rank!.Value - 1] : theme.Secondary;
                if (slot.IsFavorite)
                {
                    using var favoriteFill = new SolidBrush(Color.FromArgb(20, theme.Gold));
                    using var favoriteBorder = new Pen(Color.FromArgb(112, theme.Gold));
                    graphics.FillRectangle(favoriteFill, 14, y, 312, rowHeight);
                    graphics.DrawRectangle(favoriteBorder, 14.5f, y + .5f, 311, rowHeight - 1);
                }
                Draw(graphics, slot.Slot.ToString("00", CultureInfo.InvariantCulture), number, muted, new(21, y + 3, 20, 16));
                var label = snapshot.OverlaySelectionPending ? "" : language == "de" ? slot.LabelDe : slot.LabelEn;
                var max = ShowMax(slot);
                Draw(graphics, label, body, rowBrush, new(47, y + 1, max ? 147 : 202, 19));
                Draw(graphics, ValueText(slot, language), value, rowBrush, new(249, y, 52, 21), StringAlignment.Far);
                if (slot.IsFavorite) Draw(graphics, "★", body, gold, new(305, y + 1, 16, 19), StringAlignment.Center);
                if (max) Draw(graphics, "MAX ROLL!", tiny, gold, new(195, y + 5, 48, 11), StringAlignment.Far);
                DrawSpectrum(graphics, new(trackLeft, y + 16, trackWidth, 12.75f), spectrum, theme, BaseWidth / (float)bounds.Width, rowBrush);
                if (labels.Count > 0)
                {
                    foreach (var intervalLabel in labels)
                    {
                        rowBrush.Color = RankColors[intervalLabel.Rank - 1];
                        var x = trackLeft + (float)intervalLabel.Center * trackWidth;
                        graphics.DrawString(intervalLabel.Text, rangeLabel, rowBrush,
                            new PointF(x, y + 26.75f + intervalLabel.Row * labelPitch), rangeFormat);
                    }
                    if (slot.Unit == "Percent") Draw(graphics, "%", small, muted, new(23, y + 28, 17, 11), StringAlignment.Far);
                }
                else
                {
                    Draw(graphics, RangeText(slot.Minimum, slot.Unit, language), small, muted, new(47, y + 34, 90, 11));
                    Draw(graphics, RangeText(slot.Maximum, slot.Unit, language), small, muted, new(229, y + 34, 90, 11), StringAlignment.Far);
                }
                y += slotsHeight / rows.Length + extraHeight;
            }
            graphics.Restore(slotsState);
            var observed = snapshot.ObservedAt?.ToLocalTime().ToString("HH:mm:ss", Culture(language)) ?? "—";
            var status = snapshot.OverlaySelectionPending ? Text(language, "Bestätigung ausstehend", "Awaiting confirmation")
                : Text(language, "Stand: ", "Updated: ") + observed;
            Draw(graphics, status, small, muted, new(14, 483, 185, 13));
            Draw(graphics, Text(language, "★ Wunschstat", "★ Favorite"), small, gold, new(205, 483, 121, 13), StringAlignment.Far);
        }
        finally { graphics.Restore(state); }
    }

    private static void DrawSpectrum(Graphics graphics, RectangleF track, GenusInsightOverlaySpectrum spectrum,
        OverlayThemeDefinition theme, float pixelWidth, SolidBrush brush)
    {
        brush.Color = Color.FromArgb(90, theme.Secondary);
        graphics.FillRectangle(brush, RectangleF.Inflate(track, 1, 1));
        brush.Color = theme.Surface;
        graphics.FillRectangle(brush, track);

        var laneHeight = track.Height / GenusInsightOverlaySpectrum.RankCount;
        var stroke = Math.Min(track.Width, Math.Max(1, pixelWidth));
        brush.Color = Color.FromArgb(28, theme.Secondary);
        for (var lane = 0; lane < GenusInsightOverlaySpectrum.RankCount; lane++)
        {
            var top = track.Top + lane * laneHeight + .3f;
            graphics.FillRectangle(brush, track.Left, top, track.Width, laneHeight - .6f);
        }

        brush.Color = theme.Secondary;
        graphics.FillRectangle(brush, track.Left - stroke / 2, track.Bottom, stroke, 2);
        graphics.FillRectangle(brush, track.Right - stroke / 2, track.Bottom, stroke, 2);

        if (spectrum.Current is { } current)
        {
            var x = track.Left + (float)current * track.Width;
            var markerWidth = Math.Max(1.8f, pixelWidth);
            brush.Color = theme.Surface;
            graphics.FillRectangle(brush, x - markerWidth / 2 - 1, track.Top - 1, markerWidth + 2, track.Height + 3);
            brush.Color = theme.Primary;
            graphics.FillRectangle(brush, x - markerWidth / 2, track.Top - 1, markerWidth, track.Height + 3);
            graphics.FillPolygon(brush, new PointF[] { new(x - 3, track.Top - 2), new(x + 3, track.Top - 2), new(x, track.Top + 1) });
            graphics.FillPolygon(brush, new PointF[] { new(x - 3, track.Bottom + 2), new(x + 3, track.Bottom + 2), new(x, track.Bottom - 1) });
        }

        // A point interval remains visible even when the current marker sits on it.
        for (byte rank = 1; rank <= GenusInsightOverlaySpectrum.RankCount; rank++)
        {
            if (spectrum.Interval(rank) is not { } interval) continue;
            var top = track.Top + (rank - 1) * laneHeight + .3f;
            var start = Math.Min((float)interval.Start * track.Width, track.Width - stroke);
            var width = Math.Max((float)(interval.End - interval.Start) * track.Width, stroke);
            brush.Color = RankColors[rank - 1];
            graphics.FillRectangle(brush, track.Left + start, top, width, laneHeight - .6f);
        }
    }

    internal static bool ShowMax(GenusInsightSlotView slot) => GenusInsightOverlaySpectrum.Confirmed(slot) && slot.IsMaxRoll && slot.Value == slot.Maximum;
    internal static string ValueText(GenusInsightSlotView slot, string language = "en") => !GenusInsightOverlaySpectrum.Confirmed(slot) ? "—"
        : slot.Unit == "Percent" ? decimal.Round(slot.Value!.Value, 1, MidpointRounding.ToEven).ToString("0.#", Culture(language)) + " %"
        : slot.Value!.Value.ToString("0.##", Culture(language));
    internal static string RangeText(decimal? value, string? unit, string language = "en") => value is { } number && unit is "Points" or "Percent"
        ? GenusInsightOverlaySpectrum.BoundaryText(number, Culture(language)) + (unit == "Percent" ? " %" : "") : "—";
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
