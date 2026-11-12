using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ItemNameViewTests
{
    [Theory]
    [InlineData("Gespeicherter Gegenstandsname")]
    [InlineData("Saved item label")]
    public async Task KnownItemColorsTheNameWithoutReplacingTheCallersText(string text)
    {
        var html = await Render(new()
        {
            [nameof(ItemName.ItemId)] = 110120003,
            [nameof(ItemName.Text)] = text,
            [nameof(ItemName.Class)] = "selected-label",
            [nameof(ItemName.Title)] = "Caller tooltip",
            [nameof(ItemName.AdditionalAttributes)] = new Dictionary<string, object> { ["aria-label"] = "Item label" }
        });
        Assert.Contains("class=\"item-name selected-label\"", html);
        Assert.Contains("data-item-name=\"true\"", html);
        Assert.Contains("data-item-grade=\"Unique\"", html);
        Assert.Contains("style=\"color:#FFD02B\"", html);
        Assert.Contains(text, html);
        Assert.Contains("title=\"Caller tooltip\"", html);
        Assert.Contains("aria-label=\"Item label\"", html);
    }

    [Fact]
    public async Task ExactUnknownItemRemainsNeutralEvenWhenAnOlderGradeOrKeyIsSupplied()
    {
        var html = await Render(new()
        {
            [nameof(ItemName.ItemId)] = 1,
            [nameof(ItemName.Grade)] = "Epic",
            [nameof(ItemName.Rarity)] = LootItemRarity.Unique,
            [nameof(ItemName.DropKey)] = "Item:110120003",
            [nameof(ItemName.Text)] = "Unknown item"
        });
        Assert.Contains("Unknown item", html);
        Assert.DoesNotContain("style=", html);
        Assert.DoesNotContain("data-item-grade=", html);
    }

    [Fact]
    public async Task ChildContentIsPreservedAndEscapedWithDropKeyPresentation()
    {
        RenderFragment content = builder => builder.AddContent(0, "<Saved & untranslated>");
        var html = await Render(new()
        {
            [nameof(ItemName.DropKey)] = "Item:110120003",
            [nameof(ItemName.Text)] = "Ignored fallback",
            [nameof(ItemName.ChildContent)] = content
        });
        Assert.Contains("&lt;Saved &amp; untranslated&gt;", html);
        Assert.DoesNotContain("Ignored fallback", html);
        Assert.Contains("color:#FFD02B", html);
    }

    [Fact]
    public async Task ExplicitDisplayGradeWorksWhenThereIsNoItemIdentity()
    {
        var html = await Render(new() { [nameof(ItemName.Grade)] = "Rare", [nameof(ItemName.Text)] = "Grade label" });
        Assert.Contains("data-item-grade=\"Rare\"", html);
        Assert.Contains("color:#19E048", html);
    }

    private static async Task<string> Render(Dictionary<string, object?> parameters)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<ItemName>(ParameterView.FromDictionary(parameters));
            return root.ToHtmlString();
        });
    }
}
