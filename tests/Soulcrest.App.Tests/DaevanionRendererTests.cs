using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Network;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionRendererTests
{
    [Theory]
    [InlineData("nezekan")]
    [InlineData("zikel")]
    [InlineData("vaizel")]
    [InlineData("triniel")]
    [InlineData("ariel")]
    [InlineData("azphel")]
    public void EveryClassRendersBothLanguagesWithoutTouchingNativeFrames(string family)
    {
        using var renderer = new DaevanionOverlayRenderer();
        foreach (var character in DaevanionCatalog.Default.Classes)
        foreach (var language in new[] { "de", "en" })
        {
            var board = DaevanionCatalog.Default.GetBoard(character.Id, family)!;
            var node = board.Nodes.First(n => n.Kind != DaevanionNodeKind.Start);
            DaevanionNodeAnchor[] anchors = [new(node.Row, node.Column, new(10, 10, 84, 84), 1)];
            using var image = renderer.Render(new(104, 104), board, anchors, language);
            Assert.Equal(0, image.GetPixel(10, 10).A);
            Assert.Equal(0, image.GetPixel(12, 50).A);
            Assert.True(image.GetPixel(40, 40).A > 0);
            using var preview = renderer.Preview(character.Id, family, language);
            Assert.True(preview.Width > 500 && preview.Height > 500);
            if (character.Id == "Cleric") Export(preview, family + "-" + language + ".png");
        }
    }

    [Fact]
    public void StartAndUnconfirmedNodesLeaveNoPaintAtAll()
    {
        using var renderer = new DaevanionOverlayRenderer();
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var start = board.Nodes.Single(n => n.Kind == DaevanionNodeKind.Start);
        using var image = renderer.Render(new(140, 100), board,
            [new(start.Row, start.Column, new(10, 10, 60, 60), 1), new(1, 1, new(75, 10, 60, 60), 1)], "en");
        Assert.All(Pixels(image), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData("ariel")]
    [InlineData("azphel")]
    public void DoubleValuesPaintTwoSeparateReadableRows(string family)
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", family)!;
        var node = board.Nodes.First(n => n.Effects.Count == 2);
        using var renderer = new DaevanionOverlayRenderer();
        using var image = renderer.Render(new(110, 110), board, [new(node.Row, node.Column, new(10, 10, 90, 90), 1)], "en");
        bool HasText(int top, int bottom) => Enumerable.Range(top, bottom - top).Any(y => Enumerable.Range(25, 58).Any(x =>
        { var pixel = image.GetPixel(x, y); return pixel.R > 170 && pixel.G > 150; }));
        Assert.True(HasText(26, 54));
        Assert.True(HasText(56, 84));
    }

    [Fact]
    public void OriginalSkillColorIsPresentAndBordersRemainTransparent()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var node = board.Nodes.First(n => n.Kind == DaevanionNodeKind.Skill);
        using var renderer = new DaevanionOverlayRenderer();
        using var image = renderer.Render(new(100, 100), board, [new(node.Row, node.Column, new(5, 5, 90, 90), 1)], "en");
        Assert.Contains(Enumerable.Range(25, 50), y => Enumerable.Range(25, 50).Any(x =>
        { var p = image.GetPixel(x, y); return Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B)) > 45; }));
        Assert.Equal(0, image.GetPixel(6, 50).A);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    public void RoundStatAndSkillInteriorsLeaveTheNativeActivationRingAndCornersUntouched(int nodeSize)
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        using var renderer = new DaevanionOverlayRenderer();
        foreach (var kind in new[] { DaevanionNodeKind.Stat, DaevanionNodeKind.Skill })
        {
            var node = board.Nodes.First(n => n.Kind == kind);
            using var image = renderer.Render(new(nodeSize + 20, nodeSize + 20), board,
                [new(node.Row, node.Column, new(10, 10, nodeSize, nodeSize), 1)], "en");
            var center = 10 + nodeSize / 2d;
            Assert.True(image.GetPixel((int)center, (int)center).A > 0);
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var distance = Math.Sqrt(Math.Pow(x + .5 - center, 2) + Math.Pow(y + .5 - center, 2));
                // The original activation ring reaches radius .39-.41; keep a clear margin to it.
                if (distance >= nodeSize * .36) Assert.Equal(0, image.GetPixel(x, y).A);
            }
        }
    }

    [Fact]
    public void MissingSkillImageLeavesTheOriginalNodeCompletelyUncovered()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var node = board.Nodes.First(n => n.Kind == DaevanionNodeKind.Skill);
        using var renderer = new DaevanionOverlayRenderer(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var image = renderer.Render(new(100, 100), board, [new(node.Row, node.Column, new(5, 5, 90, 90), 1)], "en");
        Assert.All(Pixels(image), value => Assert.Equal(0, value));
    }

    [Fact]
    public void StaticArtworkCanBeReusedButOcclusionAndContextChangesRequireANewSurface()
    {
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var context = new OwnCharacterContext(new(101, 501, 300, "SyntheticBoard") { ClassId = "Cleric" }, 1);
        var detection = new DaevanionBoardDetection("nezekan", new(10, 10), 50, 40,
            [new(3, 3, new(100, 100, 40, 40), .9)], 1);
        var first = new DaevanionSnapshot(context, board, new(0, 0, 800, 800), 1, detection, "en");
        Assert.True(DaevanionOverlayForm.HasSameContent(first, first with { FrameAt = 2,
            Detection = detection with { Nodes = [detection.Nodes[0] with { Confidence = .8 }] } }));
        Assert.False(DaevanionOverlayForm.HasSameContent(first, first with { Detection = detection with { Nodes = [] } }));
        Assert.False(DaevanionOverlayForm.HasSameContent(first, first with { Language = "de" }));
        Assert.False(DaevanionOverlayForm.HasSameContent(first, first with { Context = context with { Generation = 2 } }));
        Assert.False(DaevanionOverlayForm.HasSameContent(first, first with { GameBounds = new(1, 0, 800, 800) }));
    }

    [Fact]
    public void ThemeChangesDoNotChangeTheBoardAndTheNinthOverlayIsBound()
    {
        using var renderer = new OverlayPreviewRenderer(new PetCatalog([]));
        int[]? reference = null;
        foreach (var theme in OverlayThemeDefinition.All)
        {
            using var image = renderer.Render("daevanion", new AppSettings { OverlayTheme = theme.Id }, 620);
            if (reference is null) reference = Pixels(image); else Assert.Equal(reference, Pixels(image));
        }
        Assert.False(OverlayRegistry.Get("daevanion").CanPlace);
        var settings = new AppSettings();
        Assert.True(settings.DaevanionOverlayEnabled);
        Assert.True(OverlayHotkeys.ToggleVisibility(settings, "daevanion.toggle"));
        Assert.False(settings.DaevanionOverlayEnabled);
        Assert.False(OverlayHotkeys.Defaults().ContainsKey("daevanion.toggle"));
        Assert.DoesNotContain(OverlayHotkeys.Actions, action => action.Id == "daevanion.unlock");
    }

    [Fact]
    public void LevelUpAnimationKeepsTheSameConfirmedProductionSurface()
    {
        var catalog = DaevanionCatalog.Default;
        var board = catalog.GetBoard("Cleric", "vaizel")!;
        var patterns = catalog.Boards.Where(b => b.ClassId == "Cleric").Select(b => new DaevanionBoardPattern(b.FamilyId,
            b.Nodes.Select(n => new DaevanionPatternCell(n.Row, n.Column, n.Kind == DaevanionNodeKind.Start)).ToArray())).ToArray();
        var stability = new DaevanionLayoutStability();
        var context = new OwnCharacterContext(new(101, 501, 300, "SyntheticBoard") { ClassId = "Cleric" }, 1);
        DaevanionSnapshot? previous = null;
        using var renderer = new DaevanionOverlayRenderer();
        foreach (var phase in new[] { "early", "peak", "trough", "selected-minimum", "contour-minimum", "peak", "early" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "daevanion", $"vaizel-animation-{phase}.png");
            using var source = OpenCvSharp.Cv2.ImRead(path);
            Assert.False(source.Empty());
            var bounds = new Rectangle(0, 0, source.Width, source.Height);
            var found = Assert.IsType<DaevanionBoardDetection>(DaevanionBoardLocator.Find(source, patterns, stability.Preferred("video", bounds)));
            Assert.Equal("vaizel", found.Family);
            Assert.Equal(88, found.Nodes.Count);
            // Measure the native frame, not the enlarged outer glow, so the painter stays inside it.
            Assert.InRange(found.NodeSize, 64, 73);
            var ready = stability.Update("video", bounds, System.Diagnostics.Stopwatch.GetTimestamp(), found);
            if (!ready) { Assert.Null(previous); continue; }
            var current = new DaevanionSnapshot(context, board, bounds, stability.ConfirmedFrameAt, stability.Confirmed!, "en");
            if (previous is not null) Assert.True(DaevanionOverlayForm.HasSameContent(previous, current));
            previous = current;
            if (phase != "peak") continue;
            using var image = new Bitmap(path);
            using (var graphics = Graphics.FromImage(image)) renderer.Paint(graphics, board, current.Detection.Nodes, "en");
            Export(image, "daevanion-animation.png");
        }
        Assert.NotNull(previous);
    }

    [Fact]
    public void ExportActualScreenshotWithProductionPainterWhenRequested()
    {
        var input = Environment.GetEnvironmentVariable("SOULCREST_DAEVANION_SCREENSHOT");
        var output = Environment.GetEnvironmentVariable("SOULCREST_DAEVANION_PREVIEW");
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) return;
        var catalog = DaevanionCatalog.Default;
        var patterns = catalog.Boards.GroupBy(b => b.FamilyId).Select(g => new DaevanionBoardPattern(g.Key,
            g.First().Nodes.Select(n => new DaevanionPatternCell(n.Row, n.Column, n.Kind == DaevanionNodeKind.Start)).ToArray())).ToArray();
        using var source = OpenCvSharp.Cv2.ImRead(input);
        var detection = DaevanionBoardLocator.Find(source, patterns);
        Assert.NotNull(detection);
        Assert.Equal("nezekan", detection.Family);
        Assert.Equal(88, detection.Nodes.Count);
        using var image = new Bitmap(input);
        using var renderer = new DaevanionOverlayRenderer();
        using (var graphics = Graphics.FromImage(image)) renderer.Paint(graphics, catalog.GetBoard("Cleric", detection.Family)!, detection.Nodes, "en");
        // Keep the supplied board and its tabs, excluding account balances and unrelated panels.
        var first = detection.Nodes.Min(n => n.Bounds.X);
        var last = detection.Nodes.Max(n => n.Bounds.Right);
        var top = detection.Nodes.Min(n => n.Bounds.Y);
        var bottom = detection.Nodes.Max(n => n.Bounds.Bottom);
        var spacing = (int)Math.Round(detection.Spacing);
        var crop = Rectangle.Intersect(new Rectangle(0, 0, image.Width, image.Height),
            Rectangle.FromLTRB(Math.Max(0, first - spacing), Math.Max(80, top - spacing),
                Math.Min(image.Width, last + spacing * 2), Math.Min(image.Height, bottom + spacing / 3)));
        using var social = image.Clone(crop, PixelFormat.Format32bppArgb);
        Export(social, "daevanion.png");
    }

    private static int[] Pixels(Bitmap image)
    {
        var result = new int[image.Width * image.Height];
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (var y = 0; y < image.Height; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, result, y * image.Width, image.Width); }
        finally { image.UnlockBits(data); }
        return result;
    }
    private static void Export(Bitmap image, string name)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_DAEVANION_PREVIEW") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output); image.Save(Path.Combine(output, name), ImageFormat.Png);
    }
}
