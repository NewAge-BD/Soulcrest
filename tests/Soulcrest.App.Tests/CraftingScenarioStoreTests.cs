using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Crafting;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CraftingScenarioStoreTests
{
    private const string First = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void FullManualDraftRoundTripsAndContainsNoObservedOrEconomicState()
    {
        using var f = new Folder();
        using (var store = new CraftingScenarioStore(f.File, new Clock()))
        { store.Stage(Guid.Parse(First).ToString("D").ToUpperInvariant(), Draft()); store.Flush(); Assert.False(store.State.IsDirty); }
        using var restored = new CraftingScenarioStore(f.File, new Clock());
        Assert.True(Draft().SameAs(Assert.IsType<CraftingScenarioDraft>(restored.Read(First))));
        Assert.NotNull(restored.State.SavedAt); Assert.Null(restored.State.Error);
        var json = System.IO.File.ReadAllText(f.File);
        foreach (var forbidden in new[] { "ObservedLevels", "ReferenceLevels", "BuyPrices", "SellPrices", "Result", "ServerId", "CharacterId", "TargetQuantity", "TargetAttempts" })
            Assert.DoesNotContain(forbidden, json);
        Assert.Contains("\"Quantity\": 7", json);
    }

    [Fact]
    public async Task BurstEditsUseOneHalfSecondDebounceAndOneAtomicLatestWrite()
    {
        using var f = new Folder(); var clock = new Clock(); var writes = 0;
        using var store = new CraftingScenarioStore(f.File, clock, (path, value) => { writes++; JsonFile.Save(path, value); });
        store.Stage(First, Draft()); store.Stage(First, Draft() with { Query = "latest" });
        store.Stage(Second, Draft() with { Quantity = 3 });
        Assert.Equal(TimeSpan.FromMilliseconds(500), clock.Timers[0].Due); Assert.Equal(0, writes); Assert.True(store.State.IsDirty);
        clock.Timers[0].Fire(); await store.FlushAsync(); Assert.Equal(1, writes); Assert.False(store.State.IsDirty);
        store.Stage(First, Draft() with { Query = "latest" }); store.Flush(); Assert.Equal(1, writes);
        using var read = new CraftingScenarioStore(f.File, new Clock());
        Assert.Equal("latest", read.Read(First)!.Query); Assert.Equal(3, read.Read(Second)!.Quantity);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("duplicate")]
    [InlineData("unknown-property")]
    [InlineData("truncated")]
    public void InvalidStoresAreReadOnlyAndNeverOverwritten(string variant)
    {
        using var f = new Folder();
        var text = variant switch
        {
            "schema" => "{\"SchemaVersion\":2,\"Profiles\":[]}",
            "duplicate" => "{\"SchemaVersion\":1,\"SchemaVersion\":1,\"Profiles\":[]}",
            "unknown-property" => "{\"SchemaVersion\":1,\"Profiles\":[],\"Unrecognized\":true}",
            _ => "{"
        };
        System.IO.File.WriteAllText(f.File, text);
        using var store = new CraftingScenarioStore(f.File, new Clock());
        Assert.True(store.State.IsReadOnly); Assert.Equal(CraftingScenarioStore.LoadError, store.State.Error);
        store.Stage(First, Draft()); store.Flush(); Assert.Equal(text, System.IO.File.ReadAllText(f.File));
    }

    [Fact]
    public void OversizedMapsAreRejectedAndSaveFailureCanBeRetriedWithoutLosingTheRamDraft()
    {
        using var f = new Folder(); var fail = true;
        using var store = new CraftingScenarioStore(f.File, new Clock(), (path, value) =>
        { if (fail) throw new IOException("synthetic private path"); JsonFile.Save(path, value); });
        store.Stage(First, Draft() with { OwnedItems = Enumerable.Range(1, 1025).ToImmutableDictionary(id => id, _ => 1L) });
        Assert.Null(store.Read(First));
        store.Stage(First, Draft()); store.Flush();
        Assert.Equal(CraftingScenarioStore.SaveError, store.State.Error); Assert.True(store.State.IsDirty);
        Assert.Equal(7, store.Read(First)!.Quantity);
        fail = false; store.Flush(); Assert.Null(store.State.Error); Assert.False(store.State.IsDirty);
    }

    [Fact]
    public async Task RemovingAProfileDuringAnOlderWriteCannotRecreateIt()
    {
        using var f = new Folder(); using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        using var store = new CraftingScenarioStore(f.File, new Clock(), (path, value) =>
        {
            if (Interlocked.Increment(ref count) == 1) { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
            JsonFile.Save(path, value);
        });
        store.Stage(First, Draft()); var writing = store.FlushAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); store.DeleteProfile(First);
            store.Stage(First, Draft() with { Query = "must not return" }); Assert.Null(store.Read(First));
        }
        finally { release.Set(); }
        await writing.WaitAsync(TimeSpan.FromSeconds(5));
        using var loaded = new CraftingScenarioStore(f.File, new Clock()); Assert.Null(loaded.Read(First));
    }

    [Fact]
    public void SanitizerRejectsInvalidReferencesAndKeepsOnlyKnownManualValues()
    {
        var draft = Draft() with
        {
            Profession = "obsolete", Subcategory = "obsolete", Quantity = -5,
            SimulatedLevels = ImmutableDictionary<string, int>.Empty.Add("Blacksmithing", 35).Add("Cooking", 101).Add("Other", 1),
            RecipeChoices = ImmutableDictionary<int, int>.Empty.Add(5, 301).Add(8, 300),
            Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(5, CraftingAcquisition.Craft).Add(999, CraftingAcquisition.Buy),
            ReplacementChoices = [new(200, 4, 999), new(999, 4, 6)],
            OwnedItems = ImmutableDictionary<int, long>.Empty.Add(4, 1).Add(5, -1).Add(999, 4)
        };
        draft = draft with { OwnedItems = draft.OwnedItems.Add(6, 5) };
        var clean = draft.Sanitize(Catalog(), "Elyos");
        Assert.Equal("Transfercrafting", clean.Profession); Assert.Equal("Bow", clean.Subcategory); Assert.Equal(1, clean.Quantity);
        Assert.Equal(35, Assert.Single(clean.SimulatedLevels).Value); Assert.Empty(clean.RecipeChoices); Assert.Empty(clean.Acquisition);
        Assert.Empty(clean.ReplacementChoices); Assert.Equal(2, clean.OwnedItems.Count);
        Assert.Equal(1L, clean.OwnedItems[4]); Assert.Equal(1L, clean.OwnedItems[6]);
        var preserved = Draft() with { Profession = "Alchemy", Subcategory = "Potion" };
        Assert.Equal("Alchemy", preserved.Sanitize(Catalog(), "Elyos").Profession);
        var synchronized = (preserved with { Query = "" }).Sanitize(Catalog(), "Elyos");
        Assert.Equal("Transfercrafting", synchronized.Profession); Assert.Equal("Bow", synchronized.Subcategory);
        var normalStock = (Draft() with { SelectedRecipeId = 100, TargetItemId = 1, Query = "",
            OwnedItems = ImmutableDictionary<int, long>.Empty.Add(1, 4) }).Sanitize(Catalog(), "Elyos");
        Assert.Equal(4L, normalStock.OwnedItems[1]);
    }

    [Fact]
    public async Task ServiceRestoresEachProfilesFullDraftWithoutStartingRequestsAndDisposeFlushesEdits()
    {
        using var f = new Folder(); var clock = new Clock(); using var store = new CraftingScenarioStore(f.File, clock);
        store.Stage(First, Draft()); store.Stage(Second, Draft() with { Quantity = 3, Query = "second", OwnedItems = ImmutableDictionary<int, long>.Empty });
        store.Flush(); var client = new Client(); string? context = First;
        var settings = new SettingsService(); settings.Current.ExchangeMarketProfiles = new()
        { [First] = new() { Faction = "Elyos", AutoDetectServer = false }, [Second] = new() { Faction = "Elyos", AutoDetectServer = false } };
        using (var service = new CraftingCalculatorService(settings, client, new ExchangeMarketCache(), Catalog(),
            () => CraftingProficiencySnapshot.Empty, () => (context, null), clock, store))
        {
            Assert.Equal(201, service.Snapshot.SelectedRecipeId); Assert.Equal("saved query", service.Snapshot.Query);
            Assert.Equal(77, service.Snapshot.SimulatedLevels["Handicrafting"]); Assert.Empty(service.Snapshot.ObservedLevels);
            Assert.Equal(17L, service.Snapshot.OwnedItems[5]); Assert.Equal(6, service.Snapshot.ReplacementChoices[new(200, 4)]);
            Assert.Empty(service.Snapshot.BuyPrices); Assert.Null(service.Snapshot.Result); Assert.False(service.Active);
            service.SetQuery("first edited"); service.SetOwnedQuantity(5, 19);
            context = null; await service.RefreshAsync(); Assert.Null(service.Snapshot.ProfileId);
            Assert.False(service.Snapshot.ScenarioStorage.Available);
            service.SetQuery("unbound edit"); Assert.Equal("first edited", store.Read(First)!.Query);
            context = Second; await service.RefreshAsync();
            Assert.Equal("second", service.Snapshot.Query); Assert.Empty(service.Snapshot.OwnedItems);
            service.SetQuery("second edited");
            context = First; await service.RefreshAsync();
            Assert.Equal("first edited", service.Snapshot.Query); Assert.Equal(19L, service.Snapshot.OwnedItems[5]);
            Assert.Equal(7, store.Read(First)!.Quantity); Assert.Equal(3, store.Read(Second)!.Quantity);
            Assert.Equal(0, client.Requests); Assert.True(service.Snapshot.ScenarioStorage.Available);
        }
        using var restored = new CraftingScenarioStore(f.File, new Clock());
        Assert.Equal("first edited", restored.Read(First)!.Query); Assert.Equal("second edited", restored.Read(Second)!.Query);
        Assert.False(restored.State.IsDirty);
    }

    [Fact]
    public void DisposeFlushesAnUnleasedManualEditWithoutAnyMarketQueries()
    {
        using var f = new Folder(); using var store = new CraftingScenarioStore(f.File, new Clock()); var client = new Client();
        using (var service = new CraftingCalculatorService(new SettingsService(), client, new ExchangeMarketCache(), Catalog(),
            () => CraftingProficiencySnapshot.Empty, () => (First, null), new Clock(), store)) service.SetQuery("close now");
        Assert.Equal(0, client.Requests);
        using var restored = new CraftingScenarioStore(f.File, new Clock()); Assert.Equal("close now", restored.Read(First)!.Query);
    }

    [Fact]
    public async Task OnlyTheLastLeaseFlushesPendingPreferencesAndCancelsTheUnfinishedCalculation()
    {
        using var f = new Folder(); var clock = new Clock(); using var store = new CraftingScenarioStore(f.File, clock);
        var settings = new SettingsService(); settings.Current.OnlineMarketPricesEnabled = false; var client = new Client();
        using var service = new CraftingCalculatorService(settings, client, new ExchangeMarketCache(), Catalog(),
            () => CraftingProficiencySnapshot.Empty, () => (First, null), clock, store);
        var firstLease = service.Acquire(); var secondLease = service.Acquire();
        service.SetQuery("last lease"); firstLease.Dispose(); Assert.False(System.IO.File.Exists(f.File));
        Assert.True(service.Active); secondLease.Dispose(); Assert.False(service.Active);
        await service.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        using var restored = new CraftingScenarioStore(f.File, new Clock()); Assert.Equal("last lease", restored.Read(First)!.Query);
        Assert.Equal(0, client.Requests);
    }

    private static CraftingScenarioDraft Draft() => new()
    {
        Profession = "Transfercrafting", Subcategory = "Bow", Query = "saved query", SelectedRecipeId = 201, TargetItemId = 7, Quantity = 7,
        SimulatedLevels = ImmutableDictionary<string, int>.Empty.Add("Handicrafting", 77),
        Acquisition = ImmutableDictionary<int, CraftingAcquisition>.Empty.Add(5, CraftingAcquisition.Craft),
        RecipeChoices = ImmutableDictionary<int, int>.Empty.Add(5, 300), ReplacementChoices = [new(200, 4, 6)],
        OwnedItems = ImmutableDictionary<int, long>.Empty.Add(6, 1).Add(5, 17)
    };
    private static CraftingCatalog Catalog() => new("synthetic-scenario", Enumerable.Range(1, 8).Select(id => new CraftingItem
    { Id = id, NameEn = "Synthetic " + id, NameDe = "Synthetisch " + id, IsEquipment = id is not (5 or 8), IsTradable = true }),
    [
        new() { Id = 100, Profession = "Blacksmithing", Subcategory = "Greatsword", OutputItemId = 1, Faction = "Elyos",
            OutputQuantity = 1, CraftingFee = 0, MechanicsQualified = true, SuccessRates = [1m] },
        new() { Id = 200, Profession = "Handicrafting", Subcategory = "Bow", OutputItemId = 3, Faction = "Elyos",
            OutputQuantity = 1, CraftingFee = 0, IsTransferCrafting = true, ComboRecipeId = 201, ComboActivationChance = .25m,
            TransferItemIds = [4, 6], Ingredients = [new() { ItemId = 4, Quantity = 1, ReplacementItemIds = [6] }, new() { ItemId = 5, Quantity = 1 }] },
        new() { Id = 201, Profession = "Handicrafting", Subcategory = "Bow", OutputItemId = 7, Faction = "Elyos",
            OutputQuantity = 1, CraftingFee = 0, IsTransferCrafting = true },
        new() { Id = 300, Profession = "Blacksmithing", Subcategory = "Materials", OutputItemId = 5, Faction = "Elyos",
            OutputQuantity = 1, CraftingFee = 0, MechanicsQualified = true, SuccessRates = [1m], Ingredients = [new() { ItemId = 8, Quantity = 1 }] },
        new() { Id = 301, Profession = "Blacksmithing", Subcategory = "Materials", OutputItemId = 5, Faction = "Asmodians",
            OutputQuantity = 1, CraftingFee = 0, MechanicsQualified = true, SuccessRates = [1m] }
    ]);
    private sealed class Client : IExchangeMarketClient
    {
        public int Requests { get; private set; }
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken)
        { Requests++; throw new InvalidOperationException("Unleased scenario requested servers."); }
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
        { Requests++; throw new InvalidOperationException("Unleased scenario requested prices."); }
    }
    private sealed class Folder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-scenarios-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(_root, "scenarios.json");
        public Folder() => Directory.CreateDirectory(_root);
        public void Dispose() => Directory.Delete(_root, true);
    }
    private sealed class Clock : TimeProvider
    {
        public List<Timer> Timers { get; } = [];
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(callback, state); timer.Change(dueTime, period); Timers.Add(timer); return timer; }
        public sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan Due { get; private set; }
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; return true; }
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
