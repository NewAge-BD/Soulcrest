using System.Globalization;
using System.Text.Json;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class MerchantServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
    private static readonly PetIdEntry FirstPet = PetIdTable.Find(1002)!;
    private static readonly PetIdEntry SecondPet = PetIdTable.Find(1004)!;
    private static string FirstId => FirstPet.CatalogId!;
    private static string SecondId => SecondPet.CatalogId!;

    [Theory]
    [InlineData(1002, "Cogni")]
    [InlineData(1004, "Fera")]
    [InlineData(1003, "Natura")]
    [InlineData(1013, "Varian")]
    [InlineData(1001, "?")]
    [InlineData(-1, "?")]
    [InlineData(int.MaxValue, "?")]
    public void PetCategoriesUseOnlyTheFourSourcedGenera(int number, string expected) =>
        Assert.Equal(expected, MerchantService.PetCategory(number));

    [Fact]
    public void FarmCountsUseSourceMonsterPlacementsInsteadOfPetClustersAndIgnoreOtherMaps()
    {
        var catalog = FarmCatalog(1002, new() { ["verteron"] = 57, ["altgard"] = 99, ["unknown"] = 700 },
            new() { ["verteron"] = 3 });
        Assert.Equal("Verteron (57)", MerchantService.FarmSpawnText(1002, catalog, []));
        Assert.Equal(3, catalog.Pets[0].Spawns["verteron"]);
        Assert.Equal(57, catalog.Pets[0].Monsters["verteron"]);
    }

    [Fact]
    public void AbyssCountsSumEachManifestMapOnceAndKeepTheExistingRegionOrder()
    {
        var catalog = FarmCatalog(1013, new() { ["altgard"] = 123, ["abyss-reshanta-a"] = 209,
            ["chaotic-middle-reshanta"] = 239, ["chaotic-upper-reshanta"] = 336, ["unknown-abyss"] = 999 });
        MapInfo[] maps = [new("altgard", "Altgard", "Asmodier"),
            new("chaotic-upper-reshanta", "Upper", "Abyss"), new("abyss-reshanta-a", "Lower", "Abyss"),
            new("chaotic-middle-reshanta", "Middle", "Abyss"), new("abyss-reshanta-a", "Lower again", "Abyss"),
            new("unknown-abyss", "Unconfirmed", "")];
        Assert.Equal("Abyss (784) / Altgard (123)", MerchantService.FarmSpawnText(1013, catalog, maps));
        Assert.Equal("Abyss / Altgard", string.Join(" / ", PetFarmRegions.Find(1013)!));
    }

    [Fact]
    public void AManifestDeterminesAbyssMembershipWithoutGuessingFromMapIdsOrNames()
    {
        var catalog = FarmCatalog(1013, new() { ["altgard"] = 123, ["abyss-reshanta-a"] = 999,
            ["explicit-map"] = 7, ["abyss-unconfirmed"] = 900 });
        MapInfo[] maps = [new("explicit-map", "Published map", "Abyss"),
            new("abyss-reshanta-a", "Abyss in the name", "Elyos"), new("abyss-unconfirmed", "Abyss", "")];
        Assert.Equal("Abyss (7) / Altgard (123)", MerchantService.FarmSpawnText(1013, catalog, maps));
        Assert.Equal("Abyss (?) / Altgard (123)", MerchantService.FarmSpawnText(1013, catalog,
            [new("altgard", "Altgard", "Asmodier")]));
    }

    [Fact]
    public void APackageWithoutAManifestUsesOnlyTheThreeEstablishedAbyssIds()
    {
        var catalog = FarmCatalog(1062, new() { ["verteron"] = 97, ["abyss-reshanta-a"] = 69,
            ["chaotic-middle-reshanta"] = 94, ["chaotic-upper-reshanta"] = 0, ["abyss-reshanta-b"] = 999 });
        Assert.Equal("Abyss (163) / Verteron (97)", MerchantService.FarmSpawnText(1062, catalog, []));
    }

    [Fact]
    public void ZeroNegativeAndMissingCountsStayUnknownWithoutDroppingTheirKnownFarmRegions()
    {
        var catalog = FarmCatalog(1045, new() { ["altgard"] = -8, ["verteron"] = 0 },
            new() { ["altgard"] = 99, ["verteron"] = 8 });
        Assert.Equal("Altgard (?) / Verteron (?)", MerchantService.FarmSpawnText(1045, catalog, []));
        Assert.Equal("Verteron (?)", MerchantService.FarmSpawnText(1156, new PetCatalog([]), []));
        Assert.Equal("Verteron (?)", MerchantService.FarmSpawnText(1206, FarmCatalog(1206), []));
        Assert.Equal("Verteron (?)", MerchantService.FarmSpawnText(1002, new PetCatalog([]), []));
    }

    [Fact]
    public void SpecialAndUnknownClassificationsDoNotInventASpawnCountOrUseThePetGenus()
    {
        var catalog = FarmCatalog(1009, new() { ["altgard"] = 100, ["verteron"] = 50 });
        Assert.Equal("Special", MerchantService.FarmSpawnText(1009, catalog, []));
        Assert.Equal("Special", MerchantService.FarmSpawnText(1001, new PetCatalog([]), []));
        Assert.Equal("?", MerchantService.FarmSpawnText(int.MaxValue, catalog, []));
    }

    [Fact]
    public void SourceCountsResolveProvenCatalogAliasesWithoutAddingTheirDuplicateValues()
    {
        var source = PetIdTable.Find(1013)!;
        var primary = new PetDefinition { Id = "synthetic-primary", En = "Primary", Genus = "varian",
            NumericId = "1013", Monsters = new() { ["altgard"] = 12, ["abyss-reshanta-a"] = 8 } };
        var legacy = new PetDefinition { Id = source.CatalogId!, En = "Legacy", Genus = "varian",
            NumericId = "1013", LegacyDuplicate = true, Monsters = new() { ["altgard"] = 999 } };
        var catalog = new PetCatalog([primary, legacy]);
        Assert.Same(primary, catalog.Find(source.CatalogId!));
        Assert.Equal("Abyss (8) / Altgard (12)", MerchantService.FarmSpawnText(1013, catalog, []));
    }

    [Fact]
    public void FarmCountsAreInvariantUnformattedTotalsEvenAboveTheIntegerSumLimit()
    {
        var catalog = FarmCatalog(1013, new() { ["altgard"] = 12345, ["abyss-reshanta-a"] = int.MaxValue,
            ["chaotic-middle-reshanta"] = int.MaxValue });
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("Abyss (4294967294) / Altgard (12345)", MerchantService.FarmSpawnText(1013, catalog, []));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void RowsProjectStableCategoryAndFarmTextsWhileKeepingTheOldRegionTextAndCounter()
    {
        using var f = new Fixture();
        f.Baseline();
        f.Accept(List(Offers()));
        var snapshot = f.Merchant.Snapshot;
        var first = f.Merchant.Rows.ToArray();
        Assert.Equal("Cogni", first[0].PetCategory);
        Assert.Equal("Fera", first[1].PetCategory);
        Assert.Equal("Verteron", first[0].FarmRegions);
        Assert.Equal("Verteron (?)", first[0].FarmSpawns);
        Assert.Equal("3/25", first[0].Counter);
        Assert.Equal(first, f.Merchant.Rows.ToArray());
        Assert.Same(snapshot, f.Merchant.Snapshot);
        Assert.Equal("", new MerchantCounterRow(0, FirstId, "?", false, false).PetCategory);
        Assert.Equal("", new MerchantCounterRow(0, FirstId, "?", false, false).FarmSpawns);
    }

    private static PetCatalog FarmCatalog(int number, Dictionary<string, int>? monsters = null,
        Dictionary<string, int>? spawns = null)
    {
        var source = PetIdTable.Find(number)!;
        return new PetCatalog([new PetDefinition { Id = source.CatalogId!, En = source.En,
            Genus = source.Genus.ToLowerInvariant(), NumericId = number.ToString(CultureInfo.InvariantCulture),
            Monsters = monsters ?? [], Spawns = spawns ?? [] }]);
    }

    [Fact]
    public void MixedTenSlotListSortsAllGoodsBeforeFilteringPetsAndNeverWritesProgress()
    {
        using var f = new Fixture();
        f.Baseline();
        var progressBytes = File.ReadAllBytes(f.ProgressPath);
        var goods = new[] { 90, 10, 80, 20, 70, 30, 60, 40, 50, 100 };
        var offers = Offers().Select((offer, i) => offer with { GoodsId = goods[i] }).ToArray();
        var message = List(offers);
        f.Accept(message);

        var snapshot = Assert.IsType<MerchantSnapshot>(f.Merchant.Snapshot);
        Assert.Equal((byte)77, snapshot.GroupKey);
        Assert.Equal(1, snapshot.SourceGeneration);
        Assert.Equal("synthetic-stream", snapshot.Stream);
        Assert.Equal(501, snapshot.ServerId);
        Assert.Equal(At.AddMilliseconds(100), snapshot.At);
        Assert.Equal(offers, snapshot.Offers);
        Assert.Collection(f.Merchant.Rows,
            row => { Assert.Equal(0, row.SlotIndex); Assert.Equal(SecondId, row.PetId); Assert.Equal("4/75", row.Counter); },
            row => { Assert.Equal(8, row.SlotIndex); Assert.Equal(FirstId, row.PetId); Assert.Equal("3/25", row.Counter); });
        Assert.All(f.Merchant.Rows, row => { Assert.True(row.IsFresh); Assert.False(row.IsMax); });
        Assert.Equal(progressBytes, File.ReadAllBytes(f.ProgressPath));
        Assert.Empty(f.Progress.Recent());
        Assert.False(File.Exists(f.EventsPath));

        Array.Fill(message, (byte)0);
        Assert.Equal(offers, snapshot.Offers);
        var mutable = Assert.IsAssignableFrom<IList<MerchantOffer>>(snapshot.Offers);
        Assert.Throws<NotSupportedException>(() => mutable[0] = offers[0] with { Quantity = 999 });
        var input = Offers();
        var separate = new MerchantSnapshot(77, 1, "synthetic-stream", 501, At, input);
        input[0] = input[0] with { Quantity = 999 };
        Assert.Equal(1, separate.Offers[0].Quantity);
    }

    [Fact]
    public void UnknownItemAndUnknownPetNumberKeepTheirSlotsWithoutCreatingCounterRows()
    {
        using var f = new Fixture(item => item == 20_000 ? FirstPet.Id : item == 20_001 ? int.MaxValue : null);
        f.Baseline();
        var offers = Offers();
        offers[0] = offers[0] with { GoodsId = 999 };
        f.Accept(List(offers));
        Assert.Equal(10, f.Merchant.Snapshot!.Offers.Count);
        var row = Assert.Single(f.Merchant.Rows);
        Assert.Equal(9, row.SlotIndex);
        Assert.Equal(FirstId, row.PetId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(502)]
    public void UnknownOrDifferentMerchantServerUsesTheSameGlobalPetCounters(int? sourceServer)
    {
        using var f = new Fixture();
        f.Baseline();
        f.Progress.SetSouls(FirstId, 105);
        f.Accept(List(Offers()), server: sourceServer);
        Assert.Equal(10, f.Merchant.Snapshot!.Offers.Count);
        Assert.Collection(f.Merchant.Rows,
            row => Assert.Equal("max", row.Counter),
            row => Assert.Equal("4/75", row.Counter));
    }

    [Fact]
    public void TwoUnknownServersNeverAuthorizeLegacyCounters()
    {
        using var f = new Fixture(legacyTotal: 105, legacyServer: null);
        f.Accept(List(Offers()), server: null);
        Assert.Null(f.Progress.ActiveServerId);
        Assert.All(f.Merchant.Rows, row => { Assert.Equal("?", row.Counter); Assert.False(row.IsMax); });
    }

    [Fact]
    public void CountersReactToSnapshotGainSpendAndLevelWithoutAnotherShopList()
    {
        using var f = new Fixture();
        f.Baseline();
        f.Accept(List(Offers()));
        var snapshot = f.Merchant.Snapshot;
        var changed = 0;
        f.Merchant.Changed += () => changed++;
        Assert.True(f.Progress.ApplyNetworkGain([new(FirstPet.Id, 2)], Context(3)));
        Assert.Equal("5/25", f.FirstRow.Counter);
        Assert.True(f.Progress.ApplyNetworkSpent([new(FirstPet.Id, 5)], Context(4)));
        Assert.Equal("0/25", f.FirstRow.Counter);
        Assert.True(f.Progress.ApplyNetworkSnapshot(Pets(2, 6), Context(5)));
        Assert.Equal("6/75", f.FirstRow.Counter);
        Assert.True(f.Progress.ApplyNetworkSnapshot(Pets(3, 0), Context(6)));
        Assert.Equal("max", f.FirstRow.Counter);
        Assert.True(f.FirstRow.IsMax);
        Assert.True(f.FirstRow.IsFresh);
        Assert.Same(snapshot, f.Merchant.Snapshot);
        Assert.Equal(4, changed);
        Assert.Single(f.Progress.Recent());
        Assert.Single(File.ReadAllLines(f.EventsPath));
    }

    [Fact]
    public void LegacyFarmTotalAndSeededDeltaCannotInventObservedBalanceOrMax()
    {
        using var f = new Fixture(legacyTotal: 105);
        Assert.True(f.Progress.BindNetworkContext(Context(1)));
        f.Accept(List(Offers()));
        Assert.Equal("?", f.FirstRow.Counter);
        Assert.False(f.FirstRow.IsMax);
        Assert.True(f.Progress.ApplyNetworkGain([new(FirstPet.Id, 2)], Context(2)));
        Assert.Equal(107, f.Progress.Souls(FirstId));
        Assert.Equal("?", f.FirstRow.Counter);
        Assert.False(f.FirstRow.IsMax);
    }

    [Fact]
    public void ObservedLevelControlsMaxEvenWhenTheSeparateBalanceIsUnknown()
    {
        using var f = new Fixture();
        Assert.True(f.Progress.BindNetworkContext(Context(1)));
        Assert.True(f.Progress.ApplyNetworkAcquisition(new(1001, FirstPet.Id, 3), Context(2)));
        f.Accept(List(Offers()));
        Assert.Equal("max", f.FirstRow.Counter);
        Assert.True(f.FirstRow.IsMax);
        Assert.False(f.FirstRow.IsFresh);
        f.Progress.MarkNetworkPetSourceStale();
        Assert.Equal("?", f.FirstRow.Counter);
        Assert.False(f.FirstRow.IsMax);
    }

    [Fact]
    public void LargeEarnedTotalDoesNotOverrideTheObservedLevelAndManualCounterStaysExplicit()
    {
        using var f = new Fixture();
        Assert.True(f.Progress.BindNetworkContext(Context(1)));
        Assert.True(f.Progress.ApplyNetworkSnapshot(Pets(1, 100), Context(2)));
        f.Accept(List(Offers()));
        Assert.Equal("100/25", f.FirstRow.Counter);
        Assert.False(f.FirstRow.IsMax);
        f.Progress.SetSouls(FirstId, 8);
        Assert.Equal("3/25", f.FirstRow.Counter);
        Assert.False(f.FirstRow.IsFresh);
        Assert.False(f.FirstRow.IsMax);
    }

    [Fact]
    public void ChangingTheCharacterServerKeepsTheGlobalMerchantCounter()
    {
        using var f = new Fixture();
        f.Baseline();
        f.Accept(List(Offers()));
        Assert.True(f.Progress.BindNetworkContext(Context(3, server: 502)));
        Assert.True(f.Progress.ApplyNetworkSnapshot(Pets(2, 17), Context(4, server: 502)));
        Assert.Equal("17/75", f.FirstRow.Counter);
    }

    [Theory]
    [InlineData(-1L, false)]
    [InlineData(0L, true)]
    [InlineData(20_000_000L, true)]
    [InlineData(20_000_001L, false)]
    public void HandshakeRequiresAForwardTimestampWithinTwoSeconds(long ticks, bool accepted)
    {
        using var f = new Fixture();
        f.Source.Send(Status(), at: At);
        f.Source.Send(List(Offers()), at: At.AddTicks(ticks));
        Assert.Equal(accepted, f.Merchant.Snapshot is not null);
    }

    [Fact]
    public void AFullListAloneCannotAppearAndAStatusImmediatelyClearsThePreviousList()
    {
        using var f = new Fixture();
        f.Source.Send(List(Offers()));
        Assert.Null(f.Merchant.Snapshot);
        f.Accept(List(Offers()));
        Assert.NotNull(f.Merchant.Snapshot);
        f.Source.Send(Status(88), at: At.AddSeconds(1));
        Assert.Null(f.Merchant.Snapshot);
        Assert.Empty(f.Merchant.Rows);
        f.Source.Send(List(Offers(), 88), at: At.AddSeconds(1.1));
        Assert.Equal((byte)88, f.Merchant.Snapshot!.GroupKey);
    }

    [Theory]
    [InlineData(501)]
    [InlineData(null)]
    public void IdenticalSoloListsKeepTheConfirmedReferenceWithoutPublishingOrWritingProgress(int? server)
    {
        using var f = new Fixture();
        f.Baseline();
        f.Accept(List(Offers()), server: server);
        var snapshot = Assert.IsType<MerchantSnapshot>(f.Merchant.Snapshot);
        var progressBytes = File.ReadAllBytes(f.ProgressPath);
        var changes = 0;
        f.Merchant.Changed += () => changes++;
        foreach (var milliseconds in new[] { 100, 200, 1500, 5000 })
        {
            f.Source.Send(List(Offers()), server: server, at: At.AddMilliseconds(milliseconds));
            Assert.Same(snapshot, f.Merchant.Snapshot);
        }
        Assert.Equal(At.AddMilliseconds(100), snapshot.At);
        Assert.Equal(0, changes);
        Assert.Equal("3/25", f.FirstRow.Counter);
        Assert.Equal(progressBytes, File.ReadAllBytes(f.ProgressPath));
        Assert.Empty(f.Progress.Recent());
        Assert.False(File.Exists(f.EventsPath));
    }

    [Fact]
    public void AReorderedSoloListKeepsTheReferenceAndGoodsSortedSlotCounters()
    {
        using var f = new Fixture();
        f.Baseline();
        f.Accept(List(Offers()));
        var snapshot = f.Merchant.Snapshot;
        var rows = f.Merchant.Rows.ToArray();
        f.Source.Send(List(Enumerable.Reverse(Offers()).ToArray()), at: At.AddMilliseconds(200));
        Assert.Same(snapshot, f.Merchant.Snapshot);
        Assert.Equal(rows, f.Merchant.Rows.ToArray());
        Assert.Collection(f.Merchant.Rows,
            row => { Assert.Equal(0, row.SlotIndex); Assert.Equal(FirstId, row.PetId); Assert.Equal("3/25", row.Counter); },
            row => { Assert.Equal(1, row.SlotIndex); Assert.Equal(SecondId, row.PetId); Assert.Equal("4/75", row.Counter); });
    }

    [Fact]
    public void AReorderedRepeatedPairKeepsTheReferenceAndGoodsSortedSlotCounters()
    {
        using var f = new Fixture();
        f.Baseline();
        f.Accept(List(Offers()));
        var snapshot = f.Merchant.Snapshot;
        var rows = f.Merchant.Rows.ToArray();
        f.Source.Send(Status(), at: At.AddMilliseconds(200));
        Assert.Null(f.Merchant.Snapshot);
        f.Source.Send(List(Enumerable.Reverse(Offers()).ToArray()), at: At.AddMilliseconds(300));
        Assert.Same(snapshot, f.Merchant.Snapshot);
        Assert.Equal(rows, f.Merchant.Rows.ToArray());
        Assert.Collection(f.Merchant.Rows,
            row => { Assert.Equal(0, row.SlotIndex); Assert.Equal(FirstId, row.PetId); Assert.Equal("3/25", row.Counter); },
            row => { Assert.Equal(1, row.SlotIndex); Assert.Equal(SecondId, row.PetId); Assert.Equal("4/75", row.Counter); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedSnapshotsRejectSoloListsOlderThanTheirLastAcceptedObservation(bool paired)
    {
        using var f = new Fixture();
        f.Accept(List(Offers()));
        var snapshot = f.Merchant.Snapshot;
        if (paired) f.Source.Send(Status(), at: At.AddMilliseconds(200));
        f.Source.Send(List(Offers()), at: At.AddMilliseconds(300));
        Assert.Same(snapshot, f.Merchant.Snapshot);
        f.Source.Send(List(Offers()), at: At.AddMilliseconds(200));
        Assert.Null(f.Merchant.Snapshot);
        f.Source.Send(List(Offers()), at: At.AddMilliseconds(400));
        Assert.Null(f.Merchant.Snapshot);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("stream")]
    [InlineData("generation")]
    [InlineData("server")]
    [InlineData("null-server")]
    [InlineData("goods")]
    [InlineData("item")]
    [InlineData("quantity")]
    [InlineData("original-price")]
    [InlineData("current-price")]
    [InlineData("old-time")]
    [InlineData("malformed-list")]
    [InlineData("malformed-status")]
    [InlineData("invalid-stream")]
    [InlineData("invalid-server")]
    public void SoloListsCannotChangeOrRecoverTheConfirmedPage(string mismatch)
    {
        using var f = new Fixture();
        f.Accept(List(Offers()));
        var offers = Offers();
        offers[0] = mismatch switch
        {
            "goods" => offers[0] with { GoodsId = 999 },
            "item" => offers[0] with { ItemId = 999_999 },
            "quantity" => offers[0] with { Quantity = 2 },
            "original-price" => offers[0] with { OriginalPrice = 100_001 },
            "current-price" => offers[0] with { CurrentPrice = 69_999 },
            _ => offers[0]
        };
        var message = List(offers, mismatch == "group" ? (byte)88 : (byte)77);
        if (mismatch == "malformed-list") message = message[..^1];
        if (mismatch == "malformed-status") message = Status(result: 1);
        f.Source.Send(message,
            stream: mismatch == "stream" ? "another-synthetic-stream" : mismatch == "invalid-stream" ? " " : "synthetic-stream",
            generation: mismatch == "generation" ? 2 : 1,
            server: mismatch == "server" ? 502 : mismatch == "null-server" ? null : mismatch == "invalid-server" ? 0 : 501,
            at: At.AddMilliseconds(mismatch == "old-time" ? 99 : 200));
        Assert.Null(f.Merchant.Snapshot);
        Assert.Empty(f.Merchant.Rows);
        f.Source.Send(List(Offers()), at: At.AddMilliseconds(300));
        Assert.Null(f.Merchant.Snapshot);
    }

    [Fact]
    public void AnIdenticalRepeatedPairReusesOnlyTheReferenceHeldByItsMatchingPendingStatus()
    {
        using var f = new Fixture();
        f.Accept(List(Offers()));
        var snapshot = f.Merchant.Snapshot;
        var changes = 0;
        f.Merchant.Changed += () => changes++;
        f.Source.Send(Status(), at: At.AddMilliseconds(200));
        Assert.Null(f.Merchant.Snapshot);
        Assert.Empty(f.Merchant.Rows);
        f.Source.Send(Status(), at: At.AddMilliseconds(300));
        Assert.Null(f.Merchant.Snapshot);
        f.Source.Send(List(Offers()), at: At.AddMilliseconds(400));
        Assert.Same(snapshot, f.Merchant.Snapshot);
        Assert.Equal(3, changes);
        f.Source.Send(List(Offers()), at: At.AddMilliseconds(500));
        Assert.Same(snapshot, f.Merchant.Snapshot);
        Assert.Equal(3, changes);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("stream")]
    [InlineData("generation")]
    [InlineData("server")]
    [InlineData("null-server")]
    [InlineData("offers")]
    public void AValidatedChangedPairCreatesANewReference(string change)
    {
        using var f = new Fixture();
        f.Accept(List(Offers()));
        var snapshot = f.Merchant.Snapshot;
        var group = change == "group" ? (byte)88 : (byte)77;
        var stream = change == "stream" ? "another-synthetic-stream" : "synthetic-stream";
        var generation = change == "generation" ? 2 : 1;
        int? server = change == "server" ? 502 : change == "null-server" ? null : 501;
        var offers = Offers();
        if (change == "offers") offers[0] = offers[0] with { Quantity = 2 };
        f.Source.Send(Status(group), stream: stream, generation: generation, server: server, at: At.AddMilliseconds(200));
        Assert.Null(f.Merchant.Snapshot);
        f.Source.Send(List(offers, group), stream: stream, generation: generation, server: server, at: At.AddMilliseconds(300));
        var changed = Assert.IsType<MerchantSnapshot>(f.Merchant.Snapshot);
        Assert.NotSame(snapshot, changed);
        Assert.Equal(group, changed.GroupKey);
        Assert.Equal(stream, changed.Stream);
        Assert.Equal(generation, changed.SourceGeneration);
        Assert.Equal(server, changed.ServerId);
        Assert.Equal(offers, changed.Offers);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(20_000_001L)]
    public void AReusableReferenceDoesNotRelaxTheHandshakeTimeWindow(long ticks)
    {
        using var f = new Fixture();
        f.Accept(List(Offers()));
        f.Source.Send(Status(), at: At.AddMilliseconds(200));
        f.Source.Send(List(Offers()), at: At.AddMilliseconds(200).AddTicks(ticks));
        Assert.Null(f.Merchant.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AResetDiscardsReusableReferencesAndSoloListsCannotReviveThem(bool pending)
    {
        using var f = new Fixture();
        f.Accept(List(Offers()));
        var snapshot = f.Merchant.Snapshot;
        if (pending) f.Source.Send(Status(), at: At.AddMilliseconds(200));
        f.Source.Reset();
        f.Source.Send(List(Offers()), generation: 2, at: At.AddMilliseconds(300));
        Assert.Null(f.Merchant.Snapshot);
        f.Source.Send(List(Offers()), generation: 1, at: At.AddMilliseconds(400));
        Assert.Null(f.Merchant.Snapshot);
        f.Source.Send(Status(), generation: 2, at: At.AddMilliseconds(500));
        f.Source.Send(List(Offers()), generation: 2, at: At.AddMilliseconds(600));
        Assert.NotNull(f.Merchant.Snapshot);
        Assert.NotSame(snapshot, f.Merchant.Snapshot);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("stream")]
    [InlineData("generation")]
    [InlineData("server")]
    [InlineData("null-server")]
    public void AListMustMatchEveryPendingStatusContextField(string mismatch)
    {
        using var f = new Fixture();
        f.Source.Send(Status());
        f.Source.Send(List(Offers(), mismatch == "group" ? (byte)88 : (byte)77),
            stream: mismatch == "stream" ? "another-synthetic-stream" : "synthetic-stream",
            generation: mismatch == "generation" ? 2 : 1,
            server: mismatch == "server" ? 502 : mismatch == "null-server" ? null : 501,
            at: At.AddMilliseconds(100));
        Assert.Null(f.Merchant.Snapshot);
        f.Source.Send(List(Offers()), at: At.AddMilliseconds(200));
        Assert.NotNull(f.Merchant.Snapshot); // A mismatched list must not consume the valid pending status.
    }

    [Fact]
    public void AnOlderGroupCannotConsumeTheNewerPendingStatus()
    {
        using var f = new Fixture();
        f.Source.Send(Status(77));
        f.Source.Send(Status(88), at: At.AddMilliseconds(100));
        f.Source.Send(List(Offers(), 77), at: At.AddMilliseconds(200));
        Assert.Null(f.Merchant.Snapshot);
        f.Source.Send(List(Offers(), 88), at: At.AddMilliseconds(300));
        Assert.Equal((byte)88, f.Merchant.Snapshot!.GroupKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownAndKnownServerMarkersCannotBeJoinedInEitherDirection(bool statusUnbound)
    {
        using var f = new Fixture();
        f.Source.Send(Status(), server: statusUnbound ? null : 501);
        f.Source.Send(List(Offers()), server: statusUnbound ? 501 : null, at: At.AddMilliseconds(100));
        Assert.Null(f.Merchant.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedShopMessagesClearCurrentButPreserveAnIndependentPendingStatus(bool badStatus)
    {
        using var f = new Fixture();
        f.Accept(List(Offers()));
        f.Source.Send(badStatus ? Status(result: 1) : List(Offers())[..^1]);
        Assert.Null(f.Merchant.Snapshot);
        f.Source.Send(Status(88), at: At.AddMilliseconds(200));
        f.Source.Send(badStatus ? Status(result: 1) : List(Offers())[..^1], at: At.AddMilliseconds(300));
        f.Source.Send(List(Offers(), 88), at: At.AddMilliseconds(400));
        Assert.Equal((byte)88, f.Merchant.Snapshot!.GroupKey);
    }

    [Fact]
    public void SessionResetClearsPendingAndRejectsMessagesFromThePreviousGeneration()
    {
        using var f = new Fixture();
        f.Source.Send(Status());
        f.Source.Reset();
        f.Source.Send(List(Offers()));
        f.Source.Send(Status());
        f.Source.Send(List(Offers()));
        Assert.Null(f.Merchant.Snapshot);
        f.Accept(List(Offers()), generation: 2);
        Assert.Equal(2, f.Merchant.Snapshot!.SourceGeneration);
        f.Source.Reset();
        Assert.Null(f.Merchant.Snapshot);
        Assert.Empty(f.Merchant.Rows);
    }

    [Fact]
    public void ResetBeforeTheFirstMarkerRejectsTheOldInitialGeneration()
    {
        using var f = new Fixture();
        f.Source.Reset();
        f.Accept(List(Offers()), generation: 0);
        Assert.Null(f.Merchant.Snapshot);
        f.Accept(List(Offers()), generation: 1);
        Assert.Equal(1, f.Merchant.Snapshot!.SourceGeneration);
    }

    [Fact]
    public void EveryConsecutiveResetAdvancesTheFenceEvenWithoutAnInterveningMarker()
    {
        using var f = new Fixture();
        f.Accept(List(Offers()), generation: 1);
        f.Source.Reset();
        f.Source.Reset();
        f.Accept(List(Offers()), generation: 2);
        Assert.Null(f.Merchant.Snapshot);
        f.Accept(List(Offers()), generation: 3);
        Assert.Equal(3, f.Merchant.Snapshot!.SourceGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASubscriberCanResetOrDisposeDuringPublicationWithoutRevivingTheSnapshot(bool dispose)
    {
        using var f = new Fixture();
        var acted = false;
        f.Merchant.Changed += () =>
        {
            if (acted || f.Merchant.Snapshot is null) return;
            acted = true;
            if (dispose) f.Merchant.Dispose(); else f.Source.Reset();
        };
        f.Accept(List(Offers()));
        Assert.True(acted);
        Assert.Null(f.Merchant.Snapshot);
        Assert.Empty(f.Merchant.Rows);
    }

    [Fact]
    public void AResetDuringRowProjectionCannotReturnRowsFromTheDetachedSnapshot()
    {
        Source? source = null;
        var reset = false;
        using var f = new Fixture(item =>
        {
            if (!reset) { reset = true; source!.Reset(); }
            return item == 20_000 ? FirstPet.Id : null;
        });
        source = f.Source;
        f.Baseline();
        f.Accept(List(Offers()));
        Assert.Empty(f.Merchant.Rows);
        Assert.True(reset);
        Assert.Null(f.Merchant.Snapshot);
    }

    [Fact]
    public void DisposeUnsubscribesBothSourceEventsAndNoLongerRaisesProgressChanges()
    {
        using var f = new Fixture();
        f.Baseline();
        f.Accept(List(Offers()));
        var changes = 0;
        f.Merchant.Changed += () => changes++;
        Assert.Equal(1, f.Source.MessageSubscribers);
        Assert.Equal(1, f.Source.ResetSubscribers);
        f.Merchant.Dispose();
        f.Merchant.Dispose();
        Assert.Equal(0, f.Source.MessageSubscribers);
        Assert.Equal(0, f.Source.ResetSubscribers);
        f.Progress.SetSouls(FirstId, 8);
        f.Source.Reset();
        f.Accept(List(Offers()), generation: 2);
        Assert.Equal(0, changes);
        Assert.Null(f.Merchant.Snapshot);
        Assert.Empty(f.Merchant.Rows);
    }

    private static MerchantOffer[] Offers() => Enumerable.Range(0, 10)
        .Select(i => new MerchantOffer(100 + i, 20_000 + i, 1, 100_000, 70_000)).ToArray();

    private static byte[] List(IReadOnlyList<MerchantOffer> offers, byte group = 77)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x5691); writer.Write(group); writer.Write(checked((byte)offers.Count));
        for (var i = 0; i < offers.Count; i++)
        {
            var offer = offers[i];
            writer.Write(offer.GoodsId); writer.Write(offer.ItemId); writer.Write(offer.Quantity);
            writer.Write((ushort)0xA17F); writer.Write(offer.OriginalPrice); writer.Write(offer.CurrentPrice);
            if (i % 4 == 0) writer.Write((byte)0xFF); // Opaque packed booleans do not suppress a row.
        }
        writer.Write(new byte[12]);
        return stream.ToArray();
    }

    private static byte[] Status(byte group = 77, ushort result = 0) =>
        [(byte)0x93, (byte)0x56, (byte)result, (byte)(result >> 8), group];

    private static NetworkProgressContext Context(long sequence, int server = 501) =>
        new(new(101, server, 1001, "Synthetic Merchant Profile"), 1, sequence, At.AddSeconds(sequence));

    private static PetProgressSnapshot Pets(int firstLevel, int firstBalance) => new(
        new Dictionary<int, int> { [FirstPet.Id] = firstLevel, [SecondPet.Id] = 2 },
        new Dictionary<int, int> { [FirstPet.Id] = firstBalance, [SecondPet.Id] = 4 });

    private sealed class Source : INetworkMerchantSource
    {
        public event Action<MerchantNetworkMessage>? MerchantMessageReceived;
        public event Action? MerchantSessionReset;
        internal int MessageSubscribers => MerchantMessageReceived?.GetInvocationList().Length ?? 0;
        internal int ResetSubscribers => MerchantSessionReset?.GetInvocationList().Length ?? 0;
        internal void Send(byte[] message, string stream = "synthetic-stream", long generation = 1,
            int? server = 501, DateTimeOffset? at = null) =>
            MerchantMessageReceived?.Invoke(new(stream, generation, server, at ?? At, message));
        internal void Reset() => MerchantSessionReset?.Invoke();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-merchant-" + Guid.NewGuid().ToString("N"));
        internal Source Source { get; } = new();
        internal ProgressService Progress { get; }
        internal MerchantService Merchant { get; }
        internal string ProgressPath => Path.Combine(_folder, "progress.json");
        internal string EventsPath => Path.Combine(_folder, "events.jsonl");
        internal MerchantCounterRow FirstRow => Assert.Single(Merchant.Rows, row => row.PetId == FirstId);

        internal Fixture(Func<int, int?>? lookup = null, int? legacyTotal = null, int? legacyServer = 501)
        {
            Directory.CreateDirectory(_folder);
            if (legacyTotal is { } total)
            {
                var profile = new ProgressProfile { SchemaVersion = 2, ServerId = legacyServer, ActiveServerId = legacyServer };
                profile.Pets[FirstId] = new() { Souls = total };
                File.WriteAllBytes(ProgressPath, JsonSerializer.SerializeToUtf8Bytes(profile));
            }
            var catalog = new PetCatalog(new[] { FirstPet, SecondPet }.Select(pet => new PetDefinition
            {
                Id = pet.CatalogId!, En = pet.En, Genus = pet.Genus.ToLowerInvariant(),
                NumericId = pet.Id.ToString(CultureInfo.InvariantCulture)
            }));
            Progress = new(ProgressPath, catalog);
            Merchant = new(Source, Progress, lookup ?? (item => item == 20_000 ? FirstPet.Id : item == 20_001 ? SecondPet.Id : null));
        }

        internal void Baseline()
        {
            Assert.True(Progress.BindNetworkContext(Context(1)));
            Assert.True(Progress.ApplyNetworkSnapshot(Pets(1, 3), Context(2)));
        }

        internal void Accept(byte[] message, int? server = 501, long generation = 1)
        {
            Source.Send(Status(), generation: generation, server: server, at: At);
            Source.Send(message, generation: generation, server: server, at: At.AddMilliseconds(100));
        }

        public void Dispose()
        {
            Merchant.Dispose();
            Directory.Delete(_folder, recursive: true);
        }
    }
}
