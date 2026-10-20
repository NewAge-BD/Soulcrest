using System.Diagnostics;
using System.Drawing.Drawing2D;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

public sealed partial class PetOverlayForm
{
    private const int LevelUpHeight = 82;
    internal sealed record LevelUpRow(string Name, int Level, bool IsMax, Image? Icon, double Progress, string? PetId = null);
    private readonly PetLevelUpAnimation _levelUps = new();
    private LevelUpRow? _levelUpRow;
    private IReadOnlyList<LootRow> _baseFocusRows = [], _baseLootRows = [];
    private LootRow? _baseLevelUpFallback;
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 33 };

    private void OnPetLeveled(PetLevelUp change)
    {
        // The source publishes on its worker. Only enqueue here; the UI timer owns all drawing.
        if (_settings.Current.OverlayEnabled)
            _levelUps.Enqueue(change, Stopwatch.GetTimestamp());
    }

    private LevelUpRow? CurrentLevelUp()
    {
        if (_levelUps.Sample(Stopwatch.GetTimestamp()) is not { } frame) return null;
        var change = frame.Change;
        var name = _progress.Catalog.Find(change.PetId)?.DisplayName(_settings.Current.LootNameLanguage) ?? change.PetId;
        return new(name, change.Level, change.IsMax, PetIcon(change.PetId), frame.Progress, change.PetId);
    }

    private void AnimateLevelUp(object? sender, EventArgs e)
    {
        if (PresentationHidden)
        {
            // Sampling advances the original monotonic deadline; hidden time is never replayed.
            if (_settings.Current.OverlayEnabled) RefreshLevelUpFrame();
            else ClearLevelUp();
            return;
        }
        if (!Visible || Suppressed || !_settings.Current.OverlayEnabled)
        {
            ClearLevelUp();
            return;
        }
        RefreshLevelUpFrame();
        Invalidate();
    }

    private void ClearLevelUp(bool updateSize = true)
    {
        _levelUps.SetEnabled(false);
        _levelUpRow = null;
        _baseLevelUpFallback = null;
        _focusRows = _baseFocusRows;
        _lootRows = _baseLootRows;
        _animationTimer.Stop();
        if (updateSize && !Disposing && !IsDisposed)
            SetLogicalHeight(LootLogicalHeight(_focusRows.Count, _lootRows.Count));
    }

    private void RefreshLevelUpFrame()
    {
        var frame = CurrentLevelUp();
        if (frame?.PetId != _levelUpRow?.PetId || frame?.Level != _levelUpRow?.Level || frame?.IsMax != _levelUpRow?.IsMax)
        {
            // A queued pet or an expired hint changes placement; rebuild data once, not every paint.
            RefreshLayout();
            return;
        }
        ApplyLevelUpFrame(frame);
    }

    private void ApplyLevelUpFrame(LevelUpRow? frame)
    {
        // Height, row placement and Paint share this UI snapshot even during worker notifications.
        _levelUpRow = frame;
        var rows = PlaceLevelUpRows(_baseFocusRows, _baseLootRows, frame, _baseLevelUpFallback);
        _focusRows = rows.Focus;
        _lootRows = rows.Drops;
        SetLogicalHeight(LootLogicalHeight(_focusRows.Count, _lootRows.Count, frame is { IsMax: true }));
        if (frame is not null) _animationTimer.Start();
        else _animationTimer.Stop();
    }

    private static (double Opacity, double Pulse, double Sweep) LevelUpEffects(double value)
    {
        var progress = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;
        var elapsed = progress * PetLevelUpAnimation.DurationSeconds;
        var opacity = Math.Clamp(Math.Min(elapsed / .25, (PetLevelUpAnimation.DurationSeconds - elapsed) / 1.2), 0, 1);
        var pulse = elapsed < 3 ? Math.Pow(Math.Sin(elapsed / 3 * Math.PI * 2), 2) : 0;
        return (opacity, pulse, elapsed / 2.2);
    }

    private static void DrawInlineLevelUp(Graphics g, LevelUpRow row, int y, GraphicsPath shape)
    {
        var (opacity, pulse, sweepPhase) = LevelUpEffects(row.Progress);
        if (opacity <= 0) return;
        Color A(Color color) => Color.FromArgb((int)Math.Round(color.A * opacity), color);
        var gold = Color.FromArgb(250, 215, 130);
        var bounds = new RectangleF(10, y, BaseWidth - 20, RowHeight - 4);
        using (var tint = new LinearGradientBrush(bounds, A(Color.FromArgb(78, 126, 92, 32)),
            A(Color.FromArgb(10, 126, 92, 32)), LinearGradientMode.Horizontal)) g.FillPath(tint, shape);
        if (sweepPhase < 1)
        {
            var state = g.Save();
            g.SetClip(shape, CombineMode.Intersect);
            var sweep = new RectangleF(bounds.Left - 110 + (float)(sweepPhase * (bounds.Width + 220)), bounds.Top, 110, bounds.Height);
            using var light = new LinearGradientBrush(sweep, Color.Transparent, A(Color.FromArgb(38, gold)), LinearGradientMode.Horizontal);
            light.SetBlendTriangularShape(.5f);
            g.FillRectangle(light, sweep);
            g.Restore(state);
        }
        using (var edge = new Pen(A(Color.FromArgb((int)(160 + 60 * pulse), gold)), 1.3f)) g.DrawPath(edge, shape);
        using (var rail = new Pen(A(gold), 3)) g.DrawLine(rail, 11.5f, y + 14, 11.5f, y + RowHeight - 18);
        var halo = RectangleF.Inflate(new RectangleF(18, y + 14, 36, 36), (float)(3 + 3 * pulse), (float)(3 + 3 * pulse));
        using var ring = new Pen(A(Color.FromArgb((int)(65 + 100 * pulse), gold)), 1.5f);
        g.DrawEllipse(ring, halo);
    }

    /// <summary>Static geometry, a soft light sweep and portrait pulse; no focus or window movement.</summary>
    private static void DrawLevelUp(Graphics g, LevelUpRow row, int y)
    {
        var (opacity, pulse, sweepPhase) = LevelUpEffects(row.Progress);
        if (opacity <= 0) return;
        Color A(Color color) => Color.FromArgb((int)Math.Round(color.A * opacity), color);
        var accent = row.IsMax ? Color.FromArgb(157, 245, 185) : Color.FromArgb(250, 215, 130);
        var bounds = new RectangleF(10, y + 7, BaseWidth - 20, LevelUpHeight - 14);
        using var shape = RoundedRectangle(bounds, 8);
        using (var plate = new LinearGradientBrush(bounds, A(row.IsMax ? Color.FromArgb(31, 66, 54) : Color.FromArgb(65, 53, 33)),
            A(CardColor), LinearGradientMode.Horizontal)) g.FillPath(plate, shape);

        // One restrained sweep during the introduction, clipped inside the card and behind its text.
        if (sweepPhase < 1)
        {
            var state = g.Save();
            g.SetClip(shape, CombineMode.Intersect);
            var sweep = new RectangleF(bounds.Left - 110 + (float)(sweepPhase * (bounds.Width + 220)), bounds.Top, 110, bounds.Height);
            using var light = new LinearGradientBrush(sweep, Color.Transparent, A(Color.FromArgb(38, accent)), LinearGradientMode.Horizontal);
            light.SetBlendTriangularShape(.5f);
            g.FillRectangle(light, sweep);
            g.Restore(state);
        }
        using (var edge = new Pen(A(Color.FromArgb((int)(140 + 70 * pulse), accent)), 1.3f)) g.DrawPath(edge, shape);
        using (var rail = new Pen(A(accent), 3)) g.DrawLine(rail, 11.5f, bounds.Y + 15, 11.5f, bounds.Bottom - 15);

        var icon = new Rectangle(22, y + 23, 36, 36);
        using (var backing = new SolidBrush(A(PanelColor))) g.FillEllipse(backing, icon);
        if (row.Icon is { } image)
        {
            var state = g.Save();
            using var clip = new GraphicsPath();
            clip.AddEllipse(icon);
            g.SetClip(clip, CombineMode.Intersect);
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)opacity });
            g.DrawImage(image, icon, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            g.Restore(state);
        }
        else
        {
            using var symbol = new Font("Segoe UI", 17, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(A(accent));
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("◆", symbol, brush, icon, center);
        }
        using (var ring = new Pen(A(accent), 1.8f)) g.DrawEllipse(ring, icon);
        var halo = RectangleF.Inflate(icon, (float)(3 + 4 * pulse), (float)(3 + 4 * pulse));
        using (var glow = new Pen(A(Color.FromArgb((int)(35 + 95 * pulse), accent)), 1.5f)) g.DrawEllipse(glow, halo);

        const int textX = 72;
        using var captionFont = new Font("Segoe UI Semibold", 10.5f, GraphicsUnit.Pixel);
        using var nameFont = new Font("Segoe UI Semibold", 13, GraphicsUnit.Pixel);
        using var detailFont = new Font("Segoe UI", 11, GraphicsUnit.Pixel);
        using var metricFont = new Font("Segoe UI Semibold", row.IsMax ? 15 : 18, GraphicsUnit.Pixel);
        using var bright = new SolidBrush(A(Color.FromArgb(242, 246, 248)));
        using var accentBrush = new SolidBrush(A(accent));
        using var secondary = new SolidBrush(A(Color.FromArgb(190, 205, 212)));
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(UiText.T(row.IsMax ? "Pet vervollständigt!" : "Stufenaufstieg!"), captionFont, accentBrush,
            new RectangleF(textX, y + 14, 262, 15), format);
        g.DrawString(row.Name, nameFont, bright, new RectangleF(textX, y + 31, 192, 19), format);
        g.DrawString(row.IsMax ? UiText.T("Maximalstufe erreicht") : UiText.F("Stufe {0} erreicht", row.Level), detailFont,
            secondary, new RectangleF(textX, y + 51, 262, 16), format);
        var metric = new RectangleF(274, y + 31, 60, 24);
        using var centerMetric = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(row.IsMax ? "MAX" : $"↑ {row.Level}", metricFont, accentBrush, metric, centerMetric);
    }
}
