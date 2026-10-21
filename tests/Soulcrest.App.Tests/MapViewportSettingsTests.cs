using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class MapViewportSettingsTests : IDisposable
{
    private readonly string _previousLanguage = UiText.Language;
    private readonly byte[]? _backup;
    private readonly Dictionary<string, byte[]> _defectBackups;

    public MapViewportSettingsTests()
    {
        // TestDataFolder redirects SOULCREST_DATA before AppPaths caches it for this process.
        Directory.CreateDirectory(AppPaths.DataDirectory);
        _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        _defectBackups = Directory.GetFiles(AppPaths.DataDirectory, "settings.json.defekt-*")
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
        File.Delete(AppPaths.SettingsFile);
    }

    [Fact]
    public void MissingAndLegacyFilesHaveNoRememberedViewportUntilExplicitSave()
    {
        Assert.Empty(new AppSettings().MapViewports);
        var fresh = new SettingsService();
        Assert.Empty(fresh.Current.MapViewports);
        Assert.False(File.Exists(AppPaths.SettingsFile));
        const string legacy = """{"SettingsRevision":5,"LastMap":"altgard","FollowPlayer":false,"UiLanguage":"de"}""";
        File.WriteAllText(AppPaths.SettingsFile, legacy);
        var settings = new SettingsService();
        Assert.Empty(settings.Current.MapViewports);
        Assert.Equal(legacy, File.ReadAllText(AppPaths.SettingsFile));
        settings.Update(current => current.MapViewports["altgard"] = new(2034.25, 4100.5, -1.5));
        Assert.Equal(new MapViewport(2034.25, 4100.5, -1.5), new SettingsService().Current.MapViewports["altgard"]);
        Assert.Equal(5, new SettingsService().Current.SettingsRevision);
    }

    [Fact]
    public void EachMapRetainsItsNativeCenterAndZoomAcrossUpdatesAndRestart()
    {
        var settings = new SettingsService();
        settings.Update(current =>
        {
            current.MapViewports["altgard"] = new(8000.25, 7200.75, 3.5);
            current.MapViewports["abyss-a"] = new(1200.5, 3400.125, -2);
            current.MapViewports["Altgard"] = new(5, 6, 0);
        });
        settings.Update(current => current.MapViewports["altgard"] = new(4100, 3900, 4));
        var reloaded = new SettingsService().Current.MapViewports;
        Assert.Equal(3, reloaded.Count);
        Assert.Equal(new MapViewport(4100, 3900, 4), reloaded["altgard"]);
        Assert.Equal(new MapViewport(1200.5, 3400.125, -2), reloaded["abyss-a"]);
        Assert.Equal(new MapViewport(5, 6, 0), reloaded["Altgard"]);
        Assert.Same(StringComparer.Ordinal, reloaded.Comparer);
        Assert.False(reloaded.ContainsKey("ABYSS-A"));
        using var saved = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
        var viewport = saved.RootElement.GetProperty("MapViewports").GetProperty("altgard");
        Assert.Equal(4100, viewport.GetProperty("X").GetDouble());
        Assert.Equal(3900, viewport.GetProperty("Y").GetDouble());
        Assert.Equal(4, viewport.GetProperty("Zoom").GetDouble());
        Assert.Equal(3, viewport.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"altgard\":null,\"\":{\"X\":1,\"Y\":2,\"Zoom\":3},\"  \":{\"X\":1,\"Y\":2,\"Zoom\":3}}")]
    public void NullOrInvalidEntriesLoadAsEmptyWithoutRewritingTheFile(string viewports)
    {
        var json = "{\"SettingsRevision\":5,\"LastMap\":\"abyss-a\",\"LootTrackingEnabled\":false,\"MapViewports\":" + viewports + "}";
        File.WriteAllText(AppPaths.SettingsFile, json);
        var settings = new SettingsService();
        Assert.Empty(settings.Current.MapViewports);
        Assert.Same(StringComparer.Ordinal, settings.Current.MapViewports.Comparer);
        Assert.Equal("abyss-a", settings.Current.LastMap);
        Assert.False(settings.Current.LootTrackingEnabled);
        Assert.Equal(json, File.ReadAllText(AppPaths.SettingsFile));
        settings.Update(_ => { });
        Assert.Empty(new SettingsService().Current.MapViewports);
    }

    [Theory]
    [InlineData("{\"MapViewports\":[1,2]}")]
    [InlineData("{\"MapViewports\":{\"altgard\":{\"X\":\"NaN\",\"Y\":1,\"Zoom\":2}}}")]
    [InlineData("{\"MapViewports\":")]
    public void UnreadableViewportJsonUsesTheExistingDefectiveFileRecovery(string json)
    {
        File.WriteAllText(AppPaths.SettingsFile, json);
        var settings = new SettingsService();
        Assert.Empty(settings.Current.MapViewports);
        Assert.Equal(json, File.ReadAllText(AppPaths.SettingsFile));
        Assert.Contains(Directory.GetFiles(AppPaths.DataDirectory, "settings.json.defekt-*"),
            path => File.ReadAllText(path) == json);
    }

    [Fact]
    public void AnInvalidEntryDoesNotDiscardAnotherMapsValidViewport()
    {
        File.WriteAllText(AppPaths.SettingsFile, """
            {"SettingsRevision":5,"MapViewports":{"bad":null,"altgard":{"X":1234.5,"Y":6789.25,"Zoom":1.5}}}
            """);
        var settings = new SettingsService();
        Assert.Equal("altgard", Assert.Single(settings.Current.MapViewports).Key);
        Assert.Equal(new MapViewport(1234.5, 6789.25, 1.5), settings.Current.MapViewports["altgard"]);
    }

    [Fact]
    public void NonFiniteAndNullValuesCannotBePersistedBySettingsUpdates()
    {
        var settings = new SettingsService();
        settings.Update(current =>
        {
            current.MapViewports["valid"] = new(-10.25, 8192.125, -2.5);
            current.MapViewports["nan-x"] = new(double.NaN, 1, 2);
            current.MapViewports["infinity-y"] = new(1, double.PositiveInfinity, 2);
            current.MapViewports["infinity-zoom"] = new(1, 2, double.NegativeInfinity);
            current.MapViewports["null"] = null!;
        });
        Assert.Equal("valid", Assert.Single(settings.Current.MapViewports).Key);
        Assert.Equal(new MapViewport(-10.25, 8192.125, -2.5), new SettingsService().Current.MapViewports["valid"]);
        using var saved = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
        Assert.Single(saved.RootElement.GetProperty("MapViewports").EnumerateObject());
        settings.Update(current => current.MapViewports = null!);
        Assert.Empty(new SettingsService().Current.MapViewports);
    }

    [Theory]
    [InlineData(double.NaN, 1, 2)]
    [InlineData(1, double.NaN, 2)]
    [InlineData(1, 2, double.NaN)]
    [InlineData(double.PositiveInfinity, 1, 2)]
    [InlineData(1, double.NegativeInfinity, 2)]
    [InlineData(1, 2, double.PositiveInfinity)]
    public void ViewportValidationRejectsNonFiniteCoordinatesOrZoom(double x, double y, double zoom)
    {
        Assert.False(new MapViewport(x, y, zoom).IsValid());
        Assert.True(new MapViewport(-10.25, 8192.125, -2.5).IsValid());
    }

    [Fact]
    public void ChangingViewportLeavesAllOtherSettingsAndNetworkRevisionUntouched()
    {
        var settings = new SettingsService();
        settings.Update(current =>
        {
            current.LastMap = "abyss-a";
            current.FollowPlayer = false;
            current.MapTrackingActive = false;
            current.LootTrackingEnabled = false;
            current.UiLanguage = "de";
            current.TargetArrivalRadius = 37;
            current.MapCategories["altgard"] = ["Pets/Cogni"];
            current.TimerOverlayTimerIds = ["event:catalog:shugo"];
        });
        static string OtherSettings(AppSettings current)
        {
            var node = JsonSerializer.SerializeToNode(current)!.AsObject();
            node.Remove(nameof(AppSettings.MapViewports));
            return node.ToJsonString();
        }
        var before = OtherSettings(settings.Current);
        var revision = settings.NetworkTrackingRevision;
        var changes = 0;
        settings.Changed += () => changes++;
        settings.Update(current => current.MapViewports["altgard"] = new(3456.75, 6789.125, 2));
        Assert.Equal(1, changes);
        Assert.Equal(revision, settings.NetworkTrackingRevision);
        Assert.Equal(before, OtherSettings(settings.Current));
        Assert.Equal(before, OtherSettings(new SettingsService().Current));
    }

    public void Dispose()
    {
        if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        foreach (var path in Directory.GetFiles(AppPaths.DataDirectory, "settings.json.defekt-*"))
            if (_defectBackups.TryGetValue(path, out var bytes)) File.WriteAllBytes(path, bytes); else File.Delete(path);
        UiText.Language = _previousLanguage;
    }
}
