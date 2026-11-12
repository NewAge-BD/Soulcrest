using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>The native Crafting Progress production paint path, shared by the live HUD and previews.</summary>
internal static class CraftingProgressOverlayRenderer
{
    internal const int BaseWidth = 360, HeaderHeight = 36, GoalHeight = 76, SectionHeight = 22, RowHeight = 68, FooterHeight = 22;
    internal static Color ProgressGold => LootRarityVisuals.Accent(LootItemRarity.Unique);
    internal static int HeightFor(int rows) => HeaderHeight + GoalHeight + (rows > 0 ? SectionHeight + Math.Min(3, rows) * RowHeight : 0) + FooterHeight;
    internal static double NormalizeScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, .8, 2.5) : 1;
    internal static Bitmap Render(CraftingProgressSnapshot snapshot, OverlayThemeDefinition? theme = null,
        double scale = 1, string language = "en", bool animationsEnabled = true, LootOverlayIconCache? icons = null)
    {
        scale = NormalizeScale(scale); theme ??= OverlayThemeDefinition.Aion2;
        var rows = snapshot.Rows.Take(3).ToArray();
        var image = new Bitmap(Math.Max(1, (int)Math.Round(BaseWidth * scale)),
            Math.Max(1, (int)Math.Round(HeightFor(rows.Length) * scale)), PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            using var ownedIcons = icons is null ? new LootOverlayIconCache() : null;
            icons ??= ownedIcons!;
            graphics.Clear(Color.Transparent); graphics.ScaleTransform((float)scale, (float)scale);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            theme.Panel(graphics, new(0, 0, BaseWidth, HeightFor(rows.Length)));
            using var heading = theme.Heading(15);
            using var gold = new SolidBrush(theme.Gold);
            using var secondary = new SolidBrush(theme.Secondary);
            using var primary = new SolidBrush(theme.Primary);
            using var small = theme.Body(10.5f);
            using var edge = new Pen(theme.Border);
            theme.Diamond(graphics, new(13, 17), 4, snapshot.IsFresh ? theme.Gold : theme.Secondary);
            Draw(graphics, "Crafting Progress", heading, gold, new(25, 7, 294, 23));
            graphics.DrawLine(edge, 12, HeaderHeight - 1, BaseWidth - 12, HeaderHeight - 1);
            PaintGoal(graphics, snapshot, theme, language, animationsEnabled, icons);
            var y = HeaderHeight + GoalHeight;
            if (rows.Length > 0)
            {
                theme.Diamond(graphics, new(15, y + 11), 2.5f, theme.Gold);
                Draw(graphics, Text(language, "Zuletzt Fortschritt", "Recent progress"), small, gold, new(24, y + 3, 320, 17));
                y += SectionHeight;
                foreach (var row in rows) { PaintRow(graphics, row, y, theme, language, animationsEnabled, icons); y += RowHeight; }
            }
            var footer = snapshot.Error is not null ? Text(language, "Erkennung oder Speicherung prüfen", "Check tracking or storage")
                : !snapshot.IsFresh ? Text(language, "Gespeicherter Stand · Quelle nicht aktuell", "Saved progress · source not current")
                : Text(language, "Schloss oder Alt zum Entsperren", "Lock or Alt to unlock");
            Draw(graphics, footer, small, secondary, new(12, y + 3, 336, 17));
            return image;
        }
        catch { image.Dispose(); throw; }
    }
    private static void PaintGoal(Graphics graphics, CraftingProgressSnapshot snapshot, OverlayThemeDefinition theme,
        string language, bool animated, LootOverlayIconCache icons)
    {
        var pin = snapshot.Pinned;
        if (pin is null) return;
        var area = new RectangleF(7, HeaderHeight + 3, 346, GoalHeight - 5);
        if (snapshot.IsComplete)
            LootRarityVisuals.PaintBackground(graphics, area, ProgressGold, snapshot.CompletionAgeSeconds,
                animated && snapshot.IsCompletionVisible && snapshot.CompletionAgeSeconds < 1, .42, .6, true,
                4, 2);
        var icon = new RectangleF(12, HeaderHeight + 12, 40, 40);
        if (!icons.Draw(graphics, Asset(snapshot.TargetIcon), icon)) theme.Diamond(graphics, new(32, HeaderHeight + 32), 7, theme.Secondary);
        using var name = theme.Body(12.5f, FontStyle.Bold);
        using var small = theme.Body(10.5f);
        using var count = theme.Countdown(11);
        using var primary = new SolidBrush(ItemNamePresentation.ForItem(pin.TargetItemId).Color ?? theme.Primary);
        using var secondary = new SolidBrush(theme.Secondary);
        using var accent = new SolidBrush(snapshot.IsComplete ? ProgressGold : theme.Mint);
        using var wrap = new StringFormat { Trimming = StringTrimming.EllipsisWord };
        graphics.DrawString(Name(language, snapshot.TargetNameDe, snapshot.TargetNameEn), name, primary, new RectangleF(64, HeaderHeight + 7, 282, 33), wrap);
        var label = snapshot.IsComplete ? Text(language, "Ziel erhalten", "Goal obtained")
            : snapshot.TargetIsCombo ? "COMBO · " + Text(language, "Ziel", "Goal") : Text(language, "Ziel", "Goal");
        if (snapshot.IsHistorical) label += Text(language, " · gespeichert", " · saved");
        Draw(graphics, label, small, secondary, new(64, HeaderHeight + 44, 175, 17));
        Draw(graphics, Number(pin.ConfirmedQuantity, language) + " / " + Number(pin.TargetQuantity, language), count, accent,
            new(234, HeaderHeight + 44, 112, 17), true);
        using var trough = new SolidBrush(Color.FromArgb(110, theme.Border));
        graphics.FillRectangle(trough, 64, HeaderHeight + 65, 282, 3);
        graphics.FillRectangle(accent, 64, HeaderHeight + 65, (float)(282 * Math.Clamp((double)pin.ConfirmedQuantity / pin.TargetQuantity, 0, 1)), 3);
    }
    private static void PaintRow(Graphics graphics, CraftingProgressRow row, int y, OverlayThemeDefinition theme,
        string language, bool animated, LootOverlayIconCache icons)
    {
        var bounds = new RectangleF(7, y + 1, 346, RowHeight - 3);
        if (row.IsComplete)
            LootRarityVisuals.PaintBackground(graphics, bounds, ProgressGold, row.EventAgeSeconds,
                animated && row.EventAgeSeconds < 1, .32, .7, true, 4, 2);
        else if (row.EventAgeSeconds < .8)
        {
            using var highlight = new SolidBrush(Color.FromArgb(38, ProgressGold));
            graphics.FillRectangle(highlight, bounds);
        }
        var icon = new RectangleF(12, y + 8, 32, 32);
        if (!icons.Draw(graphics, Asset(row.Icon), icon)) theme.Diamond(graphics, new(28, y + 24), 6, theme.Secondary);
        using var name = theme.Body(12.5f, FontStyle.Bold);
        using var small = theme.Body(10.5f);
        using var count = theme.Countdown(11);
        using var ink = new SolidBrush(ItemNamePresentation.ForItem(row.ItemId).Color ?? theme.Primary);
        using var secondary = new SolidBrush(theme.Secondary);
        using var accent = new SolidBrush(row.IsComplete ? ProgressGold : theme.Mint);
        Draw(graphics, Name(language, row.NameDe, row.NameEn), name, ink, new(54, y + 3, 235, 19));
        Draw(graphics, "+" + Number(row.Quantity, language), count, accent, new(290, y + 4, 56, 18), true);
        Draw(graphics, Sources(row, language), small, secondary, new(54, y + 23, 292, 16));
        var coverage = Coverage(row, language);
        if (row.IsCombo) coverage = "COMBO · " + coverage;
        Draw(graphics, coverage, small, secondary, new(54, y + 41, 292, 17));
        using var trough = new SolidBrush(Color.FromArgb(100, theme.Border));
        graphics.FillRectangle(trough, 54, y + RowHeight - 4, 292, 2);
        if (row.CoverageFraction is { } fraction) graphics.FillRectangle(accent, 54, y + RowHeight - 4, (float)(292 * fraction), 2);
    }
    internal static string Coverage(CraftingProgressRow row, string language)
    {
        if (row.KnownQuantity is not { } quantity) return Text(language, "Bestand unbekannt", "Inventory unknown");
        if (row.IsTarget) return Text(language, "Neu erhalten: ", "Newly obtained: ") + Number(quantity, language)
            + (row.RequiredQuantity is { } target ? " / " + target.ToString("N0", Culture(language)) : "");
        var prefix = row.IsManualStock ? Text(language, "Planbestand: ", "Planned stock: ")
            : !row.InventoryFresh ? Text(language, "Gespeichert: ", "Saved: ")
            : !row.InventoryComplete ? Text(language, "Bekanntes Minimum: ", "Known minimum: ") : "";
        return prefix + Number(quantity, language) + (row.RequiredQuantity is { } required
            ? " / ~" + required.ToString("N1", Culture(language)) : "");
    }
    internal static string Sources(CraftingProgressRow row, string language) => string.Join(" · ", row.SourceQuantities.OrderBy(pair => pair.Key)
        .Select(pair => Source(pair.Key, language) + " +" + Number(pair.Value, language)));
    private static string Source(CraftingProgressSource source, string language) => source switch
    {
        CraftingProgressSource.Craft => "Craft", CraftingProgressSource.Market => Text(language, "Markt", "Market"),
        CraftingProgressSource.Gathering => Text(language, "Sammeln", "Gathering"), _ => Text(language, "Sonstiges", "Other")
    };
    private static string Name(string language, string de, string en) => language == "de" && !string.IsNullOrWhiteSpace(de) ? de : en;
    private static string Text(string language, string de, string en) => language == "de" ? de : en;
    private static CultureInfo Culture(string language) => CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
    private static string Number(long value, string language) => value.ToString("N0", Culture(language));
    private static void Draw(Graphics graphics, string text, Font font, Brush brush, RectangleF bounds, bool right = false)
    {
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap, Alignment = right ? StringAlignment.Far : StringAlignment.Near };
        graphics.DrawString(text, font, brush, bounds, format);
    }
    private static string? Asset(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var parts = icon.Replace('\\', '/').Split('/');
        if (parts.Length != 2 || parts[0] is not ("loot-icons" or "crafting-icons")
            || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.Contains(':'))) return null;
        return Path.Combine(AppContext.BaseDirectory, "wwwroot", parts[0], parts[1]);
    }
}
