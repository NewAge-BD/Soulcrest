using System.Drawing;
using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Soulcrest.Ocr;
using Xunit;
namespace Soulcrest.App.Tests;
public sealed class ExplorationLayoutTests
{
    [Theory]
    [InlineData(2559,1439,563)]
    [InlineData(1920,1080,423)]
    public void MaskUsesFixedWindowProportions(int width, int height, int right)
    {
        var layout = ExplorationLayout.ForWindow(new Size(width,height));
        Assert.Equal(0,layout.Bounds.Left); Assert.Equal(right,layout.Bounds.Right);
        Assert.Equal((int)(height*.12),layout.Bounds.Top);
        Assert.Equal((int)(height*.98),layout.Bounds.Bottom);
    }
    [Fact]
    public async Task CaptureBudgetPreventsRapidReadbackBurstsAndHonorsCancellation()
    {
        using var capture = new Soulcrest.App.Capture.GameCaptureService();
        using var scan = new ExplorationScanService(new ExplorationService(new ProgressService()),capture);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i=0;i<4;i++) await scan.PaceCaptureAsync(CancellationToken.None);
        Assert.True(clock.ElapsedMilliseconds >= 700);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>scan.PaceCaptureAsync(cancel.Token));
    }
    [Fact]
    public void ScrollingInvalidatesTextFramesWhileIdenticalFramesStayVisible()
    {
        using var first = new Bitmap(563,1237);
        using (var g = Graphics.FromImage(first))
        {
            g.Clear(Color.FromArgb(30,30,30));
            using var font = new Font("Segoe UI",20);
            for (var y=30;y<1200;y+=75) g.DrawString("Dungeon name " + y,font,Brushes.LightGray,40,y);
        }
        using var same = (Bitmap)first.Clone();
        Assert.True(ExplorationScanService.SameList(first,same));
        using var scrolled = new Bitmap(first.Width,first.Height);
        using (var g = Graphics.FromImage(scrolled)) { g.Clear(Color.FromArgb(30,30,30)); g.DrawImageUnscaled(first,0,-6); }
        Assert.False(ExplorationScanService.SameList(first,scrolled));
        Assert.True(ExplorationScanService.SameList(scrolled,scrolled));
    }
    [Fact]
    public void OverlayRendersRowsAndStatusWithoutOpeningAWindow()
    {
        var feedback = new ExplorationScanFeedback(new Rectangle(-2560,0,2559,1439), new Rectangle(28,298,414,1102),
            "Alle sichtbaren Namen erkannt – weiterscrollen.", 2, 2,
            [new(new Rectangle(48,341,200,20),true),new(new Rectangle(46,415,137,18),false)],true);
        using var image = Soulcrest.App.Overlay.ExplorationScanOverlayForm.CreateImage(feedback,"dungeon",feedback.Message,14,true,out var area);
        Assert.True(area.Contains(feedback.List!.Value));
        Assert.True(image.Width > feedback.List.Value.Width);
        Assert.Equal(0,image.GetPixel(0,0).A);
        var output = Environment.GetEnvironmentVariable("SOULCREST_OVERLAY_PREVIEW");
        if (!string.IsNullOrEmpty(output)) image.Save(output,System.Drawing.Imaging.ImageFormat.Png);
    }
    [Theory]
    [InlineData("2837a271-bd31-47f3-8f11-e969dbe1bd42", "kibelisk", 0, false)]
    [InlineData("3feac438-b6cf-4dde-a1a2-db8ffd2509fa", "kibelisk", 0, false)]
    [InlineData("0f3a05d5-f0e2-47c1-88ee-7c585e29937e", "dungeon", 14, false)]
    [InlineData("57b7d0e9-2ed8-403e-af86-995e1160ccbd", "dungeon", 9, true)]
    public async Task ProvidedScreenshotsLocateListAndKeepDungeonNames(string id, string kind, int count, bool end)
    {
        var folder = Environment.GetEnvironmentVariable("SOULCREST_LAYOUT_FIXTURES");
        if (string.IsNullOrEmpty(folder)) return;
        using var full = new Bitmap(Path.Combine(folder, "codex-clipboard-" + id + ".png"));
        var layout = ExplorationLayout.ForWindow(full.Size);
        using var left = full.Clone(layout.Bounds,full.PixelFormat);
        using var shifted = new Bitmap(left.Width, left.Height);
        using (var g = Graphics.FromImage(shifted)) g.DrawImageUnscaled(left,0,-6);
        Assert.False(ExplorationScanService.SameList(left,shifted));
        Assert.True(ExplorationScanService.SameList(left,left));
        var detected = await WindowsOcrLineReader.Create("auto", 1).ReadAsync(left);
        var lines = detected.Select(l => l with { X = l.X + layout.Bounds.X, Y = l.Y + layout.Bounds.Y }).ToArray();
        Assert.Equal(563, layout.Bounds.Right);
        if (kind == "kibelisk") Assert.NotEmpty(layout.Rows(lines));
        if (kind == "dungeon")
        {
            var service = new ExplorationService(new ProgressService());
            var page = ExplorationScanService.ReadNames(layout.Rows(lines),service.Places.Where(p=>p.Map=="altgard" && p.Kind=="dungeon").ToArray());
            Assert.Equal(count,page.Ids.Count); Assert.Equal(end,page.End);
        }
    }
}
