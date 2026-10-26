using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

public sealed partial class PetOverlayForm
{
    private const int AionHeaderHeight = 36, AionSectionHeight = 22, AionRowHeight = 60, AionCelebrationHeight = 72;
    private bool _renderingTheme;

    private void ApplyTheme()
    {
        if (_dragStart is not null || _resizing != Corner.None) return;
        var requested = OverlayThemeDefinition.Resolve(_settings.Current.OverlayTheme);
        if (_theme.Id == requested.Id) return;
        _theme = requested;
        LayeredOverlayPresenter.Configure(this, !_theme.IsLegacy, .94);
        UpdateWindowShape();
        _lockButton.SetTheme(_theme);
    }

    private Rectangle LockBounds()
    {
        if (_theme.IsLegacy)
        {
            var size = Math.Max(1, (int)Math.Round(28 * DeviceDpi / 96d));
            var inset = Math.Max(0, (int)Math.Round(8 * DeviceDpi / 96d));
            return new(Math.Max(0, ClientSize.Width - size - inset), inset, size, size);
        }
        var bounds = Measure(_focusRows.Count, _lootRows.Count, _levelUpRow is { IsMax: true }, _theme).LockBounds;
        return Rectangle.Round(new RectangleF((float)(bounds.X * _scale), (float)(bounds.Y * _scale),
            (float)(bounds.Width * _scale), (float)(bounds.Height * _scale)));
    }

    private void RenderPanel()
    {
        if (_theme.IsLegacy) { Invalidate(); return; }
        if (_renderingTheme || !IsHandleCreated || IsDisposed || Disposing || PresentationHidden || _suppressed) return;
        _renderingTheme = true;
        try
        {
            using var image = RenderLootBitmap(_focusRows, _lootRows, _tracker.Running, _locked, _levelUpRow, _theme, _scale);
            LayeredOverlayPresenter.Present(this, image);
            _lockButton?.RefreshTarget();
        }
        finally { _renderingTheme = false; }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { if (_theme.IsLegacy) base.OnPaintBackground(e); }

    internal static Bitmap RenderLootBitmap(IReadOnlyList<LootRow> focus, IReadOnlyList<LootRow> drops,
        bool running, bool locked, LevelUpRow? levelUp, OverlayThemeDefinition theme, double scale = 1)
    {
        var height = LootLogicalHeight(focus.Count, drops.Count, levelUp is { IsMax: true }, theme);
        var bitmap = new Bitmap(Math.Max(1, (int)Math.Round(BaseWidth * scale)),
            Math.Max(1, (int)Math.Round(height * scale)), PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform((float)scale, (float)scale);
            PaintLootPanel(graphics, focus, drops, running, locked, levelUp, theme);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    internal sealed record PetPanelLayout(Size Size, RectangleF HeaderBounds, RectangleF DragBounds, RectangleF LockBounds,
        int ContentTop, int SectionHeight, int RowHeight, int CelebrationHeight, int FooterHeight);

    internal static PetPanelLayout Measure(int focus, int drops, bool celebrating = false, OverlayThemeDefinition? theme = null)
    {
        var legacy = theme is not { IsLegacy: false };
        var header = legacy ? 54 : AionHeaderHeight;
        var section = legacy ? 24 : AionSectionHeight;
        var row = legacy ? RowHeight : AionRowHeight;
        var celebration = legacy ? LevelUpHeight : AionCelebrationHeight;
        var footer = legacy ? 28 : 22;
        var padding = legacy ? 8 : 6;
        var empty = focus == 0 && drops == 0 && !celebrating;
        var height = header + (celebrating ? celebration : 0)
            + (focus == 0 ? 0 : section + focus * row + padding)
            + (drops == 0 ? 0 : section + drops * row + padding) + (empty ? 0 : footer);
        return new(new(BaseWidth, height), new(0, 0, BaseWidth, header), new(0, 0, BaseWidth, height),
            legacy ? new(BaseWidth - 36, 8, 28, 28) : new(BaseWidth - 28, 8, 18, 18),
            header, section, row, celebration, footer);
    }

    private static void PaintAionLootPanel(Graphics graphics, IReadOnlyList<LootRow> focus,
        IReadOnlyList<LootRow> drops, bool running, bool locked, LevelUpRow? levelUp, OverlayThemeDefinition theme)
    {
        var banner = levelUp is { IsMax: true } ? levelUp : null;
        var layout = Measure(focus.Count, drops.Count, banner is not null, theme);
        var height = layout.Size.Height;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        theme.Panel(graphics, new(0, 0, BaseWidth, height));
        using var heading = theme.Heading(15);
        using var small = theme.Body(10.5f);
        using var gold = new SolidBrush(theme.Gold);
        using var secondary = new SolidBrush(theme.Secondary);
        using var line = new Pen(theme.Border);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        theme.Diamond(graphics, new(13, 17), 4, running ? theme.Gold : theme.Secondary);
        graphics.DrawString(UiText.T("Pet-Fortschritt"), heading, gold, new RectangleF(25, 7, layout.LockBounds.Left - 34, 24), format);
        if (height == layout.ContentTop) return;
        graphics.DrawLine(line, 12, layout.ContentTop - 1, BaseWidth - 12, layout.ContentTop - 1);
        var y = layout.ContentTop;
        if (banner is not null)
        {
            PaintAionMaxLevel(graphics, banner, y, theme);
            y += layout.CelebrationHeight;
        }
        Section(UiText.T("Fokus"), focus);
        Section(UiText.T("Letzte Beute"), drops);
        graphics.DrawString(HotkeyHint(locked), small, secondary, new RectangleF(12, y + 3, BaseWidth - 24, 17), format);
        if (!locked)
        {
            using var grip = new Pen(theme.Gold, 2);
            graphics.DrawLines(grip, [new Point(BaseWidth - 15, height - 5), new Point(BaseWidth - 5, height - 5), new Point(BaseWidth - 5, height - 15)]);
        }

        void Section(string title, IReadOnlyList<LootRow> rows)
        {
            if (rows.Count == 0) return;
            theme.Diamond(graphics, new(15, y + 11), 2.5f, theme.Gold);
            graphics.DrawString(title, small, gold, new RectangleF(24, y + 3, BaseWidth - 40, 17), format);
            y += layout.SectionHeight;
            foreach (var row in rows)
            {
                PaintAionPetRow(graphics, row, y, theme, layout.RowHeight);
                y += layout.RowHeight;
            }
            y += 6;
        }
    }

    private static void PaintAionPetRow(Graphics graphics, LootRow row, int top, OverlayThemeDefinition theme, int rowHeight)
    {
        var icon = new Rectangle(12, top + 8, 32, 32);
        if (row.LevelUp is { IsMax: false } celebration)
            DrawThemedLevelUpEffects(graphics, celebration, new(6, top + 1, BaseWidth - 12, rowHeight - 2), icon, theme);
        Color A(Color color) => Color.FromArgb(row.Alpha * color.A / 255, color);
        using var name = theme.Body(12.5f, FontStyle.Bold);
        using var body = theme.Body(10.5f);
        using var count = theme.Countdown(11);
        using var primary = new SolidBrush(A(theme.Primary));
        using var secondary = new SolidBrush(A(theme.Secondary));
        using var mint = new SolidBrush(A(theme.Mint));
        using var gold = new SolidBrush(A(theme.Gold));
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat { Alignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        PaintAionPortrait(graphics, row.Icon, icon, row.Alpha, theme, GenusColor(row.Genus));
        var nameWidth = BaseWidth - 68 - (row.Quantity > 0 ? 49 : 0);
        graphics.DrawString(row.Name, name, primary, new RectangleF(54, top + 3, nameWidth, 19), format);
        if (row.Quantity > 0) graphics.DrawString("+" + row.Quantity, count, mint, new RectangleF(BaseWidth - 58, top + 4, 45, 18), right);
        if (!string.IsNullOrWhiteSpace(row.SoulName))
            graphics.DrawString(UiText.F("Soul: {0}", row.SoulName), body, secondary,
                new RectangleF(54, top + 23, BaseWidth - 68, 16), format);
        var level = row.LevelUp is { IsMax: false } up ? "↑ " + UiText.F("Stufe {0} erreicht", up.Level)
            : row.IsMax ? "MAX" : row.Level == 0 ? UiText.T("gesperrt") : UiText.F("Stufe {0}", row.Level);
        graphics.DrawString(level, body, row.LevelUp is not null ? gold : row.IsMax ? mint : secondary,
            new RectangleF(54, top + 40, BaseWidth - 162, 16), format);
        if (!row.IsMax) graphics.DrawString($"{row.Current} / {row.Needed}", count, primary,
            new RectangleF(BaseWidth - 95, top + 40, 82, 16), right);
        using var trough = new SolidBrush(A(Color.FromArgb(110, theme.Border)));
        graphics.FillRectangle(trough, 54, top + rowHeight - 4, BaseWidth - 67, 2);
        var fraction = row.IsMax || row.Needed == 0 ? 1 : Math.Clamp(row.Current / (double)row.Needed, 0, 1);
        graphics.FillRectangle(mint, 54, top + rowHeight - 4, (float)((BaseWidth - 67) * fraction), 2);
        if (row.LevelUp is { IsMax: false } upEffect)
        {
            var effects = LevelUpEffects(upEffect.Progress);
            theme.Diamond(graphics, new(48, top + 47), 2.5f, Color.FromArgb((int)(255 * effects.Opacity), theme.Gold));
        }
    }

    private static void PaintAionMaxLevel(Graphics graphics, LevelUpRow row, int top, OverlayThemeDefinition theme)
    {
        var effects = LevelUpEffects(row.Progress);
        var alpha = (int)Math.Round(effects.Opacity * 255);
        var icon = new Rectangle(12, top + 12, 40, 40);
        DrawThemedLevelUpEffects(graphics, row, new(6, top + 2, BaseWidth - 12, AionCelebrationHeight - 4), icon, theme);
        PaintAionPortrait(graphics, row.Icon, icon, alpha, theme, theme.Mint);
        using var heading = theme.Heading(14);
        using var body = theme.Body(11);
        using var mint = new SolidBrush(Color.FromArgb(alpha, theme.Mint));
        using var primary = new SolidBrush(Color.FromArgb(alpha, theme.Primary));
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString(UiText.T("Pet vervollständigt!"), heading, mint, new RectangleF(64, top + 8, BaseWidth - 77, 20), format);
        graphics.DrawString(row.Name, body, primary, new RectangleF(64, top + 30, BaseWidth - 77, 16), format);
        graphics.DrawString(UiText.T("Maximalstufe erreicht"), body, mint, new RectangleF(64, top + 47, BaseWidth - 77, 16), format);
    }

    private static void PaintAionPortrait(Graphics graphics, Image? image, Rectangle bounds, int alpha,
        OverlayThemeDefinition theme, Color accent)
    {
        if (image is not null)
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = alpha / 255f });
            graphics.DrawImage(image, bounds, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }
        else theme.Diamond(graphics, new(bounds.Left + bounds.Width / 2f, bounds.Top + bounds.Height / 2f),
            5, Color.FromArgb(alpha, accent));
        using var edge = new Pen(Color.FromArgb(alpha * accent.A / 255, accent));
        graphics.DrawRectangle(edge, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
