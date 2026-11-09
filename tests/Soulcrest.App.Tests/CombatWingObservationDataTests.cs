using System.Collections.Immutable;
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

public sealed class CombatWingObservationDataTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static CharacterCombatSnapshot Snapshot() => new()
    {
        ProfileId = "PRIVATE_PROFILE", ClassId = "PRIVATE_CLASS", IsFresh = true, Generation = 7, Sequence = 12,
        WingOwnership = new(new(ImmutableDictionary<int, byte>.Empty.Add(40101100, 0).Add(40100100, 8)),
            NetworkCombatSourceStatus.Complete, true, At, 9, "own-wing-ownership-list"),
        EquippedWing = new(new(40101100, 0, 40100100), NetworkCombatSourceStatus.Complete, true, At, 10, "own-entry-wing-state")
    };

    [Fact]
    public void ExplicitDiagnosticShapeIsActorFreeAndRetainsSeparateEquipmentAndSkin()
    {
        var dto = Assert.IsType<CombatWingObservationDto>(CombatWingObservationPresentation.Create(Snapshot()));
        var json = CombatWingObservationPresentation.Serialize(dto);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "equipped", "generation", "ownership", "schema", "sequence" }, document.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain("PRIVATE_", json);
        Assert.DoesNotContain("profile", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entity", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(40101100, dto.Equipped.WingId);
        Assert.Equal(40100100, dto.Equipped.SkinId);
        Assert.Equal((byte)8, Assert.Single(dto.Ownership.Wings!, row => row.Id == 40100100).EnchantLevel);
        Assert.Throws<NotSupportedException>(() => ((IList<CombatWingObservedRowDto>)dto.Ownership.Wings!).Clear());
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("gap")]
    [InlineData("conflict")]
    [InlineData("unknown-source")]
    [InlineData("missing-time")]
    [InlineData("generation")]
    public void InvalidDomainsNeverExposeRetainedValues(string variant)
    {
        var snapshot = Snapshot();
        snapshot = variant switch
        {
            "stale" => snapshot with { WingOwnership = snapshot.WingOwnership with { Status = NetworkCombatSourceStatus.Stale }, EquippedWing = snapshot.EquippedWing with { Status = NetworkCombatSourceStatus.Stale } },
            "gap" => snapshot with { WingOwnership = snapshot.WingOwnership with { IsFresh = false }, EquippedWing = snapshot.EquippedWing with { IsFresh = false } },
            "conflict" => snapshot with { WingOwnership = snapshot.WingOwnership with { Status = NetworkCombatSourceStatus.Conflict }, EquippedWing = snapshot.EquippedWing with { Status = NetworkCombatSourceStatus.Conflict } },
            "unknown-source" => snapshot with { WingOwnership = snapshot.WingOwnership with { Source = "PRIVATE_SOURCE</script>" }, EquippedWing = snapshot.EquippedWing with { Source = "PRIVATE_SOURCE" } },
            "missing-time" => snapshot with { WingOwnership = snapshot.WingOwnership with { ObservedAt = null }, EquippedWing = snapshot.EquippedWing with { ObservedAt = null } },
            "generation" => snapshot with { Generation = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        var dto = Assert.IsType<CombatWingObservationDto>(CombatWingObservationPresentation.Create(snapshot));
        Assert.False(dto.Ownership.IsFresh); Assert.Null(dto.Ownership.Wings);
        Assert.False(dto.Equipped.IsFresh); Assert.Null(dto.Equipped.WingId); Assert.Null(dto.Equipped.SkinId); Assert.Null(dto.Equipped.EnchantLevel);
        Assert.DoesNotContain("PRIVATE_", CombatWingObservationPresentation.Serialize(dto));
    }

    [Fact]
    public void ObservedEmptyListIsDifferentFromUnknownAndChangeSourceIsSupported()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { WingOwnership = snapshot.WingOwnership with { Value = new(ImmutableDictionary<int, byte>.Empty) },
            EquippedWing = snapshot.EquippedWing with { Source = "own-wing-change", Value = new(0, null, 0) } };
        var dto = Assert.IsType<CombatWingObservationDto>(CombatWingObservationPresentation.Create(snapshot));
        Assert.True(dto.Ownership.IsFresh); Assert.Empty(dto.Ownership.Wings!);
        Assert.True(dto.Equipped.IsFresh); Assert.Equal(0, dto.Equipped.WingId); Assert.Equal(0, dto.Equipped.SkinId);
    }

    [Fact]
    public async Task DebugScriptIsOptInAndRequiresFreshProfileBinding()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        async Task<string> Render(CharacterCombatSnapshot snapshot, bool enabled) => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<CombatWingObservationData>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(CombatWingObservationData.Snapshot)] = snapshot, [nameof(CombatWingObservationData.Enabled)] = enabled }));
            return component.ToHtmlString();
        });
        Assert.Empty(await Render(Snapshot(), false));
        Assert.Empty(await Render(Snapshot() with { IsFresh = false }, true));
        Assert.Empty(await Render(Snapshot() with { ProfileId = null }, true));
        Assert.Empty(await Render(CharacterCombatSnapshot.Empty, true));
        var html = await Render(Snapshot(), true);
        Assert.Contains("data-wing-observation", html); Assert.DoesNotContain("PRIVATE_", html); Assert.DoesNotContain("&quot;", html);
    }
}
