using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Kibelisk identities and per-character manually retained progress.</summary>
public sealed class KibeliskTests
{
    private static readonly ExplorationPlace[] Places =
    [
        new("altgard|kibelisk|Hill", "altgard", "kibelisk", 0, "Hill", null, 100, 100),
        new("altgard|kibelisk|Lake", "altgard", "kibelisk", 1, "Lake", null, 400, 100),
        new("altgard|dungeon|Cave", "altgard", "dungeon", 2, "Cave", null, 100, 110),
        new("verteron|kibelisk|Hill", "verteron", "kibelisk", 3, "Hill", null, 100, 100),
    ];

    [Fact]
    public void KibelisksSharingANameGetTheirOwnId()
    {
        var places = ExplorationService.UniqueKibelisks(
        [
            new("altgard|kibelisk|Post", "altgard", "kibelisk", 0, "Post", null, 5721, 5205),
            new("altgard|kibelisk|Post", "altgard", "kibelisk", 1, "Post", null, 5407, 3467),
            new("altgard|dungeon|Cave", "altgard", "dungeon", 2, "Cave", null, 1, 1),
            new("altgard|kibelisk|Hill", "altgard", "kibelisk", 3, "Hill", null, 9, 9),
        ]);
        Assert.Equal(["altgard|kibelisk|Post#2", "altgard|kibelisk|Post#1", "altgard|dungeon|Cave", "altgard|kibelisk|Hill"], places.Select(p => p.Id));
    }

    [Fact]
    public void ChecksOfTheOldTradingPostIdsMoveToTheNewNames()
    {
        var path = Path.Combine(Path.GetTempPath(), "soulcrest-kibelisk-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, """
                {"Active":"a","Characters":[{"Id":"a","Name":"Erfunden","Done":["altgard|kibelisk|Steel Hammer Temporary Trading Post#1","altgard|kibelisk|Steel Hammer Temporary Trading Post#2"]}]}
                """);
            ExplorationPlace[] places =
            [
                new("altgard|kibelisk|Shulak Street Stall", "altgard", "kibelisk", 0, "Shulak Street Stall", null, 5407, 3467),
                new("altgard|kibelisk|Steel Hammer Temporary Trading Post", "altgard", "kibelisk", 1, "Steel Hammer Temporary Trading Post", null, 5721, 5205),
            ];
            var exploration = new ExplorationService(path, places);
            Assert.Equal(["altgard|kibelisk|Shulak Street Stall", "altgard|kibelisk|Steel Hammer Temporary Trading Post"], exploration.Completed.Order());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CheckingOffIsPerCharacter()
    {
        var path = Path.Combine(Path.GetTempPath(), "soulcrest-kibelisk-" + Guid.NewGuid() + ".json");
        try
        {
            var exploration = new ExplorationService(path, Places);
            var first = exploration.ActiveId;
            exploration.SetDone(first, ["altgard|kibelisk|Hill"]);
            exploration.Add("Zweitcharakter");
            Assert.Empty(exploration.Completed);
            exploration.Select(first);
            Assert.Equal(["altgard|kibelisk|Hill"], exploration.Completed);
        }
        finally { File.Delete(path); }
    }
}
