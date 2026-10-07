using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// Diagnose: renders the pet overlay with a few focus pets and fresh souls into a PNG
/// (SOULCREST_OVERLAY_PNG), on a temporary data folder. The live overlay is excluded from screenshots.
/// </summary>
public sealed class OverlayRenderExploration
{
    [Fact]
    public void RenderOverlay()
    {
        var target = Environment.GetEnvironmentVariable("SOULCREST_OVERLAY_PNG");
        if (string.IsNullOrEmpty(target))
            return;
        var directory = Path.Combine(Path.GetTempPath(), "soulcrest-overlay-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("SOULCREST_DATA");
        Environment.SetEnvironmentVariable("SOULCREST_DATA", directory);
        try
        {
            var settings = new SettingsService();
            var progress = new ProgressService();
            if (progress.MapDataDirectory is null)
                return;
            foreach (var (pet, souls) in new[] { ("kailin", 19), ("ursus", 52), ("young-ursus", 3), ("mumu-worker", 140) })
            {
                progress.SetSouls(pet, souls);
                progress.ToggleFocus(pet);
            }
            progress.AddSouls("young-kailin", 1, "network", "test");
            progress.AddSouls("tayga", 2, "network", "test");
            var tracker = new TrackerService(settings);
            using var scan = new PetScanService(settings, progress);
            using var form = new PetOverlayForm(progress, tracker, settings, scan);
            form.CreateControl();
            form.Size = new Size(360, 34 + 4 * 44 + 2 * 46 + 26);
            using var bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(target, ImageFormat.Png);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOULCREST_DATA", previous);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
