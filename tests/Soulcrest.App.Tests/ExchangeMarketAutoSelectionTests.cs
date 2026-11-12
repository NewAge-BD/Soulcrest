using System.Collections.Immutable;
using System.Text.Json;
using System.Reflection;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class ExchangeMarketAutoSelectionTests
{
    private const string First = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly ExchangeMarketServer Server = new(11301, "Synthetic market", "EU", "Europe", "Elyos", true) { GameServerKey = 1301 };

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(null, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public void OnlyAnExplicitManualChoiceDisablesAutomaticSelection(bool? mode, bool selected, bool automatic)
    {
        var model = Model(selected) with { AutoDetectServer = mode };
        Assert.Equal(automatic, model.AutomaticServerSelection);
        var stored = JsonSerializer.Serialize(model);
        Assert.DoesNotContain("AutomaticServerSelection", stored);
        Assert.DoesNotContain("HasServerSelection", stored);
        Assert.Equal(model, JsonSerializer.Deserialize<ExchangeMarketProfileSettings>(stored));
    }

    [Fact]
    public async Task ExactOwnServerJoinUsesProviderRegionAndFactionWithoutChangingFeesOrOtherProfiles()
    {
        using var f = new Fixture();
        f.Settings.Current.ExchangeMarketProfiles[First] = Model(false) with { SalesTaxPercent = 17, RegistrationFeePercent = 3 };
        var other = Model(true) with { AutoDetectServer = false };
        f.Settings.Current.ExchangeMarketProfiles[Second] = other;
        await using var service = f.Create();
        await service.FlushAsync();
        var model = f.Settings.GetExchangeMarketProfile(First);
        Assert.Equal(Server.ServerId, model.ServerId);
        Assert.Equal(Server.GameServerKey, model.GameServerKey);
        Assert.Equal(Server.Region, model.Region);
        Assert.Equal(Server.Faction, model.Faction);
        Assert.True(model.AutomaticServerSelection);
        Assert.Equal(17, model.SalesTaxPercent); Assert.Equal(3, model.RegistrationFeePercent);
        Assert.Equal(other, f.Settings.GetExchangeMarketProfile(Second));
        Assert.Equal(First, service.Snapshot.ProfileId);
        Assert.True(service.Snapshot.MappingKnown);
        Assert.False(service.Snapshot.IsError);
        Assert.Equal(1, f.Client.ServerCalls);
        await service.FlushAsync(); await service.FlushAsync();
        Assert.Equal(1, f.Client.ServerCalls);
        Assert.Equal(0, f.Client.QuoteCalls);
    }

    [Fact]
    public void FeeOnlySavePreservesAnAutomaticAssignmentCommittedAfterTheEditorReadItsOldModel()
    {
        using var f = new Fixture();
        var rendered = Model(false);
        Assert.False(rendered.HasServerSelection);
        Assert.True(f.Settings.TrySetDetectedExchangeMarketProfile(First, Server, () => true));
        f.Settings.SetExchangeMarketFees(First, 14, 3);
        var saved = f.Settings.GetExchangeMarketProfile(First);
        Assert.Equal(Server.ServerId, saved.ServerId); Assert.Equal(Server.Region, saved.Region); Assert.Equal(Server.Faction, saved.Faction);
        Assert.True(saved.AutoDetectServer); Assert.Equal(14, saved.SalesTaxPercent); Assert.Equal(3, saved.RegistrationFeePercent);
        Assert.Equal(saved, new SettingsService().GetExchangeMarketProfile(First));
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Settings.SetExchangeMarketFees(First, 99, 2));
        Assert.Equal(saved, f.Settings.GetExchangeMarketProfile(First));
    }

    [Fact]
    public async Task QualifiedOwnProfileWinsOverAManuallySelectedExplorationProfileAndRevocationStopsIt()
    {
        using var f = new Fixture();
        var path = Path.Combine(Path.GetTempPath(), "SoulcrestAutoMarket-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var exploration = new ExplorationService(path, []);
            var own = new OwnCharacterContext(new(1, Server.GameServerKey!.Value, 1, "Synthetic character"), 1);
            exploration.BindNetworkContext(new(own.Identity, own.Generation, 1, DateTimeOffset.UtcNow));
            var bound = exploration.TryGetBoundCharacter(own)!;
            exploration.Add("Synthetic manually selected profile");
            Assert.NotEqual(bound.ProfileId, exploration.ActiveId);
            var source = new FarmSource { OwnCharacter = own };
            await using var service = new ExchangeMarketAutoSelectionService(f.Settings, source, exploration, f.Client, f.Cache);
            await service.FlushAsync();
            Assert.Equal(bound.ProfileId, service.Snapshot.ProfileId);
            Assert.Equal(Server.ServerId, f.Settings.GetExchangeMarketProfile(bound.ProfileId).ServerId);
            Assert.False(f.Settings.GetExchangeMarketProfile(exploration.ActiveId).HasServerSelection);
            source.Running = false; source.Publish(); await service.FlushAsync();
            Assert.Null(service.Snapshot.ProfileId);
            Assert.False(service.Snapshot.OwnCharacterKnown);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OfflineOrUnboundSourcesNeverFetchOrAssign(bool online, bool bound)
    {
        using var f = new Fixture();
        f.Settings.Current.OnlineMarketPricesEnabled = online;
        if (!bound) f.Authority = null;
        await using var service = f.Create(); await service.FlushAsync();
        Assert.Equal(0, f.Client.ServerCalls);
        Assert.False(f.Settings.GetExchangeMarketProfile(First).HasServerSelection);
        Assert.Equal(bound ? First : null, service.Snapshot.ProfileId);
        Assert.False(service.Snapshot.MappingKnown);
    }

    [Fact]
    public async Task ExplicitManualSelectionsAreNeverOverwritten()
    {
        using var f = new Fixture();
        var original = Model(true) with { ServerId = 999, AutoDetectServer = false };
        f.Settings.Current.ExchangeMarketProfiles[First] = original;
        await using var service = f.Create(); await service.FlushAsync();
        Assert.Equal(original, f.Settings.GetExchangeMarketProfile(First));
        Assert.Equal(0, f.Client.ServerCalls);
        Assert.False(service.Snapshot.Automatic);
    }

    [Fact]
    public async Task ACompleteLegacySelectionWithoutAnExplicitModeIsCorrectedAutomatically()
    {
        using var f = new Fixture();
        f.Settings.Current.ExchangeMarketProfiles[First] = Model(true) with { ServerId = 999, SalesTaxPercent = 15 };
        await using var service = f.Create(); await service.FlushAsync();
        var corrected = f.Settings.GetExchangeMarketProfile(First);
        Assert.Equal(Server.ServerId, corrected.ServerId);
        Assert.True(corrected.AutoDetectServer);
        Assert.Equal(15, corrected.SalesTaxPercent);
        Assert.True(service.Snapshot.MappingKnown);
    }

    [Fact]
    public void StartupEnablesOnlinePricesInMemoryWithoutRewritingTheSettingsFile()
    {
        using var f = new Fixture();
        f.Settings.SetOnlineMarketPricesEnabled(false);
        Assert.False(f.Settings.Current.OnlineMarketPricesEnabled);
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        var loaded = new SettingsService();
        Assert.True(loaded.Current.OnlineMarketPricesEnabled);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, 999, false)]
    [InlineData(null, 1301, true)]
    [InlineData(null, 11301, false)]
    [InlineData(true, null, false)]
    [InlineData(true, 999, false)]
    [InlineData(true, 1301, true)]
    [InlineData(true, 11301, false)]
    [InlineData(false, null, true)]
    [InlineData(false, 999, true)]
    public void OwnServerAccessorSuppressesStaleAutomaticMarketsWithoutChangingTheSavedChoice(bool? mode, int? nativeServer, bool available)
    {
        using var f = new Fixture();
        var original = Model(true) with { AutoDetectServer = mode, SalesTaxPercent = 17, RegistrationFeePercent = 1 };
        f.Settings.Current.ExchangeMarketProfiles[First] = original;
        var qualified = f.Settings.GetExchangeMarketProfileForOwnServer(First, nativeServer);
        Assert.Equal(available, qualified.HasServerSelection);
        Assert.Equal(original.SalesTaxPercent, qualified.SalesTaxPercent);
        Assert.Equal(original.RegistrationFeePercent, qualified.RegistrationFeePercent);
        Assert.Equal(original.AutoDetectServer, qualified.AutoDetectServer);
        Assert.Equal(original, f.Settings.GetExchangeMarketProfile(First));
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("missing-faction")]
    [InlineData("missing-native-key")]
    [InlineData("duplicate-native-key")]
    public async Task MissingOrIncompleteCatalogueMappingRemainsUnknownAndClearsOnlyAutomaticSelection(string failure)
    {
        using var f = new Fixture();
        f.Settings.Current.ExchangeMarketProfiles[First] = Model(true) with { AutoDetectServer = true };
        f.Client.Servers = failure switch
        {
            "missing-id" => [Server with { GameServerKey = 999 }],
            "missing-faction" => [Server with { Faction = "" }],
            "missing-native-key" => [Server with { GameServerKey = null }],
            _ => [Server, Server with { ServerId = 999 }],
        };
        await using var service = f.Create(); await service.FlushAsync();
        var model = f.Settings.GetExchangeMarketProfile(First);
        Assert.False(model.HasServerSelection); Assert.True(model.AutomaticServerSelection);
        Assert.Equal(10, model.SalesTaxPercent);
        Assert.False(service.Snapshot.MappingKnown); Assert.False(service.Snapshot.IsError);
        Assert.Equal(0, f.Client.QuoteCalls);
    }

    [Fact]
    public async Task NativeKeysAreJoinedExplicitlyWithoutArithmeticOrNameFallbacks()
    {
        using var f = new Fixture();
        // The explicit mapping is authoritative even when API ID, key and name have no numerical relation.
        f.Client.Servers = [Server with { ServerId = 77221, Name = "Different synthetic name" },
            Server with { ServerId = Server.GameServerKey!.Value, GameServerKey = 88 }];
        await using var service = f.Create(); await service.FlushAsync();
        var model = f.Settings.GetExchangeMarketProfile(First);
        Assert.Equal(77221, model.ServerId);
        Assert.Equal(Server.GameServerKey, model.GameServerKey);
        Assert.True(f.Settings.GetExchangeMarketProfileForOwnServer(First, Server.GameServerKey).HasServerSelection);
        Assert.False(f.Settings.GetExchangeMarketProfileForOwnServer(First, 77221).HasServerSelection);
        Assert.True(service.Snapshot.MappingKnown);
        Assert.Equal(model, new SettingsService().GetExchangeMarketProfile(First));
    }

    [Fact]
    public void LegacyAutomaticChoicesWithoutTheNativeMappingRemainUnknownUntilReassignment()
    {
        using var f = new Fixture();
        f.Settings.Current.ExchangeMarketProfiles[First] = Model(true) with { GameServerKey = null };
        Assert.False(f.Settings.GetExchangeMarketProfileForOwnServer(First, Server.GameServerKey).HasServerSelection);
        Assert.False(f.Settings.GetExchangeMarketProfileForOwnServer(First, Server.ServerId).HasServerSelection);
        Assert.True(f.Settings.GetExchangeMarketProfile(First).HasServerSelection);
    }

    [Fact]
    public async Task AServerWithoutPricesStillHasAnExactKnownIdentity()
    {
        using var f = new Fixture(); f.Client.Servers = [Server with { HasData = false }];
        await using var service = f.Create(); await service.FlushAsync();
        Assert.True(service.Snapshot.MappingKnown);
        Assert.Equal(Server.ServerId, f.Settings.GetExchangeMarketProfile(First).ServerId);
        Assert.Equal(0, f.Client.QuoteCalls);
    }

    [Fact]
    public async Task ConcurrentManualSelectionWinsOverAnInFlightCatalogueAndCancelsItsRequest()
    {
        using var f = new Fixture(); f.Client.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = f.Create();
        await f.Client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = Model(true) with { ServerId = 777, AutoDetectServer = false };
        f.Settings.SetExchangeMarketProfile(First, manual);
        Assert.True(f.Client.LastToken.IsCancellationRequested);
        f.Client.Pending.SetResult([Server]);
        await service.FlushAsync();
        Assert.Equal(manual, f.Settings.GetExchangeMarketProfile(First));
        Assert.False(service.Snapshot.Automatic);
    }

    [Fact]
    public async Task ConcurrentFeeChangesArePreservedWhenAutomaticMappingFinishes()
    {
        using var f = new Fixture(); f.Client.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = f.Create(); await f.Client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Settings.SetExchangeMarketProfile(First, Model(false) with { SalesTaxPercent = 19, RegistrationFeePercent = 1 });
        Assert.False(f.Client.LastToken.IsCancellationRequested);
        f.Client.Pending.SetResult([Server]); await service.FlushAsync();
        var model = f.Settings.GetExchangeMarketProfile(First);
        Assert.Equal(Server.ServerId, model.ServerId);
        Assert.Equal(19, model.SalesTaxPercent); Assert.Equal(1, model.RegistrationFeePercent);
    }

    [Fact]
    public async Task ACharacterSwitchCannotApplyThePreviousCharactersDelayedCatalogueResult()
    {
        using var f = new Fixture(); f.Client.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = f.Create(); await f.Client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Authority = new(Second, 2101, 2, 2);
        f.Client.Pending.SetResult([Server, Server with { ServerId = 12101, GameServerKey = 2101, Region = "NAE", Faction = "Asmodian" }]);
        await service.FlushAsync();
        Assert.False(f.Settings.GetExchangeMarketProfile(First).HasServerSelection);
        Assert.Equal(12101, f.Settings.GetExchangeMarketProfile(Second).ServerId);
        Assert.Equal(Second, service.Snapshot.ProfileId);
    }

    [Fact]
    public async Task DisabledOnlinePricesCancelInFlightWorkAndDoNotApplyItsResult()
    {
        using var f = new Fixture(); f.Client.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = f.Create(); await f.Client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Settings.SetOnlineMarketPricesEnabled(false);
        Assert.True(f.Client.LastToken.IsCancellationRequested);
        f.Client.Pending.SetResult([Server]); await service.FlushAsync();
        Assert.False(f.Settings.GetExchangeMarketProfile(First).HasServerSelection);
        Assert.Equal(1, f.Client.ServerCalls);
    }

    [Fact]
    public async Task StorageFailurePreservesFileAndModelAndDoesNotRetryOnEverySourceRefresh()
    {
        using var f = new Fixture();
        f.Settings.SetExchangeMarketProfile(First, Model(false));
        f.Cache.SaveServers([Server]);
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        using var blocker = new FileStream(AppPaths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var service = f.Create(); await service.FlushAsync();
        Assert.True(service.Snapshot.IsError);
        Assert.False(f.Settings.GetExchangeMarketProfile(First).HasServerSelection);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
        await service.FlushAsync();
        Assert.True(service.Snapshot.IsError);
        Assert.Equal(0, f.Client.ServerCalls);
    }

    [Fact]
    public async Task ExplicitRetryRecoversTheSameProfileAfterAStorageErrorWithoutChangingItsFees()
    {
        using var f = new Fixture();
        f.Settings.SetExchangeMarketProfile(First, Model(false) with { SalesTaxPercent = 15, RegistrationFeePercent = 1 });
        f.Cache.SaveServers([Server]);
        using var blocker = new FileStream(AppPaths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var service = f.Create();
        await service.FlushAsync(); Assert.True(service.Snapshot.IsError);
        blocker.Dispose();
        await service.RetryAsync();
        Assert.False(service.Snapshot.IsError); Assert.True(service.Snapshot.MappingKnown);
        var model = f.Settings.GetExchangeMarketProfile(First);
        Assert.Equal(Server.ServerId, model.ServerId); Assert.Equal(15, model.SalesTaxPercent); Assert.Equal(1, model.RegistrationFeePercent);
        Assert.Equal(0, f.Client.ServerCalls);
    }

    [Fact]
    public async Task AuthorityLossAtTheAtomicCommitGuardRejectsWritingBeforeAnySourceChangedCallback()
    {
        using var f = new Fixture();
        f.Settings.SetExchangeMarketProfile(First, Model(false));
        f.Cache.SaveServers([Server]);
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        var settingsGate = typeof(SettingsService).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Settings)!;
        var revoked = false;
        ExchangeMarketAutoSelectionAuthority? ReadAuthority()
        {
            if (Monitor.IsEntered(settingsGate)) { f.Authority = null; revoked = true; }
            return f.Authority;
        }
        await using var service = new ExchangeMarketAutoSelectionService(f.Settings, ReadAuthority, f.Client, f.Cache, TimeProvider.System);
        await service.FlushAsync();
        Assert.True(revoked);
        Assert.False(f.Settings.GetExchangeMarketProfile(First).HasServerSelection);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
        Assert.Null(service.Snapshot.ProfileId);
    }

    [Fact]
    public async Task NativeAuthorityPointerCanBeReadWhileTheTransportGateIsOwnedElsewhere()
    {
        using var source = new NetworkLootService(new ProgressService());
        var gate = typeof(NetworkLootService).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                acquired.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
        });
        await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.Null(await Task.Run(() => source.OwnCharacterSnapshot).WaitAsync(TimeSpan.FromSeconds(2))); }
        finally { release.Set(); await holder; }
        Assert.False(source.Running);
    }

    [Fact]
    public async Task MarketDiagnosisProjectsOnlyAnonymousAutomaticSelectionFlags()
    {
        using var f = new Fixture();
        await using var service = f.Create(); await service.FlushAsync();
        var contributors = DiagnosticContributors.Create(new ExistingProvider(f.Settings, service, f.Cache));
        var result = contributors.Single(item => item.FeatureIds.Contains("loot-market")).Collect("loot-market");
        var json = JsonSerializer.Serialize(result);
        Assert.Equal(true, result.Facts["automaticServerMapped"]);
        Assert.Equal(1, result.Facts["catalogueServers"]);
        Assert.Equal(1, result.Facts["nativeServerKeys"]);
        Assert.Equal(0, result.Facts["ambiguousNativeServerKeys"]);
        Assert.DoesNotContain(Server.GameServerKey!.Value.ToString(), json);
        Assert.DoesNotContain(First, json);
        Assert.DoesNotContain(Server.Name, json);
        Assert.DoesNotContain(Server.ServerId.ToString(), json);
    }

    [Fact]
    public async Task FrequentSourceRefreshesDoNotExtendTheCatalogueLifetimeOrFlashLoadingOnCacheHits()
    {
        var clock = new Clock();
        using var f = new Fixture(clock);
        await using var service = f.Create(); await service.FlushAsync();
        var changes = new List<ExchangeMarketAutoSelectionSnapshot>();
        service.Changed += () => changes.Add(service.Snapshot);
        for (var minute = 0; minute < 10; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await service.FlushAsync();
            Assert.Equal(1, f.Client.ServerCalls);
        }
        Assert.Empty(changes);
        clock.Advance(TimeSpan.FromTicks(1)); await service.FlushAsync();
        Assert.Equal(2, f.Client.ServerCalls);
        Assert.True(service.Snapshot.MappingKnown);
        Assert.Contains(changes, state => state.Loading);
    }

    private static ExchangeMarketProfileSettings Model(bool selected) => new()
        { Region = selected ? Server.Region : null, Faction = selected ? Server.Faction : null, ServerId = selected ? Server.ServerId : null, GameServerKey = selected ? Server.GameServerKey : null };

    private sealed class Fixture : IDisposable
    {
        private readonly byte[]? _before = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        internal SettingsService Settings { get; } = new();
        internal Client Client { get; } = new();
        internal ExchangeMarketCache Cache { get; }
        internal TimeProvider Clock { get; }
        internal ExchangeMarketAutoSelectionAuthority? Authority { get; set; } = new(First, Server.GameServerKey!.Value, 1, 1);
        internal Fixture(TimeProvider? clock = null)
        {
            Clock = clock ?? TimeProvider.System; Cache = new(Clock);
            Settings.Current.OnlineMarketPricesEnabled = true; Settings.Current.ExchangeMarketProfiles = [];
        }
        internal ExchangeMarketAutoSelectionService Create() => new(Settings, () => Authority, Client, Cache, Clock);
        public void Dispose() { if (_before is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _before); }
    }

    private sealed class Client : IExchangeMarketClient
    {
        internal ImmutableArray<ExchangeMarketServer> Servers { get; set; } = [Server];
        internal TaskCompletionSource<ImmutableArray<ExchangeMarketServer>>? Pending { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken LastToken { get; private set; }
        internal int ServerCalls, QuoteCalls;
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken)
        { Interlocked.Increment(ref ServerCalls); LastToken = cancellationToken; Entered.TrySetResult(); return Pending?.Task ?? Task.FromResult(Servers); }
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
        { Interlocked.Increment(ref QuoteCalls); throw new InvalidOperationException("Identity mapping must never request prices."); }
    }

    private sealed class ExistingProvider(params object[] instances) : IServiceProvider
    { public object? GetService(Type type) => instances.FirstOrDefault(type.IsInstanceOfType); }

    private sealed class FarmSource : INetworkFarmSource
    {
        public bool Running { get; set; } = true;
        public OwnCharacterContext? OwnCharacter { get; set; }
        public NetworkDailyMonsterKinaSnapshot DailyMonsterKinaSnapshot => new(null, null, null, 0);
        public event Action? Changed;
        public event Action? OwnCharacterChanged;
        public event Action<NetworkKinaGain>? MonsterKinaGained { add { } remove { } }
        public event Action<NetworkFarmDrop>? FarmDropGained { add { } remove { } }
        internal void Publish() { Changed?.Invoke(); OwnCharacterChanged?.Invoke(); }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero).AddTicks(Interlocked.Read(ref _ticks));
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }
}
