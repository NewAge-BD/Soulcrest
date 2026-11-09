using System.Collections.Immutable;
using System.Net;
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

/// <summary>Production component with anonymous goal examples; no window, capture or profile store.</summary>
public sealed class KibeliskViewRenderDiagnostic
{
    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de", "kibelisk")]
    [InlineData("en", "kibelisk")]
    [InlineData("de", "exploration")]
    [InlineData("en", "exploration")]
    [InlineData("de", "custom")]
    [InlineData("en", "custom")]
    [InlineData("de", "remaining-custom")]
    [InlineData("en", "remaining-custom")]
    [InlineData("de", "screenshots")]
    [InlineData("en", "screenshots")]
    public async Task ExportConfirmedGoalCards(string language, string showcase)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_KIBELISK_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media"))
            + Path.DirectorySeparatorChar, folder, StringComparison.OrdinalIgnoreCase);
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            var native = CombatAchievementDisplayCatalog.Default;
            var examples = showcase switch
            {
                "kibelisk" => new[] { (Id: 421100100, Count: 1, Claim: (byte)1), (Id: 421100200, Count: 1, Claim: (byte)1), (Id: 421102400, Count: 1, Claim: (byte)1) },
                "exploration" => new[] { (Id: 421106200, Count: 61, Claim: (byte)1), (Id: 420100100, Count: 1, Claim: (byte)0), (Id: 420100800, Count: 16, Claim: (byte)4) },
                "custom" => new[] { (Id: 403100300, Count: 200, Claim: (byte)7), (Id: 403100400, Count: 32, Claim: (byte)5),
                    (Id: 403100600, Count: 50, Claim: (byte)7), (Id: 403100800, Count: 44, Claim: (byte)6),
                    (Id: 403101000, Count: 42, Claim: (byte)6), (Id: 403101700, Count: 5, Claim: (byte)1) },
                "remaining-custom" => new[] { (Id: 403100500, Count: 20, Claim: (byte)6),
                    (Id: 403101200, Count: 10, Claim: (byte)4), (Id: 403100200, Count: 16, Claim: (byte)5) },
                "screenshots" => new[] { (Id: 443100300, Count: 3, Claim: (byte)3),
                    (Id: 430100400, Count: 3, Claim: (byte)3), (Id: 445100100, Count: 41, Claim: (byte)0),
                    (Id: 400100100, Count: 6, Claim: (byte)6), (Id: 440100800, Count: 536, Claim: (byte)6),
                    (Id: 401100300, Count: 6000, Claim: (byte)7) },
                _ => throw new ArgumentOutOfRangeException(nameof(showcase))
            };
            var ids = examples.Select(row => row.Id).ToArray();
            var rows = ids.Select(id => native.Find(id)!).ToArray();
            var groups = rows.Select(row => row.GroupId).OfType<int>().ToHashSet();
            var catalog = CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
            {
                schema = 1, metadataBuild = native.MetadataBuild, referenceOnly = true,
                doesNotEstablishCompletionOrClaim = true, categories = native.Categories,
                groups = native.Groups.Where(group => groups.Contains(group.Id)).ToArray(), achievements = rows
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            var owner = new AchievementProfileBinding(new string('a', 32), new string('b', 32));
            var at = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
            var raw = new CharacterAchievementState(examples.ToImmutableDictionary(row => row.Id,
                row => new CharacterAchievement(row.Count, long.MaxValue, row.Claim)), ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
            var snapshot = new CharacterCombatSnapshot
            {
                ProfileId = owner.ProfileId, IsFresh = true, Generation = 1, Sequence = 1,
                Achievements = new(raw, NetworkCombatSourceStatus.Complete, true, at, 1, "own-achievement-list")
            };
            var state = new AchievementViewState
            {
                Binding = owner, ProfileId = owner.ProfileId, Source = AchievementViewSource.Live,
                MetadataBuild = native.MetadataBuild, Generation = 1, Sequence = 1, ObservedAt = at,
                Progress = new AchievementRuleEvaluator().Evaluate(raw, native.MetadataBuild, 1, 1),
                ObservedFamilyIds = ids.ToImmutableHashSet(),
                ObservedValues = examples.ToImmutableDictionary(row => row.Id, row => row.Count)
            };
            var services = new ServiceCollection(); services.AddLogging();
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var component = await renderer.RenderComponentAsync<CombatAchievementsView>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CombatAchievementsView.Snapshot)] = snapshot,
                    [nameof(CombatAchievementsView.AchievementState)] = state,
                    [nameof(CombatAchievementsView.Catalog)] = catalog,
                    [nameof(CombatAchievementsView.Faction)] = Factions.Asmodian,
                    [nameof(CombatAchievementsView.Favorites)] = new AchievementFavoriteSnapshot(owner, []),
                    [nameof(CombatAchievementsView.ToggleFavorite)] = EventCallback.Factory.Create<int>(this, _ => { })
                }));
                return component.ToHtmlString();
            });
            Assert.DoesNotContain(owner.ProfileId, html);
            Assert.DoesNotContain(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), html);
            var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
            var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css" }
                .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, showcase + "-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<base href=\"" + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri)
                + "\"><style>" + css + "\nhtml,body{height:auto}body{padding:24px}</style></head><body>" + html + "</body></html>");
        }
        finally { UiText.Language = previous; }
    }
}
