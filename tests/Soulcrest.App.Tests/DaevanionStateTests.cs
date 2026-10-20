using System.Diagnostics;
using System.Drawing;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionLayoutStabilityTests
{
    private static readonly Rectangle Bounds = new(-1920, 0, 800, 800);
    private static long At(int millisecondsAgo) => Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * millisecondsAgo / 1000d);
    private static DaevanionNodeAnchor Node(int row, int column, int shift = 0) => new(row, column,
        new OpenCvSharp.Rect(10 + (column - 1) * 50 + shift, 10 + (row - 1) * 50 + shift, 40, 40), .9);
    private static DaevanionBoardDetection Detection(int shift = 0, string family = "nezekan") => new(family,
        new(30 + shift, 30 + shift), 50, 40, new[] { Node(3, 3, shift), Node(3, 4, shift), Node(4, 3, shift) }, .95);

    [Fact]
    public void TwoDistinctFramesAreRequiredAndRepeatedReadsKeepTheirOriginalEvidenceTime()
    {
        var state = new DaevanionLayoutStability(); var first = At(40); var second = At(20);
        Assert.False(state.Update("own", Bounds, first, Detection()));
        Assert.False(state.Update("own", Bounds, first, Detection()));
        Assert.Equal(0, state.ConfirmedFrameAt);
        Assert.True(state.Update("own", Bounds, second, Detection()));
        Assert.True(state.Update("own", Bounds, second, Detection()));
        Assert.Equal(second, state.ConfirmedFrameAt);
    }

    [Fact]
    public void PreferredLayoutRequiresTheSameConfirmedFreshContextAndOnlySuppliesFrozenGeometry()
    {
        var state = new DaevanionLayoutStability(); var first = At(60); var second = At(40);
        Assert.False(state.Update("own", Bounds, first, Detection()));
        Assert.Null(state.Preferred("own", Bounds));
        Assert.True(state.Update("own", Bounds, second, Detection(1)));
        var preferred = Assert.IsType<DaevanionBoardDetection>(state.Preferred("own", Bounds));
        Assert.Equal(Detection().Origin, preferred.Origin);
        Assert.Null(state.Preferred("other-generation", Bounds));
        Assert.Null(state.Preferred("own", Bounds with { X = -1919 }));
        Assert.True(state.RetainAfterMissingCapture("own", Bounds, true));
        Assert.Equal(second, state.ConfirmedFrameAt);
        Assert.False(state.RetainAfterMissingCapture("own", Bounds, false));
        Assert.Null(state.Preferred("own", Bounds));
        Assert.Equal(0, state.ConfirmedFrameAt);
    }

    [Fact]
    public void BusyAndRepeatedReadSequenceRetainsTheOriginalFrameDeadlineAndCannotConfirmPendingState()
    {
        var state = new DaevanionLayoutStability(); var first = At(80); var confirmed = At(60);
        Assert.False(state.Update("own", Bounds, first, Detection()));
        Assert.False(state.RetainAfterMissingCapture("own", Bounds, true));
        Assert.False(state.Update("own", Bounds, first, Detection()));
        Assert.True(state.Update("own", Bounds, confirmed, Detection()));
        for (var i = 0; i < 20; i++)
        {
            Assert.True(state.RetainAfterMissingCapture("own", Bounds, true));
            Assert.True(state.Update("own", Bounds, confirmed, Detection()));
            Assert.Equal(confirmed, state.ConfirmedFrameAt);
        }
        Assert.True(DaevanionLayoutStability.Fresh(confirmed, confirmed + Stopwatch.Frequency));
        Assert.False(DaevanionLayoutStability.Fresh(confirmed, confirmed + Stopwatch.Frequency + 1));
        Assert.False(state.RetainAfterMissingCapture("own", Bounds, false));
        Assert.Null(state.Confirmed);
    }

    [Theory]
    [InlineData("identity")] [InlineData("bounds")] [InlineData("negative-frame")]
    public void MissingCaptureRetentionCannotHideAContextChangeOrFreshNegativeBoardEvidence(string change)
    {
        var state = new DaevanionLayoutStability();
        Assert.False(state.Update("own", Bounds, At(60), Detection()));
        Assert.True(state.Update("own", Bounds, At(40), Detection()));
        if (change == "negative-frame") Assert.False(state.Update("own", Bounds, At(20), null));
        Assert.False(state.RetainAfterMissingCapture(change == "identity" ? "new-own" : "own",
            change == "bounds" ? Bounds with { X = 1 } : Bounds, true));
        Assert.Null(state.Confirmed); Assert.Equal(0, state.ConfirmedFrameAt);
    }

    [Fact]
    public void PixelNoiseUsesFrozenCopiedGeometryAndDoesNotAccumulateDrift()
    {
        var state = new DaevanionLayoutStability();
        var nodes = new[] { Node(3, 3), Node(3, 4), Node(4, 3) };
        var original = Detection() with { Nodes = nodes };
        Assert.False(state.Update("own", Bounds, At(60), original));
        var expected = nodes.Select(n => n.Bounds).ToArray();
        nodes[0] = Node(3, 3, 40);
        Assert.True(state.Update("own", Bounds, At(50), Detection(1)));
        Assert.Equal(expected, state.Confirmed!.Nodes.Select(n => n.Bounds));
        Assert.True(state.Update("own", Bounds, At(40), Detection(2)));
        Assert.Equal(expected, state.Confirmed!.Nodes.Select(n => n.Bounds));
        Assert.False(state.Update("own", Bounds, At(30), Detection(3)));
        Assert.Null(state.Confirmed);
    }

    [Fact]
    public void OnlyCurrentlyVisibleNodesAreDrawnAndReturningNodesUseTheFrozenGrid()
    {
        var state = new DaevanionLayoutStability(); var initial = Detection() with { Nodes = new[] { Node(3, 3) } };
        Assert.False(state.Update("own", Bounds, At(60), initial));
        Assert.True(state.Update("own", Bounds, At(50), initial));
        Assert.True(state.Update("own", Bounds, At(40), Detection(1) with { Nodes = new[] { Node(3, 4, 1) } }));
        var returned = Assert.Single(state.Confirmed!.Nodes);
        Assert.Equal((3, 4), (returned.Row, returned.Column));
        Assert.Equal(Node(3, 4).Bounds, returned.Bounds);
    }

    [Theory]
    [InlineData("identity")] [InlineData("bounds")] [InlineData("family")]
    [InlineData("missing")] [InlineData("backwards")] [InlineData("stale")] [InlineData("future")]
    [InlineData("duplicate")] [InlineData("outside")] [InlineData("invalid-row")]
    [InlineData("nan")] [InlineData("confidence")]
    public void NewContextAndInvalidCurrentEvidenceInvalidateTheConfirmedLayout(string change)
    {
        var state = new DaevanionLayoutStability(); var first = At(50); var second = At(40);
        Assert.False(state.Update("own", Bounds, first, Detection()));
        Assert.True(state.Update("own", Bounds, second, Detection()));
        var identity = change == "identity" ? "other" : "own";
        var bounds = change == "bounds" ? Bounds with { X = -1919 } : Bounds;
        var at = change switch { "backwards" => first, "stale" => At(2000), "future" => long.MaxValue, _ => At(20) };
        var found = change switch
        {
            "family" => Detection(family: "azphel"),
            "missing" => null,
            "duplicate" => Detection() with { Nodes = new[] { Node(3, 3), Node(3, 3) } },
            "outside" => Detection() with { Nodes = new[] { Node(15, 15) with { Bounds = new(790, 790, 40, 40) } } },
            "invalid-row" => Detection() with { Nodes = new[] { Node(0, 3) } },
            "nan" => Detection() with { Spacing = double.NaN },
            "confidence" => Detection() with { Confidence = double.NaN },
            _ => Detection()
        };
        Assert.False(state.Update(identity, bounds, at, found));
        Assert.Null(state.Confirmed);
        Assert.Equal(0, state.ConfirmedFrameAt);
    }
}

[Collection("Settings file")]
public sealed class DaevanionStateTests
{
    private static readonly OwnCharacterIdentity Own = new(101, 501, 300, "SyntheticBoard") { ClassId = "Cleric" };
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-board-state-" + Guid.NewGuid().ToString("N"));
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _language = UiText.Language;
        internal SettingsService Settings { get; }
        internal NetworkLootService Network { get; }
        internal GameCaptureService Capture { get; } = new();
        internal DaevanionService Service { get; }
        internal Rectangle? Bounds = new Rectangle(-1920, 0, 800, 800);
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Settings = new SettingsService(); Settings.Current.DaevanionOverlayEnabled = true; Settings.Current.OverlayLanguage = "en";
            var progress = new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([]));
            Network = new(progress);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Service = new(Network, Settings, Capture, () => Bounds);
            Network.ObserveIdentity("own", Own, DateTimeOffset.UnixEpoch);
        }
        internal DaevanionSnapshot Frame(long? at = null)
        {
            var board = DaevanionCatalog.Default.GetBoard("Cleric", "nezekan")!;
            var detection = new DaevanionBoardDetection("nezekan", new(30, 30), 50, 40,
                new[] { new DaevanionNodeAnchor(3, 3, new(110, 110, 40, 40), .9) }, .95);
            return new(Network.OwnCharacter!, board, Bounds!.Value, at ?? Stopwatch.GetTimestamp(), detection, Settings.Current.LootNameLanguage);
        }
        public void Dispose()
        {
            Service.Dispose(); Network.Dispose(); Capture.Dispose();
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            UiText.Language = _language; Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CurrentPublishedFrameCanPresentWithoutStartingCaptureOrMapTracking()
    {
        using var f = new Fixture(); var frame = f.Frame();
        Assert.True(f.Service.TryPublishObservation(frame, f.Service.ObservationRevision));
        Assert.Same(frame, f.Service.Snapshot); Assert.True(f.Service.CanPresent(frame));
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
        Assert.False(f.Service.CanPresent(frame with { }));
    }

    [Fact]
    public void RepeatedBusyCaptureDoesNotClearOrRefreshThePublishedSourcePicture()
    {
        using var f = new Fixture(); var frame = f.Frame(); var revision = f.Service.ObservationRevision;
        Assert.True(f.Service.TryPublishObservation(frame, revision));
        var changes = 0; f.Service.Changed += () => changes++;
        for (var i = 0; i < 20; i++) f.Service.ObserveMissingCapture(revision);
        Assert.Same(frame, f.Service.Snapshot);
        Assert.Equal(frame.FrameAt, f.Service.Snapshot!.FrameAt);
        Assert.Equal(revision, f.Service.ObservationRevision);
        Assert.True(f.Service.CanPresent(frame)); Assert.Equal(0, changes);
        f.Network.ResetProgressContext();
        f.Service.ObserveMissingCapture(revision);
        Assert.Null(f.Service.Snapshot);
    }

    [Fact]
    public void FramePublishedDuringPaintingAdoptsEquivalentCurrentEvidenceWithoutBlankingTheSurface()
    {
        using var f = new Fixture(); var rendered = f.Frame(); var revision = f.Service.ObservationRevision;
        var nodes = new[] { rendered.Detection.Nodes[0], new DaevanionNodeAnchor(3, 4, new(160, 110, 40, 40), .9) };
        rendered = rendered with { Detection = rendered.Detection with { Nodes = nodes } };
        Assert.True(f.Service.TryPublishObservation(rendered, revision));
        var current = rendered with { FrameAt = Stopwatch.GetTimestamp(), Detection = rendered.Detection with
            { Confidence = .85, Nodes = Enumerable.Reverse(nodes).Select(n => n with { Confidence = .75 }).ToArray() } };
        Assert.True(f.Service.TryPublishObservation(current, revision));
        Assert.False(f.Service.CanPresent(rendered));
        Assert.True(f.Service.CanPresent(current));
        Assert.Same(current, DaevanionOverlayForm.EquivalentCurrent(rendered, current));
        Assert.Equal(current.FrameAt, DaevanionOverlayForm.EquivalentCurrent(rendered, current)!.FrameAt);
        Assert.Equal(DaevanionOverlayForm.SurfaceBounds(rendered), DaevanionOverlayForm.SurfaceBounds(current));
    }

    [Fact]
    public void EdgeNodeOcclusionUsesTheSameWindowCanvasAndRequiresUpdatedArtwork()
    {
        using var f = new Fixture(); var original = f.Frame();
        var all = original.Board.Nodes.Where(n => n.Kind != DaevanionNodeKind.Start).Select(n =>
            new DaevanionNodeAnchor(n.Row, n.Column, new(10 + (n.Column - 1) * 50, 10 + (n.Row - 1) * 50, 40, 40), .9)).ToArray();
        original = original with { Detection = original.Detection with { Nodes = all } };
        var occluded = original with { Detection = original.Detection with { Nodes = all.Skip(10).ToArray() } };
        Assert.True(DaevanionOverlayForm.HasSameBoard(original, occluded));
        Assert.False(DaevanionOverlayForm.HasSameContent(original, occluded));
        Assert.Null(DaevanionOverlayForm.EquivalentCurrent(original, occluded));
        Assert.Equal(DaevanionOverlayForm.SurfaceBounds(original), DaevanionOverlayForm.SurfaceBounds(occluded));
        Assert.False(DaevanionOverlayForm.HasSameBoard(original, occluded with { Language = "de" }));
        Assert.False(DaevanionOverlayForm.HasSameBoard(original, occluded with { Context = original.Context with { Generation = 999 } }));
        Assert.False(DaevanionOverlayForm.HasSameBoard(original, occluded with { Board = DaevanionCatalog.Default.GetBoard("Cleric", "azphel")! }));
        Assert.False(DaevanionOverlayForm.HasSameBoard(original, occluded with { Detection = occluded.Detection with { Origin = new(31, 30) } }));
        Assert.False(DaevanionOverlayForm.HasSameBoard(original, occluded with { Detection = occluded.Detection with { Spacing = 51 } }));
        Assert.False(DaevanionOverlayForm.HasSameBoard(original, occluded with { Detection = occluded.Detection with { NodeSize = 41 } }));
        Assert.Equal(Rectangle.Empty, DaevanionOverlayForm.SurfaceBounds(original with { Board = original.Board with { Nodes = [] } }));
        Assert.Equal(Rectangle.Empty, DaevanionOverlayForm.SurfaceBounds(original with { Detection = original.Detection with { Origin = new(double.NaN, 0) } }));
    }

    [Theory]
    [InlineData("reset")] [InlineData("class")]
    [InlineData("language")] [InlineData("disabled")] [InlineData("show-mode")]
    public void InvalidationPreventsAWorkerFromPublishingAfterItsObservationStarted(string change)
    {
        using var f = new Fixture(); var revision = f.Service.ObservationRevision; var old = f.Frame();
        Assert.True(f.Service.TryPublishObservation(old, revision));
        switch (change)
        {
            case "reset": f.Network.ResetProgressContext(); break;
            case "class": f.Network.ObserveIdentity("own", Own with { ClassId = "Sorcerer" }, DateTimeOffset.UnixEpoch); break;
            case "language": f.Settings.Update(s => s.OverlayLanguage = "de"); break;
            case "disabled": f.Settings.Update(s => s.DaevanionOverlayEnabled = false); break;
            case "show-mode": f.Settings.Update(s => s.OverlayShowMode = "always"); break;
        }
        Assert.Null(f.Service.Snapshot);
        Assert.False(f.Service.CanPresent(old));
        Assert.False(f.Service.TryPublishObservation(old, revision));
        Assert.Null(f.Service.Snapshot);
    }

    [Theory]
    [InlineData("theme")] [InlineData("foreign-position")] [InlineData("recordings")]
    [InlineData("inactive-language")] [InlineData("unchanged")]
    public void UnrelatedSettingsPreserveThePublishedGeometryRevisionAndFrameEvidence(string change)
    {
        using var f = new Fixture(); var frame = f.Frame(); var revision = f.Service.ObservationRevision;
        Assert.True(f.Service.TryPublishObservation(frame, revision));
        var notifications = 0; f.Service.Changed += () => notifications++;
        f.Settings.Update(s =>
        {
            switch (change)
            {
                case "theme": s.OverlayTheme = "obsidian"; break;
                case "foreign-position": s.OverlayX += 20; break;
                case "recordings": s.OverlaysInRecordings = !s.OverlaysInRecordings; break;
                case "inactive-language": s.NameLanguage = "de"; break; // explicit overlay English still wins
                case "unchanged": s.DaevanionOverlayEnabled = true; break;
            }
        });
        Assert.Same(frame, f.Service.Snapshot);
        Assert.Equal(revision, f.Service.ObservationRevision);
        Assert.Equal(frame.FrameAt, f.Service.Snapshot!.FrameAt);
        Assert.True(f.Service.CanPresent(frame));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void LanguageChangeAndBackCannotReviveAnObservationFromAnOlderRevision()
    {
        using var f = new Fixture(); var old = f.Frame(); var revision = f.Service.ObservationRevision;
        f.Settings.Update(s => s.OverlayLanguage = "de"); f.Settings.Update(s => s.OverlayLanguage = "en");
        Assert.False(f.Service.TryPublishObservation(old, revision));
        var current = f.Frame(); Assert.True(f.Service.TryPublishObservation(current, f.Service.ObservationRevision));
        Assert.True(f.Service.CanPresent(current));
    }

    [Theory]
    [InlineData("moved")] [InlineData("missing")] [InlineData("stale")] [InlineData("future")]
    [InlineData("wrong-board-class")] [InlineData("wrong-board-family")]
    public void StaleOrMismatchedEvidenceCannotBePublishedOrPresented(string invalid)
    {
        using var f = new Fixture(); var frame = f.Frame();
        frame = invalid switch
        {
            "stale" => frame with { FrameAt = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2 },
            "future" => frame with { FrameAt = long.MaxValue },
            "wrong-board-class" => frame with { Board = DaevanionCatalog.Default.GetBoard("Sorcerer", "nezekan")! },
            "wrong-board-family" => frame with { Detection = frame.Detection with { Family = "azphel" } },
            _ => frame
        };
        if (invalid == "moved") f.Bounds = new Rectangle(-1919, 0, 800, 800);
        if (invalid == "missing") f.Bounds = null;
        Assert.False(f.Service.TryPublishObservation(frame, f.Service.ObservationRevision));
        Assert.False(f.Service.CanPresent(frame));
    }

    [Fact]
    public void UnknownClassCannotBorrowThePreviousKnownClassAndDisposeCannotPublish()
    {
        using var f = new Fixture(); var frame = f.Frame();
        Assert.True(f.Service.TryPublishObservation(frame, f.Service.ObservationRevision));
        f.Network.ObserveIdentity("own", Own with { ClassId = null }, DateTimeOffset.UnixEpoch);
        Assert.Null(f.Service.ClassId); Assert.Null(f.Service.Snapshot);
        Assert.False(f.Service.TryPublishObservation(frame, f.Service.ObservationRevision));
        f.Service.Dispose();
        Assert.False(f.Service.CanPresent(frame));
    }
}
