using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementFavoriteServiceTests
{
    private const int First = 401100100, Second = 401100200, Retired = 239241400;
    private const int Title = 11010001;

    [Fact]
    public void TitlePinsAreIndependentOrderedAndImmutableAcrossRestart()
    {
        using var fixture = new Fixture();
        using var service = new AchievementFavoriteService(fixture.Root);
        var otherTitle = CombatTitleDisplayCatalog.Default.Titles.Keys.Order().First(id => id != Title);
        Assert.Empty(new AchievementFavoriteSnapshot(fixture.Binding, []).TitleIds);
        Assert.False(service.Get(null).ContainsTitle(Title));
        service.ToggleTitle(fixture.Binding, Title); var frozen = service.Get(fixture.Binding);
        Assert.Empty(frozen.FamilyIds); Assert.True(frozen.ContainsTitle(Title));
        service.Toggle(fixture.Binding, First); service.ToggleTitle(fixture.Binding, otherTitle);
        service.ToggleTitle(fixture.Binding, Title); service.ToggleTitle(fixture.Binding, Title);
        Assert.Equal(new[] { Title }, frozen.TitleIds); Assert.Empty(frozen.FamilyIds);
        Assert.Equal(new[] { First }, service.Get(fixture.Binding).FamilyIds);
        Assert.Equal(new[] { otherTitle, Title }, service.Get(fixture.Binding).TitleIds);
        using var restarted = new AchievementFavoriteService(fixture.Root);
        Assert.Equal(new[] { First }, restarted.Get(fixture.Binding).FamilyIds);
        Assert.Equal(new[] { otherTitle, Title }, restarted.Get(fixture.Binding).TitleIds);
        service.ToggleTitle(fixture.Binding, Title);
        Assert.True(service.Get(fixture.Binding).Contains(First));
        Assert.False(service.Get(fixture.Binding).ContainsTitle(Title));
    }

    [Fact]
    public void LegacyFilesMigrateOnlyOnExplicitWriteWithoutConvertingPins()
    {
        using var fixture = new Fixture(); fixture.Write([First, Retired, 999999999]);
        var bytes = File.ReadAllBytes(fixture.File);
        using var service = new AchievementFavoriteService(fixture.Root);
        Assert.Equal(new[] { First }, service.Get(fixture.Binding).FamilyIds);
        Assert.Empty(service.Get(fixture.Binding).TitleIds); Assert.Equal(bytes, File.ReadAllBytes(fixture.File));
        service.ToggleTitle(fixture.Binding, Title);
        var file = JsonNode.Parse(File.ReadAllText(fixture.File))!;
        Assert.Equal(2, file["schemaVersion"]!.GetValue<int>());
        Assert.Equal(new[] { First, Retired, 999999999 }, file["familyIds"]!.AsArray().Select(value => value!.GetValue<int>()));
        Assert.Equal(new[] { Title }, file["titleIds"]!.AsArray().Select(value => value!.GetValue<int>()));
        service.Toggle(fixture.Binding, Second);
        using var restarted = new AchievementFavoriteService(fixture.Root);
        Assert.Equal(new[] { First, Second }, restarted.Get(fixture.Binding).FamilyIds);
        Assert.Equal(new[] { Title }, restarted.Get(fixture.Binding).TitleIds);
    }

    [Fact]
    public void UnknownStoredTitlesArePreservedButCannotBePinnedOrExposed()
    {
        using var fixture = new Fixture(); fixture.WriteCurrent([First], [999999999, Title]);
        var bytes = File.ReadAllBytes(fixture.File);
        using var service = new AchievementFavoriteService(fixture.Root);
        Assert.Equal(new[] { Title }, service.Get(fixture.Binding).TitleIds);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.File));
        foreach (var id in new[] { 0, -1, 999999999 })
            Assert.Throws<ArgumentOutOfRangeException>(() => service.ToggleTitle(fixture.Binding, id));
        service.Toggle(fixture.Binding, Second);
        var file = JsonNode.Parse(File.ReadAllText(fixture.File))!;
        Assert.Equal(new[] { 999999999, Title }, file["titleIds"]!.AsArray().Select(value => value!.GetValue<int>()));
        service.ToggleTitle(fixture.Binding, Title);
        file = JsonNode.Parse(File.ReadAllText(fixture.File))!;
        Assert.Equal(new[] { 999999999 }, file["titleIds"]!.AsArray().Select(value => value!.GetValue<int>()));
        Assert.Equal(new[] { First, Second }, service.Get(fixture.Binding).FamilyIds);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("duplicate-title")]
    [InlineData("negative-title")]
    [InlineData("missing-titles")]
    [InlineData("null-titles")]
    [InlineData("unknown-property")]
    [InlineData("duplicate-property")]
    [InlineData("array-root")]
    [InlineData("string-version")]
    [InlineData("legacy-with-titles")]
    public void InvalidTitleFilesRemainUntouchedAndUnwritable(string variant)
    {
        using var fixture = new Fixture(); fixture.WriteCurrent([First], [Title]);
        var node = JsonNode.Parse(File.ReadAllText(fixture.File))!;
        switch (variant)
        {
            case "future": node["schemaVersion"] = 3; break;
            case "duplicate-title": node["titleIds"]!.AsArray().Add(Title); break;
            case "negative-title": node["titleIds"]!.AsArray().Add(-1); break;
            case "missing-titles": node.AsObject().Remove("titleIds"); break;
            case "null-titles": node["titleIds"] = null; break;
            case "unknown-property": node["unknown"] = true; break;
            case "string-version": node["schemaVersion"] = "2"; break;
            case "legacy-with-titles": node["schemaVersion"] = 1; break;
        }
        var text = variant switch
        {
            "array-root" => "[]", "duplicate-property" => "{\"titleIds\":[]," + node.ToJsonString()[1..],
            _ => node.ToJsonString()
        };
        File.WriteAllText(fixture.File, text); var bytes = File.ReadAllBytes(fixture.File);
        using var service = new AchievementFavoriteService(fixture.Root);
        Assert.Empty(service.Get(fixture.Binding).FamilyIds); Assert.Empty(service.Get(fixture.Binding).TitleIds);
        Assert.Throws<InvalidDataException>(() => service.ToggleTitle(fixture.Binding, Title));
        Assert.Throws<InvalidDataException>(() => service.Toggle(fixture.Binding, Second));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.File));
    }

    [Fact]
    public void FailedTitleSaveDoesNotPublishAnUnsavedPreference()
    {
        using var fixture = new Fixture();
        using (var initial = new AchievementFavoriteService(fixture.Root))
        { initial.Toggle(fixture.Binding, First); initial.ToggleTitle(fixture.Binding, Title); }
        var bytes = File.ReadAllBytes(fixture.File);
        using var failing = new AchievementFavoriteService(fixture.Root, null, AchievementDefinitionCatalog.Default,
            (_, _) => throw new IOException("Synthetic title favorite save failure."));
        var changed = 0; failing.Changed += () => changed++;
        Assert.Throws<IOException>(() => failing.ToggleTitle(fixture.Binding, Title));
        Assert.Equal(0, changed); Assert.True(failing.Get(fixture.Binding).ContainsTitle(Title));
        Assert.True(failing.Get(fixture.Binding).Contains(First)); Assert.Equal(bytes, File.ReadAllBytes(fixture.File));
    }

    [Fact]
    public void ReadIsSideEffectFreeAndPinsPreserveTheirOrderAcrossRestart()
    {
        using var fixture = new Fixture();
        using var service = new AchievementFavoriteService(fixture.Root);
        Assert.Null(service.Get(null).Binding); Assert.Empty(service.Get(fixture.Binding).FamilyIds);
        Assert.False(Directory.Exists(fixture.Root));
        var changed = 0;
        service.Changed += () => { changed++; Assert.NotNull(service.Get(fixture.Binding)); };
        service.Toggle(fixture.Binding, First); var frozen = service.Get(fixture.Binding);
        service.Toggle(fixture.Binding, Second);
        Assert.Equal(new[] { First }, frozen.FamilyIds);
        Assert.Equal(new[] { First, Second }, service.Get(fixture.Binding).FamilyIds);
        using var restarted = new AchievementFavoriteService(fixture.Root);
        Assert.Equal(new[] { First, Second }, restarted.Get(fixture.Binding).FamilyIds);
        Assert.True(restarted.Get(fixture.Binding).Contains(First));
        service.Toggle(fixture.Binding, First); service.Toggle(fixture.Binding, First);
        Assert.Equal(new[] { Second, First }, service.Get(fixture.Binding).FamilyIds);
        Assert.Equal(4, changed);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void AnotherProfileOrAnotherNativeAssignmentNeverInheritsPins()
    {
        using var fixture = new Fixture(); using var service = new AchievementFavoriteService(fixture.Root);
        service.Toggle(fixture.Binding, First);
        service.ToggleTitle(fixture.Binding, Title);
        var anotherProfile = new AchievementProfileBinding(Guid.NewGuid().ToString("N"), fixture.Binding.Revision);
        var anotherRevision = fixture.Binding with { Revision = Guid.NewGuid().ToString("N") };
        Assert.Empty(service.Get(anotherProfile).FamilyIds); Assert.Empty(service.Get(anotherRevision).FamilyIds);
        Assert.Empty(service.Get(anotherProfile).TitleIds); Assert.Empty(service.Get(anotherRevision).TitleIds);
        service.Toggle(anotherRevision, Second);
        service.ToggleTitle(anotherRevision, CombatTitleDisplayCatalog.Default.Titles.Keys.Order().First(id => id != Title));
        Assert.Equal(new[] { First }, service.Get(fixture.Binding).FamilyIds);
        Assert.Equal(new[] { Title }, service.Get(fixture.Binding).TitleIds);
        Assert.Equal(new[] { Second }, service.Get(anotherRevision).FamilyIds);
    }

    [Fact]
    public void InvalidBindingsUnknownReferenceAndRetiredDefinitionsCannotBePinned()
    {
        using var fixture = new Fixture(); using var service = new AchievementFavoriteService(fixture.Root);
        Assert.Throws<InvalidDataException>(() => service.Get(new("..", fixture.Binding.Revision)));
        Assert.Throws<InvalidDataException>(() => service.Toggle(new(fixture.Binding.ProfileId, ".."), First));
        foreach (var id in new[] { 0, -1, 999999999, 100030000, Retired, 510340100 })
            Assert.Throws<ArgumentOutOfRangeException>(() => service.Toggle(fixture.Binding, id));
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public void RetiredAndUnresolvedPinsRemainStoredButAreNotEffective()
    {
        using var fixture = new Fixture();
        fixture.Write([Retired, 999999999, First]); var original = File.ReadAllBytes(fixture.File);
        using var service = new AchievementFavoriteService(fixture.Root);
        Assert.Equal(new[] { First }, service.Get(fixture.Binding).FamilyIds);
        Assert.Equal(original, File.ReadAllBytes(fixture.File));
        service.Toggle(fixture.Binding, Second);
        var file = JsonNode.Parse(File.ReadAllText(fixture.File))!;
        Assert.Equal(new[] { Retired, 999999999, First, Second }, file["familyIds"]!.AsArray().Select(node => node!.GetValue<int>()));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("binding")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-property")]
    [InlineData("malformed")]
    public void UnreadableOrForeignFilesAreNeverSilentlyOverwritten(string variant)
    {
        using var fixture = new Fixture(); fixture.Write([First]);
        var node = JsonNode.Parse(File.ReadAllText(fixture.File))!;
        switch (variant)
        {
            case "schema": node["schemaVersion"] = 3; break;
            case "binding": node["binding"]!["revision"] = Guid.NewGuid().ToString("N"); break;
            case "duplicate-id": node["familyIds"]!.AsArray().Add(First); break;
        }
        var text = variant switch { "malformed" => "{", "duplicate-property" =>
            "{\"schemaVersion\":1," + node.ToJsonString()[1..], _ => node.ToJsonString() };
        File.WriteAllText(fixture.File, text); var bytes = File.ReadAllBytes(fixture.File);
        using var service = new AchievementFavoriteService(fixture.Root);
        Assert.Empty(service.Get(fixture.Binding).FamilyIds);
        Assert.Throws<InvalidDataException>(() => service.Toggle(fixture.Binding, Second));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.File));
    }

    [Fact]
    public void FailedAtomicSaveDoesNotPublishAnUnsavedPreference()
    {
        using var fixture = new Fixture();
        using (var initial = new AchievementFavoriteService(fixture.Root)) initial.Toggle(fixture.Binding, First);
        var bytes = File.ReadAllBytes(fixture.File);
        using var failing = new AchievementFavoriteService(fixture.Root, null, AchievementDefinitionCatalog.Default,
            (_, _) => throw new IOException("Synthetic favorite save failure."));
        var changed = 0; failing.Changed += () => changed++;
        Assert.Throws<IOException>(() => failing.Toggle(fixture.Binding, Second));
        Assert.Equal(0, changed); Assert.Equal(new[] { First }, failing.Get(fixture.Binding).FamilyIds);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.File));
    }

    [Fact]
    public void ProfileDeletionInvalidatesPinsAndBlocksDelayedRecreationAcrossServiceInstances()
    {
        using var fixture = new Fixture();
        var profiles = new ExplorationService(Path.Combine(fixture.Base, "profiles.json"), []);
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var owner = new OwnCharacterContext(new(101, 7, 42, "Synthetic owner"), 1);
        profiles.BindNetworkContext(new(owner.Identity, owner.Generation, 1, at));
        var binding = profiles.GetAchievementBinding(owner)!;
        using var service = new AchievementFavoriteService(fixture.Root, profiles);
        service.Toggle(binding, First);
        service.ToggleTitle(binding, Title);
        Assert.True(profiles.Remove(binding.ProfileId));
        Assert.Empty(service.Get(binding).FamilyIds);
        Assert.Empty(service.Get(binding).TitleIds);
        Assert.Throws<InvalidOperationException>(() => service.Toggle(binding, First));
        Assert.Throws<InvalidOperationException>(() => service.ToggleTitle(binding, Title));
        using var restarted = new AchievementFavoriteService(fixture.Root);
        Assert.Null(restarted.Get(binding).Binding);
        Assert.Throws<InvalidOperationException>(() => restarted.Toggle(binding, First));
        Assert.Throws<InvalidOperationException>(() => restarted.ToggleTitle(binding, Title));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, binding.ProfileId)));
    }

    [Fact]
    public void ContentPolicyCoversExactlyThe22VersionBoundRetiredFamilies()
    {
        var retired = AchievementDefinitionCatalog.Default.Definitions.Where(definition =>
            AchievementContentPolicy.IsRetired(definition.Id, AchievementContentPolicy.MetadataBuild)).Select(definition => definition.Id).ToArray();
        Assert.Equal(22, retired.Length);
        Assert.Contains(510340100, retired); Assert.Contains(520340100, retired);
        Assert.All(retired, id => Assert.False(AchievementContentPolicy.IsRetired(id, AchievementContentPolicy.MetadataBuild + 1)));
        Assert.False(AchievementContentPolicy.IsRetired(First, AchievementContentPolicy.MetadataBuild));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Base { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-achievement-favorites-" + Guid.NewGuid().ToString("N"));
        internal string Root => Path.Combine(Base, "favorites");
        internal AchievementProfileBinding Binding { get; } = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        internal string File => Path.Combine(Root, Binding.ProfileId, Binding.Revision + ".json");
        internal void Write(int[] ids)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(new { schemaVersion = 1, binding = Binding, familyIds = ids },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        internal void WriteCurrent(int[] families, int[] titles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(new { schemaVersion = 2, binding = Binding,
                familyIds = families, titleIds = titles }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        public void Dispose()
        {
            var path = Path.GetFullPath(Base);
            Assert.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
