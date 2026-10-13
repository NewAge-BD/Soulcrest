using System.Globalization;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkPetLevelTransitionTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> LevelTransitions()
    {
        foreach (var (pet, gain) in new[] { (1135, 1), (1163, 2) }) // Ashen Dionae / Basilisk
        foreach (var level in new[] { 1, 2, 3 })
        foreach (var order in new[]
        {
            "gain,acquire,spend", "gain,spend,acquire", "acquire,gain,spend",
            "acquire,spend,gain", "spend,gain,acquire", "spend,acquire,gain",
        })
            yield return [pet, gain, level, order];
    }

    [Theory]
    [MemberData(nameof(LevelTransitions))]
    public void InterleavedSoulGainsSurviveEveryLevelUpOrderAndRemainGlobal(int petNumber, int gain, int level, string order)
    {
        using var f = new Fixture();
        var source = Assert.IsType<PetIdEntry>(PetIdTable.Find(petNumber));
        var petId = Assert.IsType<string>(source.CatalogId);
        var cost = new[] { 5, 25, 75 }[level - 1];
        var acquiredBase = new[] { 5, 30, 105 }[level - 1];
        var expectedBalance = 1 + gain;
        var expectedTotal = acquiredBase + expectedBalance;
        Assert.True(f.Progress.BindNetworkContext(Context(1)));
        Assert.True(f.Progress.ApplyNetworkSnapshot(new(
            new Dictionary<int, int> { [petNumber] = level - 1 },
            new Dictionary<int, int> { [petNumber] = cost + 1 }), Context(2)));
        f.Progress.ToggleFocus(petId);

        var sequence = 2L;
        foreach (var operation in order.Split(','))
        {
            var context = Context(++sequence);
            Assert.True(operation switch
            {
                "gain" => f.Progress.ApplyNetworkGain([new(petNumber, gain)], context),
                "acquire" => f.Progress.ApplyNetworkAcquisition(new(1001, petNumber, level), context),
                "spend" => f.Progress.ApplyNetworkSpent([new(petNumber, cost)], context),
                _ => throw new ArgumentOutOfRangeException(nameof(order)),
            });
        }

        Assert.Equal(level, f.Progress.Level(petId));
        Assert.Equal(level, f.Progress.ObservedLevel(petId));
        Assert.Equal(expectedBalance, f.Progress.AvailableSouls(petId));
        Assert.Equal(expectedBalance, f.Progress.SoulsInLevel(petId));
        Assert.Equal(expectedTotal, f.Progress.Souls(petId));
        Assert.Equal(gain, Assert.Single(f.Progress.Recent()).Quantity);
        Assert.Single(File.ReadAllLines(f.EventsPath));
        Assert.Equal(level == 3, f.Progress.IsMax(petId));

        // Legacy character and server bindings share the global pet collection.
        Assert.True(f.Progress.BindNetworkContext(Context(++sequence, character: 202)));
        Assert.Equal(expectedTotal, f.Progress.Souls(petId));
        Assert.Equal(expectedBalance, f.Progress.AvailableSouls(petId));
        Assert.Equal(level, f.Progress.Level(petId));
        Assert.True(f.Progress.IsFocus(petId));

        Assert.True(f.Progress.BindNetworkContext(Context(++sequence, character: 303, server: 502)));
        Assert.Equal(expectedTotal, f.Progress.Souls(petId));
        Assert.Equal(expectedBalance, f.Progress.AvailableSouls(petId));
        Assert.True(f.Progress.IsFocus(petId));
        Assert.True(f.Progress.BindNetworkContext(Context(++sequence, character: 404)));
        Assert.Equal(expectedTotal, f.Progress.Souls(petId));
        Assert.Equal(expectedBalance, f.Progress.AvailableSouls(petId));
        Assert.Equal(level, f.Progress.ObservedLevel(petId));

        // The next pickup still works after level-up and a character/server round trip.
        var next = Context(++sequence, character: 404);
        Assert.True(f.Progress.ApplyNetworkGain([new(petNumber, 1)], next));
        Assert.False(f.Progress.ApplyNetworkGain([new(petNumber, 1)], next));
        Assert.Equal(expectedTotal + 1, f.Progress.Souls(petId));
        Assert.Equal(expectedBalance + 1, f.Progress.AvailableSouls(petId));
        Assert.Equal(1, f.Progress.Recent()[0].Quantity);
        Assert.Equal(2, f.Progress.Recent().Count);
        Assert.Equal(2, File.ReadAllLines(f.EventsPath).Length);

        var loaded = f.Load();
        Assert.Null(loaded.ActiveServerId);
        Assert.Equal(expectedTotal + 1, loaded.Souls(petId));
        Assert.Equal(expectedBalance + 1, loaded.AvailableSouls(petId));
        Assert.Equal(level, loaded.ObservedLevel(petId));
        Assert.True(loaded.IsFocus(petId));
    }

    private static NetworkProgressContext Context(long sequence, long character = 101, int server = 501) =>
        new(new(character, server, 1001, "Synthetic pet transition"), 1, sequence, At.AddSeconds(sequence));

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-pet-transition-" + Guid.NewGuid().ToString("N"));
        private readonly PetCatalog _catalog;
        internal ProgressService Progress { get; }
        internal string EventsPath => Path.Combine(_folder, "events.jsonl");
        internal Fixture()
        {
            Directory.CreateDirectory(_folder);
            _catalog = new PetCatalog(new[] { 1135, 1163 }.Select(number =>
            {
                var source = PetIdTable.Find(number)!;
                return new PetDefinition { Id = source.CatalogId!, En = source.En,
                    Genus = source.Genus.ToLowerInvariant(), NumericId = number.ToString(CultureInfo.InvariantCulture) };
            }));
            Progress = Load();
        }
        internal ProgressService Load() => new(Path.Combine(_folder, "progress.json"), _catalog);
        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }
}
