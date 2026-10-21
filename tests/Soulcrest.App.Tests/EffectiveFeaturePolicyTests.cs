using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class EffectiveFeaturePolicyTests
{
    private static WorldContextSnapshot Snapshot(WorldContextStatus status, WorldContextKind kind, string? map = null) =>
        new(status, kind, 100, null, null, null, null, null, 1, DateTimeOffset.UtcNow, "synthetic-test")
        { SoulcrestMapId = map };

    [Fact]
    public void ConfirmedInstanceOverridesVisualFlagsEvenWithAMinimapOrStoppedTracking()
    {
        var instance = Snapshot(WorldContextStatus.Confirmed, WorldContextKind.Instance, "synthetic-world");
        Assert.Equal(true, EffectiveFeaturePolicy.ConfirmedInstance(instance));
        Assert.False(EffectiveFeaturePolicy.AllowsLiveMap(instance, "synthetic-world"));
        foreach (var tracking in new[] { false, true })
        foreach (var found in new[] { false, true })
        {
            var context = new OverlayContext(tracking, found, false, false, false, true);
            Assert.True(OverlayVisibilityPolicy.IsSuppressed(new(true, true, false, true), context));
            Assert.False(OverlayVisibilityPolicy.IsSuppressed(new(true, true, true, true), context));
            foreach (var id in new[] { "boss", "notifications" })
                Assert.False(OverlayVisibilityPolicy.IsSuppressed(new AppSettings(), id, context));
        }
    }

    [Fact]
    public void OnlyAnExplicitCompatibleConfirmedOpenWorldBridgeAllowsLiveCoordinates()
    {
        var world = Snapshot(WorldContextStatus.Confirmed, WorldContextKind.OpenWorld, "synthetic-world");
        Assert.Equal(false, EffectiveFeaturePolicy.ConfirmedInstance(world));
        Assert.True(EffectiveFeaturePolicy.AllowsLiveMap(world, "synthetic-world"));
        Assert.False(EffectiveFeaturePolicy.AllowsLiveMap(world, "other-map"));
        Assert.False(EffectiveFeaturePolicy.AllowsLiveMap(world with { SoulcrestMapId = null }, "synthetic-world"));
        var unknown = world with { Kind = WorldContextKind.Unknown };
        Assert.Null(EffectiveFeaturePolicy.ConfirmedInstance(unknown));
        Assert.False(EffectiveFeaturePolicy.AllowsLiveMap(unknown, "synthetic-world"));
    }

    [Theory]
    [InlineData(WorldContextStatus.Unknown)]
    [InlineData(WorldContextStatus.Stale)]
    [InlineData(WorldContextStatus.Transitioning)]
    public void UnconfirmedNetworkNeverEnablesFallbackUnlessTheSavedSwitchIsOff(WorldContextStatus status)
    {
        var snapshot = Snapshot(status, WorldContextKind.OpenWorld, "synthetic-world");
        Assert.Null(EffectiveFeaturePolicy.ConfirmedInstance(snapshot));
        Assert.False(EffectiveFeaturePolicy.AllowsLiveMap(snapshot, "synthetic-world"));
        Assert.Equal(MapTrackingMode.NetworkSuspended, EffectiveFeaturePolicy.TrackingMode(true, snapshot));
        Assert.True(EffectiveFeaturePolicy.AllowsLiveMap(false, snapshot, "synthetic-world"));
        Assert.Equal(MapTrackingMode.VisualFallback, EffectiveFeaturePolicy.TrackingMode(false, snapshot));
    }

    [Fact]
    public void MissingNetworkServiceIsNotPermissionForAVisualFallback()
    {
        Assert.Equal(MapTrackingMode.NetworkSuspended, EffectiveFeaturePolicy.TrackingMode(true, null));
        Assert.False(EffectiveFeaturePolicy.AllowsLiveMap(null, "synthetic-world"));
        Assert.Equal(MapTrackingMode.VisualFallback, EffectiveFeaturePolicy.TrackingMode(false, null));
        Assert.True(EffectiveFeaturePolicy.AllowsLiveMap(false, null, "synthetic-world"));
    }

    [Fact]
    public void ConfirmedOpenWorldCanOverrideTheVisualInstanceHeuristicWithoutWritingPreferences()
    {
        var rules = new OverlayVisibilityRules(true, true, false, true);
        var context = new OverlayContext(true, true, false, false, true, false);
        Assert.False(OverlayVisibilityPolicy.IsSuppressed(rules, context));
        Assert.False(rules.Instances);
    }

    [Fact]
    public void OnlyExplicitSupportedAbyssKeepsOpenPvpPermissionsWithoutChangingItsNativeKind()
    {
        var abyss = Snapshot(WorldContextStatus.Confirmed, WorldContextKind.Instance, "abyss-reshanta-a")
            with { Category = "Abyss" };
        Assert.Equal(WorldContextKind.Instance, abyss.Kind);
        Assert.Equal(false, EffectiveFeaturePolicy.ConfirmedInstance(abyss));
        Assert.True(EffectiveFeaturePolicy.AllowsLiveMap(abyss, "abyss-reshanta-a"));
        Assert.False(EffectiveFeaturePolicy.AllowsLiveMap(abyss, "other-abyss-map"));
        Assert.Equal(true, EffectiveFeaturePolicy.ConfirmedInstance(abyss with { SoulcrestMapId = null }));
        Assert.False(EffectiveFeaturePolicy.AllowsLiveMap(abyss with { Category = "Story" }, "abyss-reshanta-a"));
    }
    [Fact]
    public void AbyssBaseMapExceptionNeverOpensAPositiveOrUnqualifiedInstanceLayer()
    {
        var abyss = Snapshot(WorldContextStatus.Confirmed, WorldContextKind.Instance, "synthetic-abyss")
            with { Category = "Abyss", DungeonId = 500 };
        foreach (var layer in new bool?[] { true, null })
        {
            Assert.True(EffectiveFeaturePolicy.ConfirmedInstance(abyss with { InstanceLayer = layer }));
            Assert.False(EffectiveFeaturePolicy.AllowsLiveMap(abyss with { InstanceLayer = layer }, "synthetic-abyss"));
        }
        Assert.False(EffectiveFeaturePolicy.ConfirmedInstance(abyss with { InstanceLayer = false }));
        Assert.True(EffectiveFeaturePolicy.AllowsLiveMap(abyss with { InstanceLayer = false }, "synthetic-abyss"));
    }
}
