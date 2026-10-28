using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootTrackerSessionStatisticsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task RealRazorButtonsGroupSortAndPersistWholeSessionCombatStatistics(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Abyss,
            CombatEvents = [new("kill", LootTrackerCombatKind.Kill, At, TimeSpan.Zero)
                { OpponentClassId = "Cleric", OpponentAbyssRankGrade = 19 },
                new("death", LootTrackerCombatKind.Death, At.AddMinutes(2), TimeSpan.FromMinutes(2))
                { OpponentClassId = "Templar", OpponentAbyssRankGrade = 1 }]
        });
        Assert.Contains("class-icons/Cleric.png", html); Assert.Contains("data-combat-category=\"Templar\"", html);
        Assert.Contains("100 %", WebUtility.HtmlDecode(html));
        Assert.Contains("aria-sort=\"descending\"", html);
        html = await fixture.Click("data-statistic-sort", "combat-deaths");
        Assert.Equal(AbyssCombatSort.DeathsDescending, new SettingsService().Current.AbyssCombatSort);
        Assert.True(html.IndexOf("data-combat-category=\"Templar\"", StringComparison.Ordinal) < html.IndexOf("data-combat-category=\"Cleric\"", StringComparison.Ordinal));
        html = await fixture.Click("data-combat-grouping", "rank");
        Assert.Equal(AbyssCombatGrouping.Rank, new SettingsService().Current.AbyssCombatGrouping);
        Assert.Contains("abyss-rank-icons/rank-01.png", html); Assert.DoesNotContain("class-icons/Cleric.png", html);
        await fixture.Click("data-statistic-sort", "combat-deaths");
        Assert.Equal(AbyssCombatSort.DeathsAscending, new SettingsService().Current.AbyssCombatSort);
    }

    [Fact]
    public async Task ExampleControlsChangeViewWithoutWritingPreferencesAndResourceTableIncludesUnplacedAttempts()
    {
        await using var fixture = new Fixture("en");
        var html = await fixture.Render(new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Lifeskill, TimelineVersion = 1,
            TimelineEvents = [new("success", LootTrackerTimelineEventKind.HarvestSuccess, At, TimeSpan.Zero)
            {
                GatherableId = 42, Loot = [new("sample", "Sample reward", LootTrackerLootKind.Item, 3)]
            }, new("failure", LootTrackerTimelineEventKind.HarvestFailure, At.AddMinutes(1), TimeSpan.FromMinutes(1)) { GatherableId = 42 }]
        }, isExample: true);
        Assert.Contains("data-resource-category=\"resource-42\"", html); Assert.Contains("Resource #42", html);
        await fixture.Click("data-statistic-sort", "resource-failures");
        Assert.Equal(LootResourceSort.SuccessesDescending, fixture.Settings.Current.LifeskillResourceSort);
        Assert.Equal(LootResourceSort.SuccessesDescending, new SettingsService().Current.LifeskillResourceSort);
    }

    [Fact]
    public async Task PveTablesSortIndependentlyAndEncodeSuppliedNamesWithoutInventingMonsterRows()
    {
        await using var fixture = new Fixture("en");
        var html = await fixture.Render(new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Grind,
            Loot = [new("large", "Beta", LootTrackerLootKind.Item, 20), new("small", "<script>sample</script>", LootTrackerLootKind.Item, 1)]
        });
        Assert.Contains("&lt;script&gt;sample&lt;/script&gt;", html); Assert.DoesNotContain("data-mob-statistic", html);
        html = await fixture.Click("data-statistic-sort", "loot-quantity");
        Assert.True(html.IndexOf("data-loot-statistic=\"small\"", StringComparison.Ordinal) < html.IndexOf("data-loot-statistic=\"large\"", StringComparison.Ordinal));
        Assert.Equal(LootQuantitySort.QuantityAscending, new SettingsService().Current.PveLootSort);
        Assert.Equal(LootQuantitySort.QuantityDescending, new SettingsService().Current.PveMobSort);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _oldLanguage = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CaptureLootTrackerSessionStatistics? _component;
        public SettingsService Settings => _provider.GetRequiredService<SettingsService>();
        public Fixture(string language)
        {
            JsonFile.Save(AppPaths.SettingsFile, new AppSettings());
            _provider = new ServiceCollection().AddLogging().AddSingleton<SettingsService>().BuildServiceProvider();
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            Settings.Current.UiLanguage = language; UiText.Language = language;
        }
        public async Task<string> Render(LootTrackerSession session, bool isExample = false)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CaptureLootTrackerSessionStatistics>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LootTrackerSessionStatistics.Session)] = session,
                    [nameof(LootTrackerSessionStatistics.IsExample)] = isExample,
                    [nameof(CaptureLootTrackerSessionStatistics.Captured)] = (Action<CaptureLootTrackerSessionStatistics>)(component => _component = component)
                })));
            return await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        }
#pragma warning disable BL0006
        public async Task<string> Click(string attribute, string value)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                using var builder = new RenderTreeBuilder();
                typeof(LootTrackerSessionStatistics).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(_component, [builder]);
                var frames = builder.GetFrames();
                var match = Enumerable.Range(0, frames.Count).Single(index => frames.Array[index].FrameType == RenderTreeFrameType.Attribute
                    && frames.Array[index].AttributeName == attribute && Equals(frames.Array[index].AttributeValue, value));
                var element = match;
                while (frames.Array[element].FrameType != RenderTreeFrameType.Element) element--;
                object? callback = null;
                for (var index = element + 1; index < frames.Count && frames.Array[index].FrameType == RenderTreeFrameType.Attribute; index++)
                    if (frames.Array[index].AttributeName == "onclick") callback = frames.Array[index].AttributeValue;
                switch (callback)
                {
                    case EventCallback<MouseEventArgs> typed: await typed.InvokeAsync(new()); break;
                    case EventCallback untyped: await untyped.InvokeAsync(new MouseEventArgs()); break;
                    case MulticastDelegate handler:
                        if (handler.DynamicInvoke(handler.Method.GetParameters().Length == 0 ? [] : [new MouseEventArgs()]) is Task task) await task;
                        break;
                    default: throw new InvalidOperationException("Rendered statistics button has no click handler.");
                }
                await _component!.RefreshNow();
            });
            return await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        }
#pragma warning restore BL0006
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _provider.DisposeAsync();
            if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
            UiText.Language = _oldLanguage;
        }
    }
}

public sealed class CaptureLootTrackerSessionStatistics : LootTrackerSessionStatistics
{
    [Parameter] public Action<CaptureLootTrackerSessionStatistics>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}
