using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LifeskillBindingSettingsTests : IDisposable
{
    private readonly byte[]? _settings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-lifeskill-binding-" + Guid.NewGuid().ToString("N"));
    private static OwnCharacterIdentity Identity(long character = 100, int server = 1, int entity = 10) =>
        new(character, server, entity, "ExampleRanger") { ClassId = "Ranger" };

    private ExplorationService Exploration()
    {
        Directory.CreateDirectory(_folder);
        return new(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".json"), []);
    }

    [Fact]
    public void BoundProfileIgnoresAnotherManuallySelectedCharacterAndFreezesStartValues()
    {
        var exploration = Exploration();
        var identity = Identity();
        exploration.BindNetworkContext(new(identity, 3, 1, DateTimeOffset.UtcNow));
        var boundId = exploration.ActiveId;
        var own = new OwnCharacterContext(identity, 3) { CombatScore = 1840, GearScore = 235 };
        var frozen = Assert.IsType<LootTrackerCharacterSnapshot>(exploration.TryGetBoundCharacter(own));
        exploration.Add("ExampleCleric");
        Assert.NotEqual(boundId, exploration.ActiveId);
        var selectedOther = Assert.IsType<LootTrackerCharacterSnapshot>(exploration.TryGetBoundCharacter(own));
        Assert.Equal(boundId, selectedOther.ProfileId);
        Assert.Equal("ExampleRanger", selectedOther.Name);
        exploration.Select(boundId);
        exploration.Rename("RenamedRanger");
        var later = Assert.IsType<LootTrackerCharacterSnapshot>(exploration.TryGetBoundCharacter(own with { CombatScore = 2000, GearScore = 250 }));
        Assert.Equal("ExampleRanger", frozen.Name);
        Assert.Equal(1840, frozen.CombatScore);
        Assert.Equal(235, frozen.GearScore);
        Assert.Equal("RenamedRanger", later.Name);
        Assert.Equal(2000, later.CombatScore);
        Assert.Equal(250, later.GearScore);
        Assert.Equal(boundId, later.ProfileId);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("character")]
    [InlineData("server")]
    [InlineData("entity")]
    [InlineData("name")]
    [InlineData("class")]
    public void DifferentOwnGenerationOrIdentityCannotBorrowTheBoundProfile(string mismatch)
    {
        var exploration = Exploration();
        var identity = Identity();
        exploration.BindNetworkContext(new(identity, 3, 1, DateTimeOffset.UtcNow));
        var expectedIdentity = mismatch switch
        {
            "character" => identity with { CharacterId = 200 },
            "server" => identity with { ServerId = 2 },
            "entity" => identity with { EntityKey = 20 },
            "name" => identity with { Name = "ExampleCleric" },
            "class" => identity with { ClassId = "Cleric" },
            _ => identity,
        };
        var own = new OwnCharacterContext(expectedIdentity, mismatch == "generation" ? 4 : 3)
            { CombatScore = 1840, GearScore = 235 };
        Assert.Null(exploration.TryGetBoundCharacter(own));
    }

    [Fact]
    public void StaleBindingAndCharacterSwitchRejectTheOldStartContextAndKeepUnknownScoresUnknown()
    {
        var exploration = Exploration();
        var identity = Identity();
        var old = new OwnCharacterContext(identity, 3) { CombatScore = 1840, GearScore = 235 };
        Assert.Null(exploration.TryGetBoundCharacter(old));
        exploration.BindNetworkContext(new(identity, 3, 1, DateTimeOffset.UtcNow));
        Assert.NotNull(exploration.TryGetBoundCharacter(old));
        exploration.MarkNetworkStale();
        Assert.Null(exploration.TryGetBoundCharacter(old));
        var other = Identity(200, 1, 20) with { Name = "ExampleCleric" };
        exploration.BindNetworkContext(new(other, 4, 1, DateTimeOffset.UtcNow));
        Assert.Null(exploration.TryGetBoundCharacter(old));
        var current = Assert.IsType<LootTrackerCharacterSnapshot>(exploration.TryGetBoundCharacter(new(other, 4)));
        Assert.Equal("ExampleCleric", current.Name);
        Assert.Null(current.CombatScore);
        Assert.Null(current.GearScore);
    }

    [Fact]
    public void LifeskillPreferenceDefaultsOffRoundTripsAndNotifiesOnlyAfterPersistence()
    {
        Assert.False(JsonSerializer.Deserialize<AppSettings>("{}")!.LifeskillTrackingEnabled);
        var settings = new SettingsService();
        settings.Current.LifeskillTrackingEnabled = false;
        var notifications = 0;
        settings.Changed += () =>
        {
            notifications++;
            Assert.Equal(settings.Current.LifeskillTrackingEnabled,
                JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile))!.LifeskillTrackingEnabled);
        };
        settings.SetLifeskillTrackingEnabled(true);
        Assert.True(new SettingsService().Current.LifeskillTrackingEnabled);
        Assert.Equal(1, notifications);
        settings.SetLifeskillTrackingEnabled(true);
        Assert.Equal(1, notifications);
        settings.SetLifeskillTrackingEnabled(false);
        Assert.False(settings.Current.LifeskillTrackingEnabled);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void LifeskillPreferenceWriteFailureRollsBackWithoutPublishingChange()
    {
        var settings = new SettingsService();
        settings.Current.LifeskillTrackingEnabled = false;
        var notifications = 0;
        settings.Changed += () => notifications++;
        File.Delete(AppPaths.SettingsFile);
        Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            Assert.NotNull(Record.Exception(() => settings.SetLifeskillTrackingEnabled(true)));
            Assert.False(settings.Current.LifeskillTrackingEnabled);
            Assert.Equal(0, notifications);
        }
        finally { Directory.Delete(AppPaths.SettingsFile); }
    }

    public void Dispose()
    {
        if (_settings is null) File.Delete(AppPaths.SettingsFile);
        else File.WriteAllBytes(AppPaths.SettingsFile, _settings);
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
