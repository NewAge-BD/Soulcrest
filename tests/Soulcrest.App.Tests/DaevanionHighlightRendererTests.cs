using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionHighlightRendererTests
{
    [Theory]
    [InlineData("nezekan", 8)]
    [InlineData("zikel", 8)]
    [InlineData("vaizel", 8)]
    [InlineData("triniel", 8)]
    [InlineData("ariel", 14)]
    [InlineData("azphel", 12)]
    public void EveryClassHasTheQualifiedStatTypesExpectedForItsBoard(string family, int expected)
    {
        var catalog = DaevanionCatalog.Default;
        var boards = catalog.Boards.Where(b => b.FamilyId == family).ToArray();
        Assert.Equal(8, boards.Length);
        var reference = Stats(boards[0]);
        Assert.Equal(expected, reference.Length);
        foreach (var board in boards)
        {
            Assert.Equal(reference, Stats(board));
            Assert.All(Stats(board), stat => Assert.NotNull(catalog.GetStat(stat)));
        }
        if (family == "ariel")
        {
            Assert.Contains(reference, id => id.StartsWith("PvE", StringComparison.Ordinal));
            Assert.Contains(reference, id => id.StartsWith("BossNpc", StringComparison.Ordinal));
        }
        if (family == "azphel") Assert.Contains(reference, id => id.StartsWith("PvP", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("nezekan")]
    [InlineData("zikel")]
    [InlineData("vaizel")]
    [InlineData("triniel")]
    [InlineData("ariel")]
    [InlineData("azphel")]
    public void OnlySelectedStatsReceiveAnOutsideRim(string family)
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", family)!;
        var node = original.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat);
        var board = original with { Nodes = [node] };
        var frame = new Rectangle(24, 24, 60, 60);
        DaevanionNodeAnchor[] anchors = [new(node.Row, node.Column, new(frame.X, frame.Y, frame.Width, frame.Height), 1)];
        var renderer = new DaevanionHighlightRenderer();
        using var empty = renderer.Render(new(128, 128), board, anchors, Selected());
        using var other = renderer.Render(new(128, 128), board, anchors, Selected("not-a-stat"));
        using var match = renderer.Render(new(128, 128), board, anchors, Selected(node.Effects[0].StatId!));
        Assert.All(Pixels(empty), pixel => Assert.Equal(0, pixel.Color.A));
        Assert.Equal(RawPixels(empty), RawPixels(other));
        Assert.Contains(Pixels(match), pixel => pixel.Color.A > 180 && pixel.Color.R > 230 && pixel.Color.G > 220);
        Assert.DoesNotContain(Pixels(match), pixel => frame.Contains(pixel.X, pixel.Y) && pixel.Color.A > 0);
        var envelope = DaevanionNodeFitting.Bounds(frame);
        Assert.All(Pixels(match).Where(pixel => pixel.Color.A > 0), pixel =>
            Assert.True(envelope.IntersectsWith(new RectangleF(pixel.X, pixel.Y, 1, 1))));
    }

    [Theory]
    [InlineData("ariel", 0d)]
    [InlineData("azphel", 0d)]
    [InlineData("ariel", .25d)]
    [InlineData("azphel", .75d)]
    public void EitherBonusMatchesADoubleNodeWithoutDrawingTheContourTwice(string family, double phase)
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", family)!;
        var node = original.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat && n.Effects.Count == 2);
        var board = original with { Nodes = [node] };
        DaevanionNodeAnchor[] anchors = [new(node.Row, node.Column, new(24, 24, 60, 60), 1)];
        var renderer = new DaevanionHighlightRenderer();
        using var first = renderer.Render(new(128, 128), board, anchors, Selected(node.Effects[0].StatId!), phase: phase);
        using var second = renderer.Render(new(128, 128), board, anchors, Selected(node.Effects[1].StatId!), phase: phase);
        using var both = renderer.Render(new(128, 128), board, anchors, Selected(node.Effects[0].StatId!, node.Effects[1].StatId!), phase: phase);
        Assert.Contains(Pixels(first), pixel => pixel.Color.A > 0);
        Assert.Equal(RawPixels(first), RawPixels(second));
        Assert.Equal(RawPixels(first), RawPixels(both));
    }

    [Theory]
    [InlineData("PvEAddDamage")]
    [InlineData("PvPAddDamage")]
    [InlineData("BossNpcAddDamage")]
    public void QualifiedStatsDoNotSelectAnotherCombatContext(string selectedId)
    {
        string[] ids = ["PvEAddDamage", "PvPAddDamage", "BossNpcAddDamage"];
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var seed = original.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat);
        var cells = ids.Select((id, index) => seed with
        {
            Id = 100 + index, Row = 3, Column = 3 + index,
            Effects = [new() { Kind = DaevanionEffectKind.StatBonus, StatId = id }]
        }).ToArray();
        var anchors = cells.Select((node, index) => new DaevanionNodeAnchor(node.Row, node.Column,
            new(24 + index * 76, 24, 60, 60), 1)).ToArray();
        var renderer = new DaevanionHighlightRenderer();
        using var image = renderer.Render(new(300, 128), original with { Nodes = cells }, anchors, Selected(selectedId), spacing: 76);
        var painted = Pixels(image).Where(pixel => pixel.Color.A > 0).ToArray();
        for (var index = 0; index < ids.Length; index++)
        {
            var bounds = DaevanionNodeFitting.Bounds(new Rectangle(24 + index * 76, 24, 60, 60), 76);
            if (ids[index] == selectedId) Assert.Contains(painted, pixel => bounds.IntersectsWith(new RectangleF(pixel.X, pixel.Y, 1, 1)));
            else Assert.DoesNotContain(painted, pixel => bounds.IntersectsWith(new RectangleF(pixel.X, pixel.Y, 1, 1)));
        }
    }

    [Fact]
    public void SkillsAndStartAreNeverHighlightedEvenIfTheirPayloadIsUnrelatedOrMalformed()
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
        var stat = original.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat);
        var start = stat with { Id = 1, Row = 1, Kind = DaevanionNodeKind.Start };
        var skill = stat with { Id = 2, Row = 2, Kind = DaevanionNodeKind.Skill };
        var board = original with { Nodes = [start, skill] };
        var renderer = new DaevanionHighlightRenderer();
        using var image = renderer.Render(new(160, 200), board,
            [new(start.Row, start.Column, new(24, 24, 60, 60), 1), new(skill.Row, skill.Column, new(24, 100, 60, 60), 1)],
            Selected(stat.Effects[0].StatId!), spacing: 76);
        Assert.All(Pixels(image), pixel => Assert.Equal(0, pixel.Color.A));
    }

    [Theory]
    [InlineData(48, 61, 0d)]
    [InlineData(60, 76, 0d)]
    [InlineData(90, 114, 0d)]
    [InlineData(100, 116, 0d)]
    [InlineData(60, 62, 0d)]
    [InlineData(60, 61, 0d)]
    [InlineData(48, 61, .25d)]
    [InlineData(60, 76, .5d)]
    [InlineData(100, 116, .75d)]
    public void NoNativeFrameOrUnconfirmedNeighborReceivesAnyPaint(int nodeSize, int spacing, double phase)
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var seed = original.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat) with { Row = 3, Column = 3 };
        var cells = Enumerable.Range(0, 3).SelectMany(row => Enumerable.Range(0, 3)
            .Select(column => seed with { Id = 100 + row * 3 + column, Row = row + 2, Column = column + 2 })).ToArray();
        var center = cells.Single(n => n.Row == 3 && n.Column == 3);
        var board = original with { Nodes = cells };
        var frame = new Rectangle(24 + spacing, 24 + spacing, nodeSize, nodeSize);
        var renderer = new DaevanionHighlightRenderer();
        using var image = renderer.Render(new(nodeSize + spacing * 2 + 48, nodeSize + spacing * 2 + 48), board,
            [new(center.Row, center.Column, new(frame.X, frame.Y, frame.Width, frame.Height), 1)],
            Selected(seed.Effects[0].StatId!), spacing: spacing, phase: phase);
        var painted = Pixels(image).Where(pixel => pixel.Color.A > 0).ToArray();
        foreach (var cell in cells)
        {
            var native = new Rectangle(frame.X + (cell.Column - center.Column) * spacing,
                frame.Y + (cell.Row - center.Row) * spacing, nodeSize, nodeSize);
            Assert.DoesNotContain(painted, pixel => native.Contains(pixel.X, pixel.Y));
            if (cell.Id != center.Id)
            {
                var missingEnvelope = DaevanionNodeFitting.Bounds(native, spacing);
                Assert.DoesNotContain(painted, pixel => missingEnvelope.Contains(pixel.X + .5f, pixel.Y + .5f));
            }
        }
        if (spacing > nodeSize + 2) Assert.NotEmpty(painted);
    }

    [Theory]
    [InlineData(DaevanionNodeQuality.Common)]
    [InlineData(DaevanionNodeQuality.Rare)]
    [InlineData(DaevanionNodeQuality.Legend)]
    [InlineData(DaevanionNodeQuality.Unique)]
    public void NativeActivationAndQualityNeverChangeTheSelectionMark(DaevanionNodeQuality quality)
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var node = original.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat) with { Quality = quality };
        var board = original with { Nodes = [node] };
        var anchor = new DaevanionNodeAnchor(node.Row, node.Column, new(24, 24, 60, 60), 1);
        var selected = Selected(node.Effects[0].StatId!);
        var renderer = new DaevanionHighlightRenderer();
        using var inactive = renderer.Render(new(128, 128), board, [anchor with { Appearance = DaevanionNodeAppearance.Inactive }], selected);
        using var active = renderer.Render(new(128, 128), board, [anchor with { Appearance = DaevanionNodeAppearance.Active }], selected);
        using var common = renderer.Render(new(128, 128), board with { Nodes = [node with { Quality = DaevanionNodeQuality.Common }] }, [anchor], selected);
        Assert.Equal(RawPixels(common), RawPixels(active));
        Assert.Equal(RawPixels(active), RawPixels(inactive));
    }

    [Fact]
    public void RemovingAnOccludedAnchorRemovesOnlyItsMarkAndOriginIsAppliedOnce()
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var left = original.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat) with { Row = 3, Column = 3 };
        var right = left with { Id = left.Id + 1, Column = 4 };
        var board = original with { Nodes = [left, right] };
        var a = new DaevanionNodeAnchor(3, 3, new(24, 24, 60, 60), 1);
        var b = new DaevanionNodeAnchor(3, 4, new(100, 24, 60, 60), 1);
        var renderer = new DaevanionHighlightRenderer();
        var selected = Selected(left.Effects[0].StatId!);
        using var full = renderer.Render(new(200, 128), board, [a, b], selected, spacing: 76);
        using var partial = renderer.Render(new(200, 128), board, [a], selected, spacing: 76);
        var rightEnvelope = DaevanionNodeFitting.Bounds(new Rectangle(100, 24, 60, 60), 76);
        var originalPixels = RawPixels(full); var partialPixels = RawPixels(partial);
        foreach (var pixel in Pixels(full))
        {
            if (rightEnvelope.IntersectsWith(new RectangleF(pixel.X, pixel.Y, 1, 1))) Assert.Equal(0, partialPixels[pixel.Y * full.Width + pixel.X]);
            else Assert.Equal(originalPixels[pixel.Y * full.Width + pixel.X], partialPixels[pixel.Y * full.Width + pixel.X]);
        }
        using var translated = renderer.Render(new(200, 128), board,
            [a with { Bounds = new(524, 724, 60, 60) }], selected, new(500, 700), 76);
        Assert.Equal(partialPixels, RawPixels(translated));
    }

    [Fact]
    public void MissingPitchCannotGuessAnUnseenNeighborPosition()
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var node = original.Nodes.First(n => n.Kind == DaevanionNodeKind.Stat);
        var renderer = new DaevanionHighlightRenderer();
        using var image = renderer.Render(new(128, 128), original, [new(node.Row, node.Column, new(24, 24, 60, 60), 1)],
            Selected(node.Effects[0].StatId!));
        Assert.All(Pixels(image), pixel => Assert.Equal(0, pixel.Color.A));
    }

    [Fact]
    public void ClockwiseLightTrailUsesDistinctDeterministicPhasesAndWrapsContinuously()
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var node = original.Nodes.First(item => item.Kind == DaevanionNodeKind.Stat);
        var board = original with { Nodes = [node] };
        DaevanionNodeAnchor[] anchors = [new(node.Row, node.Column, new(24, 24, 60, 60), 1)];
        var selected = Selected(node.Effects[0].StatId!);
        var renderer = new DaevanionHighlightRenderer();
        using var zero = renderer.Render(new(128, 128), board, anchors, selected);
        using var quarter = renderer.Render(new(128, 128), board, anchors, selected, phase: .25);
        using var half = renderer.Render(new(128, 128), board, anchors, selected, phase: .5);
        using var threeQuarters = renderer.Render(new(128, 128), board, anchors, selected, phase: .75);
        using var wrap = renderer.Render(new(128, 128), board, anchors, selected, phase: 1);
        using var negative = renderer.Render(new(128, 128), board, anchors, selected, phase: -.25);
        var phases = new[] { RawPixels(zero), RawPixels(quarter), RawPixels(half), RawPixels(threeQuarters) };
        for (var i = 0; i < phases.Length; i++)
        for (var j = i + 1; j < phases.Length; j++) Assert.False(phases[i].SequenceEqual(phases[j]));
        Assert.Equal(RawPixels(zero), RawPixels(wrap));
        Assert.Equal(RawPixels(threeQuarters), RawPixels(negative));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteAnimationPhasesAreRejected(double phase)
    {
        var renderer = new DaevanionHighlightRenderer();
        var board = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Render(new(32, 32), board, [], Selected(), phase: phase));
    }

    [Fact]
    public void SharedStampsHonorAndRestoreTheCallersExternalClipAndTransform()
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var node = original.Nodes.First(item => item.Kind == DaevanionNodeKind.Stat);
        var board = original with { Nodes = [node] };
        using var image = new Bitmap(180, 180, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.Transparent);
        graphics.TranslateTransform(11, 17);
        var clip = new Rectangle(0, 0, 60, 120);
        graphics.SetClip(clip);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
        using var beforeTransform = graphics.Transform;
        using var beforeClip = graphics.Clip;
        using var identity = new System.Drawing.Drawing2D.Matrix();
        var unit = graphics.PageUnit;
        var smoothing = graphics.SmoothingMode;
        var renderer = new DaevanionHighlightRenderer();
        renderer.Paint(graphics, board, [new(node.Row, node.Column, new(24, 24, 60, 60), 1)],
            Selected(node.Effects[0].StatId!), phase: .5);
        using var afterTransform = graphics.Transform;
        using var afterClip = graphics.Clip;
        Assert.Equal(beforeTransform.Elements, afterTransform.Elements);
        Assert.Equal(beforeClip.GetRegionScans(identity), afterClip.GetRegionScans(identity));
        Assert.Equal(unit, graphics.PageUnit);
        Assert.Equal(smoothing, graphics.SmoothingMode);
        graphics.Dispose();
        var translatedClip = clip; translatedClip.Offset(11, 17);
        var painted = Pixels(image).Where(pixel => pixel.Color.A > 0).ToArray();
        Assert.NotEmpty(painted);
        Assert.All(painted, pixel => Assert.True(translatedClip.Contains(pixel.X, pixel.Y)));
    }

    [Theory]
    [InlineData(32, 44)]
    [InlineData(60, 62)]
    public void ATooltipRaceClearsTheWholeCompactHighlightEnvelopeAndKeepsItsNeighbor(int nodeSize, int spacing)
    {
        var original = DaevanionCatalog.Default.GetBoard("Cleric", "ariel")!;
        var left = original.Nodes.First(node => node.Kind == DaevanionNodeKind.Stat) with { Row = 3, Column = 3 };
        var right = left with { Id = left.Id + 1, Column = 4 };
        var board = original with { Nodes = [left, right] };
        var a = new DaevanionNodeAnchor(3, 3, new(24, 24, nodeSize, nodeSize), 1);
        var b = new DaevanionNodeAnchor(3, 4, new(24 + spacing, 24, nodeSize, nodeSize), 1);
        var context = new OwnCharacterContext(new(101, 501, 300, "SyntheticHighlight") { ClassId = "Cleric" }, 1);
        var source = new DaevanionSnapshot(context, board, new(100, 100, 240, 160), 100,
            new("ariel", new(24 + nodeSize / 2d - 2 * spacing, 24 + nodeSize / 2d - 2 * spacing),
                spacing, nodeSize, [a, b], .95), "en");
        var current = source with { FrameAt = 200, Detection = source.Detection with { Nodes = [b] } };
        var renderer = new DaevanionHighlightRenderer();
        using var image = renderer.Render(new(240, 160), board, source.Detection.Nodes, Selected(left.Effects[0].StatId!), spacing: spacing);
        Assert.False(DaevanionNodeFitting.CanExtend(new Rectangle(24, 24, nodeSize, nodeSize), spacing));
        Assert.Contains(Pixels(image), pixel => pixel.Color.A > 0);
        var before = RawPixels(image);
        var artwork = Assert.IsType<DaevanionSnapshot>(DaevanionOverlayForm.ReconcileRendered(image, source, current,
            Point.Empty, includeStatEnvelope: true));
        Assert.Equal(current.FrameAt, artwork.FrameAt);
        Assert.Single(artwork.Detection.Nodes);
        var removedBounds = DaevanionNodeFitting.Bounds(new Rectangle(24, 24, nodeSize, nodeSize), spacing);
        var cleared = Rectangle.FromLTRB((int)Math.Floor(removedBounds.Left), (int)Math.Floor(removedBounds.Top),
            (int)Math.Ceiling(removedBounds.Right), (int)Math.Ceiling(removedBounds.Bottom));
        foreach (var pixel in Pixels(image))
        {
            if (cleared.Contains(pixel.X, pixel.Y)) Assert.Equal(0, pixel.Color.A);
            else Assert.Equal(before[pixel.Y * image.Width + pixel.X], pixel.Color.ToArgb());
        }
        var final = current with { FrameAt = 300, Detection = current.Detection with { Nodes = [] } };
        var pruned = Assert.IsType<DaevanionSnapshot>(DaevanionOverlayForm.ReconcileRendered(image, artwork, final,
            Point.Empty, includeStatEnvelope: true));
        Assert.Equal(final.FrameAt, pruned.FrameAt);
        Assert.Empty(pruned.Detection.Nodes);
        Assert.All(Pixels(image), pixel => Assert.Equal(0, pixel.Color.A));
    }

    private static string[] Stats(DaevanionBoard board) => board.Nodes.Where(n => n.Kind == DaevanionNodeKind.Stat)
        .SelectMany(n => n.Effects).Select(e => e.StatId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static HashSet<string> Selected(params string[] ids) => new(ids, StringComparer.Ordinal);
    private static int[] RawPixels(Bitmap image)
    {
        var values = new int[image.Width * image.Height];
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < image.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, values, y * image.Width, image.Width);
        }
        finally { image.UnlockBits(data); }
        return values;
    }
    private static IEnumerable<(int X, int Y, Color Color)> Pixels(Bitmap image)
    {
        var values = RawPixels(image);
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++) yield return (x, y, Color.FromArgb(values[y * image.Width + x]));
    }
}
