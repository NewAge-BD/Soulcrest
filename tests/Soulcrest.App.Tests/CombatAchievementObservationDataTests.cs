using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatAchievementObservationDataTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DisabledUnboundOrNonCurrentCharacterStateEmitsNoScript()
    {
        Assert.Null(CombatAchievementObservationPresentation.Create(null));
        await using var fixture = new RendererFixture();
        Assert.Empty(await fixture.Render(Snapshot(), false));
        Assert.Empty(await fixture.Render(Snapshot() with { IsFresh = false }, true));
        Assert.Empty(await fixture.Render(CharacterCombatSnapshot.Empty, true));
        foreach (var profile in new string?[] { null, "", "   " })
            Assert.Empty(await fixture.Render(Snapshot() with { ProfileId = profile }, true));
    }

    [Theory]
    [InlineData("own-achievement-list", NetworkCombatSourceStatus.Complete)]
    [InlineData("own-achievement-update", NetworkCombatSourceStatus.Partial)]
    [InlineData("own-achievement-reward", NetworkCombatSourceStatus.Complete)]
    public void ExplicitDtoContainsOnlyAllowedActorFreeFieldsAndPreservesZeroAndBits(string source, NetworkCombatSourceStatus status)
    {
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            ClassId = "PRIVATE_CLASS", CharacterLevel = 12345, CurrentCombatScore = 987654321,
            Graph = new() { ClassId = "PRIVATE_NATIVE_ID", Sources = [new() { Id = "PRIVATE_TRANSPORT" }] },
            Achievements = snapshot.Achievements with { Source = source, Status = status }
        };
        var dto = Assert.IsType<CombatAchievementObservationDto>(CombatAchievementObservationPresentation.Create(snapshot));
        var json = CombatAchievementObservationPresentation.Serialize(dto);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(new[] { "domainSequence", "generation", "isFresh", "observedAt", "rewardGroups", "rows", "schema", "sequence", "source", "status" },
            root.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(1, root.GetProperty("schema").GetInt32()); Assert.Equal(7, root.GetProperty("generation").GetInt64());
        Assert.Equal(12, root.GetProperty("sequence").GetInt64()); Assert.Equal(9, root.GetProperty("domainSequence").GetInt64());
        Assert.Equal(source, root.GetProperty("source").GetString()); Assert.True(root.GetProperty("isFresh").GetBoolean());
        Assert.Equal(status.ToString(), root.GetProperty("status").GetString());
        var row = root.GetProperty("rows")[0];
        Assert.Equal(new[] { "completeTime", "count", "id", "rewardedStep" }, row.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(1, row.GetProperty("id").GetInt32()); Assert.Equal(0, row.GetProperty("count").GetInt32());
        Assert.Equal("0", row.GetProperty("completeTime").GetString()); Assert.Equal(0, row.GetProperty("rewardedStep").GetByte());
        Assert.Equal(new[] { false, true, false }, root.GetProperty("rewardGroups")[0].GetProperty("bits").EnumerateArray().Select(bit => bit.GetBoolean()));
        Assert.DoesNotContain("PRIVATE_", json); Assert.DoesNotContain("987654321", json); Assert.DoesNotContain("12345", json);
        Assert.DoesNotContain("profileId", json); Assert.DoesNotContain("classId", json); Assert.DoesNotContain("graph", json);
        Assert.DoesNotContain("server", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("name", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("gap")]
    [InlineData("conflict")]
    [InlineData("unknown")]
    [InlineData("time")]
    [InlineData("epoch")]
    [InlineData("source")]
    public void UnqualifiedDomainsExportNoRowOrRewardValues(string variant)
    {
        var snapshot = Snapshot();
        snapshot = variant switch
        {
            "stale" => snapshot with { Achievements = snapshot.Achievements with { Status = NetworkCombatSourceStatus.Stale } },
            "gap" => snapshot with { Achievements = snapshot.Achievements with { IsFresh = false } },
            "conflict" => snapshot with { Achievements = snapshot.Achievements with { Status = NetworkCombatSourceStatus.Conflict } },
            "unknown" => snapshot with { Achievements = NetworkCombatDomain<CharacterAchievementState>.Unknown },
            "time" => snapshot with { Achievements = snapshot.Achievements with { ObservedAt = null } },
            "epoch" => snapshot with { Generation = 0 },
            "source" => snapshot with { Achievements = snapshot.Achievements with { Source = "PRIVATE_SOURCE</script>" } },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        var dto = Assert.IsType<CombatAchievementObservationDto>(CombatAchievementObservationPresentation.Create(snapshot));
        Assert.False(dto.IsFresh); Assert.Null(dto.Rows); Assert.Null(dto.RewardGroups);
        var json = CombatAchievementObservationPresentation.Serialize(dto);
        Assert.DoesNotContain("completeTime", json); Assert.DoesNotContain("rewardedStep", json); Assert.DoesNotContain("bits", json);
        Assert.DoesNotContain("PRIVATE_", json);
        if (variant == "conflict") Assert.Equal(NetworkCombatSourceStatus.Conflict, dto.Status);
        if (variant is "stale" or "gap") Assert.Equal(NetworkCombatSourceStatus.Stale, dto.Status);
        if (variant is "source" or "unknown") Assert.Equal("unknown", dto.Source);
    }

    [Fact]
    public void CompleteTimeRetainsEveryInt64DigitAsInvariantJsonString()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Achievements = snapshot.Achievements with { Value = new(
            ImmutableDictionary<int, CharacterAchievement>.Empty.Add(1, new(0, long.MinValue, 0))
                .Add(2, new(1, long.MaxValue, byte.MaxValue)).Add(3, new(2, 9007199254740993, 1)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty) } };
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var culture = (CultureInfo)CultureInfo.GetCultureInfo("de-DE").Clone();
            culture.NumberFormat.NegativeSign = "NEG"; CultureInfo.CurrentCulture = culture;
            var dto = Assert.IsType<CombatAchievementObservationDto>(CombatAchievementObservationPresentation.Create(snapshot));
            using var document = JsonDocument.Parse(CombatAchievementObservationPresentation.Serialize(dto));
            var rows = document.RootElement.GetProperty("rows");
            Assert.Equal(JsonValueKind.String, rows[0].GetProperty("completeTime").ValueKind);
            Assert.Equal("-9223372036854775808", rows[0].GetProperty("completeTime").GetString());
            Assert.Equal("9223372036854775807", rows[1].GetProperty("completeTime").GetString());
            Assert.Equal("9007199254740993", rows[2].GetProperty("completeTime").GetString());
            Assert.Equal(byte.MaxValue, rows[1].GetProperty("rewardedStep").GetByte());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ObservedEmptyListsAreExplicitAndDtoCollectionsAreFrozen()
    {
        var snapshot = Snapshot();
        var dto = Assert.IsType<CombatAchievementObservationDto>(CombatAchievementObservationPresentation.Create(snapshot));
        Assert.Throws<NotSupportedException>(() => ((IList<CombatAchievementObservedRowDto>)dto.Rows!).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<bool>)Assert.Single(dto.RewardGroups!).Bits).Clear());
        snapshot = snapshot with { Achievements = snapshot.Achievements with { Value = new(
            ImmutableDictionary<int, CharacterAchievement>.Empty, ImmutableDictionary<int, ImmutableArray<bool>>.Empty) } };
        dto = Assert.IsType<CombatAchievementObservationDto>(CombatAchievementObservationPresentation.Create(snapshot));
        Assert.True(dto.IsFresh); Assert.NotNull(dto.Rows); Assert.Empty(dto.Rows); Assert.NotNull(dto.RewardGroups); Assert.Empty(dto.RewardGroups);
    }

    [Fact]
    public async Task ResetAndProfileUnbindingRemoveOldValuesAndAReboundSnapshotSuppliesOnlyItsOwnRows()
    {
        await using var fixture = new RendererFixture();
        var html = await fixture.Render(Snapshot(), true);
        Assert.Contains("data-achievement-observation", html);
        html = await fixture.Update(CharacterCombatSnapshot.Empty);
        Assert.Empty(html);
        html = await fixture.Update(Snapshot() with { ProfileId = null }); Assert.Empty(html);
        var next = Snapshot() with { ProfileId = "PRIVATE_NEXT_PROFILE", Generation = 8 };
        next = next with { Achievements = next.Achievements with { Value = new(
            ImmutableDictionary<int, CharacterAchievement>.Empty.Add(99, new(17, 5, 3)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty) } };
        html = await fixture.Update(next);
        using var document = JsonDocument.Parse(ScriptJson(html));
        Assert.Equal(8, document.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal(99, Assert.Single(document.RootElement.GetProperty("rows").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.DoesNotContain("PRIVATE_", html);
        html = await fixture.Update(next, false); Assert.Empty(html);
    }

    [Fact]
    public async Task ScriptContainsParseableJsonAndUnknownSourceCannotInjectMarkup()
    {
        await using var fixture = new RendererFixture();
        var snapshot = Snapshot();
        snapshot = snapshot with { ProfileId = "</script><script>PRIVATE_NAME</script>",
            Achievements = snapshot.Achievements with { Source = "</script><script>PRIVATE_SOURCE</script>" } };
        var html = await fixture.Render(snapshot, true);
        Assert.Single(Regex.Matches(html, "</script>").Cast<Match>());
        var json = ScriptJson(html);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("unknown", document.RootElement.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("rows").ValueKind);
        Assert.DoesNotContain("PRIVATE_", html); Assert.DoesNotContain("&quot;", json);
    }

    private static string ScriptJson(string html)
    {
        var match = Regex.Match(html, "<script[^>]*data-achievement-observation[^>]*>(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success); return match.Groups[1].Value;
    }

    private static CharacterCombatSnapshot Snapshot() => new()
    {
        ProfileId = "PRIVATE_BOUND_PROFILE", IsFresh = true, Generation = 7, Sequence = 12,
        Achievements = new(new CharacterAchievementState(
            ImmutableDictionary<int, CharacterAchievement>.Empty.Add(1, new(0, 0, 0)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty.Add(7, [false, true, false])),
            NetworkCombatSourceStatus.Complete, true, At, 9, "own-achievement-list")
    };

    private sealed class RendererFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private Host? _host;
        public RendererFixture()
        {
            var services = new ServiceCollection(); services.AddLogging(); _provider = services.BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public async Task<string> Render(CharacterCombatSnapshot snapshot, bool enabled)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(Host.Snapshot)] = snapshot, [nameof(Host.Enabled)] = enabled,
                    [nameof(Host.Capture)] = (Action<Host>)(host => _host = host) })));
            return await _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        }
        public Task<string> Update(CharacterCombatSnapshot snapshot, bool enabled = true) => _renderer.Dispatcher.InvokeAsync(() =>
        { _host!.Update(snapshot, enabled); return _root.ToHtmlString(); });
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); }
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public CharacterCombatSnapshot Snapshot { get; set; } = CharacterCombatSnapshot.Empty;
        [Parameter] public bool Enabled { get; set; }
        [Parameter] public Action<Host>? Capture { get; set; }
        protected override void OnInitialized() => Capture?.Invoke(this);
        internal void Update(CharacterCombatSnapshot snapshot, bool enabled) { Snapshot = snapshot; Enabled = enabled; StateHasChanged(); }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CombatAchievementObservationData>(0); builder.AddAttribute(1, nameof(CombatAchievementObservationData.Snapshot), Snapshot);
            builder.AddAttribute(2, nameof(CombatAchievementObservationData.Enabled), Enabled); builder.CloseComponent();
        }
    }
}
