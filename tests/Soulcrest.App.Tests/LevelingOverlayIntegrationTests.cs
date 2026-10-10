using System.Reflection;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LevelingOverlayIntegrationTests : IDisposable
{
    private readonly Dictionary<string, byte[]?> _originalFiles = [];
    private readonly HashSet<string> _originalDefects;
    private readonly string _originalLanguage = UiText.Language;

    public LevelingOverlayIntegrationTests()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        _originalDefects = Directory.GetFiles(AppPaths.DataDirectory, "settings.json.defekt-*").ToHashSet();
        foreach (var path in new[]
        {
            AppPaths.SettingsFile, AppPaths.RoutesFile, AppPaths.TargetsFile,
            Path.Combine(AppPaths.DataDirectory, "exploration.json"),
            Path.Combine(AppPaths.DataDirectory, "leveling-progress.json")
        })
        {
            _originalFiles.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        foreach (var (path, contents) in _originalFiles)
        {
            if (contents is null) File.Delete(path);
            else File.WriteAllBytes(path, contents);
        }
        foreach (var path in Directory.GetFiles(AppPaths.DataDirectory, "settings.json.defekt-*"))
            if (!_originalDefects.Contains(path)) File.Delete(path);
        UiText.Language = _originalLanguage;
    }

    [Fact]
    public async Task HeldAltControlsAllPanelsWithoutShowingHiddenOverlaysOrSavingLocks()
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings
        {
            OverlayEnabled = false,
            BossRushEnabled = false,
            BossAlertsEnabled = false,
            LevelingOverlayEnabled = true,
            LevelingOverlayLocked = true,
            CheckUpdatesOnStart = false,
            LootTrackingEnabled = false
        });
        RouteFile.Save(AppPaths.RoutesFile, new[]
        {
            new SavedRoute("leveling", "Leveling route",
                [new RouteStop("altgard", 100, 200, "Quest", "NPCs", null)],
                Category: LevelingRouteStyle.Category)
        });

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(); // Never shown: no capture or WebView startup.
                var settings = form.Services.GetRequiredService<SettingsService>();
                var targets = form.Services.GetRequiredService<MapTargetsService>();
                var pet = (Form)typeof(MainForm).GetField("_overlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                var panels = new[] { "_overlay", "_bossOverlay", "_levelingOverlay" }
                    .Select(name => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!)
                    .ToArray();
                bool Locked(object panel) => (bool)panel.GetType()
                    .GetField("_locked", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
                targets.StartRoute("leveling");
                Assert.Equal("leveling", targets.ActiveLevelingRouteId);
                Assert.True(settings.Current.LevelingOverlayLocked);
                Assert.False(pet.Visible);
                var savedSettings = File.ReadAllBytes(AppPaths.SettingsFile);

                form.SetOverlayInteractionEnabled(true);
                Assert.All(panels, panel => Assert.False(Locked(panel)));
                Assert.True(settings.Current.LevelingOverlayLocked); // legacy storage is never toggled by Alt
                Assert.False(settings.Current.OverlayEnabled);
                Assert.False(pet.Visible);

                form.SetOverlayInteractionEnabled(true); // Holding Alt cannot toggle back to locked.
                Assert.All(panels, panel => Assert.False(Locked(panel)));
                form.SetOverlayInteractionEnabled(false);
                Assert.All(panels, panel => Assert.True(Locked(panel)));
                Assert.True(settings.Current.LevelingOverlayLocked);
                Assert.False(settings.Current.OverlayEnabled);
                Assert.False(pet.Visible);
                Assert.False(form.Visible);
                Assert.Equal(savedSettings, File.ReadAllBytes(AppPaths.SettingsFile));
                form.Dispose(); // Finish service shutdown before restoring the shared files.
                completed.SetResult();
            }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void LevelingOverlayDefaultsToEnabledAndLocked()
    {
        var settings = new SettingsService();
        Assert.True(settings.Current.LevelingOverlayEnabled);
        Assert.True(settings.Current.LevelingOverlayLocked);
        Assert.Equal(1, settings.Current.LevelingOverlayScale);
    }

    [Theory]
    [InlineData("1e309", 1)]
    [InlineData("-1e309", 1)]
    [InlineData("2", 1.6)]
    [InlineData("0.2", 0.7)]
    [InlineData("-5", 0.7)]
    [InlineData("1.25", 1.25)]
    public void LoadingSettingsNormalizesLevelingOverlayScale(string value, double expected)
    {
        File.WriteAllText(AppPaths.SettingsFile, "{\"LevelingOverlayScale\":" + value + "}");
        var settings = new SettingsService();
        Assert.Equal(expected, settings.Current.LevelingOverlayScale);
        Assert.True(settings.Current.LevelingOverlayEnabled);
        Assert.True(settings.Current.LevelingOverlayLocked);
    }
}
