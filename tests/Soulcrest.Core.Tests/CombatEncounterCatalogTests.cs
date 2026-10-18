using System.Text;
using System.Text.Json.Nodes;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CombatEncounterCatalogTests
{
    [Fact]
    public void LocalRoleAndSeparateEndEvidenceRemainDescriptiveAndNamesStayNullable()
    {
        var document = ValidDocument();
        Evidence(document, "npc")["sourceRef"] = "missing://synthetic-source-is-never-opened";
        var catalog = Load(document);
        Assert.Equal("synthetic-v1", catalog.CatalogVersion);
        Assert.Equal("global-eu", catalog.Region);
        var dungeon = catalog.FindDungeon(42, 420)!;
        Assert.Null(catalog.FindDungeon(42, 421));
        Assert.Null(dungeon.Names.De);
        var npc = Assert.Single(dungeon.Npcs);
        Assert.Equal(CombatCatalogNpcRole.IntermediateBoss, npc.Role);
        Assert.Equal(CombatCatalogEndProof.MiddleBossReward, npc.EndProof);
        Assert.Equal(new[] { "npc", "reward" }, npc.SourceRefs);
        Assert.Equal(new[] { "reward" }, npc.EndSourceRefs);
        Assert.Equal("missing://synthetic-source-is-never-opened", catalog.Evidence[0].SourceRef);
    }

    [Fact]
    public void CaptureOnlyDungeonAndRewardOnlyNpcDoNotInventNamesRolesOrEndApproval()
    {
        var document = ValidDocument();
        var dungeon = Dungeon(document);
        dungeon["names"] = new JsonObject();
        dungeon["sourceRefs"] = Refs("context");
        var npc = Npc(document);
        npc["names"] = new JsonObject();
        npc["role"] = "unknown";
        npc["sourceRefs"] = Refs("reward");
        npc["endProof"] = "none";
        var result = Load(document).FindDungeon(42, 420)!;
        Assert.Equal(new CombatCatalogNames(null, null), result.Names);
        Assert.Equal(CombatCatalogNpcRole.Unknown, result.Npcs[0].Role);
        Assert.Equal(CombatCatalogEndProof.None, result.Npcs[0].EndProof);
        Assert.Equal(new[] { "reward" }, result.Npcs[0].EndSourceRefs);

        dungeon["names"] = new JsonObject { ["en"] = "Unconfirmed" };
        Assert.Throws<InvalidDataException>(() => Load(document));
        dungeon["names"] = new JsonObject();
        npc["role"] = "intermediateBoss";
        Assert.Throws<InvalidDataException>(() => Load(document));
    }

    [Theory]
    [InlineData("localMapData")]
    [InlineData("localActivityData")]
    public void EitherReviewedMapOrActivityEvidenceCanEstablishTheDungeonJoin(string kind)
    {
        var document = ValidDocument();
        Evidence(document, "map")["kind"] = kind;
        Assert.Equal(CombatCatalogNpcRole.IntermediateBoss, Load(document).Dungeons[0].Npcs[0].Role);
    }

    [Fact]
    public void TerminalEndApprovalRequiresTwoCaptureObservationsOfTheCorrectKind()
    {
        var document = ValidDocument();
        var npc = Npc(document);
        npc["role"] = "finalBoss";
        npc["endProof"] = "dungeonSuccess";
        npc["endSourceRefs"] = Refs("terminal");
        Assert.Equal(CombatCatalogEndProof.DungeonSuccess, Load(document).Dungeons[0].Npcs[0].EndProof);
        Evidence(document, "terminal")["captures"] = 1;
        Assert.Throws<InvalidDataException>(() => Load(document));
        npc["endProof"] = "none"; // A one-capture fact can be retained without granting an end proof.
        Assert.Equal(CombatCatalogEndProof.None, Load(document).Dungeons[0].Npcs[0].EndProof);
    }

    [Theory]
    [InlineData("unknown", "middleBossReward", "reward")]
    [InlineData("boss", "middleBossReward", "reward")]
    [InlineData("finalBoss", "middleBossReward", "reward")]
    [InlineData("intermediateBoss", "dungeonSuccess", "terminal")]
    [InlineData("intermediateBoss", "middleBossReward", "terminal")]
    [InlineData("finalBoss", "dungeonSuccess", "reward")]
    public void RoleAndCaptureKindCannotBeSubstitutedForADifferentEndContract(string role, string proof, string evidence)
    {
        var document = ValidDocument();
        Npc(document)["role"] = role;
        Npc(document)["endProof"] = proof;
        Npc(document)["endSourceRefs"] = Refs(evidence);
        Assert.Throws<InvalidDataException>(() => Load(document));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("missing")]
    [InlineData("unknownRoot")]
    [InlineData("unknownNpc")]
    [InlineData("unknownName")]
    [InlineData("integerEnum")]
    [InlineData("unknownEnum")]
    [InlineData("wrongCase")]
    [InlineData("stringId")]
    [InlineData("invalidId")]
    [InlineData("invalidHash")]
    [InlineData("shortHash")]
    [InlineData("invalidCounts")]
    [InlineData("captureWithoutCapture")]
    [InlineData("missingRef")]
    [InlineData("duplicateRef")]
    [InlineData("emptyRef")]
    [InlineData("emptyName")]
    [InlineData("unnamedRoleSource")]
    public void InvalidOrUnprovenFieldsFailClosed(string change)
    {
        var document = ValidDocument();
        switch (change)
        {
            case "schema": document["schemaVersion"] = 2; break;
            case "missing": Npc(document).Remove("endProof"); break;
            case "unknownRoot": document["amountEvidenceScope"] = "invented-scope"; break;
            case "unknownNpc": Npc(document)["unused"] = 0; break;
            case "unknownName": Npc(document)["names"]!["fr"] = "Synthetic"; break;
            case "integerEnum": Npc(document)["role"] = 1; break;
            case "unknownEnum": Npc(document)["role"] = "worldBoss"; break;
            case "wrongCase": Evidence(document, "npc")["kind"] = "LocalNpcData"; break;
            case "stringId": Npc(document)["npcDataId"] = "700"; break;
            case "invalidId": Dungeon(document)["mapDataId"] = 0; break;
            case "invalidHash": Evidence(document, "npc")["sha256"] = new string('z', 64); break;
            case "shortHash": Evidence(document, "npc")["sha256"] = new string('a', 63); break;
            case "invalidCounts": Evidence(document, "reward")["captures"] = 2; break;
            case "captureWithoutCapture": Evidence(document, "reward")["captures"] = 0; break;
            case "missingRef": Npc(document)["sourceRefs"] = Refs("not-present"); break;
            case "duplicateRef": Npc(document)["sourceRefs"] = Refs("npc", "npc"); break;
            case "emptyRef": Npc(document)["sourceRefs"] = Refs(); break;
            case "emptyName": Npc(document)["names"]!["en"] = " "; break;
            case "unnamedRoleSource": Npc(document)["sourceRefs"] = Refs("reward"); break;
        }
        Assert.Throws<InvalidDataException>(() => Load(document));
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("dungeon")]
    [InlineData("npc")]
    public void DuplicateIdentifiersAndJoinsAreRejected(string category)
    {
        var document = ValidDocument();
        var array = category switch
        {
            "evidence" => document["evidence"]!.AsArray(),
            "dungeon" => document["dungeons"]!.AsArray(),
            _ => Dungeon(document)["npcs"]!.AsArray()
        };
        array.Add(array[0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => Load(document));
    }

    [Theory]
    [InlineData("evidence", 1001)]
    [InlineData("dungeons", 101)]
    [InlineData("npcs", 1001)]
    public void BoundedCollectionsAreCheckedBeforeTheirEntriesAreAccepted(string category, int count)
    {
        var document = ValidDocument();
        var owner = category == "npcs" ? Dungeon(document) : document;
        var original = owner[category]!.AsArray()[0]!;
        owner[category] = new JsonArray(Enumerable.Range(0, count).Select(_ => original.DeepClone()).ToArray());
        Assert.Throws<InvalidDataException>(() => Load(document));
    }

    [Fact]
    public void NpcLimitAppliesToTheWholeDocumentRatherThanEachDungeon()
    {
        var document = ValidDocument();
        var original = Npc(document);
        Dungeon(document)["npcs"] = new JsonArray(Enumerable.Range(1, 600).Select(index =>
        {
            var copy = original.DeepClone().AsObject();
            copy["npcDataId"] = index;
            return (JsonNode)copy;
        }).ToArray());
        var next = Dungeon(document).DeepClone().AsObject();
        next["dungeonDataId"] = 43;
        document["dungeons"]!.AsArray().Add(next);
        Assert.Throws<InvalidDataException>(() => Load(document));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1} trailing")]
    [InlineData("[]")]
    [InlineData("null")]
    public void DuplicatePropertiesAndIncompleteOrTrailingDocumentsAreRejected(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => CombatEncounterCatalog.Load(stream));
    }

    [Fact]
    public void NamesAndNestedCollectionsCannotMutateTheLoadedCatalog()
    {
        var document = ValidDocument();
        var catalog = Load(document);
        Npc(document)["names"]!["en"] = "Changed input";
        var dungeon = catalog.FindDungeon(42, 420)!;
        Assert.Equal("Synthetic NPC", dungeon.Npcs[0].Names.En);
        Assert.Throws<NotSupportedException>(() => ((IList<CombatCatalogDungeon>)catalog.Dungeons).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CombatCatalogEvidence>)catalog.Evidence).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CombatCatalogNpc>)dungeon.Npcs).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)dungeon.SourceRefs)[0] = "reward");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)dungeon.Npcs[0].SourceRefs)[0] = "reward");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)dungeon.Npcs[0].EndSourceRefs).Clear());
        Assert.Equal("Synthetic NPC", catalog.FindDungeon(42, 420)!.Npcs[0].Names.En);
    }

    [Fact]
    public void InputIsLeftOpenAndNoMoreThanOneMiBPlusOneByteIsRead()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(ValidDocument().ToJsonString()));
        CombatEncounterCatalog.Load(input);
        Assert.True(input.CanRead);
        using var oversized = new OversizedNonSeekingStream();
        Assert.Throws<InvalidDataException>(() => CombatEncounterCatalog.Load(oversized));
        Assert.Equal(CombatEncounterCatalog.MaximumDocumentBytes + 1, oversized.BytesRead);
        Assert.True(oversized.CanRead);
    }

    [Fact]
    public void MissingEmbeddedResourceHasNoFileOrNetworkFallback() =>
        Assert.Throws<InvalidDataException>(() => CombatEncounterCatalog.LoadEmbedded(typeof(CombatEncounterCatalogTests).Assembly));

    private static CombatEncounterCatalog Load(JsonNode document)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
        return CombatEncounterCatalog.Load(stream);
    }

    private static JsonObject Dungeon(JsonObject document) => document["dungeons"]!.AsArray()[0]!.AsObject();
    private static JsonObject Npc(JsonObject document) => Dungeon(document)["npcs"]!.AsArray()[0]!.AsObject();
    private static JsonObject Evidence(JsonObject document, string id) => document["evidence"]!.AsArray()
        .Select(value => value!.AsObject()).Single(value => value["id"]!.GetValue<string>() == id);
    private static JsonArray Refs(params string[] values) => new(values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());

    private static JsonObject ValidDocument()
    {
        JsonObject Source(string id, string kind, int observations = 1, int captures = 0) => new()
        {
            ["id"] = id, ["kind"] = kind, ["sourceRef"] = "synthetic-source", ["sha256"] = new string('a', 64),
            ["observations"] = observations, ["captures"] = captures
        };
        return new()
        {
            ["schemaVersion"] = 1, ["version"] = "synthetic-v1", ["region"] = "global-eu",
            ["evidence"] = new JsonArray(Source("npc", "localNpcData"), Source("map", "localMapData"),
                Source("reward", "captureMiddleBossReward", 1, 1), Source("terminal", "captureDeathDungeonSuccess", 2, 2),
                Source("context", "captureDungeonContext", 1, 1)),
            ["dungeons"] = new JsonArray(new JsonObject
            {
                ["dungeonDataId"] = 42, ["mapDataId"] = 420,
                ["names"] = new JsonObject { ["en"] = "Synthetic Dungeon" }, ["sourceRefs"] = Refs("map"),
                ["npcs"] = new JsonArray(new JsonObject
                {
                    ["npcDataId"] = 700, ["names"] = new JsonObject { ["en"] = "Synthetic NPC" },
                    ["role"] = "intermediateBoss", ["sourceRefs"] = Refs("npc", "reward"),
                    ["endProof"] = "middleBossReward", ["endSourceRefs"] = Refs("reward")
                })
            })
        };
    }

    private sealed class OversizedNonSeekingStream : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, CombatEncounterCatalog.MaximumDocumentBytes + 128 - BytesRead);
            buffer.AsSpan(offset, read).Fill((byte)' ');
            BytesRead += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
