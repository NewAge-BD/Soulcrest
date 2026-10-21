using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ProgressProjectionConcurrencyTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ConcurrentNetworkLevelCorrectionsKeepProgressLabelsConsistent(string language)
    {
        var folder = Path.Combine(Path.GetTempPath(), "soulcrest-progress-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previousLanguage = UiText.Language;
        try
        {
            UiText.Language = language;
            var source = PetIdTable.Find(1062)!;
            var petId = source.CatalogId ?? PetCatalog.Slug(source.En);
            var catalog = new PetCatalog([new PetDefinition { Id = petId, En = source.En, Genus = source.Genus,
                NumericId = source.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }]);
            var progress = new ProgressService(Path.Combine(folder, "progress.json"), catalog);
            var at = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
            PetProgressContext Context(long sequence) => new(1, sequence, at.AddSeconds(sequence));
            PetProgressSnapshot Snapshot(bool max) => new(new Dictionary<int, int> { [source.Id] = max ? 3 : 2 },
                new Dictionary<int, int> { [source.Id] = max ? 0 : 7 });
            Assert.True(progress.BindPetSession(Context(0)));
            Assert.True(progress.ApplyPetSnapshot(Snapshot(false), Context(1)));
            var activeLabel = UiText.F("St. {0} · {1}/{2}", 2, 7, 75);
            var maxLabel = UiText.F("St. {0} · max", 3);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var start = new ManualResetEventSlim();
            var writer = Task.Factory.StartNew(() =>
            {
                start.Wait(timeout.Token);
                // Full observations can correct either direction; each state is valid and self-consistent.
                for (var sequence = 2; sequence < 258; sequence++)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    Assert.True(progress.ApplyPetSnapshot(Snapshot(sequence % 2 == 0), Context(sequence)));
                    Thread.Yield();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var reader = Task.Factory.StartNew(() =>
            {
                start.Wait(timeout.Token);
                for (var read = 0; read < 4096; read++)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    Assert.Contains(progress.NeededForNextLevel(petId), new[] { 0, 75 });
                    Assert.Contains(progress.SoulCount(petId), new[] { "max", "7/75" });
                    Assert.Contains(progress.PetMapLabel(petId), new[] { activeLabel, maxLabel });
                    Assert.Contains(progress.LevelText(petId), new[] { activeLabel, "max" });
                    Thread.Yield();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            // No readiness countdown: a faulted worker cannot prevent the other one from starting.
            start.Set();
            await Task.WhenAll(writer, reader).WaitAsync(timeout.Token);
        }
        finally
        {
            UiText.Language = previousLanguage;
            Directory.Delete(folder, recursive: true);
        }
    }
}
