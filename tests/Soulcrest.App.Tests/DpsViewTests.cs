using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DpsViewTests
{
    [Theory]
    [InlineData("de", "Noch keine Soulcrest-Kämpfe gespeichert.")]
    [InlineData("en", "No Soulcrest encounters stored yet.")]
    public async Task EmptyHistoryIsExplicitInBothLanguages(string language, string expected)
    {
        var html = await RenderAsync(new FakeReader(), language);
        Assert.Contains(expected, html);
        Assert.Contains("data-dps-empty", html);
        Assert.DoesNotContain("data-dps-attempt", html);
    }

    [Fact]
    public async Task UnknownValuesIncompleteCoverageAndFormerMembersAreVisible()
    {
        var run = DpsHistoryFixture.Run();
        var reader = new FakeReader(run);
        var html = await RenderAsync(reader, "en");
        Assert.Contains("Incomplete combat report", html);
        Assert.Contains("Complete damage coverage has not been confirmed.", html);
        Assert.Contains("Member A", html);
        Assert.Contains("Member B", html);
        Assert.Contains("Member C", html);
        Assert.Contains("Member D", html);
        Assert.Contains("Skill fixture-skill-a", html);
        Assert.Contains("123", html);
        Assert.Contains("—", html);
        Assert.Contains("5,000", html);
        Assert.DoesNotContain("fixture-stream", html);
        Assert.DoesNotContain("fixture-boss", html);
        Assert.Equal(2, Regex.Matches(html, "<td data-dps-player-damage>—</td>").Count);
        Assert.Contains("Observed healing", html);
        Assert.DoesNotContain("Raw healing", html);
        Assert.Contains("Unvollständiger Kampfbericht", await RenderAsync(reader, "de"));
    }

    [Fact]
    public async Task PendingNavigationSelectsTheHistoricalParticipantWithoutStartingCapture()
    {
        var run = DpsHistoryFixture.Run();
        var ui = new UiState();
        ui.ShowCombatHistory(run.Id, run.Attempts[0].Id, "member-c");
        var html = await RenderAsync(new FakeReader(run), "en", ui);
        Assert.Equal("dps", ui.Tab);
        Assert.Null(ui.TakePendingCombatHistoryFocus());
        Assert.Contains("Skill fixture-skill-c", html);
        Assert.DoesNotContain("Skill fixture-skill-a", html);
    }

    [Fact]
    public async Task DamagedHistoryShowsAnErrorInsteadOfAnEmptyHistory()
    {
        var reader = new FakeReader { Error = new(CombatHistoryReadFailure.InvalidData, new InvalidDataException("fixture")) };
        var html = await RenderAsync(reader, "en");
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains("unsupported version", html);
        Assert.DoesNotContain("data-dps-empty", html);
        Assert.Contains("nicht unterstützte Version", await RenderAsync(reader, "de"));
    }

    [Fact]
    public async Task LoadingCanRenderBeforeDiskWorkCompletes()
    {
        var previous = UiText.Language;
        UiText.Language = "en";
        var ready = new TaskCompletionSource<CombatHistoryReadOnlyOverview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new FakeReader { Pending = ready.Task };
        await using var provider = Services(reader, new UiState());
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        try
        {
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<DpsView>());
            var loading = await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
            Assert.Contains("Loading combat history", loading);
            Assert.Contains("aria-busy=\"true\"", loading);
            Assert.DoesNotContain("data-dps-empty", loading);
            ready.SetResult(new(new(), 0));
            await root.QuiescenceTask;
            var loaded = await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
            Assert.Contains("No Soulcrest encounters stored yet.", loaded);
            Assert.Contains("aria-busy=\"false\"", loaded);
        }
        finally { ready.TrySetResult(new(new(), 0)); UiText.Language = previous; }
    }

    private static async Task<string> RenderAsync(FakeReader reader, string language, UiState? ui = null)
    {
        var previous = UiText.Language;
        UiText.Language = language;
        try
        {
            await using var provider = Services(reader, ui ?? new());
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<DpsView>()).ToHtmlString()));
            if (Environment.GetEnvironmentVariable("SOULCREST_DPS_UI_PREVIEW") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                var css = string.Join("\n", new[] { "app.css", "dps.css" }.Select(name => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "css", name))));
                var state = reader.Error is not null ? "error" : reader.HasRun ? "run" : "empty";
                File.WriteAllText(Path.Combine(output, "preview-" + state + "-" + language + ".html"),
                    "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Soulcrest DPS preview</title><style>" + css + "</style></head><body>" + html + "</body></html>");
            }
            return html;
        }
        finally { UiText.Language = previous; }
    }

    private static ServiceProvider Services(FakeReader reader, UiState ui) => new ServiceCollection()
        .AddLogging().AddSingleton<ICombatHistoryReader>(reader).AddSingleton(ui).BuildServiceProvider();

    private sealed class FakeReader(CombatRunDocument? run = null) : ICombatHistoryReader
    {
        public bool HasRun => run is not null;
        public CombatHistoryReadException? Error { get; init; }
        public Task<CombatHistoryReadOnlyOverview>? Pending { get; init; }
        public Task<CombatHistoryReadOnlyOverview> LoadOverviewAsync(CancellationToken cancellationToken = default) =>
            Error is not null ? Task.FromException<CombatHistoryReadOnlyOverview>(Error) : Pending ?? Task.FromResult(new CombatHistoryReadOnlyOverview(new()
            {
                Runs = run is null ? [] : [new(run.Id, run.DungeonKey, run.StartedAtUtc, run.EndedAtUtc, false, run.Attempts.Length)]
            }, 0));
        public Task<CombatHistoryRunView> LoadRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CombatHistoryRunView(run!, run!.Attempts.Select(attempt => CombatProjection.Project(attempt)).ToArray()));
    }
}
