using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayAbyssBoundKinaMigrationTests
{
    [Fact]
    public void ExistingAbyssWidgetsAreReplacedWithAnExactBackupAndTheirDesignIntact()
    {
        using var fixture = new Fixture();
        var before = Collection(Window("renamed", "Custom", [OverlayTriggerArea.Abyss]),
            Window("world", "Abyss", [OverlayTriggerArea.OpenWorld]));
        fixture.WriteLegacy(before);
        var original = File.ReadAllBytes(fixture.File);
        var normalized = new OverlaySettingsStore(fixture.Directory).Load();
        using var service = fixture.Create();
        Assert.Null(service.LoadError);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(fixture.Backups)));
        var expected = normalized.Overlays[0] with
        {
            Settings = normalized.Overlays[0].Settings with
            {
                Widgets = normalized.Overlays[0].Settings.Widgets.Select(widget => widget with
                {
                    Kind = widget.Kind switch
                    {
                        "dropped-kina" => "bound-kina",
                        "dropped-kina-hour" => "bound-kina-hour",
                        _ => widget.Kind,
                    },
                }).ToArray(),
            },
        };
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(service.Overlays[0]));
        Assert.Equal(JsonSerializer.Serialize(normalized.Overlays[1]), JsonSerializer.Serialize(service.Overlays[1]));
        Assert.Equal(before.SelectedOverlayId, service.SelectedOverlayId);
        var saved = new OverlaySettingsStore(fixture.Directory).Load();
        Assert.Equal(2, saved.Version);
        Assert.Equal(1, saved.AbyssBoundKinaMigrationVersion);
        Assert.Equal(JsonSerializer.Serialize(service.Overlays), JsonSerializer.Serialize(saved.Overlays));
    }

    [Fact]
    public async Task ReloadAndRestartDoNotRewriteTheMigratedFileOrRepeatTheBackup()
    {
        using var fixture = new Fixture();
        fixture.WriteLegacy(Collection(Window("one", "Custom", [OverlayTriggerArea.Abyss])));
        using var service = fixture.Create();
        Assert.Null(service.LoadError);
        var migrated = File.ReadAllBytes(fixture.File);
        var writeTime = File.GetLastWriteTimeUtc(fixture.File);
        Assert.True((await service.ReloadAsync()).Succeeded);
        using var reopened = fixture.Create();
        Assert.Null(reopened.LoadError);
        Assert.Equal(migrated, File.ReadAllBytes(fixture.File));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(fixture.File));
        Assert.Single(fixture.Backups);
    }

    [Fact]
    public async Task AnIntentionalLaterUnboundChoiceSurvivesRestart()
    {
        using var fixture = new Fixture();
        fixture.WriteLegacy(Collection(Window("one", "Custom", [OverlayTriggerArea.Abyss])));
        using var service = fixture.Create();
        var selected = service.Settings;
        Assert.True((await service.SaveAsync(selected with
        {
            Widgets = selected.Widgets.Select(widget => widget.Kind == "bound-kina"
                ? widget with { Kind = "dropped-kina" } : widget).ToArray(),
        })).Succeeded);
        var saved = File.ReadAllBytes(fixture.File);
        using var reopened = fixture.Create();
        Assert.Null(reopened.LoadError);
        Assert.Contains(reopened.Settings.Widgets, widget => widget.Kind == "dropped-kina");
        Assert.Equal(saved, File.ReadAllBytes(fixture.File));
        Assert.Single(fixture.Backups);
    }

    [Fact]
    public async Task EmptyUnaffectedLayoutsAreStampedOnlyOnARegularSave()
    {
        using var fixture = new Fixture();
        var window = Window("one", "Custom", [OverlayTriggerArea.Abyss]) with
        {
            Settings = new() { TriggerRules = new() { Areas = [OverlayTriggerArea.Abyss] }, Widgets = [] },
        };
        fixture.WriteLegacy(Collection(window));
        var original = File.ReadAllBytes(fixture.File);
        using var service = fixture.Create();
        Assert.Null(service.LoadError);
        Assert.Empty(service.Settings.Widgets);
        Assert.Equal(original, File.ReadAllBytes(fixture.File));
        Assert.Empty(fixture.Backups);
        Assert.True((await service.ReloadAsync()).Succeeded);
        Assert.Equal(original, File.ReadAllBytes(fixture.File));
        Assert.True((await service.SaveAsync(service.Settings with
        {
            Widgets = [OverlayCatalog.CreateWidget("dropped-kina")],
        })).Succeeded);
        using var reopened = fixture.Create();
        Assert.Null(reopened.LoadError);
        Assert.Equal("dropped-kina", Assert.Single(reopened.Settings.Widgets).Kind);
        Assert.Equal(1, new OverlaySettingsStore(fixture.Directory).Load().AbyssBoundKinaMigrationVersion);
        Assert.Empty(fixture.Backups);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MixedUnspecifiedAndOpenWorldLayoutsAreNotReinterpreted(bool mixed, bool openWorld)
    {
        using var fixture = new Fixture();
        OverlayTriggerArea[] areas = mixed ? [OverlayTriggerArea.Abyss, OverlayTriggerArea.OpenWorld]
            : openWorld ? [OverlayTriggerArea.OpenWorld] : [];
        fixture.WriteLegacy(Collection(Window("builtin-abyss", "Abyss", areas)));
        var original = File.ReadAllBytes(fixture.File);
        using var service = fixture.Create();
        Assert.Null(service.LoadError);
        Assert.Contains(service.Settings.Widgets, widget => widget.Kind == "dropped-kina");
        Assert.True((await service.ReloadAsync()).Succeeded);
        Assert.Equal(original, File.ReadAllBytes(fixture.File));
        Assert.Empty(fixture.Backups);
    }

    [Fact]
    public async Task MissingLayoutsRemainReadOnlyAndStartAtTheCurrentMigrationVersion()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        Assert.Null(service.LoadError);
        Assert.False(System.IO.Directory.Exists(fixture.Directory));
        Assert.True((await service.ReloadAsync()).Succeeded);
        Assert.False(System.IO.Directory.Exists(fixture.Directory));
        Assert.True((await service.SavePositionAsync(.2, .3)).Succeeded);
        Assert.Equal(1, new OverlaySettingsStore(fixture.Directory).Load().AbyssBoundKinaMigrationVersion);
        Assert.Empty(fixture.Backups);
    }

    [Fact]
    public async Task InvalidFileRemainsProtectedWithoutBackupOrMigration()
    {
        using var fixture = new Fixture();
        System.IO.Directory.CreateDirectory(fixture.Directory);
        File.WriteAllText(fixture.File, "{ invalid layout");
        var original = File.ReadAllBytes(fixture.File);
        using var service = fixture.Create();
        Assert.NotNull(service.LoadError);
        Assert.False((await service.SavePositionAsync(.2, .3)).Succeeded);
        Assert.False((await service.ReloadAsync()).Succeeded);
        Assert.Equal(original, File.ReadAllBytes(fixture.File));
        Assert.Empty(fixture.Backups);
    }

    [Fact]
    public void NegativeMigrationVersionIsRejectedWithoutChangingTheFile()
    {
        using var fixture = new Fixture();
        var before = Collection(Window("one", "Custom", [OverlayTriggerArea.Abyss])) with
        {
            AbyssBoundKinaMigrationVersion = -1,
        };
        new OverlaySettingsStore(fixture.Directory).Save(before);
        var original = File.ReadAllBytes(fixture.File);
        using var service = fixture.Create();
        Assert.NotNull(service.LoadError);
        Assert.Equal(original, File.ReadAllBytes(fixture.File));
        Assert.Empty(fixture.Backups);
    }

    [Fact]
    public async Task ExistingServiceUsesTheSameMigrationWhenReloadingOrSaving()
    {
        using var reloadFixture = new Fixture();
        using var saveFixture = new Fixture();
        using var reloader = reloadFixture.Create();
        using var saver = saveFixture.Create();
        reloadFixture.WriteLegacy(Collection(Window("one", "Custom", [OverlayTriggerArea.Abyss])));
        saveFixture.WriteLegacy(Collection(Window("default", "Custom", [OverlayTriggerArea.Abyss])));
        Assert.True((await reloader.ReloadAsync()).Succeeded);
        Assert.True((await saver.SavePositionAsync(.4, .5)).Succeeded);
        Assert.Contains(reloader.Settings.Widgets, widget => widget.Kind == "bound-kina");
        Assert.Contains(saver.Settings.Widgets, widget => widget.Kind == "bound-kina");
        Assert.Equal((.4, .5), (saver.Settings.PositionX, saver.Settings.PositionY));
        Assert.Single(reloadFixture.Backups);
        Assert.Single(saveFixture.Backups);
    }

    [Fact]
    public async Task ConcurrentServiceInstancesCommitOnlyOneMigrationAndBackup()
    {
        using var fixture = new Fixture();
        fixture.WriteLegacy(Collection(Window("one", "Custom", [OverlayTriggerArea.Abyss])));
        var services = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(fixture.Create)));
        try
        {
            Assert.All(services, service =>
            {
                Assert.Null(service.LoadError);
                Assert.Contains(service.Settings.Widgets, widget => widget.Kind == "bound-kina");
            });
            Assert.Single(fixture.Backups);
        }
        finally { foreach (var service in services) service.Dispose(); }
    }

    [Fact]
    public async Task FailedAtomicSaveRetainsTheOriginalFileAndDoesNotExposeMigratedWidgets()
    {
        using var fixture = new Fixture();
        fixture.WriteLegacy(Collection(Window("one", "Custom", [OverlayTriggerArea.Abyss])));
        var original = File.ReadAllBytes(fixture.File);
        using (var held = new FileStream(fixture.File, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var service = fixture.Create())
        {
            Assert.NotNull(service.LoadError);
            Assert.DoesNotContain(service.Overlays.SelectMany(window => window.Settings.Widgets), widget => widget.Kind == "bound-kina");
            Assert.False((await service.SavePositionAsync(.2, .3)).Succeeded);
            Assert.Equal(original, File.ReadAllBytes(fixture.File));
            Assert.Equal(original, File.ReadAllBytes(Assert.Single(fixture.Backups)));
            Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.tmp"));
        }
        using var retried = fixture.Create();
        Assert.Null(retried.LoadError);
        Assert.Contains(retried.Settings.Widgets, widget => widget.Kind == "bound-kina");
    }

    [Fact]
    public void FailedBackupKeepsTheOriginalFileWithoutExposingMigratedWidgets()
    {
        using var fixture = new Fixture();
        fixture.WriteLegacy(Collection(Window("one", "Custom", [OverlayTriggerArea.Abyss])));
        var original = File.ReadAllBytes(fixture.File);
        var directory = new DirectoryInfo(fixture.Directory);
        var originalPermissions = directory.GetAccessControl(AccessControlSections.Access);
        var deniedPermissions = directory.GetAccessControl(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        deniedPermissions.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.CreateFiles, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(deniedPermissions);
            using var service = fixture.Create();
            Assert.NotNull(service.LoadError);
            Assert.DoesNotContain(service.Overlays.SelectMany(window => window.Settings.Widgets), widget => widget.Kind == "bound-kina");
            Assert.Equal(original, File.ReadAllBytes(fixture.File));
            Assert.Empty(fixture.Backups);
        }
        finally { directory.SetAccessControl(originalPermissions); }
    }

    [Fact]
    public void PersonalTemplatesRemainByteIdenticalDuringWindowMigration()
    {
        using var fixture = new Fixture();
        var window = Window("one", "Custom", [OverlayTriggerArea.Abyss]);
        fixture.WriteLegacy(Collection(window));
        new OverlayTemplateStore(fixture.Directory).Save([OverlayTemplate.Create("Abyss", window.Settings)]);
        var original = File.ReadAllBytes(fixture.TemplateFile);
        using var service = fixture.Create();
        Assert.Null(service.LoadError);
        Assert.Null(service.TemplateError);
        Assert.Equal(original, File.ReadAllBytes(fixture.TemplateFile));
        Assert.Contains(Assert.Single(service.Templates).Layout.Widgets, widget => widget.Kind == "dropped-kina");
    }

    private static OverlayCollectionSettings Collection(params OverlayInstance[] windows) => new()
    {
        SelectedOverlayId = windows[0].Id, Overlays = windows, OverlaysVisible = false,
    };

    private static OverlayInstance Window(string id, string name, OverlayTriggerArea[] areas) => new()
    {
        Id = id, Name = name,
        Settings = new()
        {
            Enabled = true, Width = 512, Height = 344, PositionX = .31, PositionY = .42,
            Scale = 1.3, BackgroundOpacity = .7, SnapToGrid = false, ShowBorder = false,
            AutoAlign = false, CaptureExcluded = false, Visibility = "always", Interaction = "locked",
            TriggerRules = new() { Areas = areas, SessionActive = OverlayTriggerRequirement.Required },
            Widgets = [
                OverlayCatalog.CreateWidget("dropped-kina", 17, 23) with
                {
                    Width = 183, Height = 79, ContentWidth = 168, ContentHeight = 72,
                    FontScale = 1.4, ShowIcon = false, ShowLabel = false,
                },
                OverlayCatalog.CreateWidget("dropped-kina-hour", 203, 23) with
                {
                    Width = 187, Height = 84, ContentWidth = 168, ContentHeight = 72,
                    FontScale = 1.2, ShowLabel = true,
                },
                OverlayCatalog.CreateWidget("clock", 11, 135),
            ],
        },
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Soulcrest-overlay-bound-migration-" + Guid.NewGuid().ToString("N"));
        internal string Directory => Path.Combine(_root, "overlay-editor");
        internal string File => Path.Combine(Directory, "overlay.json");
        internal string TemplateFile => Path.Combine(Directory, "overlay-templates.json");
        internal string[] Backups => System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.GetFiles(Directory, "overlay.before-bound-kina-*.bak") : [];
        internal OverlayService Create() => new(Directory, new OverlayCollectionSettings());
        internal void WriteLegacy(OverlayCollectionSettings collection)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var json = JsonNode.Parse(JsonSerializer.Serialize(collection))!.AsObject();
            json.Remove(nameof(OverlayCollectionSettings.AbyssBoundKinaMigrationVersion));
            System.IO.File.WriteAllText(File, json.ToJsonString(new() { WriteIndented = true }));
        }
        public void Dispose() { if (System.IO.Directory.Exists(_root)) System.IO.Directory.Delete(_root, recursive: true); }
    }
}
