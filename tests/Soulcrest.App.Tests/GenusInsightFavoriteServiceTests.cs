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
        Assert.Equal(new[] { "favorites", "schemaVersion" }, document.RootElement.EnumerateObject().Select(row => row.Name).Order());
        var favorite = Assert.Single(document.RootElement.GetProperty("favorites").EnumerateArray());
        Assert.Equal(new[] { "genus", "slot", "statId" }, favorite.EnumerateObject().Select(row => row.Name).Order());
        Assert.Equal("special", favorite.GetProperty("genus").GetString());
        Assert.Equal(1, favorite.GetProperty("slot").GetInt32());
        Assert.Equal("FixingDamage", favorite.GetProperty("statId").GetString());
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
        Assert.Throws<InvalidDataException>(() => service.Toggle(First));
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
        fail = false; service.Toggle(OtherSlot);
        Assert.True(service.Contains(First)); Assert.True(service.Contains(OtherSlot)); Assert.Equal(2, changed);
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
