using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AdvanceWarningPickerTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task PresetsCustomValuesAndPauseUseIndependentSelections(string language)
    {
        var oldLanguage = UiText.Language;
        UiText.Language = language;
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            CaptureAdvanceWarningPicker? picker = null;
            var selection = new AdvanceWarningPicker.Selection(true, [900, 300]);
            var changes = 0;
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CaptureAdvanceWarningPicker>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CaptureAdvanceWarningPicker.Captured)] = (Action<CaptureAdvanceWarningPicker>)(value => picker = value),
                [nameof(AdvanceWarningPicker.Values)] = selection.Values,
                [nameof(AdvanceWarningPicker.Seconds)] = true,
                [nameof(AdvanceWarningPicker.Changed)] = EventCallback.Factory.Create<AdvanceWarningPicker.Selection>(this, value => { selection = value; changes++; })
            })));
            async Task Invoke(string method, params object[] args) => await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = typeof(AdvanceWarningPicker).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(picker, args);
                if (result is Task task) await task;
                await picker!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(AdvanceWarningPicker.Values)] = selection.Values, [nameof(AdvanceWarningPicker.Enabled)] = selection.Enabled }));
            });
            await Invoke("Toggle", 600);
            Assert.Equal(new[] { 900, 600, 300 }, selection.Values);
            await Invoke("Toggle", 300);
            Assert.Equal(new[] { 900, 600 }, selection.Values);
            await Invoke("Pause");
            Assert.False(selection.Enabled);
            Assert.Equal(new[] { 900, 600 }, selection.Values);
            await Invoke("Toggle", 600);
            Assert.True(selection.Enabled);
            Assert.Equal(new[] { 900, 600 }, selection.Values);
            typeof(AdvanceWarningPicker).GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(picker, "147");
            await Invoke("Add");
            Assert.Equal(new[] { 900, 600, 147 }, selection.Values);
            typeof(AdvanceWarningPicker).GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(picker, "91");
            await Invoke("Add");
            Assert.Equal(new[] { 900, 600, 147, 91 }, selection.Values);
            typeof(AdvanceWarningPicker).GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(picker, "147");
            await Invoke("Add");
            Assert.Equal(4, selection.Values.Length);
            typeof(AdvanceWarningPicker).GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(picker, "3601");
            var before = changes;
            await Invoke("Add");
            Assert.Equal(before, changes);
            await Invoke("Remove", 147);
            Assert.Equal(new[] { 900, 600, 91 }, selection.Values);
            await Invoke("Remove", 91);
            Assert.Equal(new[] { 900, 600 }, selection.Values);
            await renderer.Dispatcher.InvokeAsync(() => picker!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(AdvanceWarningPicker.Disabled)] = true })));
            before = changes;
            await Invoke("Toggle", 120);
            Assert.Equal(before, changes);
            Assert.Contains("data-advance-warnings", WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString)));
        }
        finally { UiText.Language = oldLanguage; }
    }
}

public sealed class CaptureAdvanceWarningPicker : AdvanceWarningPicker
{
    [Parameter] public Action<CaptureAdvanceWarningPicker>? Captured { get; set; }
    protected override void OnInitialized() => Captured?.Invoke(this);
}
