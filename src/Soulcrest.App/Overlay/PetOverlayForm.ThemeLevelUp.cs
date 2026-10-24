using System.Drawing.Drawing2D;

namespace Soulcrest.App.Overlay;

public sealed partial class PetOverlayForm
{
    /// <summary>Theme-sized celebration behind the existing row, without changing its layout or animation clock.</summary>
    private static void DrawThemedLevelUpEffects(Graphics graphics, LevelUpRow row, RectangleF bounds,
        Rectangle portrait, OverlayThemeDefinition theme)
    {
        // User request 2026-10-23: restore the Legacy celebration's sweep and pulse in every new theme.
        var (opacity, pulse, sweepPhase) = LevelUpEffects(row.Progress);
        if (opacity <= 0) return;
        var accent = row.IsMax ? theme.Mint : theme.Gold;
        Color A(int alpha) => Color.FromArgb((int)Math.Round(alpha * opacity), accent);
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds, CombineMode.Intersect);
            using (var tint = new LinearGradientBrush(bounds, A(64), A(6), LinearGradientMode.Horizontal))
                graphics.FillRectangle(tint, bounds);

            // The sweep repeats on the original animation clock, also during the final fade.
            if (sweepPhase < 1)
            {
                var sweep = new RectangleF(bounds.Left - 110 + (float)(sweepPhase * (bounds.Width + 220)),
                    bounds.Top, 110, bounds.Height);
                using var light = new LinearGradientBrush(sweep, Color.Transparent, A(56), LinearGradientMode.Horizontal);
                light.SetBlendTriangularShape(.5f);
                graphics.FillRectangle(light, sweep);
            }

            using (var edge = new Pen(A((int)(140 + 70 * pulse)), 1.3f))
                graphics.DrawRectangle(edge, bounds.X + .75f, bounds.Y + .75f, bounds.Width - 1.5f, bounds.Height - 1.5f);
            using (var rail = new Pen(A(230), 2))
                graphics.DrawLine(rail, bounds.Left + 1, bounds.Top + 10, bounds.Left + 1, bounds.Bottom - 10);

            // Square halos match the themed portraits; the original genus border remains on top.
            var halo = RectangleF.Inflate(portrait, (float)(2 + 3 * pulse), (float)(2 + 3 * pulse));
            using var glow = new Pen(A((int)(65 + 100 * pulse)), 1.5f);
            graphics.DrawRectangle(glow, halo.X, halo.Y, halo.Width, halo.Height);
        }
        finally { graphics.Restore(state); }
    }
}
