using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatGenusObservationDataTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static CharacterCombatSnapshot Snapshot() => new()
    {
        ProfileId = "PRIVATE_PROFILE", ClassId = "PRIVATE_CLASS", IsFresh = true, Generation = 7, Sequence = 12,
        Pets = new(new PetProgressSnapshot(ImmutableDictionary<int, int>.Empty.Add(1135, 3), ImmutableDictionary<int, int>.Empty)
        {
            SelectedGenusPanel = 2,
            GenusBoards = ImmutableDictionary<byte, CharacterGenusBoard>.Empty.Add(3, new(3, 10, 12345,
                ImmutableDictionary<byte, ImmutableArray<CharacterGenusEffect>>.Empty
                    .Add(2, [new(8, 254, ushort.MaxValue, long.MinValue, true, false), new(0, 4, 19, 9007199254740993L, false, true)])
                    .Add(0, [new(1, 2, 193, long.MaxValue, false, true)])))
        }, NetworkCombatSourceStatus.Complete, true, At, 9, "own-pet-genus-list")
    };

    [Fact]
    public void ExplicitDiagnosticShapeIsActorFreeAndRawI64ValuesAreInvariantStrings()
    {
        var dto = Assert.IsType<CombatGenusObservationDto>(CombatGenusObservationPresentation.Create(Snapshot()));
        var json = CombatGenusObservationPresentation.Serialize(dto);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "boards", "domainSequence", "generation", "isFresh", "observedAt", "schema", "selectedPanel", "sequence", "source", "status" },
            document.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.DoesNotContain("PRIVATE_", json);
        foreach (var excluded in new[] { "profile", "class", "character", "entity", "transport", "server", "experience", "ride", "1135" })
            Assert.DoesNotContain(excluded, json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, dto.Generation); Assert.Equal(12, dto.Sequence); Assert.Equal(9, dto.DomainSequence);
        Assert.Equal(At, dto.ObservedAt); Assert.Equal((byte)2, dto.SelectedPanel);
        var board = Assert.Single(dto.Boards); Assert.Equal((byte)3, board.CreatureType); Assert.Equal(10, board.Level);
        Assert.Equal(new byte[] { 0, 2 }, board.Panels.Select(panel => panel.Panel));
        var first = Assert.Single(board.Panels[0].Slots);
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), first.Value);
        var unknown = board.Panels[1].Slots[0];
        Assert.Equal((byte)8, unknown.Slot); Assert.Equal((byte)254, unknown.Grade); Assert.Equal(ushort.MaxValue, unknown.StatType);
        Assert.Equal(long.MinValue.ToString(CultureInfo.InvariantCulture), unknown.Value);
        Assert.True(unknown.IsLocked); Assert.False(unknown.IsActivated);
        Assert.Equal("9007199254740993", board.Panels[1].Slots[1].Value);
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("boards")[0].GetProperty("panels")[0]
            .GetProperty("slots")[0].GetProperty("value").ValueKind);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("gap")]
    [InlineData("conflict")]
    [InlineData("wrong-source")]
    [InlineData("missing-time")]
    [InlineData("missing-value")]
    [InlineData("unbound")]
    [InlineData("missing-profile")]
    [InlineData("generation")]
    public void InvalidOrRetainedDomainsNeverPublishRawValues(string variant)
    {
        var snapshot = Snapshot();
        snapshot = variant switch
        {
            "stale" => snapshot with { Pets = snapshot.Pets with { Status = NetworkCombatSourceStatus.Stale } },
            "gap" => snapshot with { Pets = snapshot.Pets with { IsFresh = false } },
            "conflict" => snapshot with { Pets = snapshot.Pets with { Status = NetworkCombatSourceStatus.Conflict } },
            "wrong-source" => snapshot with { Pets = snapshot.Pets with { Source = "PRIVATE_SOURCE</script>" } },
            "missing-time" => snapshot with { Pets = snapshot.Pets with { ObservedAt = null } },
            "missing-value" => snapshot with { Pets = snapshot.Pets with { Value = null } },
            "unbound" => snapshot with { IsFresh = false },
            "missing-profile" => snapshot with { ProfileId = null },
            "generation" => snapshot with { Generation = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        Assert.Null(CombatGenusObservationPresentation.Create(snapshot));
    }

    [Theory]
    [InlineData("own-pet-genus-lock-update")]
    [InlineData("own-pet-genus-roll-update")]
    public void OwnBoundRefinementsKeepTheSameActorFreeShape(string source)
    {
        var snapshot = Snapshot();
        var dto = CombatGenusObservationPresentation.Create(snapshot with
            { Pets = snapshot.Pets with { Source = source } });
        Assert.NotNull(dto); Assert.Equal(source, dto.Source);
    }

    [Fact]
    public void ExplicitEmptyBoardsAndPanelZeroAreNotRewrittenAsMissingOrThreeInventedPresets()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Pets = snapshot.Pets with { Value = snapshot.Pets.Value! with
            { SelectedGenusPanel = 0, GenusBoards = ImmutableDictionary<byte, CharacterGenusBoard>.Empty } } };
        var dto = Assert.IsType<CombatGenusObservationDto>(CombatGenusObservationPresentation.Create(snapshot));
        Assert.True(dto.IsFresh); Assert.Empty(dto.Boards); Assert.Equal((byte)0, dto.SelectedPanel);
    }

    [Fact]
    public void PartialScopeRetainsItsStatusAndEveryDtoCollectionIsFrozen()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Pets = snapshot.Pets with { Status = NetworkCombatSourceStatus.Partial } };
        var dto = Assert.IsType<CombatGenusObservationDto>(CombatGenusObservationPresentation.Create(snapshot));
        Assert.Equal(NetworkCombatSourceStatus.Partial, dto.Status);
        Assert.Throws<NotSupportedException>(() => ((IList<CombatGenusBoardObservationDto>)dto.Boards).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CombatGenusPanelObservationDto>)dto.Boards[0].Panels).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CombatGenusSlotObservationDto>)dto.Boards[0].Panels[0].Slots).Clear());
        var before = CombatGenusObservationPresentation.Serialize(dto);
        snapshot = snapshot with { Pets = snapshot.Pets with { Value = snapshot.Pets.Value! with
            { GenusBoards = ImmutableDictionary<byte, CharacterGenusBoard>.Empty } } };
        Assert.Equal(before, CombatGenusObservationPresentation.Serialize(dto));
        Assert.NotEmpty(dto.Boards); Assert.Empty(CombatGenusObservationPresentation.Create(snapshot)!.Boards);
    }

    [Fact]
    public async Task DebugScriptIsOptInAndRequiresTheCurrentBoundOwnSource()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        async Task<string> Render(CharacterCombatSnapshot snapshot, bool enabled) => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<CombatGenusObservationData>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(CombatGenusObservationData.Snapshot)] = snapshot, [nameof(CombatGenusObservationData.Enabled)] = enabled }));
            return component.ToHtmlString();
        });
        Assert.Empty(await Render(Snapshot(), false));
        Assert.Empty(await Render(CharacterCombatSnapshot.Empty, true));
        Assert.Empty(await Render(Snapshot() with { Pets = Snapshot().Pets with { IsFresh = false } }, true));
        var html = await Render(Snapshot(), true);
        Assert.Contains("data-genus-observation", html); Assert.Contains("9007199254740993", html);
        Assert.DoesNotContain("PRIVATE_", html); Assert.DoesNotContain("&quot;", html);
    }
}
