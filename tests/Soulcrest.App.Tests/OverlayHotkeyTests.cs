using Microsoft.AspNetCore.Components.Web;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class OverlayHotkeyTests : IDisposable
{
    private readonly byte[]? _saved = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
    private readonly string _language = UiText.Language;
    public OverlayHotkeyTests() => JsonFile.Save(AppPaths.SettingsFile, new AppSettings());
    public void Dispose()
    {
        if (_saved is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _saved);
        UiText.Language = _language;
    }

    [Fact]
    public void OldSettingsKeepTheLootShortcutAndDoNotClaimOtherKeys()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{\"SettingsRevision\":2,\"LevelingOverlayEnabled\":false}");
        var settings = new SettingsService();
        Assert.Equal(new OverlayHotkey(0x50, 3), settings.Current.OverlayHotkeys["loot.toggle"]);
        Assert.Single(settings.Current.OverlayHotkeys);
        Assert.False(settings.Current.LevelingOverlayEnabled);
    }

    [Theory]
    [InlineData("KeyL", true, true, false, 0x4c, 3u)]
    [InlineData("F8", false, false, false, 0x77, 0u)]
    [InlineData("Digit4", false, false, true, 0x34, 4u)]
    [InlineData("Numpad4", false, true, false, 0x64, 2u)]
    [InlineData("ArrowRight", true, false, false, 0x27, 1u)]
    [InlineData("Minus", false, true, false, 0xbd, 2u)]
    public void RecorderMapsPhysicalKeys(string code, bool alt, bool ctrl, bool shift, int key, uint modifiers) =>
        Assert.Equal(new OverlayHotkey(key, modifiers), OverlayHotkey.FromKeyboard(new KeyboardEventArgs { Code = code, AltKey = alt, CtrlKey = ctrl, ShiftKey = shift }));

    [Fact]
    public void PlainLettersAndModifierOnlyBindingsAreRejected()
    {
        Assert.Null(OverlayHotkey.FromKeyboard(new KeyboardEventArgs { Code = "KeyL" }));
        Assert.False(new OverlayHotkey(0x12, 1).IsValid);
        Assert.False(new OverlayHotkey(0x4c, 32).IsValid);
        Assert.False(new OverlayHotkey(-1, 1).IsValid);
    }

    [Fact]
    public void SaveClearAndRestartPreserveTheExplicitChoices()
    {
        var settings = new SettingsService();
        using var service = new OverlayHotkeyService(settings, new FakeRegistrar());
        Assert.Null(service.Assign("leveling.toggle", new OverlayHotkey(0x4c, 6)));
        Assert.Null(service.Assign("loot.toggle", null));
        var restored = new SettingsService();
        Assert.Null(restored.Current.OverlayHotkeys["loot.toggle"]);
        Assert.Equal(new OverlayHotkey(0x4c, 6), restored.Current.OverlayHotkeys["leveling.toggle"]);
    }

    [Fact]
    public void DuplicateAssignmentLeavesThePreviousBindingIntact()
    {
        using var service = new OverlayHotkeyService(new SettingsService(), new FakeRegistrar());
        Assert.NotNull(service.Assign("boss.toggle", new OverlayHotkey(0x50, 3)));
        Assert.Null(service.Binding("boss.toggle"));
        Assert.NotNull(service.Assign("boss.toggle", new OverlayHotkey(0x10, 3)));
    }

    [Fact]
    public void RegistrationCollisionIsReportedAndDoesNotDispatch()
    {
        var native = new FakeRegistrar { Accept = false };
        using var service = new OverlayHotkeyService(new SettingsService(), native);
        var called = false; service.Invoked += _ => called = true;
        service.SetWindow(123);
        Assert.NotNull(service.Error("loot.toggle"));
        Assert.False(service.ProcessHotkey(0x5100));
        Assert.False(called);
        Assert.Empty(native.Active);
    }

    [Fact]
    public void RebindCaptureAndWindowRecreationReleaseOnlyOwnedRegistrations()
    {
        var native = new FakeRegistrar(); var settings = new SettingsService();
        using var service = new OverlayHotkeyService(settings, native);
        service.SetWindow(123);
        var invoked = new List<string>(); service.Invoked += invoked.Add;
        Assert.True(service.ProcessHotkey(0x5100)); Assert.Equal(["loot.toggle"], invoked);
        service.SetCapturing(true);
        Assert.Empty(native.Active); Assert.False(service.ProcessHotkey(0x5100));
        service.Assign("boss.unlock", new OverlayHotkey(0x42, 6));
        Assert.Empty(native.Active);
        service.SetCapturing(false);
        Assert.Equal(2, native.Active.Count);
        service.SetWindow(0); Assert.Empty(native.Active);
        service.SetWindow(456);
        Assert.All(native.Active.Keys, p => Assert.Equal((nint)456, p.Window));
        var attempts = native.Attempts;
        settings.Update(s => s.LevelingOverlayX++);
        Assert.Equal(attempts, native.Attempts); // positions/loot updates must not re-register keys
        service.Dispose(); Assert.Empty(native.Active);
    }

    [Fact]
    public void EachVisibilityShortcutHasAnIndependentPersistedSwitch()
    {
        var settings = new SettingsService();
        foreach (var action in OverlayHotkeys.Actions.Where(a => !a.Unlock))
            settings.Update(s => Assert.True(OverlayHotkeys.ToggleVisibility(s, action.Id)));
        var current = new SettingsService().Current;
        Assert.False(current.OverlayEnabled); Assert.False(current.BossOverlayVisible);
        Assert.False(current.LevelingOverlayEnabled); Assert.False(current.MapOverlayEnabled);
        Assert.False(current.PetScanOverlayEnabled); Assert.False(current.ExplorationScanOverlayEnabled);
        Assert.False(BossOverlayForm.PanelVisible(new AppSettings { BossRushEnabled = true, BossAlertsEnabled = true, BossOverlayVisible = false }, 3));
        Assert.False(OverlayHotkeys.ToggleVisibility(current, "leveling.unlock"));
        Assert.True(current.BossOverlayEnabled); // preserve the boss list and alert configuration
    }

    [Fact]
    public void InvalidSavedBindingsCannotBlockTheEntireHotkeyService()
    {
        JsonFile.Save(AppPaths.SettingsFile, new AppSettings { OverlayHotkeys = new()
        {
            ["loot.toggle"] = new(0x50, 3), ["boss.toggle"] = new(0x50, 3),
            ["leveling.toggle"] = new(0x10, 1), ["removed.action"] = new(0x70, 0)
        } });
        var current = new SettingsService().Current;
        Assert.NotNull(current.OverlayHotkeys["loot.toggle"]);
        Assert.Null(current.OverlayHotkeys["boss.toggle"]); Assert.Null(current.OverlayHotkeys["leveling.toggle"]);
        Assert.False(current.OverlayHotkeys.ContainsKey("removed.action"));
    }

    private sealed class FakeRegistrar : IOverlayHotkeyRegistrar
    {
        public bool Accept { get; init; } = true;
        public int Attempts { get; private set; }
        public Dictionary<(nint Window, int Id), OverlayHotkey> Active { get; } = [];
        public bool Register(nint window, int id, OverlayHotkey gesture)
        {
            Attempts++;
            if (!Accept) return false;
            Active.Add((window, id), gesture); return true;
        }
        public void Unregister(nint window, int id) => Assert.True(Active.Remove((window, id)));
    }
}
