using System.Globalization;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GlobalPetCollectionSnapshotTests
{
    private const int Number = 1135;
    private static readonly PetIdEntry Source = PetIdTable.Find(Number)!;
    private static string Id => Source.CatalogId!;
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static PetProgressContext Context(long sequence, long generation = 1) => new(generation, sequence, At.AddSeconds(sequence));
    private static PetProgressSnapshot Levels(int level) => new(new Dictionary<int, int> { [Number] = level }, new Dictionary<int, int>());

    [Fact]
    public void UntouchedRosterIsUnknownAndReadingItDoesNotCreateProgressFiles()
    {
        using var f = new Folder(); var progress = f.Load();
        var row = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal(Id, row.Id); Assert.Equal(Source.Genus.ToLowerInvariant(), row.Genus);
        Assert.Equal(0, row.Level); Assert.False(row.IsKnown); Assert.False(row.IsCurrent); Assert.False(row.IsManual);
        Assert.Null(row.ObservedAt); Assert.False(File.Exists(f.ProfilePath));
        Assert.Empty(PetCollectionSnapshot.Empty.Pets);
    }

    [Fact]
    public void PartialSoulGainsNeverPromoteTheAcquiredLevelOrProveAnUnownedPet()
    {
        using var f = new Folder(); var progress = f.Load();
        Assert.True(progress.BindPetSession(Context(0)));
        Assert.True(progress.ApplyPetGain([new(Number, 105)], Context(1)));
        Assert.Equal(105, progress.Souls(Id));
        var row = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal(0, row.Level); Assert.False(row.IsKnown); Assert.False(row.IsCurrent);
        Assert.Equal(At.AddSeconds(1), row.ObservedAt);
    }

    [Fact]
    public void ClearingAManualCorrectionCannotConfirmAnUnobservedNetworkZero()
    {
        using var f = new Folder(); var progress = f.Load();
        Assert.True(progress.BindPetSession(Context(0)));
        Assert.True(progress.ApplyPetGain([new(Number, 105)], Context(1)));
        progress.SetLevel(Id, 2, 0);
        progress.ClearManualOverride(Id);
        var row = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal(0, row.Level); Assert.False(row.IsKnown); Assert.False(row.IsCurrent); Assert.False(row.IsManual);
    }

    [Fact]
    public void FullCollectionConfirmsBothPositiveLevelsAndExplicitAbsence()
    {
        using var f = new Folder(); var progress = f.Load();
        Assert.True(progress.BindPetSession(Context(0)));
        Assert.True(progress.ApplyPetSnapshot(Levels(2), Context(1)));
        var owned = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal(2, owned.Level); Assert.True(owned.IsKnown); Assert.True(owned.IsCurrent);
        Assert.Equal(At.AddSeconds(1), owned.ObservedAt);
        Assert.True(progress.ApplyPetSnapshot(new(new Dictionary<int, int>(), new Dictionary<int, int>()), Context(2)));
        var absent = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal(0, absent.Level); Assert.True(absent.IsKnown); Assert.True(absent.IsCurrent);
    }

    [Fact]
    public void ManualCorrectionsRemainIndependentOfTheRawNetworkLevel()
    {
        using var f = new Folder(); var progress = f.Load();
        progress.SetLevel(Id, 2, 0);
        Assert.True(progress.BindPetSession(Context(0)));
        Assert.True(progress.ApplyPetSnapshot(Levels(1), Context(1)));
        var manual = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal(2, manual.Level); Assert.True(manual.IsKnown); Assert.True(manual.IsManual); Assert.False(manual.IsCurrent);
        Assert.NotNull(manual.ObservedAt);
        progress.ClearManualOverride(Id);
        var restored = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal(1, restored.Level); Assert.True(restored.IsKnown); Assert.False(restored.IsManual); Assert.False(restored.IsCurrent);
    }

    [Fact]
    public void LegacyLevelSeedsRemainHistoricalAndAreNotRaisedFromGrowingEarnedTotals()
    {
        using var f = new Folder();
        JsonFile.Save(f.ProfilePath, new ProgressProfile { SchemaVersion = 3,
            Pets = new() { [Id] = new() { Souls = 8, UpdatedAt = At } } });
        var progress = f.Load();
        var saved = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal(1, saved.Level); Assert.True(saved.IsKnown); Assert.False(saved.IsCurrent);
        Assert.True(progress.BindPetSession(Context(0)));
        Assert.True(progress.ApplyPetGain([new(Number, 105)], Context(1)));
        var partial = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal(1, partial.Level); Assert.True(partial.IsKnown); Assert.False(partial.IsCurrent);
        Assert.True(progress.Souls(Id) > progress.Thresholds.Max);
    }

    [Fact]
    public void ARecordedLegacyZeroDiffersFromAnUntouchedUnknownEntry()
    {
        using var f = new Folder();
        JsonFile.Save(f.ProfilePath, new ProgressProfile { SchemaVersion = 3,
            Pets = new() { [Id] = new() { Souls = 0, UpdatedAt = At } } });
        var saved = Assert.Single(ReadWithoutWrites(f, f.Load()).Pets);
        Assert.Equal(0, saved.Level); Assert.True(saved.IsKnown); Assert.False(saved.IsCurrent); Assert.Equal(At, saved.ObservedAt);
    }

    [Fact]
    public void FocusOnlyLegacyRecordsDoNotProveARecordedLevelBeforeOrAfterReload()
    {
        using var f = new Folder(); var progress = f.Load();
        progress.ToggleFocus(Id);
        var focused = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.True(progress.IsFocus(Id));
        Assert.Equal(0, focused.Level); Assert.False(focused.IsKnown); Assert.False(focused.IsCurrent);
        Assert.False(focused.IsManual); Assert.Null(focused.ObservedAt);
        var reloaded = f.Load();
        var saved = Assert.Single(ReadWithoutWrites(f, reloaded).Pets);
        Assert.True(reloaded.IsFocus(Id)); Assert.False(saved.IsKnown); Assert.Equal(0, saved.Level);
    }

    [Fact]
    public void RestartAndBindingAloneNeverReconfirmAStoredFullSnapshot()
    {
        using var f = new Folder(); var progress = f.Load();
        progress.BindPetSession(Context(0)); progress.ApplyPetSnapshot(Levels(3), Context(1));
        var reloaded = f.Load();
        var saved = Assert.Single(ReadWithoutWrites(f, reloaded).Pets);
        Assert.Equal(3, saved.Level); Assert.True(saved.IsKnown); Assert.False(saved.IsCurrent);
        Assert.True(reloaded.BindPetSession(Context(0, 2)));
        Assert.False(Assert.Single(ReadWithoutWrites(f, reloaded).Pets).IsCurrent);
    }

    [Fact]
    public void LevelOnlyAcquisitionSurvivesRestartButCannotBecomeCurrentThroughBindingAlone()
    {
        using var f = new Folder(); var progress = f.Load();
        Assert.True(progress.BindPetSession(Context(0)));
        Assert.True(progress.ApplyPetAcquisition(new(1001, Number, 2), Context(1)));
        var observed = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal(2, observed.Level); Assert.True(observed.IsKnown); Assert.False(observed.IsCurrent);
        var reloaded = f.Load();
        var saved = Assert.Single(ReadWithoutWrites(f, reloaded).Pets);
        Assert.Equal(2, saved.Level); Assert.True(saved.IsKnown); Assert.False(saved.IsCurrent);
        Assert.True(reloaded.BindPetSession(Context(0, 2)));
        Assert.False(Assert.Single(ReadWithoutWrites(f, reloaded).Pets).IsCurrent);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SourceRejectionOrTransportGapKeepsHistoricalLevelsWithoutFreshness(bool rejected)
    {
        using var f = new Folder(); var progress = f.Load();
        progress.BindPetSession(Context(0)); progress.ApplyPetSnapshot(Levels(2), Context(1));
        if (rejected) progress.MarkNetworkPetSourceStale(); else progress.MarkPetNetworkStale();
        var row = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal(2, row.Level); Assert.True(row.IsKnown); Assert.False(row.IsCurrent);
    }

    [Fact]
    public void CharacterAndServerChangesDoNotSelectADifferentPetCollection()
    {
        using var f = new Folder(); var progress = f.Load();
        NetworkProgressContext Legacy(long sequence, long character, int server) =>
            new(new(character, server, 1001, "SyntheticGlobal"), 1, sequence, At.AddSeconds(sequence));
        Assert.True(progress.BindNetworkContext(Legacy(0, 101, 501)));
        Assert.True(progress.ApplyNetworkSnapshot(Levels(2), Legacy(1, 101, 501)));
        var before = ReadWithoutWrites(f, progress);
        Assert.True(progress.BindNetworkContext(Legacy(2, 202, 502)));
        Assert.Equal(before.Pets.ToArray(), ReadWithoutWrites(f, progress).Pets.ToArray());
        Assert.Null(progress.ActiveServerId);
    }

    [Fact]
    public void SpecialPetsRemainInTheSnapshotAndAmbiguousSoulIdsAreExcludedExactlyLikeTheMainTab()
    {
        var special = PetIdTable.All.Values.First(pet => pet.Genus.Equals("special", StringComparison.OrdinalIgnoreCase));
        using var f = new Folder([
            Definition("ambiguous-a", Number), Definition("ambiguous-b", Number),
            Definition("special", special.Id)]);
        var progress = f.Load();
        var only = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal("special", only.Id); Assert.Equal("special", only.Genus);
        Assert.Equal(progress.VerifiedPets.Select(pet => pet.Id), progress.ReadCollectionSnapshot().Pets.Select(pet => pet.Id));
    }

    [Fact]
    public void ExplicitLegacyAliasesAreReadOnceWithoutMutatingTheSourceProfile()
    {
        using var f = new Folder([Definition(Id, Number), Definition("legacy-alias", Number) with { LegacyDuplicate = true }]);
        JsonFile.Save(f.ProfilePath, new ProgressProfile { SchemaVersion = 3,
            Pets = new() { ["legacy-alias"] = new() { Souls = 30, UpdatedAt = At } } });
        var progress = f.Load();
        var row = Assert.Single(ReadWithoutWrites(f, progress).Pets);
        Assert.Equal(Id, row.Id); Assert.Equal(2, row.Level); Assert.True(row.IsKnown);
    }

    [Fact]
    public void CollectionGenusKeysNormalizeOriginalMetadataCaseAtTheSnapshotBoundary()
    {
        using var f = new Folder([Definition(Id, Number) with { Genus = Source.Genus.ToUpperInvariant() }]);
        var row = Assert.Single(ReadWithoutWrites(f, f.Load()).Pets);
        Assert.Equal(Source.Genus.ToLowerInvariant(), row.Genus);
    }

    [Fact]
    public void SnapshotsAreFrozenAndStayUnchangedAfterLaterProgressWrites()
    {
        using var f = new Folder(); var progress = f.Load();
        var before = ReadWithoutWrites(f, progress);
        Assert.Throws<NotSupportedException>(() => ((IList<PetCollectionEntry>)before.Pets).Add(before.Pets[0]));
        Assert.Throws<NotSupportedException>(() => ((IList<PetCollectionEntry>)before.Pets)[0] = before.Pets[0] with { Level = 3 });
        progress.SetLevel(Id, 3, 0);
        var after = ReadWithoutWrites(f, progress);
        Assert.Equal(0, before.Pets[0].Level); Assert.False(before.Pets[0].IsKnown);
        Assert.Equal(3, after.Pets[0].Level); Assert.True(after.Pets[0].IsManual);
    }

    private static PetCollectionSnapshot ReadWithoutWrites(Folder folder, ProgressService progress)
    {
        var before = Directory.GetFiles(folder.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
        var snapshot = progress.ReadCollectionSnapshot();
        var after = Directory.GetFiles(folder.Root, "*", SearchOption.AllDirectories);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        return snapshot;
    }

    private static PetDefinition Definition(string id, int number)
    {
        var pet = PetIdTable.Find(number)!;
        return new() { Id = id, En = pet.En, Genus = pet.Genus.ToLowerInvariant(), NumericId = number.ToString(CultureInfo.InvariantCulture) };
    }
    private sealed class Folder : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-collection-snapshot-" + Guid.NewGuid().ToString("N"));
        internal string ProfilePath => Path.Combine(Root, "progress.json");
        private readonly PetCatalog _catalog;
        internal Folder(PetDefinition[]? pets = null)
        {
            Directory.CreateDirectory(Root);
            _catalog = new(pets ?? [Definition(Id, Number)]);
        }
        internal ProgressService Load() => new(ProfilePath, _catalog);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
