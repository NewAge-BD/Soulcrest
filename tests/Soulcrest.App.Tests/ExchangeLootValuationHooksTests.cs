using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Fact]
    public async Task MarketHookIsOneCommittedFactForMultiItemHarvestAndNeverReplayOrFailedWrite()
    {
        await using var f = new Fixture(); await f.Ready();
        var committed = new List<CommittedLootFact>();
        f.Service.LootFactCommitted += row =>
        {
            var stored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
            Assert.Contains(stored.TimelineEvents, fact => fact.Id == row.Fact.Id);
            committed.Add(row);
        };
        f.Source.Harvest(1, new NetworkGatheringItem(610530001, 2), new NetworkGatheringItem(610530005, 1));
        await f.Ready();
        Assert.Equal(2, Assert.Single(committed).Fact.Loot.Length);
        Assert.Equal(Assert.Single(f.Service.Snapshot.Sessions).Id, committed[0].CanonicalSessionId);
        await f.Ready(); Assert.Single(committed);
        f.FailAppend = true; f.Source.Drop(610530001, 1, 2); await f.Ready(); Assert.Single(committed);
    }

    [Fact]
    public async Task MarketDeletionHookRunsAfterJournalDeletionBeforeCompletionAndOnlyOnce()
    {
        await using var f = new Fixture(); await f.Ready(); f.Source.Drop(610530001, 1, 1); await f.Ready();
        var id = Assert.Single(f.Service.Snapshot.Sessions).Id; var deleted = new List<string>();
        f.Service.SessionDeleted += sessionId =>
        { Assert.Empty(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions); deleted.Add(sessionId); };
        await f.Service.DeleteSessionAsync(id); Assert.Equal(new[] { id }, deleted);
        await f.Service.DeleteSessionAsync(id); Assert.Single(deleted);
    }

    [Fact]
    public async Task MarketAssumptionsAreCapturedBeforeSessionStartedCanChangeSettings()
    {
        await using var f = new Fixture(); await f.Ready();
        const string profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var original = new ExchangeMarketProfileSettings { Region = "eu", Faction = "elyos", ServerId = 7, SalesTaxPercent = 10, RegistrationFeePercent = 2 };
        f.Settings.Current.ExchangeMarketProfiles[profile] = original;
        f.Service.SessionStarted += _ => f.Settings.Current.ExchangeMarketProfiles[profile] = original with { ServerId = 8, SalesTaxPercent = 20 };
        CommittedLootFact? observed = null; f.Service.LootFactCommitted += fact => observed = fact;
        f.Source.Drop(610530001, 1, 1); await f.Ready();
        Assert.NotNull(observed); Assert.Equal(original, observed.MarketProfile);
        Assert.Equal(8, f.Settings.Current.ExchangeMarketProfiles[profile].ServerId);
    }
}
