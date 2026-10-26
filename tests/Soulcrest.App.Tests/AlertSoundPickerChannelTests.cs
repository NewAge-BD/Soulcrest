using System.Text.Json;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AlertSoundPickerChannelTests
{
    [Fact]
    public async Task DelegatedPreviewNeverInvokesSharedBossOrEventPlayback()
    {
        await using var fixture = new LootAlertUiFixture();
        var previews = 0;
        var selections = new List<AlertSoundPicker.Selection>();
        var before = JsonSerializer.Serialize(fixture.Settings.Current);
        await fixture.RenderPicker(selections.Add, () => { previews++; return Task.FromResult(true); });
        var html = await fixture.InvokePicker("Test");
        Assert.Equal(1, previews);
        Assert.Equal(0, fixture.SharedAudioCalls);
        Assert.Equal(0, fixture.DropAudioCalls);
        Assert.Empty(selections);
        Assert.Empty(fixture.Alerts.SoundFiles);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Settings.Current));
        Assert.Contains("Test sound started.", html);
    }

    [Fact]
    public async Task DelegatedPreviewFailureUsesItsOwnStatusWithoutFallbackToSharedAudio()
    {
        await using var fixture = new LootAlertUiFixture();
        await fixture.RenderPicker(_ => { }, () => Task.FromResult(false), () => "Synthetic drop channel error");
        var html = await fixture.InvokePicker("Test");
        Assert.Contains("Synthetic drop channel error", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.Equal(0, fixture.SharedAudioCalls);
    }

    [Fact]
    public async Task CancelledPickerLeavesTierChoicesAndTheSoundLibraryUnchanged()
    {
        await using var fixture = new LootAlertUiFixture();
        Assert.True(fixture.Alerts.AddSoundFile(fixture.SoundFile));
        var libraryBefore = File.ReadAllBytes(fixture.AlertsFile);
        var settingsBefore = File.ReadAllBytes(AppPaths.SettingsFile);
        var choices = new List<AlertSoundPicker.Selection>();
        await fixture.RenderPicker(choices.Add, () => Task.FromResult(true));
        fixture.Choice = new();
        var html = await fixture.InvokePicker("Choose");
        Assert.Equal(1, fixture.PickerCalls);
        Assert.Empty(choices);
        Assert.Equal(libraryBefore, File.ReadAllBytes(fixture.AlertsFile));
        Assert.Equal(settingsBefore, File.ReadAllBytes(AppPaths.SettingsFile));
        Assert.Equal(fixture.SoundFile, Assert.Single(fixture.Alerts.SoundFiles));
        Assert.Contains("File selection cancelled.", html);
        Assert.Equal(0, fixture.SharedAudioCalls);
    }

    [Fact]
    public async Task DisabledPickerCannotSelectOpenADialogOrPreview()
    {
        await using var fixture = new LootAlertUiFixture();
        var choices = new List<AlertSoundPicker.Selection>();
        var previews = 0;
        await fixture.RenderPicker(choices.Add, () => { previews++; return Task.FromResult(true); }, disabled: true);
        await fixture.InvokePicker("Select", new AlertSoundPicker.Selection("custom", fixture.SoundFile));
        await fixture.InvokePicker("Choose");
        await fixture.InvokePicker("Test");
        Assert.Empty(choices);
        Assert.Equal(0, previews);
        Assert.Equal(0, fixture.PickerCalls);
        Assert.Equal(0, fixture.SharedAudioCalls);
        Assert.Empty(fixture.Alerts.SoundFiles);
    }

    [Fact]
    public async Task DefaultPreviewRetainsTheExistingSharedAlertChannel()
    {
        await using var fixture = new LootAlertUiFixture();
        await fixture.RenderPicker(_ => { });
        await fixture.InvokePicker("Test");
        Assert.Equal(1, fixture.SharedAudioCalls);
        Assert.Equal(0, fixture.DropAudioCalls);
        Assert.Equal(0, fixture.PickerCalls);
    }
}
