using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>User request 2026-10-06: after a loading screen the profile follows the logged-in character.</summary>
public sealed class CharacterDetectionTests
{
    [Fact]
    public void KnownNamesAreChosenAnUnknownOneTakesOverTheDefaultProfile()
    {
        var path = Path.Combine(Path.GetTempPath(), "soulcrest-characters-" + Guid.NewGuid() + ".json");
        try
        {
            var exploration = new ExplorationService(path, []);
            var original = exploration.ActiveId;             // "Character 1", holds progress made before
            exploration.Add("Zweit");

            Assert.Equal(CharacterChoice.Adopted, exploration.UseCharacter("Erst"));
            Assert.Equal(original, exploration.ActiveId);    // its progress stays with the character
            Assert.Equal(CharacterChoice.Selected, exploration.UseCharacter("zweit"));
            Assert.Equal(CharacterChoice.Unchanged, exploration.UseCharacter("Zweit"));
            Assert.Equal(CharacterChoice.Created, exploration.UseCharacter("Dritt"));
            Assert.Equal(["Erst", "Zweit", "Dritt"], exploration.Characters.Select(c => c.Name));
            Assert.Equal("Dritt", new ExplorationService(path, []).Characters.Single(c => c.Id == new ExplorationService(path, []).ActiveId).Name); // saved
        }
        finally { File.Delete(path); }
    }

    /// <summary>The user's capture of 2026-10-06 (login, second character, back, teleport), when present.</summary>
    [Fact]
    public void LocalCaptureFindsOneCharacterPerLoadingScreen()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest", "captures", "aion2-20261006-052927.pcapng");
        if (!File.Exists(file))
            return;
        var names = NetworkLootService.ReadCharacters(file, new HashSet<int> { 57117 });
        Assert.Equal(4, names.Count);
        Assert.Equal(2, names.Select(n => n.Name).Distinct().Count());
        Assert.Equal(names[0].Name, names[2].Name); // back to the first character
        Assert.Equal(names[2].Name, names[3].Name); // teleport keeps it
    }
}
