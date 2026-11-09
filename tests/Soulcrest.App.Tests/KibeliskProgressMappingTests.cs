using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class KibeliskProgressMappingTests
{
    private const int SafeHaven = 421100100, SafeHavenStage = 421100101, Aggregate = 421106200;
    private static readonly DateTimeOffset At = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private static int Build => AchievementDefinitionCatalog.Default.MetadataBuild;

    // Finite public expectations: exact family, exact stage, definition fingerprint and goal.
    // No family ranges, counterpart arithmetic or source-path fixture can widen this scope.
    private sealed record ExpectedBinding(int FamilyId, int StageId, string Signature, long Goal);
    private static readonly ExpectedBinding[] Expected =
    [
        new(421100100, 421100101, "2236a86d41ed55a1190d35b055758c2023df4d9094c9b3c54872b1e36ca0fca4", 1),
        new(421100200, 421100201, "63a2688123cb5948e5047c9e94e0c7247ddd8d16eaf100c5055b701ea36d415c", 1),
        new(421100300, 421100301, "083b298e8955ec8e918d759c444c8479559c53a5e99f01cfd685ffb8a44de7da", 1),
        new(421100400, 421100401, "8b562974be655305c03cb697bc42c7adcfd465c2ded305278a17beb77d839a51", 1),
        new(421100500, 421100501, "221be3a344ee01864870c2fcdffe79b2cc4b6314578947dbfe999cfd3bb8be19", 1),
        new(421100600, 421100601, "e56a48f95400af8fde82ca7bbfe38c73ac5b9bb79fe08c4822bab7bfd90e6c8e", 1),
        new(421100700, 421100701, "e8726038bd0357ac1cf4c35fab2a82289d40149984d5a4de72d27e3526067ed3", 1),
        new(421100800, 421100801, "e15fcb43559fa66487e89b3a7de90e59c36b26bf7157b4552879b42308a06320", 1),
        new(421100900, 421100901, "568bdd22e3757cd90663b419491b078d9556af34e39c25b05cd1fd18c6bdbf25", 1),
        new(421101000, 421101001, "778391110f9d8527e57a1cf1f5ed41525534c11094eb89ab929bcc1fb34796a5", 1),
        new(421101100, 421101101, "ac149d18c2cbca24ed1d284125e3f02bc312a1c53dcf19540b2f6e82b00d5011", 1),
        new(421101200, 421101201, "9929a5b189641ca4e5a67b3005553369b2f09bf1f115b1969ecad18271e159cc", 1),
        new(421101300, 421101301, "ba7106b0e2741543d446c128b31e45dc48a24aa40fbdb35e79b3be3b4dad4be6", 1),
        new(421101400, 421101401, "8ed07a796e5eadfb36bf094b4cc3cab3ef5c2c58ac89989188d2816702677355", 1),
        new(421101500, 421101501, "9efc5518f9ad7585c3551674d8ad7853a44f8239b1feee77505e6547d7e5c32e", 1),
        new(421101600, 421101601, "3c65d1e52d937d83887409bad941957e797c588a1e17707ce4be40a3b02957ec", 1),
        new(421101700, 421101701, "e58a6466414c5d6b2ef127b234a8f180283e8d4c2f1213744835a4066b0efa2f", 1),
        new(421101800, 421101801, "9a7b4d16bb23afe566d39df3ab1bf6322b2e5d46cb100204dc369e780f686af1", 1),
        new(421101900, 421101901, "e693601e29fdb232f370f3b90eb84d478b023855ed4116f1be4f5232b2381363", 1),
        new(421102000, 421102001, "81a801badd415ff2f708c5da7a29fd35161028391b92e6ac965cd0e8b5547fea", 1),
        new(421102100, 421102101, "62e1f40f9f025291c81cb56d34e1cde64a593a90921aac9b548ff90f2a4693bb", 1),
        new(421102200, 421102201, "c75d180a43e21200d18d1e16f161aac880dc7c40900a5c18b68eab44ef7fc67b", 1),
        new(421102300, 421102301, "c76f6e3fd230a55c4725059494ff4b7d22ca4b186508bcfc1429778cdcc6bbbc", 1),
        new(421102400, 421102401, "d3fb0fbbcc6fab0a8a4b9165ec2d95234f96a8ba1601255bd8c656b1e9ecc6f9", 1),
        new(421102500, 421102501, "5de6187d6ce23d2d8f15eeca5042e204f90cebbda89a6e4549fa85fa5c7bbcd0", 1),
        new(421102600, 421102601, "a8e90473719a6a08f1655674697a288cef2a20a568c06bb995d5684fe4ad6409", 1),
        new(421102700, 421102701, "01ca53d736b3235b3cb19d4d1f73dda3f636de08ef5dcf14783d92bc0643a2f2", 1),
        new(421102800, 421102801, "590539035781c4b863454841a409f84451ede72c87e2c1a67906da0a7df20982", 1),
        new(421102900, 421102901, "059194f7f5c17e5c7227aab25ad03de2969c908b44c6b5c537fa7c986e2db2fd", 1),
        new(421103000, 421103001, "db198006c39c896fbf3d59a7e786e7f75731db07f82d570f820fea66df6cffde", 1),
        new(421103100, 421103101, "4b23ebd52b7dbe346bf67d6cfee1f484dfbbb948e25e00d078427e9e7b183077", 1),
        new(421103200, 421103201, "b61edcacc541757182025153aa95ce485524ff0c588f69aa4e079a51b76ace55", 1),
        new(421103300, 421103301, "65d51e61bcdc72b68c69edc78b9a805c5079a044d2bce625e43212bcd415aa8b", 1),
        new(421103400, 421103401, "54493007da810b328e0e2d7927d8b341821adab0e4c2770070840035da78b6e6", 1),
        new(421103500, 421103501, "0701642c6247d544ec41d21fceaaae4665703987cb902d94640e47ddc869780a", 1),
        new(421103600, 421103601, "f21dc378ba32d17ea44166110ab2480319f362a5b58e75bd9c9e625232afb784", 1),
        new(421103700, 421103701, "b07d5814cdd97f17eefc722936be26e78dee748c1b94502dc0df982d306373f7", 1),
        new(421103800, 421103801, "cc72b18472915f22c3c5cef5800ecf34d77c94525a7db86514ab44930a9c916c", 1),
        new(421103900, 421103901, "c3ed9595cc697059387d991952dcbece054ae8d82cc9153f46555ccc2e5cd321", 1),
        new(421104000, 421104001, "e1171f627694f80164bce44cd8a670de76fb2004b093a33c6e482979672f86d4", 1),
        new(421104100, 421104101, "abfaa4afafc29796fb66d8e36c0ba1ff28b92ff6ae2355c2f9d88a2b70831786", 1),
        new(421104200, 421104201, "c30b2e23eb281729499a29a9809eb2d626aa6f65ca289d506f56cadf42da03ad", 1),
        new(421104300, 421104301, "bba77cfb8016b62748053e07014b966314a54f89a486a84c2745cf69cbeb570a", 1),
        new(421104400, 421104401, "4b70412771aa7ed1bebb5cd4dd010dd5b3e93669a13cccb602d0fe04b0fb99fd", 1),
        new(421104500, 421104501, "8159f473d189e348bb633d32f2b810cd1fd2ca5bae9bc999a2e7f959cfe31860", 1),
        new(421104600, 421104601, "8278dd341a8f99563c2d9037126874d5b7878463cb296df9643252c79192c670", 1),
        new(421104700, 421104701, "6ce7bb8829a9c04240c2c1f614b1748ff2b48ac4c8511e4a0f5a9d0f536feb24", 1),
        new(421104800, 421104801, "c748cfd33b3c041c8ec1e4b5ccfd5696cd18d51825868f39ac6bd10c9b035c19", 1),
        new(421104900, 421104901, "d20eca9f570277dccf64345418f0c25a9406970c18d3d39afd3b550a71ea7f63", 1),
        new(421105000, 421105001, "3b1cc25a638651a2cf2f812682612216a13c77e0ce6fdf54a6be41e8c5c83050", 1),
        new(421105100, 421105101, "c0930a12eb2071abd82b65f994b4b86e2c2f1099608815e4230415568bc557c7", 1),
        new(421105200, 421105201, "d9ade4cc23a55646e1386794994d405f100317e925fd99bb1a95956e7902d761", 1),
        new(421105300, 421105301, "65cc048030a08d748b835e8aad3b07f3d8fde3495bd10f8b0f96143531a271ce", 1),
        new(421105400, 421105401, "f42fe5b789700d375902bec9add7a93c5830bc3026b4fd0fc5daa220f27eb419", 1),
        new(421105500, 421105501, "8cef2d06a53128474396eaa56e180d97f218d3b6bbc23c00b0c68d69324d358f", 1),
        new(421105600, 421105601, "563be1ad31a64c44baa4b4f7dd76e865ee9605385a63b6e7d6456e1e457ba558", 1),
        new(421105700, 421105701, "b4b70f544c0abcc968e67100d4518c030ab1f91bfaced43593e42ec4697c0976", 1),
        new(421105800, 421105801, "86e2edd2e6ad80e729cc9e010c0829cd1a7bf7fa5de927bd0688b99cd7ed22d2", 1),
        new(421105900, 421105901, "52f1c6a7910b776d398de0ab76ee317917870dfd0cb66235894cffbec7993456", 1),
        new(421106000, 421106001, "6f951e3c6e2863d8e92656bed6bdd9363be4e4a66e81fc86a88c3066ec5a310e", 1),
        new(421106100, 421106101, "e6d0faa22049970c211165b5860d1e2ca391b092da81cf262c0def48178d7d7e", 1),
    ];

    [Fact]
    public void AllSixtyOneBindingsMatchTheIndependentExplicitNativeWhitelist()
    {
        var thresholds = AchievementProgressThresholdCatalog.Default;
        Assert.Equal(496, thresholds.Families.Count);
        Assert.Equal(2048, thresholds.Families.Sum(family => family.Stages.Count));
        Assert.Equal(330, thresholds.Families.Count(family => family.CounterSemantics == AchievementProgressCounterSemantics.Cumulative));
        Assert.Equal(166, thresholds.Families.Count(family => family.ModelCurrentState));
        Assert.Equal(61, Expected.Length);
        Assert.Equal(61, Expected.Select(row => row.FamilyId).Distinct().Count());
        var definitions = AchievementDefinitionCatalog.Default;
        var display = CombatAchievementDisplayCatalog.Default;
        var actualGroup = thresholds.Families.Where(family => display.Find(family.Id)?.GroupId == 400000302
            && definitions.Find(family.Id)?.SemanticGroup == "object-interactions")
            .Select(family => family.Id).Order();
        Assert.Equal(Expected.Select(row => row.FamilyId).Order(), actualGroup);
        foreach (var row in Expected)
        {
            var threshold = thresholds.FindFamily(row.FamilyId)!;
            var definition = definitions.Find(row.FamilyId)!;
            Assert.Equal(AchievementArea.Normal, definition.Area);
            Assert.Equal(AchievementRole.PlayerFacing, definition.Role);
            Assert.Equal("object-interactions", definition.SemanticGroup);
            Assert.Equal(row.Signature, definition.Signature);
            Assert.Equal(row.Signature, threshold.DefinitionSignature);
            Assert.Equal(new[] { row.StageId }, definition.StageIds);
            Assert.Equal(new[] { row.StageId }, display.Find(row.FamilyId)!.Stages.Select(stage => stage.Id));
            Assert.True(threshold.ModelCurrentState);
            Assert.Equal(AchievementProgressCounterSemantics.CurrentState, threshold.CounterSemantics);
            var stage = Assert.Single(threshold.Stages);
            Assert.Equal(row.StageId, stage.Id); Assert.Equal(row.Goal, stage.Target);
        }
    }

    [Fact]
    public void AllConfirmedRowsHaveRegularOneOfOneProgressAndIndependentClaimedFacts()
    {
        var raw = AllRows(1, 1);
        var direct = new AchievementRuleEvaluator().Evaluate(raw, Build, 28, 11);
        var legacy = CombatAchievementProgressProjection.Create(Snapshot(raw), Build);
        foreach (var result in new[] { direct, legacy })
        {
            Assert.Equal(61, result.Families.Count);
            foreach (var row in Expected)
            {
                var family = result.Find(row.FamilyId)!;
                Assert.NotEqual(CombatKnowledgeStatus.Conflict, family.Status);
                Assert.Equal(row.StageId, family.CurrentStageId);
                var facts = Assert.Single(family.Stages).Value;
                Assert.Equal(1, facts.Current); Assert.Equal(1, facts.Target);
                Assert.Equal(CombatAchievementProgressUnit.Count, facts.Unit);
                Assert.True(facts.RequirementsMetNow); Assert.True(facts.Completed); Assert.True(facts.Claimed);
            }
        }
        Assert.All(raw.Achievements.Values, row => Assert.Equal(1, row.RewardedStep));
        Assert.All(raw.Achievements.Values, row => Assert.Equal(0, row.CompleteTime));
    }

    [Fact]
    public void AZeroCounterDoesNotEstablishNeverCompletedOrAcquireMeaningFromOpaqueCompleteTime()
    {
        var raw = AllRows(0, 0, long.MaxValue);
        var result = new AchievementRuleEvaluator().Evaluate(raw, Build, 28, 11);
        foreach (var row in Expected)
        {
            var family = result.Find(row.FamilyId)!;
            var stage = family.Find(row.StageId)!;
            Assert.NotEqual(CombatKnowledgeStatus.Conflict, family.Status);
            Assert.Equal(0, stage.Current); Assert.Equal(1, stage.Target);
            Assert.False(stage.RequirementsMetNow); Assert.Null(stage.Completed); Assert.False(stage.Claimed);
        }
        Assert.All(raw.Achievements.Values, row => Assert.Equal(long.MaxValue, row.CompleteTime));
    }

    [Fact]
    public void MissingAndNegativeCountersNeverBecomeZeroOrRegularGoalFacts()
    {
        var evaluator = new AchievementRuleEvaluator();
        Assert.Empty(evaluator.Evaluate(Raw(), Build, 28, 11).Families);
        var raw = AllRows(-17, 0);
        var result = evaluator.Evaluate(raw, Build, 28, 12);
        foreach (var row in Expected)
        {
            var facts = result.Find(row.FamilyId)!.Find(row.StageId)!;
            Assert.Null(facts.Current); Assert.Null(facts.Target); Assert.Null(facts.Completed);
            Assert.Null(facts.RequirementsMetNow); Assert.False(facts.Claimed);
        }
        Assert.All(raw.Achievements.Values, row => Assert.Equal(-17, row.Count));
    }

    [Fact]
    public void PermanentClaimsRemainTrueWhenTheCurrentCounterAndRawPrefixAreZero()
    {
        var raw = AllRows(0, 0);
        var evaluator = new AchievementRuleEvaluator();
        var claimed = Expected.Select(row => row.StageId).ToImmutableHashSet();
        var result = evaluator.Evaluate(raw, Build, 28, 11, knownClaimedStageIds: claimed);
        foreach (var row in Expected)
        {
            var family = result.Find(row.FamilyId)!;
            var facts = family.Find(row.StageId)!;
            Assert.NotEqual(CombatKnowledgeStatus.Conflict, family.Status);
            Assert.False(facts.RequirementsMetNow); Assert.True(facts.Completed); Assert.True(facts.Claimed);
            Assert.Equal("permanent", evaluator.QualifiedPeriodId(row.FamilyId));
        }
        Assert.All(raw.Achievements.Values, row => Assert.Equal(0, row.RewardedStep));
    }

    [Fact]
    public void EveryElyosCounterpartRemainsOutsideTheQualifiedKibeliskGoalScope()
    {
        var thresholds = AchievementProgressThresholdCatalog.Default;
        Assert.Null(thresholds.FindFamily(321106200));
        var definitions = AchievementDefinitionCatalog.Default;
        var display = CombatAchievementDisplayCatalog.Default;
        Assert.Equal("achievement-dependencies", definitions.Find(Aggregate)!.SemanticGroup);
        var elyos = definitions.Definitions.Where(definition => definition.Area == AchievementArea.Normal
            && definition.Role == AchievementRole.PlayerFacing && definition.SemanticGroup == "object-interactions"
            && display.Find(definition.Id)?.GroupId == 300000302).Select(definition => definition.Id).ToArray();
        Assert.Equal(61, elyos.Length);
        Assert.All(elyos, id => Assert.Null(thresholds.FindFamily(id)));
        var raw = Raw(elyos.Append(321106200).Select(id => (Id: id, Count: 1, Prefix: (byte)0)).ToArray());
        var result = new AchievementRuleEvaluator().Evaluate(raw, Build, 28, 11);
        foreach (var family in result.Families.Values)
            Assert.All(family.Stages.Values, facts => { Assert.Null(facts.Current); Assert.Null(facts.Target); });
    }

    [Fact]
    public void MetadataBuildAndDefinitionSignatureRemainRequiredForTheseNewGoals()
    {
        var raw = AllRows(1, 1);
        Assert.Empty(new AchievementRuleEvaluator().Evaluate(raw, Build + 1, 28, 11).Families);
        Assert.Empty(CombatAchievementProgressProjection.Create(Snapshot(raw), Build + 1).Families);
        var document = JsonNode.Parse(ThresholdDocument())!.AsObject();
        var row = document["families"]!.AsArray().Single(family => family!["id"]!.GetValue<int>() == SafeHaven)!;
        row["definitionSignature"] = new string('f', 64);
        Assert.Throws<InvalidDataException>(() => AchievementProgressThresholdCatalog.FromJson(document.ToJsonString()));
    }

    [Fact]
    public async Task FullyClaimedBaselineCreatesNoAutomaticOrPinnedTrackerCardAndKeepsThePreference()
    {
        using var fixture = new Fixture();
        fixture.Favorites.Toggle(fixture.Binding, SafeHaven);
        fixture.Source.Emit(AllRows(1, 1), NetworkAchievementObservationKind.Baseline);
        await fixture.Drain();
        Assert.Empty(fixture.Tracker.Snapshot.Rows);
        Assert.True(fixture.Favorites.Get(fixture.Binding).Contains(SafeHaven));
        Assert.Equal(61, fixture.Service.View.ObservedValues.Count);
        Assert.Empty(fixture.Service.View.Activities);
        Assert.Equal(61, fixture.Service.View.Milestones.Count(item => item.Kind == AchievementMilestoneKind.Completed));
        Assert.Equal(61, fixture.Service.View.Milestones.Count(item => item.Kind == AchievementMilestoneKind.RewardClaimed));
        Assert.DoesNotContain(fixture.Service.View.Milestones, item => item.NotificationEligible);
        fixture.Source.Emit(AllRows(1, 1), NetworkAchievementObservationKind.Baseline);
        await fixture.Drain();
        Assert.Empty(fixture.Tracker.Snapshot.Rows);
        Assert.True(fixture.Favorites.Get(fixture.Binding).Contains(SafeHaven));
        Assert.Equal(122, fixture.Service.View.Milestones.Length);
    }

    [Fact]
    public async Task SyntheticZeroToOneUsesTheExistingLiveCompletionAndClaimFlow()
    {
        using var fixture = new Fixture();
        fixture.Favorites.Toggle(fixture.Binding, SafeHaven);
        fixture.Source.Emit(Raw((SafeHaven, 0, 0)), NetworkAchievementObservationKind.Baseline);
        await fixture.Drain();
        Assert.Empty(fixture.Service.View.Milestones);
        Assert.Null(fixture.Service.View.Progress.Find(SafeHaven)!.Find(SafeHavenStage)!.Completed);
        fixture.Source.Emit(Raw((SafeHaven, 1, 0)), NetworkAchievementObservationKind.Update);
        await fixture.Drain();
        var completion = Assert.Single(fixture.Service.View.Milestones);
        Assert.Equal(AchievementMilestoneKind.Completed, completion.Kind);
        Assert.Equal(SafeHavenStage, completion.StageId);
        Assert.Equal("Live", completion.Origin); Assert.True(completion.NotificationEligible);
        var tracker = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(SafeHaven, tracker.FamilyId); Assert.Equal(SafeHavenStage, tracker.StageId);
        Assert.Equal(1, tracker.Current); Assert.Equal(1, tracker.Target);
        Assert.True(tracker.IsAnimating); Assert.True(tracker.HasUnclaimedRewards);
        fixture.Source.Emit(Raw((SafeHaven, 1, 1)), NetworkAchievementObservationKind.Reward);
        await fixture.Drain();
        Assert.Empty(fixture.Tracker.Snapshot.Rows);
        Assert.True(fixture.Favorites.Get(fixture.Binding).Contains(SafeHaven));
        var claim = Assert.Single(fixture.Service.View.Milestones, item => item.Kind == AchievementMilestoneKind.RewardClaimed);
        Assert.True(claim.NotificationEligible);
        fixture.Source.Emit(Raw((SafeHaven, 1, 1)), NetworkAchievementObservationKind.Update);
        await fixture.Drain();
        Assert.Empty(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(2, fixture.Service.View.Milestones.Length);
    }

    private static CharacterAchievementState AllRows(int count, byte prefix, long opaqueTime = 0) => new(
        Expected.ToImmutableDictionary(row => row.FamilyId, _ => new CharacterAchievement(count, opaqueTime, prefix)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);

    private static CharacterAchievementState Raw(params (int Id, int Count, byte Prefix)[] rows) => new(
        rows.ToImmutableDictionary(row => row.Id, row => new CharacterAchievement(row.Count, 0, row.Prefix)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);

    private static CharacterCombatSnapshot Snapshot(CharacterAchievementState raw) => new()
    {
        ProfileId = "SYNTHETIC-KIBELISK-PROFILE", IsFresh = true, Generation = 28, Sequence = 11,
        Achievements = new(raw, NetworkCombatSourceStatus.Complete, true, At, 11, "own-achievement-list")
    };

    private static string ThresholdDocument()
    {
        using var stream = typeof(AchievementProgressThresholdCatalog).Assembly
            .GetManifestResourceStream("Soulcrest.AchievementProgressThresholds.json")
            ?? throw new InvalidOperationException("Missing bundled achievement goals.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-kibelisk-goals-" + Guid.NewGuid().ToString("N"));
        internal AchievementProfileBinding Binding { get; } = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        internal Source Source { get; } = new();
        internal AchievementFavoriteService Favorites { get; }
        internal AchievementStateService Service { get; }
        internal AchievementTrackerService Tracker { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Favorites = new(Path.Combine(_root, "favorites"));
            Service = new(Source, new AchievementStateStore(Path.Combine(_root, "history")), _ => Binding, () => Binding);
            Tracker = new(Service, Favorites);
        }
        internal async Task Drain()
        {
            await Service.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Tracker.Tick();
        }
        public void Dispose()
        {
            Tracker.Dispose(); Service.Dispose(); Favorites.Dispose();
            var path = Path.GetFullPath(_root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private sealed class Source : INetworkAchievementSource
    {
        private long _sequence;
        private readonly Guid _instance = Guid.NewGuid();
        private readonly OwnCharacterContext _owner = new(new(101, 7, 42, "Synthetic owner"), 1);
        public NetworkAchievementObservation AchievementSnapshot { get; private set; } = new(0, 0, At, null,
            NetworkCombatDomain<CharacterAchievementState>.Unknown, NetworkAchievementObservationKind.Invalidated);
        public AchievementIngressDiagnostics AchievementDiagnostics { get; } = new();
        public event Action<NetworkAchievementObservation>? AchievementObserved;
        internal void Emit(CharacterAchievementState raw, NetworkAchievementObservationKind kind)
        {
            var sequence = Interlocked.Increment(ref _sequence);
            var at = At.AddSeconds(sequence);
            var source = kind switch
            {
                NetworkAchievementObservationKind.Update => "own-achievement-update",
                NetworkAchievementObservationKind.Reward => "own-achievement-reward",
                _ => "own-achievement-list"
            };
            AchievementSnapshot = new(sequence, _owner.Generation, at, _owner,
                new(raw, NetworkCombatSourceStatus.Complete, true, at, sequence, source), kind)
                { SourceInstanceId = _instance };
            AchievementObserved?.Invoke(AchievementSnapshot);
        }
    }
}
