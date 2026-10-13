using System.Globalization;
using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkPetBalanceFallbackTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1135)] // Ashen Dionae
    [InlineData(1163)] // Basilisk
    [InlineData(1143)] // Mutated Ursus
    public void RejectedPetFormatCannotRestoreSpentSoulsFromTheFarmTotal(int petNumber)
    {
        using var f = new Fixture(petNumber);
        var service = f.Load();
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkSnapshot(f.Snapshot(1, 25), Context(2)));
        Assert.True(service.ApplyNetworkSpent([new(petNumber, 25)], Context(3)));
        Assert.Equal(0, service.SoulsInLevel(f.PetId));
        Assert.Equal(30, service.Souls(f.PetId));

        service.MarkNetworkPetSourceStale();
        Assert.Null(service.AvailableSouls(f.PetId));
        Assert.Null(service.ObservedLevel(f.PetId));
        Assert.Equal(1, service.Level(f.PetId));
        Assert.Equal(0, service.SoulsInLevel(f.PetId));
        Assert.Equal(30, service.Souls(f.PetId));
        Assert.True(service.ApplyNetworkGain([new(petNumber, 2)], Context(4)));
        Assert.Equal(2, service.SoulsInLevel(f.PetId));
        Assert.Equal(32, service.Souls(f.PetId));
        Assert.Equal(2, Assert.Single(service.Recent()).Quantity);
        Assert.Equal(ProgressFreshness.Unknown, service.EntrySnapshot(f.PetId).Freshness);

        Assert.True(service.ApplyNetworkAcquisition(new(1001, petNumber, 2), Context(5)));
        Assert.Equal(2, service.Level(f.PetId));
        Assert.Equal(2, service.SoulsInLevel(f.PetId));
        Assert.Null(service.AvailableSouls(f.PetId));
        var reloaded = f.Load();
        Assert.Equal(2, reloaded.SoulsInLevel(f.PetId));
        Assert.Equal(32, reloaded.Souls(f.PetId));
        Assert.Null(reloaded.AvailableSouls(f.PetId));

        Assert.True(service.ApplyNetworkSnapshot(f.Snapshot(2, 2), Context(6)));
        Assert.Equal(2, service.AvailableSouls(f.PetId));
        Assert.Equal(ProgressFreshness.Fresh, service.EntrySnapshot(f.PetId).Freshness);
        Assert.Single(service.Recent());
    }

    [Theory]
    [InlineData(1135)]
    [InlineData(1163)]
    [InlineData(1143)]
    public void UnconfirmedAcquisitionKeepsTheSeparateBalanceUntilSpendingActuallyArrives(int petNumber)
    {
        using var f = new Fixture(petNumber);
        var service = f.Load();
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkSnapshot(f.Snapshot(1, 26), Context(2)));
        Assert.True(service.ApplyNetworkAcquisition(new(1001, petNumber, 2), Context(3)));
        service.MarkNetworkPetSourceStale();
        Assert.Equal(26, service.SoulsInLevel(f.PetId));
        Assert.Equal(31, service.Souls(f.PetId));
        Assert.Null(service.AvailableSouls(f.PetId));
        Assert.True(service.ApplyNetworkSpent([new(petNumber, 25)], Context(4)));
        Assert.Equal(1, service.SoulsInLevel(f.PetId));
        Assert.Equal(31, service.Souls(f.PetId));
        Assert.True(service.ApplyNetworkGain([new(petNumber, 1)], Context(5)));
        Assert.Equal(2, service.SoulsInLevel(f.PetId));
        Assert.Equal(32, service.Souls(f.PetId));
        Assert.Equal(1, Assert.Single(service.Recent()).Quantity);
        Assert.Equal(2, f.Load().SoulsInLevel(f.PetId));
    }

    [Theory]
    [InlineData(1135, 0, 0)]
    [InlineData(1135, 8, 1)]
    [InlineData(1163, 0, 0)]
    [InlineData(1163, 8, 1)]
    public void LegacyLevelAndRestSeedTheSeparateUnconfirmedBalanceBeforeItsFirstSnapshot(int petNumber, int legacySouls, int level)
    {
        using var f = new Fixture(petNumber);
        var profile = new ProgressProfile();
        profile.Pets[f.PetId] = new() { Souls = legacySouls };
        File.WriteAllBytes(f.Path, JsonSerializer.SerializeToUtf8Bytes(profile));
        var service = f.Load();
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkGain([new(petNumber, 2)], Context(2)));
        var rest = legacySouls == 0 ? 2 : 5;
        Assert.Equal(level, service.Level(f.PetId));
        Assert.Equal(rest, service.SoulsInLevel(f.PetId));
        Assert.Null(service.AvailableSouls(f.PetId));
        Assert.Null(service.ObservedLevel(f.PetId));
        Assert.True(service.ApplyNetworkSpent([new(petNumber, 1)], Context(3)));
        Assert.Equal(rest - 1, service.SoulsInLevel(f.PetId));
        Assert.Equal(legacySouls + 2, service.Souls(f.PetId));
        Assert.Equal(rest - 1, f.Load().SoulsInLevel(f.PetId));
    }

    [Theory]
    [InlineData(1135)]
    [InlineData(1163)]
    public void ManualCorrectionKeepsItsOwnDisplayWhileTheUnconfirmedNetworkBalanceStaysSeparate(int petNumber)
    {
        using var f = new Fixture(petNumber);
        var service = f.Load();
        Assert.True(service.BindNetworkContext(Context(1)));
        Assert.True(service.ApplyNetworkSnapshot(f.Snapshot(1, 25), Context(2)));
        Assert.True(service.ApplyNetworkSpent([new(petNumber, 25)], Context(3)));
        service.MarkNetworkPetSourceStale();
        service.SetSouls(f.PetId, 8);
        Assert.True(service.ApplyNetworkGain([new(petNumber, 2)], Context(4)));
        Assert.Equal(10, service.Souls(f.PetId));
        Assert.Equal(1, service.Level(f.PetId));
        Assert.Equal(5, service.SoulsInLevel(f.PetId));
        Assert.True(service.EntrySnapshot(f.PetId).ManualOverride);
        service.ClearManualOverride(f.PetId);
        Assert.Equal(32, service.Souls(f.PetId));
        Assert.Equal(2, service.SoulsInLevel(f.PetId));
        Assert.Null(service.AvailableSouls(f.PetId));
        Assert.False(service.EntrySnapshot(f.PetId).ManualOverride);
    }

    private static NetworkProgressContext Context(long sequence) =>
        new(new(101, 501, 1001, "Synthetic balance fallback"), 1, sequence, At.AddSeconds(sequence));

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-pet-balance-" + Guid.NewGuid().ToString("N"));
        private readonly PetCatalog _catalog;
        private readonly int _petNumber;
        internal string Path => System.IO.Path.Combine(_folder, "progress.json");
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
