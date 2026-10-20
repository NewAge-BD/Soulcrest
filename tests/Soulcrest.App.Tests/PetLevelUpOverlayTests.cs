using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Real service-to-overlay wiring on an unshown STA form: no game capture, form handle or message loop.</summary>
[Collection("Settings file")]
public sealed class PetLevelUpOverlayTests
{
    [Fact]
    public Task ExplicitUpgradesWithoutLootUseInlineRecentRowsUntilMaxGetsItsOwnCard() => RunSta(() =>
    {
        using var fixture = new Fixture();
        Assert.Empty(fixture.Progress.Recent());
        foreach (var level in new[] { 1, 2 })
        {
            fixture.Upgrade(1135, level);
            Refresh(fixture.Form);
            var active = Assert.IsType<PetOverlayForm.LevelUpRow>(Cached(fixture.Form));
            Assert.Equal(fixture.Name(1135), active.Name);
            Assert.Equal(level, active.Level);
            Assert.False(active.IsMax);
            Assert.Empty(Rows(fixture.Form, focus: true));
            var inline = Assert.Single(Rows(fixture.Form, focus: false));
            Assert.Equal(Fixture.Id(1135), inline.PetId);
            Assert.Same(active, inline.LevelUp);
            Assert.Equal(0, inline.Quantity);
            Assert.Equal(PetOverlayForm.LootLogicalHeight(0, 1), fixture.Form.Height);
        }
        fixture.Upgrade(1135, 3);
        Refresh(fixture.Form);
        var row = Assert.IsType<PetOverlayForm.LevelUpRow>(Cached(fixture.Form));
        Assert.Equal(fixture.Name(1135), row.Name);
        Assert.Equal(3, row.Level);
        Assert.True(row.IsMax);
        Assert.Empty(Rows(fixture.Form, focus: false));
        Assert.Equal(PetOverlayForm.LootLogicalHeight(0, 0, celebrating: true), fixture.Form.Height);
        Assert.Empty(fixture.Progress.Recent());
        Assert.False(File.Exists(Path.Combine(fixture.Folder, "events.jsonl")));
        AssertUnshown(fixture.Form);
    });

    [Fact]
    public Task DisabledOverlayIgnoresAnUpgradeAndDoesNotReplayItWhenEnabled() => RunSta(() =>
    {
        using var fixture = new Fixture(enabled: false);
        fixture.Upgrade(1135, 1);
        Assert.Null(Current(fixture.Form));
        fixture.Settings.Current.OverlayEnabled = true;
        Refresh(fixture.Form);
        Assert.Null(Current(fixture.Form));
        fixture.Upgrade(1135, 2);
        Assert.Equal(2, Assert.IsType<PetOverlayForm.LevelUpRow>(Current(fixture.Form)).Level);
        fixture.Settings.Current.OverlayEnabled = false;
        // Exercise the same refresh used by the UI timer, without running a message loop.
        Refresh(fixture.Form);
        fixture.Upgrade(1135, 3);
        Assert.Null(Current(fixture.Form));
        fixture.Settings.Current.OverlayEnabled = true;
        Refresh(fixture.Form);
        Assert.Null(Current(fixture.Form));
        AssertUnshown(fixture.Form);
    });

    [Fact]
    public Task SuppressionClearsActiveAndWaitingAnimationsAndIgnoresFurtherUpgrades() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Upgrade(1135, 1);
        fixture.Upgrade(1163, 1); // waits behind the first pet
        var baseHeight = fixture.Form.Height;
        Refresh(fixture.Form);
        Assert.True(fixture.Form.Height > baseHeight);
        Assert.Equal(fixture.Name(1135), Assert.IsType<PetOverlayForm.LevelUpRow>(Current(fixture.Form)).Name);
        fixture.Form.SetSuppressed(true);
        Assert.Null(Current(fixture.Form));
        Assert.Equal(baseHeight, fixture.Form.Height);
        fixture.Upgrade(1163, 2);
        fixture.Upgrade(1146, 1);
        Assert.Null(Current(fixture.Form));
        // Resume while disabled to keep this integration test entirely unshown.
        fixture.Settings.Current.OverlayEnabled = false;
        fixture.Form.SetSuppressed(false);
        fixture.Settings.Current.OverlayEnabled = true;
        Refresh(fixture.Form);
        Assert.Null(Current(fixture.Form));
        fixture.Upgrade(1135, 2);
        Assert.Equal(fixture.Name(1135), Assert.IsType<PetOverlayForm.LevelUpRow>(Current(fixture.Form)).Name);
        var animation = Animation(fixture.Form);
        var afterDuration = Stopwatch.GetTimestamp() + (long)Math.Ceiling(PetLevelUpAnimation.DurationSeconds * Stopwatch.Frequency) + 1;
        Assert.Null(animation.Sample(afterDuration)); // neither the old waiting pet nor suppressed upgrades survived
        Assert.Null(Current(fixture.Form));
        AssertUnshown(fixture.Form);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PresentationHidingPreservesActiveAndQueuedUpgradesAcrossShowHideAndContextChanges(bool foreground) => RunSta(() =>
    {
        using var fixture = new Fixture();
        void SetHidden(bool hidden)
        {
            if (foreground) fixture.Form.SetForegroundSuppressed(hidden);
            else fixture.Form.SetPlacementHidden(hidden);
        }
        fixture.Upgrade(1135, 1);
        fixture.Upgrade(1163, 1);
        Refresh(fixture.Form);
        var animation = Animation(fixture.Form);
        var started = typeof(PetLevelUpAnimation).GetField("_startedAt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(animation);
        var first = Assert.IsType<PetOverlayForm.LevelUpRow>(Current(fixture.Form));
        var settingsBefore = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        SetHidden(true);
        fixture.Form.Hide();
        fixture.Form.Show();
        fixture.Form.SetSuppressed(true);
        fixture.Form.SetSuppressed(false);
        Refresh(fixture.Form);
        typeof(PetOverlayForm).GetMethod("AnimateLevelUp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Form, [null, EventArgs.Empty]);
        Assert.False(fixture.Form.Visible);
        Assert.Equal(first.PetId, Assert.IsType<PetOverlayForm.LevelUpRow>(Current(fixture.Form)).PetId);
        Assert.Equal(started, typeof(PetLevelUpAnimation).GetField("_startedAt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(animation));
        SetHidden(false);
        Assert.True(fixture.Form.Visible);
        Assert.True(fixture.Form.Locked);
        Assert.Equal(first.PetId, Assert.IsType<PetOverlayForm.LevelUpRow>(Current(fixture.Form)).PetId);
        Assert.Equal(started, typeof(PetLevelUpAnimation).GetField("_startedAt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(animation));
        var next = animation.Sample(Stopwatch.GetTimestamp() + (long)Math.Ceiling(PetLevelUpAnimation.DurationSeconds * Stopwatch.Frequency));
        Assert.Equal(Fixture.Id(1163), Assert.IsType<PetLevelUpAnimation.Frame>(next).Change.PetId);
        Assert.Empty(fixture.Progress.Recent());
        Assert.False(File.Exists(Path.Combine(fixture.Folder, "events.jsonl")));
        Assert.Equal(settingsBefore, File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PresentationTimeConsumesTheOriginalAnimationDeadlineAndAcceptsRealNewEvents(bool foreground) => RunSta(() =>
    {
        using var fixture = new Fixture();
        void SetHidden(bool hidden)
        {
            if (foreground) fixture.Form.SetForegroundSuppressed(hidden);
            else fixture.Form.SetPlacementHidden(hidden);
        }
        fixture.Upgrade(1135, 1);
        Refresh(fixture.Form);
        SetHidden(true);
        // Use the same monotonic source as production instead of sleeping for 30 seconds.
        typeof(PetLevelUpAnimation).GetField("_startedAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Animation(fixture.Form), Stopwatch.GetTimestamp() - 31 * Stopwatch.Frequency);
        typeof(PetOverlayForm).GetMethod("AnimateLevelUp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Form, [null, EventArgs.Empty]);
        Assert.Null(Current(fixture.Form));
        fixture.Upgrade(1163, 1);
        Refresh(fixture.Form);
        Assert.Equal(Fixture.Id(1163), Assert.IsType<PetOverlayForm.LevelUpRow>(Current(fixture.Form)).PetId);
        Assert.False(fixture.Form.Visible);
        SetHidden(false);
        Assert.Equal(Fixture.Id(1163), Assert.IsType<PetOverlayForm.LevelUpRow>(Current(fixture.Form)).PetId);
        Assert.Empty(fixture.Progress.Recent());
    });

    [Fact]
    public Task DisposingTheOverlayDisconnectsItsSourceEvenWhenTheServiceContinuesPublishing() => RunSta(() =>
    {
        using var fixture = new Fixture();
        var sourceEvents = 0;
        fixture.Progress.PetLeveled += _ => sourceEvents++;
        fixture.Upgrade(1135, 1);
        Assert.NotNull(Current(fixture.Form));
        Assert.Equal(1, sourceEvents);
        fixture.Form.Dispose();
        Assert.True(fixture.Form.IsDisposed);
        fixture.Upgrade(1135, 2);
        Assert.Equal(2, sourceEvents);
        Assert.Null(Current(fixture.Form));
        Assert.Null(Animation(fixture.Form).Sample(long.MaxValue));
        Assert.Empty(fixture.Progress.Recent());
        Assert.False(fixture.Form.IsHandleCreated);
    });

    [Fact]
    public Task PaintingUsesTheSameAnimationSnapshotAsTheLayoutUntilTheNextUiRefresh() => RunSta(() =>
    {
        using var fixture = new Fixture();
        var baseHeight = fixture.Form.Height;
        fixture.Upgrade(1135, 2);
        // Reseed the confirmed event at a visible sweep phase rather than waiting for real time.
        Animation(fixture.Form).Clear();
        Animation(fixture.Form).Enqueue(new(PetIdTable.Find(1135)!.CatalogId!, 2, false),
            Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        Refresh(fixture.Form);
        var cached = Assert.IsType<PetOverlayForm.LevelUpRow>(Cached(fixture.Form));
        Assert.Equal(2, cached.Level);
        Assert.True(fixture.Form.Height > baseHeight);
        var height = fixture.Form.Height;
        var before = Paint(fixture.Form);
        fixture.Upgrade(1135, 3); // a worker event changes the queue between layout and paint
        Assert.Same(cached, Cached(fixture.Form));
        Assert.Equal(height, fixture.Form.Height);
        Assert.Equal(before, Paint(fixture.Form)); // painting must not sample the newer event independently
        Refresh(fixture.Form);
        var updated = Assert.IsType<PetOverlayForm.LevelUpRow>(Cached(fixture.Form));
        Assert.Equal(3, updated.Level);
        Assert.True(updated.IsMax);
        Assert.Empty(Rows(fixture.Form, focus: false));
        Assert.NotEqual(height, fixture.Form.Height);
        Assert.Equal(PetOverlayForm.LootLogicalHeight(0, 0, celebrating: true), fixture.Form.Height);
        Assert.NotEqual(before, Paint(fixture.Form));
        AssertUnshown(fixture.Form);
    });

    [Fact]
    public Task FocusedLevelUpsDecorateOnlyTheFocusRowEvenWhenThePetAlsoHasRecentLoot() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Mark(1135, 100);
        fixture.Gain(1135, 2);
        foreach (var level in new[] { 1, 2 })
        {
            fixture.Upgrade(1135, level);
            Refresh(fixture.Form);
            var focus = Assert.Single(Rows(fixture.Form, focus: true));
            Assert.Equal(Fixture.Id(1135), focus.PetId);
            Assert.Equal(level, Assert.IsType<PetOverlayForm.LevelUpRow>(focus.LevelUp).Level);
            Assert.Equal(level, focus.Level);
            Assert.Equal(fixture.Progress.SoulsInLevel(Fixture.Id(1135)), focus.Current);
            Assert.Equal(2, focus.Quantity);
            Assert.Empty(Rows(fixture.Form, focus: false));
            Assert.Equal(PetOverlayForm.LootLogicalHeight(1, 0), fixture.Form.Height);
        }
        Assert.Equal(2, Assert.Single(fixture.Progress.Recent()).Quantity);
        AssertUnshown(fixture.Form);
    });

    [Fact]
    public Task ChangingFocusMovesTheActiveInlineCelebrationToRecentWithoutCreatingALootEvent() => RunSta(() =>
    {
        using var fixture = new Fixture();
        var first = fixture.Mark(1135, 100);
        fixture.Mark(1163, 200);
        fixture.Upgrade(1135, 1);
        Refresh(fixture.Form);
        Assert.NotNull(Assert.Single(Rows(fixture.Form, focus: true)).LevelUp);
        Assert.Empty(Rows(fixture.Form, focus: false));
        fixture.Unmark(first);
        Refresh(fixture.Form);
        var focus = Assert.Single(Rows(fixture.Form, focus: true));
        Assert.Equal(Fixture.Id(1163), focus.PetId);
        Assert.Null(focus.LevelUp);
        var recent = Assert.Single(Rows(fixture.Form, focus: false));
        Assert.Equal(Fixture.Id(1135), recent.PetId);
        Assert.Equal(1, Assert.IsType<PetOverlayForm.LevelUpRow>(recent.LevelUp).Level);
        Assert.Equal(0, recent.Quantity);
        Assert.Equal(PetOverlayForm.LootLogicalHeight(1, 1), fixture.Form.Height);
        Assert.Empty(fixture.Progress.Recent());
        Assert.False(File.Exists(Path.Combine(fixture.Folder, "events.jsonl")));
        AssertUnshown(fixture.Form);
    });

    [Fact]
    public Task MaxHasItsOwnCardAndStaysOutOfRecentLootAfterExpiryAndFurtherPickupsWithoutChangingTheLedger() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Gain(1135, 2);
        fixture.Gain(1163, 1);
        Refresh(fixture.Form);
        Assert.Contains(Rows(fixture.Form, focus: false), row => row.PetId == Fixture.Id(1135));
        fixture.Upgrade(1135, 3);
        Refresh(fixture.Form);
        Assert.True(Assert.IsType<PetOverlayForm.LevelUpRow>(Cached(fixture.Form)).IsMax);
        Assert.DoesNotContain(Rows(fixture.Form, focus: false), row => row.PetId == Fixture.Id(1135));
        Assert.Equal(PetOverlayForm.LootLogicalHeight(0, 1, celebrating: true), fixture.Form.Height);
        fixture.Gain(1135, 1); // a new soul for an already completed pet must also stay out of Recent Loot
        Refresh(fixture.Form);
        Assert.DoesNotContain(Rows(fixture.Form, focus: false), row => row.PetId == Fixture.Id(1135));
        var ledger = fixture.Progress.Recent().ToArray();
        Assert.Equal(3, ledger.Length);
        Assert.Equal(3, ledger.Where(row => row.PetId == Fixture.Id(1135)).Sum(row => row.Quantity));
        var journalPath = Path.Combine(fixture.Folder, "events.jsonl");
        var journal = File.ReadAllBytes(journalPath);
        var afterDuration = Stopwatch.GetTimestamp() + (long)Math.Ceiling(PetLevelUpAnimation.DurationSeconds * Stopwatch.Frequency) + 1;
        Assert.Null(Animation(fixture.Form).Sample(afterDuration));
        Refresh(fixture.Form);
        Assert.Null(Cached(fixture.Form));
        var remaining = Assert.Single(Rows(fixture.Form, focus: false));
        Assert.Equal(Fixture.Id(1163), remaining.PetId);
        Assert.Null(remaining.LevelUp);
        Assert.Equal(PetOverlayForm.LootLogicalHeight(0, 1), fixture.Form.Height);
        Assert.Equal(ledger, fixture.Progress.Recent().ToArray());
        Assert.Equal(journal, File.ReadAllBytes(journalPath));
        AssertUnshown(fixture.Form);
    });

    [Fact]
    public Task EmptyShowAndFocusAloneStayHiddenUntilTheFirstRealGainEvenWithoutAnInitialHandle() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Mark(1135, 100);
        Refresh(fixture.Form);
        Assert.Single(Rows(fixture.Form, focus: true));
        fixture.Form.Show();
        AssertUnshown(fixture.Form);
        fixture.Progress.SetSouls(Fixture.Id(1135), 4);
        Refresh(fixture.Form);
        Assert.Empty(fixture.Progress.Recent());
        AssertUnshown(fixture.Form);
        fixture.Gain(1135, 1);
        Refresh(fixture.Form);
        Assert.True(fixture.Form.IsHandleCreated);
        Assert.True(fixture.Form.Visible);
        Assert.Equal(1, Assert.Single(Rows(fixture.Form, focus: true)).Quantity);
    });

    [Fact]
    public Task ExplicitHideBeforeTheFirstFrameClearsPendingAnimationWithoutReplayingIt() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Form.Show();
        AssertUnshown(fixture.Form);
        fixture.Upgrade(1135, 1); // Worker event before the next normal UI refresh.
        Assert.NotNull(Current(fixture.Form));
        fixture.Form.Hide();
        Refresh(fixture.Form);
        Assert.Null(Current(fixture.Form));
        Assert.Null(Cached(fixture.Form));
        AssertUnshown(fixture.Form);
        fixture.Upgrade(1135, 2);
        Refresh(fixture.Form);
        Assert.True(fixture.Form.Visible);
        Assert.Equal(2, Cached(fixture.Form)!.Level);
    });

    [Fact]
    public Task SnapshotAndManualProgressDoNotOpenThePanelOrCreateRecentLoot() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Form.Show();
        fixture.Snapshot(1135, 2, 10);
        Refresh(fixture.Form);
        Assert.Empty(fixture.Progress.Recent());
        AssertUnshown(fixture.Form);
        fixture.Progress.SetSouls(Fixture.Id(1135), 14);
        Refresh(fixture.Form);
        Assert.Empty(fixture.Progress.Recent());
        AssertUnshown(fixture.Form);
    });

    [Fact]
    public Task ExpiredSoulToastHidesThePanelDespiteFocusAndLaterGainsCanShowItAgain() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Mark(1135, 100);
        fixture.Form.Show();
        fixture.Gain(1135, 1);
        Refresh(fixture.Form);
        Assert.True(fixture.Form.Visible);
        fixture.AgeLoot(TimeSpan.FromMinutes(1));
        Refresh(fixture.Form);
        Assert.False(fixture.Form.Visible);
        Assert.Single(Rows(fixture.Form, focus: true));
        Assert.Equal(0, Assert.Single(Rows(fixture.Form, focus: true)).Quantity);
        fixture.Form.Show(); // External Show cannot revive an expired pickup or a focus-only panel.
        Assert.False(fixture.Form.Visible);
        fixture.Gain(1163, 2);
        Refresh(fixture.Form);
        Assert.True(fixture.Form.Visible);
        Assert.Equal(2, Assert.Single(Rows(fixture.Form, focus: false)).Quantity);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task EmptyAutoHideKeepsAcceptingFullThirtySecondLevelUpFeedbackIncludingMax(int level) => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Form.Show();
        fixture.Gain(1135, 1);
        Refresh(fixture.Form);
        Assert.True(fixture.Form.Visible);
        fixture.AgeLoot(TimeSpan.FromMinutes(1));
        Refresh(fixture.Form);
        Assert.False(fixture.Form.Visible);
        fixture.Upgrade(1135, level);
        Refresh(fixture.Form);
        Assert.True(fixture.Form.Visible);
        Assert.Equal(level, Assert.IsType<PetOverlayForm.LevelUpRow>(Cached(fixture.Form)).Level);
        Assert.Equal(level == 3, Cached(fixture.Form)!.IsMax);
        var animation = Animation(fixture.Form);
        var beforeEnd = Stopwatch.GetTimestamp() + 29 * Stopwatch.Frequency;
        Assert.NotNull(animation.Sample(beforeEnd));
        Assert.True(fixture.Form.Visible);
        Assert.Null(animation.Sample(beforeEnd + 2 * Stopwatch.Frequency));
        Refresh(fixture.Form);
        Assert.False(fixture.Form.Visible);
        Assert.Null(Cached(fixture.Form));
    });

    [Fact]
    public Task SuppressionAndExplicitShowCannotLeakThePanelAndExpiredContentDoesNotReturnAfterSuppression() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Form.Show();
        fixture.Gain(1135, 1);
        Refresh(fixture.Form);
        Assert.True(fixture.Form.Visible);
        fixture.Form.SetSuppressed(true);
        fixture.Form.Show();
        Refresh(fixture.Form);
        Assert.False(fixture.Form.Visible);
        fixture.AgeLoot(TimeSpan.FromMinutes(1));
        fixture.Form.SetSuppressed(false);
        Assert.False(fixture.Form.Visible);
        fixture.Gain(1163, 1);
        Refresh(fixture.Form);
        Assert.True(fixture.Form.Visible);
    });

    private static IReadOnlyList<PetOverlayForm.LootRow> Rows(PetOverlayForm form, bool focus) =>
        (IReadOnlyList<PetOverlayForm.LootRow>)typeof(PetOverlayForm)
            .GetField(focus ? "_focusRows" : "_lootRows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    private static void Refresh(PetOverlayForm form) => typeof(PetOverlayForm)
        .GetMethod("RefreshLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
    private static PetOverlayForm.LevelUpRow? Cached(PetOverlayForm form) => (PetOverlayForm.LevelUpRow?)typeof(PetOverlayForm)
        .GetField("_levelUpRow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
    private static byte[] Paint(PetOverlayForm form)
    {
        using var image = new Bitmap(form.Width, form.Height);
        using (var graphics = Graphics.FromImage(image))
            typeof(PetOverlayForm).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, [new System.Windows.Forms.PaintEventArgs(graphics, new Rectangle(Point.Empty, form.Size))]);
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static PetOverlayForm.LevelUpRow? Current(PetOverlayForm form) => (PetOverlayForm.LevelUpRow?)typeof(PetOverlayForm)
        .GetMethod("CurrentLevelUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
    private static PetLevelUpAnimation Animation(PetOverlayForm form) => (PetLevelUpAnimation)typeof(PetOverlayForm)
        .GetField("_levelUps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static void AssertUnshown(PetOverlayForm form)
    {
        Assert.False(form.IsHandleCreated);
        Assert.False(form.Visible);
    }
    private static Task RunSta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, byte[]?> _backups = [];
        private readonly string _previousLanguage = UiText.Language;
        private readonly TrackerService _tracker;
        private readonly MapTargetsService _targets;
        private long _sequence;
        internal string Folder { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-level-up-overlay-" + Guid.NewGuid().ToString("N"));
        internal SettingsService Settings { get; }
        internal ProgressService Progress { get; }
        internal PetOverlayForm Form { get; }

        internal Fixture(bool enabled = true)
        {
            Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Folder);
            // Settings have no path constructor: preserve the shared isolated file and change only Current in memory.
            foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.TargetsFile, AppPaths.RoutesFile, AppPaths.LegacyRoutesFile,
                Path.Combine(AppPaths.DataDirectory, "leveling-progress.json") })
            {
                _backups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
                if (path != AppPaths.SettingsFile) File.Delete(path);
            }
            Settings = new SettingsService();
            Settings.Current.OverlayEnabled = enabled;
            Settings.Current.OverlayLanguage = "en";
            Settings.Current.ProgressionEnabled = false;
            Settings.Current.LastMap = "altgard";
            Settings.Current.OverlayScale = 1;
            var catalog = new PetCatalog(new[] { 1135, 1163, 1146 }.Select(number =>
            {
                var source = PetIdTable.Find(number)!;
                return new PetDefinition { Id = source.CatalogId!, En = source.En, Genus = source.Genus.ToLowerInvariant(),
                    NumericId = number.ToString(CultureInfo.InvariantCulture) };
            }));
            var profile = Path.Combine(Folder, "progress.json");
            JsonFile.Save(profile, new ProgressProfile { SchemaVersion = 3 });
            Progress = new ProgressService(profile, catalog);
            Assert.True(Progress.BindPetSession(Context(0)));
            _tracker = new TrackerService(Settings, null);
            _targets = new MapTargetsService();
            Form = new PetOverlayForm(Progress, _tracker, Settings, _targets);
            AssertUnshown(Form);
        }
        internal static string Id(int number) => PetIdTable.Find(number)!.CatalogId!;
        internal string Name(int number) => Progress.Catalog.Find(Id(number))!.DisplayName("en");
        internal void Upgrade(int number, int level) => Assert.True(Progress.ApplyPetUpgrade(new(0, number, level), Context(++_sequence)));
        internal void Gain(int number, int quantity) => Assert.True(Progress.ApplyPetGain([new(number, quantity)], Context(++_sequence)));
        internal void Snapshot(int number, int level, int souls) =>
            Assert.True(Progress.ApplyPetSnapshot(new(new Dictionary<int, int> { [number] = level },
                new Dictionary<int, int> { [number] = souls }), Context(++_sequence)));
        internal void AgeLoot(TimeSpan age)
        {
            var recent = (List<LoggedSoul>)typeof(ProgressService).GetField("_recent", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Progress)!;
            for (var index = 0; index < recent.Count; index++) recent[index] = recent[index] with { At = DateTimeOffset.Now - age };
        }
        internal string Mark(int number, double x)
        {
            Assert.True(_targets.Toggle("altgard", x, 200, Name(number), "Pets", null, petId: Id(number)));
            return Assert.Single(_targets.Targets, target => target.PetId == Id(number)).Id;
        }
        internal void Unmark(string id) => _targets.Remove(id);
        private static PetProgressContext Context(long sequence) => new(1, sequence, DateTimeOffset.UtcNow);

        public void Dispose()
        {
            if (!Form.IsDisposed) Form.Dispose();
            _tracker.Dispose();
            _targets.Dispose();
            foreach (var (path, bytes) in _backups)
            {
                if (bytes is null) File.Delete(path);
                else File.WriteAllBytes(path, bytes);
            }
            UiText.Language = _previousLanguage;
            Directory.Delete(Folder, recursive: true);
        }
    }
}
