using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkKibeliskBindingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 3, 0, 0, TimeSpan.Zero);
    private static readonly ExplorationPlace[] Places =
    [
        new("hidden", "altgard", "kibelisk", 0, "Synthetic Cave", null, TeleportId: 1052020),
        new("hidden-side", "altgard", "kibelisk", 1, "Synthetic Cave Side", null, TeleportId: 1052020),
        new("other", "altgard", "kibelisk", 2, "Synthetic Other Pillar", null, TeleportId: 1052021),
        new("wrong-dungeon", "altgard", "dungeon", 3, "Synthetic Chamber", null, TeleportId: 1052099),
        new("wrong-stronghold", "altgard", "stronghold", 4, "Synthetic Fort", null, TeleportId: 1052099),
        new("wrong-field", "altgard", "kibelisk", 5, "Synthetic Wrong Field", null, DungeonId: 1052099),
    ];
    private static NetworkProgressContext Context(long sequence, long generation = 1, int server = 501, long character = 101) =>
        new(new(character, server, 1001, "Synthetic Alpha"), generation, sequence, At.AddSeconds(sequence));
    private static TeleportProgressSnapshot List(params int[] ids) => new(ids.ToHashSet());

    private sealed class Folder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-kibelisk-binding-" + Guid.NewGuid().ToString("N"));
        internal string File { get; }
        internal Folder() { Directory.CreateDirectory(_root); File = Path.Combine(_root, "exploration.json"); }
        internal ExplorationService Service() => new(File, Places);
        internal ExplorationProfiles Read() => JsonSerializer.Deserialize<ExplorationProfiles>(System.IO.File.ReadAllText(File))!;
        internal void Save(ExplorationProfiles value) => System.IO.File.WriteAllText(File, JsonSerializer.Serialize(value));
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void OwnBindingCompletesOnlyExactKibeliskPlacementsAndPreservesTheFirstEvidence()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        var changes = 0;
        service.Changed += () => changes++;
        Assert.True(service.ApplyNetworkTeleportBinding(1052020, Context(2)));
        Assert.Equal(new[] { "hidden", "hidden-side" }, service.Completed.Order());
        foreach (var id in new[] { "hidden", "hidden-side" })
        {
            var entry = service.Snapshot().Progress[id];
            Assert.Equal(ExplorationProgressState.DiscoveredCompleted, entry.State);
            Assert.Equal(ProgressFreshness.Fresh, entry.Freshness);
            Assert.Equal("network-kibelisk-binding", entry.Source);
            Assert.Equal(Context(2).At, entry.ObservedAt);
            Assert.True(entry.TeleportPresent);
            Assert.Equal("network-teleport-binding", entry.RawSource);
            Assert.Equal(ProgressFreshness.Fresh, entry.RawFreshness);
            Assert.Equal(Context(2).At, entry.RawObservedAt);
        }
        foreach (var id in new[] { "other", "wrong-dungeon", "wrong-stronghold", "wrong-field" })
            Assert.Null(service.Snapshot().Progress[id].State);
        Assert.False(service.ApplyNetworkTeleportBinding(1052020, Context(2)));
        Assert.Equal(1, changes);
        Assert.True(service.ApplyNetworkTeleportBinding(1052020, Context(3)));
        var saved = Assert.Single(folder.Read().Characters);
        Assert.Equal(new[] { 1052020 }, saved.RawTeleportIds);
        Assert.Equal(Context(2).At, saved.TeleportBoundAt[1052020]);
        Assert.Empty(saved.DungeonCompletedAt);
        Assert.Empty(saved.RawQuests);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("wrong-server")]
    [InlineData("wrong-character")]
    [InlineData("old-generation")]
    [InlineData("future-generation")]
    [InlineData("before-binding")]
    [InlineData("invalidated")]
    public void UntrustedContextsCannotWriteRawOrSemanticBindingEvidence(string variant)
    {
        using var folder = new Folder();
        var service = folder.Service();
        if (variant != "unbound") service.BindNetworkContext(Context(10, generation: 2));
        if (variant == "invalidated") service.MarkNetworkStale();
        var context = variant switch
        {
            "wrong-server" => Context(11, 2, server: 502),
            "wrong-character" => Context(11, 2, character: 102),
            "old-generation" => Context(11),
            "future-generation" => Context(11, 3),
            "before-binding" => Context(9, 2),
            _ => Context(11, 2),
        };
        Assert.False(service.ApplyNetworkTeleportBinding(1052020, context));
        Assert.Empty(service.Completed);
        if (System.IO.File.Exists(folder.File))
            Assert.All(folder.Read().Characters, c => { Assert.Empty(c.TeleportBoundAt); Assert.Empty(c.RawTeleportIds); });
    }

    [Fact]
    public void LegacyGuidAndExactProfileLabelSurviveWhileASeparateSelectedProfileIsUntouched()
    {
        using var folder = new Folder();
        const string guid = "0123456789abcdef0123456789abcdef";
        const string label = "sYnThEtIc AlPhA";
        folder.Save(new() { Active = guid, Characters = [new() { Id = guid, Name = label, Done = ["other"] }] });
        var service = folder.Service();
        Assert.Equal(CharacterChoice.Adopted, service.BindNetworkContext(Context(1)));
        Assert.Equal(guid, service.ActiveId);
        service.Add("Synthetic Beta");
        var selected = service.ActiveId;
        service.SetState(selected, ["other"], ExplorationProgressState.DiscoveredIncomplete);
        var before = service.Snapshot().Progress["other"];
        Assert.True(service.ApplyNetworkTeleportBinding(1052020, Context(2)));
        Assert.Equal(selected, service.ActiveId);
        Assert.Equal(before, service.Snapshot().Progress["other"]);
        Assert.Null(service.Snapshot().Progress["hidden"].State);
        var saved = folder.Read();
        Assert.Equal(label, saved.Characters.Single(c => c.Id == guid).Name);
        Assert.Equal(Context(2).At, saved.Characters.Single(c => c.Id == guid).TeleportBoundAt[1052020]);
        Assert.Empty(saved.Characters.Single(c => c.Id == selected).TeleportBoundAt);
        Assert.Empty(saved.Characters.Single(c => c.Id == selected).RawTeleportIds);
        service.BindNetworkContext(Context(1, generation: 2));
        Assert.Equal(guid, service.ActiveId);
        var reloaded = folder.Service();
        Assert.Equal(guid, reloaded.ActiveId);
        Assert.Equal(label, reloaded.Characters.Single(c => c.Id == guid).Name);
        Assert.Equal(new[] { "hidden", "hidden-side", "other" }, reloaded.Completed.Order());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(folder.File)!, "exploration.json.pre-network-*.bak"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ExplorationProgressState.Undiscovered)]
    [InlineData(ExplorationProgressState.DiscoveredIncomplete)]
    [InlineData(ExplorationProgressState.DiscoveredCompleted)]
    public void ManualStateRemainsProtectedWhileRawPresenceAndPositiveEvidencePersist(ExplorationProgressState? state)
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        service.SetState(service.ActiveId, ["hidden", "hidden-side"], state);
        var before = service.Snapshot().Progress["hidden"];
        Assert.True(service.ApplyNetworkTeleportBinding(1052020, Context(2)));
        var after = service.Snapshot().Progress["hidden"];
        AssertManual(before, after);
        Assert.True(after.TeleportPresent);
        Assert.Equal("network-teleport-binding", after.RawSource);
        Assert.True(service.ApplyNetworkTeleports(List(), Context(3)));
        var reloaded = folder.Service();
        AssertManual(before, reloaded.Snapshot().Progress["hidden"]);
        Assert.False(reloaded.Snapshot().Progress["hidden"].TeleportPresent);
        Assert.Equal(state == ExplorationProgressState.DiscoveredCompleted, reloaded.Completed.Contains("hidden"));
        Assert.Equal(Context(2).At, Assert.Single(folder.Read().Characters).TeleportBoundAt[1052020]);
    }

    [Fact]
    public void PositiveListsConfirmKnownKibelisksWhileAbsenceAndQuestUpdatesKeepBindingsAcrossExitAndReload()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.True(service.ApplyNetworkTeleports(List(1052021), Context(20)));
        Assert.True(service.Snapshot().Progress["other"].TeleportPresent);
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, service.Snapshot().Progress["other"].State);
        // Independent source sequence: a list must not consume the earlier valid binding sequence.
        Assert.True(service.ApplyNetworkTeleportBinding(1052020, Context(2)));
        Assert.True(service.ApplyNetworkTeleports(List(), Context(21)));
        Assert.True(service.ApplyNetworkQuests(new([]), Context(22)));
        Assert.False(service.Snapshot().Progress["hidden"].TeleportPresent);
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, service.Snapshot().Progress["hidden"].State);
        Assert.Equal("network-kibelisk-binding", service.Snapshot().Progress["hidden"].Source);
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, service.Snapshot().Progress["other"].State);
        Assert.Empty(Assert.Single(folder.Read().Characters).RawTeleportIds);
        service.MarkNetworkTeleportSourceStale();
        Assert.Equal(ProgressFreshness.Fresh, service.Snapshot().Progress["hidden"].Freshness);
        service.MarkNetworkStale();
        Assert.False(service.ApplyNetworkTeleportBinding(1052021, Context(23)));
        var reloaded = folder.Service();
        Assert.Contains("hidden", reloaded.Completed);
        Assert.Equal(ProgressFreshness.Stale, reloaded.Snapshot().Progress["hidden"].Freshness);
        Assert.Equal(Context(2).At, Assert.Single(folder.Read().Characters).TeleportBoundAt[1052020]);
        reloaded.BindNetworkContext(Context(1, generation: 2));
        Assert.True(reloaded.ApplyNetworkTeleportBinding(1052020, Context(2, generation: 2)));
        Assert.Equal(Context(2).At, Assert.Single(folder.Read().Characters).TeleportBoundAt[1052020]);
    }

    [Fact]
    public void BaselinePreservesDirectBindingProvenanceAndRefreshesOnlyItsConfirmation()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.True(service.ApplyNetworkTeleportBinding(1052020, Context(2)));
        service.MarkNetworkStale();
        service.BindNetworkContext(Context(1, generation: 2));
        Assert.Equal(ProgressFreshness.Stale, service.Snapshot().Progress["hidden"].Freshness);
        Assert.True(service.ApplyNetworkTeleports(List(1052020), Context(8, generation: 2)));
        var entry = service.Snapshot().Progress["hidden"];
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, entry.State);
        Assert.Equal("network-kibelisk-binding", entry.Source);
        Assert.Equal(Context(2).At, entry.ObservedAt);
        Assert.Equal(ProgressFreshness.Fresh, entry.Freshness);
        Assert.Equal("network-teleport-unconfirmed", entry.RawSource);
        Assert.Equal(Context(8, generation: 2).At, entry.RawObservedAt);
        Assert.Equal(Context(2).At, Assert.Single(folder.Read().Characters).TeleportBoundAt[1052020]);
    }

    [Fact]
    public void PositiveBaselineMapsOnlyKnownKibelisksAndRetainsItsFirstObservationOnRefresh()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        Assert.True(service.ApplyNetworkTeleports(List(1052020, 1052099, 999999), Context(2)));
        Assert.Equal(new[] { "hidden", "hidden-side" }, service.Completed.Order());
        foreach (var id in new[] { "hidden", "hidden-side" })
        {
            var entry = service.Snapshot().Progress[id];
            Assert.Equal("network-kibelisk-baseline", entry.Source);
            Assert.Equal(Context(2).At, entry.ObservedAt);
            Assert.Equal(ProgressFreshness.Fresh, entry.Freshness);
        }
        Assert.True(service.ApplyNetworkTeleports(List(1052020), Context(3)));
        var refreshed = service.Snapshot().Progress["hidden"];
        Assert.Equal("network-kibelisk-baseline", refreshed.Source);
        Assert.Equal(Context(2).At, refreshed.ObservedAt);
        Assert.Equal(Context(3).At, refreshed.RawObservedAt);
        var saved = Assert.Single(folder.Read().Characters);
        Assert.Equal(new[] { 1052020 }, saved.TeleportBoundAt.Keys);
        Assert.Equal(Context(2).At, saved.TeleportBoundAt[1052020]);
        Assert.Null(service.Snapshot().Progress["wrong-dungeon"].State);
        Assert.Null(service.Snapshot().Progress["wrong-stronghold"].State);
        Assert.Null(service.Snapshot().Progress["wrong-field"].State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ExplorationProgressState.Undiscovered)]
    [InlineData(ExplorationProgressState.DiscoveredIncomplete)]
    [InlineData(ExplorationProgressState.DiscoveredCompleted)]
    public void PositiveBaselinePreservesManualSemanticStateButStoresItsIndependentEvidence(ExplorationProgressState? state)
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        service.SetState(service.ActiveId, ["hidden", "hidden-side"], state);
        var before = service.Snapshot().Progress["hidden"];
        Assert.True(service.ApplyNetworkTeleports(List(1052020), Context(2)));
        AssertManual(before, service.Snapshot().Progress["hidden"]);
        Assert.True(service.Snapshot().Progress["hidden"].TeleportPresent);
        Assert.True(service.ApplyNetworkTeleports(List(), Context(3)));
        AssertManual(before, service.Snapshot().Progress["hidden"]);
        var reloaded = folder.Service();
        AssertManual(before, reloaded.Snapshot().Progress["hidden"]);
        Assert.Equal(state == ExplorationProgressState.DiscoveredCompleted, reloaded.Completed.Contains("hidden"));
        Assert.Equal(Context(2).At, Assert.Single(folder.Read().Characters).TeleportBoundAt[1052020]);
    }

    [Fact]
    public void InvalidOrUnmappedIdsCannotCreateEvidenceOrConsumeTheFollowingValidSequence()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        foreach (var id in new[] { 0, -1, 999999, 1052099 })
            Assert.False(service.ApplyNetworkTeleportBinding(id, Context(2)));
        var saved = Assert.Single(folder.Read().Characters);
        Assert.Empty(saved.RawTeleportIds);
        Assert.Empty(saved.TeleportBoundAt);
        Assert.Empty(service.Completed);
        Assert.True(service.ApplyNetworkTeleportBinding(1052020, Context(2)));
    }

    [Fact]
    public void StorageFailureRetainsInMemoryEvidenceDedupeAndTheNextBindingSavesBoth()
    {
        using var folder = new Folder();
        var service = folder.Service();
        service.BindNetworkContext(Context(1));
        System.IO.File.Delete(folder.File);
        Directory.CreateDirectory(folder.File);
        Assert.True(service.ApplyNetworkTeleportBinding(1052020, Context(2)));
        Assert.Contains("hidden", service.Completed);
        Assert.False(service.ApplyNetworkTeleportBinding(1052020, Context(2)));
        Directory.Delete(folder.File);
        Assert.True(service.ApplyNetworkTeleportBinding(1052021, Context(3)));
        var saved = Assert.Single(folder.Read().Characters);
        Assert.Equal(Context(2).At, saved.TeleportBoundAt[1052020]);
        Assert.Equal(Context(3).At, saved.TeleportBoundAt[1052021]);
        Assert.Equal(new[] { 1052020, 1052021 }, saved.RawTeleportIds.Order());
        Assert.Contains("other", folder.Service().Completed);
    }

    private static void AssertManual(ExplorationPlaceProgress before, ExplorationPlaceProgress after)
    {
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Source, after.Source);
        Assert.Equal(before.Freshness, after.Freshness);
        Assert.Equal(before.ObservedAt, after.ObservedAt);
        Assert.True(after.ManualOverride);
    }
}
