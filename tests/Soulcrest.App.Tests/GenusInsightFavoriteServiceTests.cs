using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.GenusInsight;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GenusInsightFavoriteServiceTests
{
    private static readonly GenusInsightFavoriteKey First = new("special", 1, "FixingDamage");
    private static readonly GenusInsightFavoriteKey OtherSlot = new("special", 5, "FixingDamage");

    [Fact]
    public void MissingPreferencesAreEmptyAndReadingCreatesNoFiles()
    {
        using var f = new Folder();
        var service = new GenusInsightFavoriteService(f.Path);
        Assert.Empty(service.Snapshot.Favorites); Assert.False(service.Contains(First));
        Assert.Empty(service.Snapshot.GlobalStats); Assert.Empty(service.Snapshot.SlotOverrides);
        Assert.True(service.CanSave); Assert.False(service.SaveFailed);
        Assert.False(File.Exists(f.Path)); Assert.Empty(Directory.GetFiles(f.Root));
    }

    [Fact]
    public void FavoritesStaySlotScopedPersistAndDoNotMutateOldSnapshots()
    {
        using var f = new Folder();
        var service = new GenusInsightFavoriteService(f.Path);
        var original = service.Snapshot;
        service.Toggle(First);
        var afterFirst = service.Snapshot;
        Assert.True(afterFirst.Contains(First)); Assert.False(afterFirst.Contains(OtherSlot));
        service.Toggle(OtherSlot); service.Toggle(First);
        Assert.False(service.Contains(First)); Assert.True(service.Contains(OtherSlot));
        Assert.Empty(original.Favorites); Assert.Single(afterFirst.Favorites);
        Assert.True(afterFirst.Contains(First));
        var bytes = File.ReadAllBytes(f.Path);
        var reloaded = new GenusInsightFavoriteService(f.Path);
        Assert.False(reloaded.Contains(First)); Assert.True(reloaded.Contains(OtherSlot));
        Assert.Equal(bytes, File.ReadAllBytes(f.Path));
    }

    [Fact]
    public void JsonContainsOnlyGlobalDisplayKeysAndNoRuntimeIdentityOrRoll()
    {
        using var f = new Folder(); var service = new GenusInsightFavoriteService(f.Path); service.Toggle(First);
        using var document = JsonDocument.Parse(File.ReadAllBytes(f.Path));
        Assert.Equal(new[] { "globalStats", "schemaVersion", "slotOverrides" }, document.RootElement.EnumerateObject().Select(row => row.Name).Order());
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("globalStats").EnumerateArray());
        var favorite = Assert.Single(document.RootElement.GetProperty("slotOverrides").EnumerateArray());
        Assert.Equal(new[] { "genus", "isFavorite", "slot", "statId" }, favorite.EnumerateObject().Select(row => row.Name).Order());
        Assert.Equal("special", favorite.GetProperty("genus").GetString());
        Assert.Equal(1, favorite.GetProperty("slot").GetInt32());
        Assert.Equal("FixingDamage", favorite.GetProperty("statId").GetString());
        Assert.True(favorite.GetProperty("isFavorite").GetBoolean());
    }

    [Theory]
    [InlineData("unknown", 1, "FixingDamage")]
    [InlineData("special", 0, "FixingDamage")]
    [InlineData("special", 10, "FixingDamage")]
    [InlineData("special", 2, "FixingDamage")]
    [InlineData("special", 1, "UnknownStat")]
    public void UnknownAndUnavailableStatSlotCombinationsCannotBeFavorited(string genus, int slot, string stat)
    {
        using var f = new Folder(); var service = new GenusInsightFavoriteService(f.Path);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Toggle(new(genus, (byte)slot, stat)));
        Assert.Empty(service.Snapshot.Favorites); Assert.False(File.Exists(f.Path));
    }

    [Theory]
    [InlineData("duplicate-key")]
    [InlineData("duplicate-property")]
    [InlineData("unknown-stat")]
    [InlineData("unavailable-slot")]
    [InlineData("unknown-field")]
    [InlineData("schema")]
    [InlineData("null-array")]
    [InlineData("null-row")]
    [InlineData("oversized")]
    public void InvalidStoredDataIsPreservedAndNeverSilentlyReplaced(string variant)
    {
        using var f = new Folder();
        const string row = "{\"genus\":\"special\",\"slot\":1,\"statId\":\"FixingDamage\"}";
        var json = "{\"schemaVersion\":1,\"favorites\":[" + row + "]}";
        json = variant switch
        {
            "duplicate-key" => json.Replace(row, row + "," + row, StringComparison.Ordinal),
            "duplicate-property" => json.Replace("\"slot\":1", "\"slot\":1,\"slot\":1", StringComparison.Ordinal),
            "unknown-stat" => json.Replace("FixingDamage", "Unknown", StringComparison.Ordinal),
            "unavailable-slot" => json.Replace("\"slot\":1", "\"slot\":2", StringComparison.Ordinal),
            "unknown-field" => json.Replace("\"slot\":1", "\"profileId\":\"must-not-load\",\"slot\":1", StringComparison.Ordinal),
            "schema" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            "null-array" => "{\"schemaVersion\":1,\"favorites\":null}",
            "null-row" => "{\"schemaVersion\":1,\"favorites\":[null]}",
            "oversized" => new string(' ', 512 * 1024 + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        File.WriteAllText(f.Path, json);
        var before = File.ReadAllBytes(f.Path);
        var service = new GenusInsightFavoriteService(f.Path);
        Assert.Empty(service.Snapshot.Favorites);
        Assert.False(service.CanSave); Assert.False(service.SaveFailed);
        Assert.Throws<InvalidDataException>(() => service.Toggle(First));
        Assert.Throws<InvalidDataException>(() => service.ToggleGlobal("FixingDamage"));
        Assert.Throws<InvalidDataException>(() => service.ResetOverride(First));
        Assert.Equal(before, File.ReadAllBytes(f.Path));
    }

    [Fact]
    public void FailedSavePreservesBothStateAndEventsAndSuccessfulRetryStillWorks()
    {
        using var f = new Folder();
        var fail = false;
        var service = new GenusInsightFavoriteService(f.Path, (path, value) =>
        {
            if (fail) throw new IOException("Synthetic preference write failure");
            JsonFile.Save(path, value);
        }, GenusInsightCatalog.Default);
        var changed = 0; service.Changed += () => changed++;
        service.Toggle(First);
        var accepted = service.Snapshot; var bytes = File.ReadAllBytes(f.Path);
        fail = true;
        Assert.Throws<IOException>(() => service.Toggle(OtherSlot));
        Assert.Same(accepted, service.Snapshot); Assert.Equal(bytes, File.ReadAllBytes(f.Path)); Assert.Equal(1, changed);
        Assert.True(service.CanSave); Assert.True(service.SaveFailed);
        fail = false; service.Toggle(OtherSlot);
        Assert.True(service.Contains(First)); Assert.True(service.Contains(OtherSlot)); Assert.Equal(2, changed);
        Assert.False(service.SaveFailed);
    }

    [Fact]
    public void GlobalStatsApplyToEveryValidSlotPoolIncludingBothUnboundNativeStats()
    {
        using var f = new Folder();
        var saves = 0;
        var service = new GenusInsightFavoriteService(f.Path, (_, _) => saves++, GenusInsightCatalog.Default);
        var original = service.Snapshot;
        var catalog = GenusInsightCatalog.Default;
        Assert.Equal(89, catalog.Stats.Count);
        foreach (var stat in catalog.Stats.Keys)
        {
            service.ToggleGlobal(stat);
            var enabled = service.Snapshot;
            Assert.Equal(stat, Assert.Single(enabled.GlobalStats));
            var expected = catalog.Slots.Where(slot => slot.Options.Any(option => option.StatId == stat))
                .Select(slot => new GenusInsightFavoriteKey(slot.Genus, slot.Slot, stat)).ToImmutableHashSet();
            Assert.NotEmpty(expected);
            Assert.True(expected.SetEquals(enabled.Favorites));
            Assert.Empty(enabled.SlotOverrides);
            service.ToggleGlobal(stat);
            Assert.Empty(service.Snapshot.Favorites); Assert.Empty(service.Snapshot.GlobalStats);
            Assert.True(expected.SetEquals(enabled.Favorites));
        }
        Assert.Empty(original.Favorites); Assert.Empty(original.GlobalStats); Assert.Empty(original.SlotOverrides);
        Assert.Equal(178, saves); Assert.False(File.Exists(f.Path));
    }

    [Fact]
    public void LocalFalseExceptionsSurviveGlobalChangesAndResetRestoresInheritance()
    {
        using var f = new Folder(); var service = new GenusInsightFavoriteService(f.Path);
        var changed = 0; service.Changed += () => changed++;
        service.ToggleGlobal("FixingDamage");
        var inherited = service.Snapshot;
        Assert.True(inherited.Contains(First)); Assert.True(inherited.Contains(OtherSlot));
        service.Toggle(First);
        Assert.False(service.Contains(First)); Assert.True(service.Contains(OtherSlot));
        Assert.False(service.Snapshot.SlotOverrides[First]);
        service.ToggleGlobal("FixingDamage");
        Assert.False(service.Contains(First)); Assert.False(service.Contains(OtherSlot));
        service.ToggleGlobal("FixingDamage");
        Assert.False(service.Contains(First)); Assert.True(service.Contains(OtherSlot));
        var reloaded = new GenusInsightFavoriteService(f.Path);
        Assert.False(reloaded.Contains(First)); Assert.False(reloaded.Snapshot.SlotOverrides[First]);
        Assert.True(reloaded.Contains(OtherSlot)); Assert.Contains("FixingDamage", reloaded.Snapshot.GlobalStats);
        service.ResetOverride(First);
        Assert.True(service.Contains(First)); Assert.True(service.Contains(OtherSlot));
        Assert.Empty(service.Snapshot.SlotOverrides); Assert.True(inherited.Contains(First));
        var bytes = File.ReadAllBytes(f.Path); var accepted = service.Snapshot;
        service.ResetOverride(First);
        Assert.Equal(5, changed); Assert.Same(accepted, service.Snapshot); Assert.Equal(bytes, File.ReadAllBytes(f.Path));
    }

    [Fact]
    public void LocalTrueExceptionsRemainExplicitWhenGlobalChoiceChanges()
    {
        using var f = new Folder(); var service = new GenusInsightFavoriteService(f.Path);
        service.Toggle(First);
        Assert.True(service.Snapshot.SlotOverrides[First]);
        service.ToggleGlobal("FixingDamage"); service.ToggleGlobal("FixingDamage");
        Assert.True(service.Contains(First)); Assert.False(service.Contains(OtherSlot));
        Assert.True(service.Snapshot.SlotOverrides[First]);
        service.ResetOverride(First);
        Assert.False(service.Contains(First)); Assert.Empty(service.Snapshot.SlotOverrides);
    }

    [Fact]
    public void LegacyFavoritesMigrateLosslesslyOnlyAfterSuccessfulSave()
    {
        using var f = new Folder();
        const string legacy = "{\"schemaVersion\":1,\"favorites\":[{\"genus\":\"special\",\"slot\":1,\"statId\":\"FixingDamage\"},{\"genus\":\"special\",\"slot\":5,\"statId\":\"FixingDamage\"}]}";
        File.WriteAllText(f.Path, legacy);
        var bytes = File.ReadAllBytes(f.Path); var fail = true;
        var service = new GenusInsightFavoriteService(f.Path, (path, value) =>
        {
            if (fail) throw new IOException("Synthetic migration write failure");
            JsonFile.Save(path, value);
        });
        var migrated = service.Snapshot; var changed = 0; service.Changed += () => changed++;
        Assert.True(service.CanSave); Assert.Empty(migrated.GlobalStats); Assert.Equal(2, migrated.SlotOverrides.Count);
        Assert.True(migrated.SlotOverrides[First]); Assert.True(migrated.SlotOverrides[OtherSlot]);
        Assert.Equal(bytes, File.ReadAllBytes(f.Path));
        service.ResetOverride(new("special", 4, "HardHit"));
        Assert.Equal(bytes, File.ReadAllBytes(f.Path)); Assert.Equal(0, changed);
        Assert.Throws<IOException>(() => service.ToggleGlobal("FixingDamage"));
        Assert.Same(migrated, service.Snapshot); Assert.Equal(bytes, File.ReadAllBytes(f.Path)); Assert.Equal(0, changed);
        Assert.True(service.SaveFailed);
        fail = false; service.ToggleGlobal("FixingDamage");
        Assert.Equal(1, changed); Assert.False(service.SaveFailed);
        using var document = JsonDocument.Parse(File.ReadAllBytes(f.Path));
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("slotOverrides").GetArrayLength());
        var reloaded = new GenusInsightFavoriteService(f.Path);
        Assert.True(reloaded.Contains(First)); Assert.True(reloaded.Contains(OtherSlot));
        Assert.True(reloaded.Snapshot.SlotOverrides[First]); Assert.True(reloaded.Snapshot.SlotOverrides[OtherSlot]);
        Assert.Contains("FixingDamage", reloaded.Snapshot.GlobalStats);
    }

    [Theory]
    [InlineData("duplicate-global")]
    [InlineData("unknown-global")]
    [InlineData("null-global")]
    [InlineData("duplicate-override")]
    [InlineData("duplicate-property")]
    [InlineData("missing-value")]
    [InlineData("unknown-field")]
    [InlineData("unavailable-slot")]
    [InlineData("null-overrides")]
    [InlineData("null-row")]
    [InlineData("future-schema")]
    public void InvalidSchemaTwoDataBlocksEveryWriteAndPreservesOriginalBytes(string variant)
    {
        using var f = new Folder();
        const string row = "{\"genus\":\"special\",\"slot\":1,\"statId\":\"FixingDamage\",\"isFavorite\":false}";
        var json = "{\"schemaVersion\":2,\"globalStats\":[\"FixingDamage\"],\"slotOverrides\":[" + row + "]}";
        json = variant switch
        {
            "duplicate-global" => json.Replace("[\"FixingDamage\"]", "[\"FixingDamage\",\"FixingDamage\"]", StringComparison.Ordinal),
            "unknown-global" => json.Replace("[\"FixingDamage\"]", "[\"Unknown\"]", StringComparison.Ordinal),
            "null-global" => json.Replace("[\"FixingDamage\"]", "null", StringComparison.Ordinal),
            "duplicate-override" => json.Replace(row, row + "," + row, StringComparison.Ordinal),
            "duplicate-property" => json.Replace("\"isFavorite\":false", "\"isFavorite\":false,\"isFavorite\":true", StringComparison.Ordinal),
            "missing-value" => json.Replace(",\"isFavorite\":false", "", StringComparison.Ordinal),
            "unknown-field" => json.Replace("\"isFavorite\":false", "\"isFavorite\":false,\"profileId\":\"must-not-load\"", StringComparison.Ordinal),
            "unavailable-slot" => json.Replace("\"slot\":1", "\"slot\":2", StringComparison.Ordinal),
            "null-overrides" => json.Replace("[" + row + "]", "null", StringComparison.Ordinal),
            "null-row" => json.Replace(row, "null", StringComparison.Ordinal),
            "future-schema" => json.Replace("\"schemaVersion\":2", "\"schemaVersion\":3", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        File.WriteAllText(f.Path, json); var bytes = File.ReadAllBytes(f.Path);
        var service = new GenusInsightFavoriteService(f.Path);
        Assert.False(service.CanSave); Assert.False(service.SaveFailed);
        Assert.Empty(service.Snapshot.Favorites); Assert.Empty(service.Snapshot.GlobalStats); Assert.Empty(service.Snapshot.SlotOverrides);
        Assert.Throws<InvalidDataException>(() => service.Toggle(First));
        Assert.Throws<InvalidDataException>(() => service.ToggleGlobal("FixingDamage"));
        Assert.Throws<InvalidDataException>(() => service.ResetOverride(First));
        Assert.Equal(bytes, File.ReadAllBytes(f.Path)); Assert.False(service.SaveFailed);
    }

    [Theory]
    [InlineData("global")]
    [InlineData("reset")]
    public void AdditionalMutationsPreserveSnapshotAndEventsWhenSaveFails(string operation)
    {
        using var f = new Folder(); var fail = false;
        var service = new GenusInsightFavoriteService(f.Path, (path, value) =>
        {
            if (fail) throw new IOException("Synthetic preference write failure");
            JsonFile.Save(path, value);
        });
        service.Toggle(First);
        var accepted = service.Snapshot; var bytes = File.ReadAllBytes(f.Path); var changed = 0;
        service.Changed += () => changed++; fail = true;
        void Mutate() { if (operation == "global") service.ToggleGlobal("FixingDamage"); else service.ResetOverride(First); }
        Assert.Throws<IOException>(Mutate);
        Assert.Same(accepted, service.Snapshot); Assert.Equal(bytes, File.ReadAllBytes(f.Path));
        Assert.Equal(0, changed); Assert.True(service.SaveFailed);
        fail = false; Mutate(); Assert.Equal(1, changed); Assert.False(service.SaveFailed);
        Assert.Equal(operation == "global", service.Contains(First));
    }

    [Fact]
    public void InvalidGlobalStatsAndOverrideResetsCannotWritePreferences()
    {
        using var f = new Folder(); var service = new GenusInsightFavoriteService(f.Path);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.ToggleGlobal("UnknownStat"));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.ResetOverride(new("special", 2, "FixingDamage")));
        Assert.False(File.Exists(f.Path)); Assert.False(service.SaveFailed);
    }

    [Fact]
    public void ExistingSnapshotConstructionKeepsEffectiveFavoritesAndUsesEmptyInheritanceMetadata()
    {
        var snapshot = new GenusInsightFavoriteSnapshot(ImmutableHashSet.Create(First));
        Assert.True(snapshot.Contains(First)); Assert.Empty(snapshot.GlobalStats); Assert.Empty(snapshot.SlotOverrides);
    }

    [Fact]
    public void SuccessCallbacksCanReadTheServiceOutsideItsLock()
    {
        using var f = new Folder(); var service = new GenusInsightFavoriteService(f.Path);
        var unlocked = false;
        service.Changed += () => unlocked = Task.Run(() => service.Contains(First)).Wait(TimeSpan.FromSeconds(5));
        service.Toggle(First); Assert.True(unlocked);
    }

    private sealed class Folder : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-genus-favorites-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Root, "preferences.json");
        internal Folder() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
