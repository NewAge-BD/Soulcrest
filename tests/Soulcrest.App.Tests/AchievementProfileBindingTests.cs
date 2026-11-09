using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementProfileBindingTests
{
    [Fact]
    public void UnboundNameAndActiveProfileCannotAuthorizeAnAchievementOwner()
    {
        using var fixture = new Fixture();
        fixture.Service.Rename("Synthetic owner");
        var owner = Owner(101, 7, 1);
        Assert.Null(fixture.Service.GetAchievementBinding(owner));
        Assert.Null(fixture.Service.TryGetAchievementBinding());
        Assert.Single(fixture.Service.Characters);
    }

    [Fact]
    public void RenameAndRestartPreserveTheOpaqueNativeAssignment()
    {
        using var fixture = new Fixture();
        var owner = Owner(101, 7, 1);
        Bind(fixture.Service, owner);
        var initial = fixture.Service.GetAchievementBinding(owner);
        Assert.NotNull(initial);
        fixture.Service.Rename("Synthetic renamed profile");
        Assert.Equal(initial, fixture.Service.GetAchievementBinding(owner));
        var restarted = new ExplorationService(fixture.Path, []);
        Assert.Equal(initial, restarted.TryGetAchievementBinding(initial.ProfileId));
        Bind(restarted, owner with { Generation = 2 });
        Assert.Equal(initial, restarted.GetAchievementBinding(owner with { Generation = 2 }));
    }

    [Fact]
    public void AcceptedOldObservationResolvesItsOwnProfileAfterCharacterSwitch()
    {
        using var fixture = new Fixture();
        var first = Owner(101, 7, 1);
        Bind(fixture.Service, first);
        var firstBinding = fixture.Service.GetAchievementBinding(first);
        var second = Owner(202, 7, 2);
        Bind(fixture.Service, second);
        var secondBinding = fixture.Service.GetAchievementBinding(second);
        Assert.NotNull(firstBinding);
        Assert.NotNull(secondBinding);
        Assert.NotEqual(firstBinding.ProfileId, secondBinding.ProfileId);
        Assert.Equal(firstBinding, fixture.Service.GetAchievementBinding(first));
        fixture.Service.Select(firstBinding.ProfileId);
        Assert.Equal(secondBinding, fixture.Service.GetAchievementBinding(second));
    }

    [Fact]
    public void SameNativeNumberOnDifferentServersDoesNotShareHistory()
    {
        using var fixture = new Fixture();
        var first = Owner(101, 7, 1);
        Bind(fixture.Service, first);
        var firstBinding = fixture.Service.GetAchievementBinding(first);
        var second = Owner(101, 8, 2);
        Bind(fixture.Service, second);
        var secondBinding = fixture.Service.GetAchievementBinding(second);
        Assert.NotNull(firstBinding);
        Assert.NotNull(secondBinding);
        Assert.NotEqual(firstBinding.ProfileId, secondBinding.ProfileId);
        Assert.NotEqual(firstBinding.Revision, secondBinding.Revision);
    }

    [Fact]
    public void ChangingTheNativeAssignmentOpensANewRevisionOfTheSameProfile()
    {
        using var fixture = new Fixture();
        var first = Owner(101, 7, 1);
        Bind(fixture.Service, first);
        var old = fixture.Service.GetAchievementBinding(first)!;
        var profiles = JsonFile.Load<ExplorationProfiles>(fixture.Path);
        var profile = profiles.Characters.Single(character => character.Id == old.ProfileId);
        profile.NetworkCharacterId = 202;
        JsonFile.Save(fixture.Path, profiles);
        var restarted = new ExplorationService(fixture.Path, []);
        Assert.Null(restarted.TryGetAchievementBinding(old.ProfileId));
        var replacement = Owner(202, 7, 2);
        Bind(restarted, replacement);
        var current = restarted.GetAchievementBinding(replacement);
        Assert.NotNull(current);
        Assert.Equal(old.ProfileId, current.ProfileId);
        Assert.NotEqual(old.Revision, current.Revision);
        Assert.Null(restarted.GetAchievementBinding(first));
    }

    [Fact]
    public void DuplicateNativeMappingsAreAmbiguousInsteadOfUsingTheActiveProfile()
    {
        using var fixture = new Fixture();
        var owner = Owner(101, 7, 1);
        Bind(fixture.Service, owner);
        var profiles = JsonFile.Load<ExplorationProfiles>(fixture.Path);
        profiles.Characters.Add(new CharacterExploration
        {
            Name = "Synthetic duplicate",
            NetworkCharacterId = owner.Identity.CharacterId,
            ServerId = owner.Identity.ServerId,
        });
        JsonFile.Save(fixture.Path, profiles);
        var restarted = new ExplorationService(fixture.Path, []);
        Assert.Null(restarted.GetAchievementBinding(owner));
    }

    [Fact]
    public void RemovingAProfileInvalidatesQueuedHistoryAndDoesNotRecreateTheOldGuid()
    {
        using var fixture = new Fixture();
        var owner = Owner(101, 7, 1);
        Bind(fixture.Service, owner);
        var binding = fixture.Service.GetAchievementBinding(owner)!;
        var removed = new List<string>();
        fixture.Service.CharacterRemoved += removed.Add;
        Assert.True(fixture.Service.Remove(binding.ProfileId));
        Assert.Equal([binding.ProfileId], removed);
        Assert.Null(fixture.Service.GetAchievementBinding(owner));
        Assert.DoesNotContain(fixture.Service.Characters, character => character.Id == binding.ProfileId);
        Assert.False(fixture.Service.Remove(binding.ProfileId));
        Bind(fixture.Service, owner with { Generation = 2 });
        Assert.NotEqual(binding.ProfileId, fixture.Service.GetAchievementBinding(owner with { Generation = 2 })!.ProfileId);
    }

    [Fact]
    public void OldProfilesWithoutAchievementMetadataRemainCompatibleAndInitiallyUnknown()
    {
        using var fixture = new Fixture();
        var owner = Owner(101, 7, 1);
        var profile = new CharacterExploration { NetworkCharacterId = 101, ServerId = 7, Name = "Synthetic owner" };
        profile.RawQuests.Add(555, new(555, 1, 0, [], 0));
        JsonFile.Save(fixture.Path, new ExplorationProfiles { SchemaVersion = 2, Active = profile.Id, Characters = [profile] });
        var restarted = new ExplorationService(fixture.Path, []);
        var unchanged = File.ReadAllBytes(fixture.Path);
        Assert.Null(restarted.TryGetAchievementBinding());
        Assert.Null(restarted.GetAchievementBinding(owner));
        Assert.Equal(unchanged, File.ReadAllBytes(fixture.Path));
        Bind(restarted, owner);
        Assert.NotNull(restarted.TryGetAchievementBinding());
        Assert.True(JsonFile.Load<ExplorationProfiles>(fixture.Path).Characters.Single().RawQuests.ContainsKey(555));
    }

    private static OwnCharacterContext Owner(long id, int server, long generation) =>
        new(new(id, server, 42, "Synthetic owner"), generation);

    private static void Bind(ExplorationService service, OwnCharacterContext owner) => service.BindNetworkContext(
        new(owner.Identity, owner.Generation, 1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-achievement-binding-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "exploration.json");
        public ExplorationService Service { get; }
        public Fixture() => Service = new(Path, []);
        public void Dispose()
        {
            var actual = System.IO.Path.GetFullPath(Root);
            Assert.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), actual, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(actual)) Directory.Delete(actual, recursive: true);
        }
    }
}
