using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class GlobalPetProgressMigrationTests
{
    private const int Number = 1135;
    private static readonly DateTimeOffset At = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
    private static readonly PetIdEntry Pet = PetIdTable.Find(Number)!;
    private static string Id => Pet.CatalogId!;
    private static PetProgressContext Context(long sequence) => new(1, sequence, At.AddSeconds(sequence));

    [Fact]
    public void ActiveLegacyServerIsCopiedExactlyWithoutSummingAndAllOldCollectionsRemainArchived()
    {
        using var f = new Folder();
        var original = new ProgressProfile { SchemaVersion = 2, ServerId = 501, ActiveServerId = 502,
            Pets = new() { [Id] = new() { Souls = 90 } },
            Servers = new()
            {
                [502] = new() { ServerId = 502, NetworkBaselineAt = At,
                    Pets = new()
                    {
                        [Id] = new() { Souls = 42, Focus = true, UpdatedAt = At, Source = "synthetic-manual",
                            ManualOverride = true, Freshness = ProgressFreshness.Fresh },
                        ["synthetic-unmapped"] = new() { Souls = 17, Focus = true },
                    },
                    NetworkPets = new()
                    {
                        [Number] = new() { Level = 2, AvailableSouls = 1, EarnedTotal = 112,
                            LevelObserved = true, BalanceObserved = true, HasBaseline = true },
                        [999_999] = new() { Level = 1, AvailableSouls = 9, EarnedTotal = 14 },
                    } },
                [503] = new() { ServerId = 503, Pets = new() { [Id] = new() { Souls = 105 } } },
            } };
        var node = JsonNode.Parse(JsonSerializer.Serialize(original))!;
        node["SyntheticFutureField"] = new JsonObject { ["value"] = 27 };
        node["Servers"]!["502"]!["SyntheticNestedField"] = "retained";
        var bytes = Encoding.UTF8.GetBytes(" \r\n" + node.ToJsonString(new() { WriteIndented = true }) + "\r\n");
        File.WriteAllBytes(f.Path, bytes);

        var progress = f.Load();
        Assert.Equal(bytes, File.ReadAllBytes(f.Path));
        Assert.Empty(f.Backups());
        Assert.Null(progress.ActiveServerId);
        Assert.Equal(42, progress.Souls(Id));
        Assert.Equal(17, progress.Souls("synthetic-unmapped"));
        Assert.True(progress.IsFocus(Id));
        var entry = progress.EntrySnapshot(Id);
        Assert.True(entry.ManualOverride);
        Assert.Equal(At, entry.UpdatedAt);
        Assert.Equal("synthetic-manual", entry.Source);
        Assert.Equal(ProgressFreshness.Stale, entry.Freshness);

        Assert.True(progress.BindPetSession(Context(0)));
        var saved = JsonFile.Load<ProgressProfile>(f.Path);
        Assert.Equal(3, saved.SchemaVersion);
        Assert.Null(saved.ServerId);
        Assert.Null(saved.ActiveServerId);
        Assert.Empty(saved.Servers);
        Assert.Equal(42, saved.Pets[Id].Souls);
        Assert.Equal(2, saved.NetworkPets[Number].Level);
        Assert.Equal(1, saved.NetworkPets[Number].AvailableSouls);
        Assert.Equal(112, saved.NetworkPets[Number].EarnedTotal);
        Assert.True(saved.NetworkPets[Number].LevelObserved);
        Assert.True(saved.NetworkPets[Number].BalanceObserved);
        Assert.False(saved.NetworkPets[Number].HasBaseline);
        Assert.Equal(14, saved.NetworkPets[999_999].EarnedTotal);
        Assert.Equal(At, saved.NetworkBaselineAt);
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(f.Backups())));

        var archive = Assert.IsType<JsonElement>(saved.LegacyServerSnapshot);
        Assert.Equal(90, archive.GetProperty("pets").GetProperty(Id).GetProperty("souls").GetInt32());
        Assert.Equal(105, archive.GetProperty("Servers").GetProperty("503").GetProperty("pets").GetProperty(Id).GetProperty("souls").GetInt32());
        Assert.True(archive.GetProperty("Servers").GetProperty("502").GetProperty("NetworkPets").GetProperty(Number.ToString(CultureInfo.InvariantCulture)).GetProperty("HasBaseline").GetBoolean());
        Assert.Equal(27, archive.GetProperty("SyntheticFutureField").GetProperty("value").GetInt32());
        Assert.Equal("retained", archive.GetProperty("Servers").GetProperty("502").GetProperty("SyntheticNestedField").GetString());

        progress.ToggleFocus(Id);
        Assert.True(progress.ApplyPetGain([new(Number, 2)], Context(1)));
        saved = JsonFile.Load<ProgressProfile>(f.Path);
        Assert.Equal(44, saved.Pets[Id].Souls);
        Assert.False(saved.Pets[Id].Focus);
        Assert.Equal(114, saved.NetworkPets[Number].EarnedTotal);
        Assert.Equal(archive.GetRawText(), saved.LegacyServerSnapshot!.Value.GetRawText());
        Assert.Single(f.Backups());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(999)]
    [InlineData(501)]
    public void MissingActiveServerOrRootSelectionUsesOnlyTheRootCollection(int? active)
    {
        using var f = new Folder();
        JsonFile.Save(f.Path, new ProgressProfile { SchemaVersion = 2, ServerId = 501, ActiveServerId = active,
            Pets = new() { [Id] = new() { Souls = 7, Focus = true } },
            Servers = new() { [501] = new() { ServerId = 501, Pets = new() { [Id] = new() { Souls = 88 } } },
                [502] = new() { ServerId = 502, Pets = new() { [Id] = new() { Souls = 105 } } } } });
        var progress = f.Load();
        Assert.Equal(7, progress.Souls(Id));
        Assert.True(progress.IsFocus(Id));
        progress.BindPetSession(Context(0));
        var archive = JsonFile.Load<ProgressProfile>(f.Path).LegacyServerSnapshot!.Value;
        Assert.Equal(7, archive.GetProperty("pets").GetProperty(Id).GetProperty("souls").GetInt32());
        Assert.Equal(88, archive.GetProperty("Servers").GetProperty("501").GetProperty("pets").GetProperty(Id).GetProperty("souls").GetInt32());
    }

    [Fact]
    public void AnEmptyActiveCollectionDoesNotBorrowAFormerServersNonzeroCounts()
    {
        using var f = new Folder();
        JsonFile.Save(f.Path, new ProgressProfile { SchemaVersion = 2, ServerId = 501, ActiveServerId = 502,
            Pets = new() { [Id] = new() { Souls = 90 } }, Servers = new() { [502] = new() { ServerId = 502 } } });
        var progress = f.Load();
        Assert.Equal(0, progress.Souls(Id));
        Assert.True(progress.BindPetSession(Context(0)));
        Assert.True(progress.ApplyPetGain([new(Number, 1)], Context(1)));
        Assert.Equal(1, progress.Souls(Id));
        Assert.Equal(1, f.Load().Souls(Id));
        Assert.Equal(90, JsonFile.Load<ProgressProfile>(f.Path).LegacyServerSnapshot!.Value
            .GetProperty("pets").GetProperty(Id).GetProperty("souls").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TheFirstManualWriteBacksUpEveryPreGlobalSchemaBeforeChangingIt(int schema)
    {
        using var f = new Folder();
        var original = new ProgressProfile { SchemaVersion = schema, Pets = new() { [Id] = new() { Souls = 8, Focus = true } } };
        var bytes = Encoding.UTF8.GetBytes("\r\n  " + JsonSerializer.Serialize(original) + "\r\n");
        File.WriteAllBytes(f.Path, bytes);
        var progress = f.Load();
        Assert.Empty(f.Backups());
        Assert.Equal(bytes, File.ReadAllBytes(f.Path));
        progress.SetSouls(Id, 12);
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(f.Backups())));
        var saved = JsonFile.Load<ProgressProfile>(f.Path);
        Assert.Equal(3, saved.SchemaVersion);
        Assert.Equal(12, saved.Pets[Id].Souls);
        Assert.True(saved.Pets[Id].Focus);
        Assert.True(saved.Pets[Id].ManualOverride);
        Assert.Equal(8, saved.LegacyServerSnapshot!.Value.GetProperty("pets").GetProperty(Id).GetProperty("souls").GetInt32());
    }

    [Fact]
    public void ReloadingSchemaThreeNeverSelectsArchivedServersOrCreatesAnotherMigrationBackup()
    {
        using var f = new Folder();
        JsonFile.Save(f.Path, new ProgressProfile { SchemaVersion = 2, ServerId = 501, ActiveServerId = 502,
            Pets = new() { [Id] = new() { Souls = 105 } },
            Servers = new() { [502] = new() { ServerId = 502, Pets = new() { [Id] = new() { Souls = 8 } } } } });
        var progress = f.Load();
        progress.SetSouls(Id, 0);
        var archive = JsonFile.Load<ProgressProfile>(f.Path).LegacyServerSnapshot!.Value.GetRawText();
        var reloaded = f.Load();
        Assert.Equal(0, reloaded.Souls(Id));
        Assert.Null(reloaded.ActiveServerId);
        Assert.True(reloaded.BindPetSession(Context(0)));
        Assert.True(reloaded.ApplyPetGain([new(Number, 2)], Context(1)));
        Assert.Equal(2, reloaded.Souls(Id));
        Assert.Equal(archive, JsonFile.Load<ProgressProfile>(f.Path).LegacyServerSnapshot!.Value.GetRawText());
        Assert.Single(f.Backups());
    }

    [Fact]
    public void ANewProfileNeedsNeitherLegacyArchiveNorMigrationBackup()
    {
        using var f = new Folder();
        var progress = f.Load();
        progress.BindPetSession(Context(0));
        progress.ApplyPetGain([new(Number, 1)], Context(1));
        var saved = JsonFile.Load<ProgressProfile>(f.Path);
        Assert.Equal(3, saved.SchemaVersion);
        Assert.Null(saved.LegacyServerSnapshot);
        Assert.Empty(saved.Servers);
        Assert.Empty(f.Backups());
    }

    private sealed class Folder : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-global-migration-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Root, "progress.json");
        private readonly PetCatalog _catalog = new([new PetDefinition { Id = Id, En = Pet.En, Genus = Pet.Genus,
            NumericId = Number.ToString(CultureInfo.InvariantCulture) }]);
        internal Folder() => Directory.CreateDirectory(Root);
        internal ProgressService Load() => new(Path, _catalog);
        internal string[] Backups() => Directory.GetFiles(Root, "progress.json.pre-network-*.bak");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
