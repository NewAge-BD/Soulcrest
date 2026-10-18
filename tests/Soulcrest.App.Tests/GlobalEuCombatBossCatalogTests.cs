using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GlobalEuCombatBossCatalogTests
{
    [Fact]
    public void EmbeddedFactsKeepExactMapNpcNamesAndSeparateUnconfirmedBerk()
    {
        var facts = CombatEncounterCatalog.LoadEmbedded();
        Assert.Equal("global-eu", facts.Region);
        var auldor = facts.FindDungeon(600012, 600012)!;
        Assert.Equal("Urugugu Canyon", auldor.Names.En); // Preserve the source label; do not invent an alias.
        Assert.Equal("Uruguguschlucht", auldor.Names.De);
        Assert.Equal("Hüterhauptmann Raur", Assert.Single(auldor.Npcs, npc => npc.NpcDataId == 2300206).Names.De);
        Assert.Equal("Heiliger Auldor", Assert.Single(auldor.Npcs, npc => npc.NpcDataId == 2300218).Names.De);
        var krao = facts.FindDungeon(600001, 600001)!;
        Assert.Equal("Kraohöhle", krao.Names.De);
        var berk = Assert.Single(krao.Npcs, npc => npc.NpcDataId == 2300171);
        Assert.Equal("Ultimate Berk", berk.Names.En);
        Assert.Equal(CombatCatalogNpcRole.Unknown, berk.Role);
        Assert.Equal(CombatCatalogEndProof.None, berk.EndProof);
    }

    [Fact]
    public void RuntimeRulesNeverNormalizeVariantsOrGrantAnAmountScope()
    {
        var facts = CombatEncounterCatalog.LoadEmbedded();
        var catalog = GlobalEuCombatBossCatalog.FromFacts(facts);
        foreach (var id in new[] { 2300206, 2300207, 2300218 })
        {
            var rule = catalog.Find(600012, 600012, id)!;
            Assert.NotNull(rule);
            Assert.Null(rule.AmountEvidenceScope);
            Assert.Equal(facts.CatalogVersion, rule.Encounter.MeasurementProfile.CatalogVersion);
            Assert.Equal(CombatCapabilities.None, rule.Encounter.Capabilities);
        }
        Assert.NotNull(catalog.Find(600001, 600001, 2300104));
        Assert.Null(catalog.Find(600001, 600001, 2300171));
        Assert.Null(catalog.Find(600011, 600011, 2310218));
        Assert.Null(catalog.Find(600012, 600012, 2310218));
        Assert.Null(catalog.Find(600002, 600002, 2310104));
        Assert.Null(catalog.Find(600001, 600012, 2300104));
    }

    [Fact]
    public void AReviewedTargetWithoutAmountSemanticsCreatesNeitherPullNorEmptyHistory()
    {
        var controller = new CombatRunController();
        var bound = new List<int>();
        var adapter = new CombatRunAdapter(controller, GlobalEuCombatBossCatalog.Load(),
            (_, entities) => { bound.Clear(); bound.AddRange(entities); return true; });
        var context = new CombatSourceContext("synthetic-stream", 1);
        var world = new OwnWorldObservation(new(1, 1, 101, "Player One"), 123456, 600012);
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        long sequence = 0;
        void Send(CombatWireObservation value) => adapter.Observe(new(++sequence, sequence, at, context, 1, value), world);
        Send(new CombatWireParty(new(1, 1, 5, [new(0, 101, 1, 1, 1, "Player One", 1, 1000, 100, 100, true, true)], 0)));
        Send(new CombatWireDungeonState(new(600012, DungeonRunState.Process, 0, 3600000)));
        Send(new CombatWireNpcAppearance(new(900, 2300218, 1000, 1000, 0, null, null, null, null, null, 0, 0)));
        Send(new CombatWireHit(new(900, 101, 1, 1, 100, false, null, null, 0)));
        Assert.Equal([900], bound);
        Assert.Equal(1, adapter.Snapshot.UnknownAmounts);
        Assert.Null(controller.ActiveAttemptId);
        Assert.Null(controller.Snapshot.CurrentRun);
        Assert.Empty(controller.DrainCheckpoints());
    }
}
