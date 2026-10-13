using System.Globalization;
using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkAutomaticPetLevelTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExplicitUpgradeInTheObservedOrderChangesTheLevelWithoutLosingSoulsOrInventingFarmEvents()
    {
        // Synthetic reconstruction of the confirmed public target/counts; no captured identity or bytes.
        using var f = new Fixture(1062);
        var service = f.Load();
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkSnapshot(f.Snapshot(1, 21), Context(2)));
        var sequence = 2L;
        for (var i = 0; i < 4; i++) Assert.True(service.ApplyNetworkGain([new(1062, 1)], Context(++sequence)));
        Assert.Equal(1, service.Level(f.PetId));
        Assert.Equal("25/25", service.SoulCount(f.PetId));
        Assert.Equal(30, service.Souls(f.PetId));

        Assert.True(service.ApplyNetworkUpgrade(new(0, 1062, 2), Context(++sequence)));
        Assert.Equal(2, service.ObservedLevel(f.PetId));
        Assert.Equal(25, service.AvailableSouls(f.PetId));
        Assert.Equal(30, service.Souls(f.PetId));
        Assert.Equal(4, service.Recent().Count);
        Assert.True(service.ApplyNetworkSpent([new(1062, 25)], Context(++sequence)));
        Assert.Equal(0, service.AvailableSouls(f.PetId));
        Assert.Equal(2, service.Level(f.PetId));
        for (var i = 0; i < 2; i++) Assert.True(service.ApplyNetworkGain([new(1062, 1)], Context(++sequence)));

        Assert.Equal("2/75", service.SoulCount(f.PetId));
        Assert.Equal(32, service.Souls(f.PetId));
        Assert.Equal(6, service.Recent().Count);
        Assert.Equal(6, service.Recent().Sum(row => row.Quantity));
        Assert.Equal(6, File.ReadAllLines(f.EventsPath).Length);
        Assert.Equal(2, f.Load().ObservedLevel(f.PetId));
        Assert.Equal(2, f.Load().AvailableSouls(f.PetId));
    }

    [Fact]
    public void CapturedMaxUpgradeCountsThirtyOnePickupsAsThirtyTwoSoulsWithoutAddingACompletionDrop()
    {
        using var f = new Fixture(1029);
        var service = f.Load();
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkSnapshot(f.Snapshot(2, 43), Context(2)));
        var sequence = 2L;
        for (var i = 0; i < 31; i++)
            Assert.True(service.ApplyNetworkGain([new(1029, i == 30 ? 2 : 1)], Context(++sequence)));
        Assert.Equal("75/75", service.SoulCount(f.PetId));
        Assert.True(service.ApplyNetworkUpgrade(new(0, 1029, 3), Context(++sequence)));
        Assert.True(service.ApplyNetworkSpent([new(1029, 75)], Context(++sequence)));
        Assert.Equal(3, service.ObservedLevel(f.PetId));
        Assert.Equal(0, service.AvailableSouls(f.PetId));
        Assert.True(service.IsMax(f.PetId));
        Assert.Equal("max", service.SoulCount(f.PetId));
        Assert.Equal(105, service.Souls(f.PetId));
        Assert.Equal(31, service.Recent().Count);
        Assert.Equal(32, service.Recent().Sum(row => row.Quantity));
        Assert.Equal(31, File.ReadAllLines(f.EventsPath).Length);
        Assert.True(f.Load().IsMax(f.PetId));
        Assert.Equal(0, f.Load().AvailableSouls(f.PetId));
    }

    [Theory]
    [InlineData(1135, 1, true)]
    [InlineData(1135, 1, false)]
    [InlineData(1135, 2, true)]
    [InlineData(1135, 2, false)]
    [InlineData(1135, 3, true)]
    [InlineData(1135, 3, false)]
    [InlineData(1163, 1, true)]
    [InlineData(1163, 1, false)]
    [InlineData(1163, 2, true)]
    [InlineData(1163, 2, false)]
    [InlineData(1163, 3, true)]
    [InlineData(1163, 3, false)]
    public void UpgradeAndSpendingOrdersKeepTheCarriedBalanceAndDoNotCountAsPickups(int petNumber, int level, bool upgradeFirst)
    {
        using var f = new Fixture(petNumber);
        var service = f.Load();
        var cost = new[] { 5, 25, 75 }[level - 1];
        var total = new[] { 5, 30, 105 }[level - 1] + 2;
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkSnapshot(f.Snapshot(level - 1, cost + 2), Context(2)));
        if (upgradeFirst)
        {
            Assert.True(service.ApplyNetworkUpgrade(new(0, petNumber, level), Context(3)));
            Assert.Equal(cost + 2, service.AvailableSouls(f.PetId));
            Assert.True(service.ApplyNetworkSpent([new(petNumber, cost)], Context(4)));
        }
        else
        {
            Assert.True(service.ApplyNetworkSpent([new(petNumber, cost)], Context(3)));
            Assert.Equal(level - 1, service.Level(f.PetId)); // spending alone never supplies the level
            Assert.True(service.ApplyNetworkUpgrade(new(0, petNumber, level), Context(4)));
        }
        Assert.Equal(level, service.Level(f.PetId));
        Assert.Equal(2, service.SoulsInLevel(f.PetId));
        Assert.Equal(2, service.AvailableSouls(f.PetId));
        Assert.Equal(total, service.Souls(f.PetId));
        Assert.Empty(service.Recent());
        Assert.False(File.Exists(f.EventsPath));
        Assert.Equal(total, f.Load().Souls(f.PetId));
        Assert.Equal(level, f.Load().ObservedLevel(f.PetId));
    }

    [Fact]
    public void FailedInvalidForeignOldAndLowerResultsCannotChangeAnObservedPet()
    {
        using var f = new Fixture(1143);
        var service = f.Load();
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkSnapshot(f.Snapshot(2, 2), Context(2)));
        var before = File.ReadAllBytes(f.Path);
        var changes = 0;
        service.Changed += () => changes++;
        foreach (var result in new PetUpgradeResult[] { new(1, 0, 0), new(1, 1143, 3), new(0, 0, 2),
            new(0, 1143, 0), new(0, 1143, 4), new(0, 1143, 1) })
            Assert.False(service.ApplyNetworkUpgrade(result, Context(3)));
        foreach (var context in new[] { Context(2), Context(1), Context(3, character: 202),
            Context(3, server: 502), Context(3, generation: 2) })
            Assert.False(service.ApplyNetworkUpgrade(new(0, 1143, 3), context));
        Assert.Equal(before, File.ReadAllBytes(f.Path));
        Assert.Equal(0, changes);
        Assert.Equal(2, service.ObservedLevel(f.PetId));
        Assert.Equal(2, service.AvailableSouls(f.PetId));
        Assert.Empty(service.Recent());

        Assert.True(service.ApplyNetworkUpgrade(new(0, 1143, 2), Context(3)));
        Assert.False(service.ApplyNetworkUpgrade(new(0, 1143, 2), Context(3)));
        before = File.ReadAllBytes(f.Path);
        Assert.False(service.ApplyNetworkAcquisition(new(1001, 1143, 1), Context(4)));
        Assert.False(service.ApplyNetworkUpgrade(new(0, 1143, 1), Context(4)));
        Assert.Equal(before, File.ReadAllBytes(f.Path));
        Assert.True(service.ApplyNetworkAcquisition(new(1001, 1143, 2), Context(4)));
        Assert.Equal(2, service.ObservedLevel(f.PetId));
        Assert.Equal(32, service.Souls(f.PetId));
        Assert.Empty(service.Recent());
    }

    [Fact]
    public void LegacyUnobservedLevelSeedCanBeReplacedWhileManualCorrectionsRemainProtected()
    {
        using var f = new Fixture(1143);
        var profile = new ProgressProfile();
        profile.Pets[f.PetId] = new() { Souls = 105 };
        File.WriteAllBytes(f.Path, JsonSerializer.SerializeToUtf8Bytes(profile));
        var service = f.Load();
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkUpgrade(new(0, 1143, 1), Context(2)));
        Assert.Equal(1, service.ObservedLevel(f.PetId));
        Assert.Equal(105, service.Souls(f.PetId));
        Assert.Null(service.AvailableSouls(f.PetId));
        Assert.Null(service.NetworkBaselineAt);
        service.SetSouls(f.PetId, 8);
        Assert.True(service.ApplyNetworkUpgrade(new(0, 1143, 2), Context(3)));
        Assert.Equal(8, service.Souls(f.PetId));
        Assert.Equal(1, service.Level(f.PetId));
        Assert.True(service.EntrySnapshot(f.PetId).ManualOverride);
        service.ClearManualOverride(f.PetId);
        Assert.Equal(2, service.ObservedLevel(f.PetId));
        Assert.Equal(105, service.Souls(f.PetId));
        Assert.Empty(service.Recent());
        Assert.False(File.Exists(f.EventsPath));
    }

    [Fact]
    public void UpgradeRemainsGlobalAndAnAuthoritativeSnapshotCanCorrectAnOlderObservedLevel()
    {
        using var f = new Fixture(1143);
        var service = f.Load();
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkSnapshot(f.Snapshot(1, 26), Context(2)));
        Assert.True(service.ApplyNetworkUpgrade(new(0, 1143, 2), Context(3)));
        Assert.True(service.ApplyNetworkSpent([new(1143, 25)], Context(4)));
        Assert.True(service.BindNetworkContext(Context(5, character: 202)));
        Assert.Equal(2, service.ObservedLevel(f.PetId));
        Assert.Equal(1, service.AvailableSouls(f.PetId));
        Assert.False(service.ApplyNetworkUpgrade(new(0, 1143, 1), Context(6, character: 202)));
        Assert.True(service.BindNetworkContext(Context(7, character: 303, server: 502)));
        Assert.Equal(31, service.Souls(f.PetId));
        Assert.False(service.ApplyNetworkUpgrade(new(0, 1143, 1), Context(8, character: 303, server: 502)));
        Assert.Equal(2, service.ObservedLevel(f.PetId));
        Assert.Equal(31, service.Souls(f.PetId));
        Assert.True(service.BindNetworkContext(Context(9, character: 404)));
        Assert.Equal(2, service.ObservedLevel(f.PetId));
        Assert.Equal(31, service.Souls(f.PetId));
        Assert.True(service.ApplyNetworkSnapshot(f.Snapshot(1, 3), Context(10, character: 404)));
        Assert.Equal(1, service.ObservedLevel(f.PetId));
        Assert.Equal(3, service.AvailableSouls(f.PetId));
        Assert.Equal(8, service.Souls(f.PetId));
        Assert.Empty(service.Recent());
    }

    private static NetworkProgressContext Context(long sequence, long character = 101, int server = 501, long generation = 1) =>
        new(new(character, server, 1001, "Synthetic explicit upgrade"), generation, sequence, At.AddSeconds(sequence));

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-explicit-upgrade-" + Guid.NewGuid().ToString("N"));
        private readonly PetCatalog _catalog;
        private readonly int _petNumber;
        internal string Path => System.IO.Path.Combine(_folder, "progress.json");
        internal string EventsPath => System.IO.Path.Combine(_folder, "events.jsonl");
        internal string PetId { get; }
        internal Fixture(int petNumber)
        {
            Directory.CreateDirectory(_folder);
            _petNumber = petNumber;
            var source = Assert.IsType<PetIdEntry>(PetIdTable.Find(petNumber));
            PetId = Assert.IsType<string>(source.CatalogId);
            _catalog = new PetCatalog([new PetDefinition { Id = PetId, En = source.En,
                Genus = source.Genus.ToLowerInvariant(), NumericId = petNumber.ToString(CultureInfo.InvariantCulture) }]);
        }
        internal PetProgressSnapshot Snapshot(int level, int balance) => new(
            new Dictionary<int, int> { [_petNumber] = level }, new Dictionary<int, int> { [_petNumber] = balance });
        internal ProgressService Load() => new(Path, _catalog);
        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }
}
