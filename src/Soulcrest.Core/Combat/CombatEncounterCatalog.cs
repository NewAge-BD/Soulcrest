using System.Reflection;
using System.Text.Json;

namespace Soulcrest.Core.Combat;

public enum CombatCatalogEvidenceKind
{
    LocalNpcData, LocalMapData, LocalActivityData, CaptureMiddleBossReward,
    CaptureDeathDungeonSuccess, LocalReference, CaptureDungeonContext
}

public enum CombatCatalogNpcRole { Unknown, Boss, IntermediateBoss, FinalBoss }
public enum CombatCatalogEndProof { None, MiddleBossReward, DungeonSuccess }

public sealed record CombatCatalogNames(string? En, string? De);
public sealed record CombatCatalogEvidence(string Id, CombatCatalogEvidenceKind Kind, string SourceRef,
    string Sha256, int Observations, int Captures);
public sealed record CombatCatalogNpc(int NpcDataId, CombatCatalogNames Names, CombatCatalogNpcRole Role,
    IReadOnlyList<string> SourceRefs, CombatCatalogEndProof EndProof, IReadOnlyList<string> EndSourceRefs);
public sealed record CombatCatalogDungeon(int DungeonDataId, int MapDataId, CombatCatalogNames Names,
    IReadOnlyList<string> SourceRefs, IReadOnlyList<CombatCatalogNpc> Npcs);

/// <summary>
/// Reviewed local data facts, independent of live entity ownership and amount semantics. Loading only
/// reads the supplied JSON; evidence references and hashes are descriptive and are never opened.
/// A role or an eligible end-proof fact does not itself bind a live entity or authorize a full DPS report.
/// </summary>
public sealed class CombatEncounterCatalog
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumDocumentBytes = 1 << 20;
    public const string ResourceName = "Soulcrest.CombatEncounters.json";
    private const int MaximumDungeons = 100;
    private const int MaximumNpcs = 1000;
    private const int MaximumEvidence = 1000;
    private const int MaximumReferences = 64;
    private readonly Dictionary<(int Dungeon, int Map), CombatCatalogDungeon> _byDungeon;

    public string CatalogVersion { get; }
    public string Region { get; }
    public IReadOnlyList<CombatCatalogEvidence> Evidence { get; }
    public IReadOnlyList<CombatCatalogDungeon> Dungeons { get; }

    private CombatEncounterCatalog(string version, string region, CombatCatalogEvidence[] evidence,
        CombatCatalogDungeon[] dungeons)
    {
        CatalogVersion = version;
        Region = region;
        Evidence = Array.AsReadOnly(evidence);
        Dungeons = Array.AsReadOnly(dungeons);
        _byDungeon = dungeons.ToDictionary(d => (d.DungeonDataId, d.MapDataId));
    }

    public CombatCatalogDungeon? FindDungeon(int dungeonDataId, int mapDataId) =>
        _byDungeon.GetValueOrDefault((dungeonDataId, mapDataId));

    /// <summary>Loads exactly schema 1, rejects unknown/duplicate properties and leaves the input open.</summary>
    public static CombatEncounterCatalog Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var bounded = ReadBounded(stream);
        try
        {
            using var json = JsonDocument.Parse(bounded, new() { MaxDepth = 16 });
            var root = Object(json.RootElement, ["schemaVersion", "version", "region", "evidence", "dungeons"]);
            if (Integer(root["schemaVersion"], 1) != CurrentSchemaVersion) throw Invalid("schemaVersion");
            var version = Text(root["version"], 128);
            var region = Text(root["region"], 64);
            var evidence = Elements(root["evidence"], MaximumEvidence).Select(ReadEvidence).ToArray();
            var evidenceById = new Dictionary<string, CombatCatalogEvidence>(StringComparer.Ordinal);
            foreach (var entry in evidence)
                if (!evidenceById.TryAdd(entry.Id, entry)) throw Invalid("duplicate evidence ID");
            var npcCount = 0;
            var dungeons = Elements(root["dungeons"], MaximumDungeons)
                .Select(element => ReadDungeon(element, evidenceById, ref npcCount)).ToArray();
            if (dungeons.Select(d => d.DungeonDataId).Distinct().Count() != dungeons.Length ||
                dungeons.Select(d => (d.DungeonDataId, d.MapDataId)).Distinct().Count() != dungeons.Length)
                throw Invalid("duplicate dungeon ID or join");
            return new(version, region, evidence, dungeons);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid combat encounter catalog JSON.", error);
        }
    }

    /// <summary>No file/network fallback: a missing resource is an explicit configuration failure.</summary>
    public static CombatEncounterCatalog LoadEmbedded(Assembly? assembly = null)
    {
        using var stream = (assembly ?? typeof(CombatEncounterCatalog).Assembly).GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("Embedded combat encounter catalog is missing.");
        return Load(stream);
    }

    private static CombatCatalogEvidence ReadEvidence(JsonElement element)
    {
        var value = Object(element, ["id", "kind", "sourceRef", "sha256", "observations", "captures"]);
        var kind = Text(value["kind"], 64) switch
        {
            "localNpcData" => CombatCatalogEvidenceKind.LocalNpcData,
            "localMapData" => CombatCatalogEvidenceKind.LocalMapData,
            "localActivityData" => CombatCatalogEvidenceKind.LocalActivityData,
            "captureMiddleBossReward" => CombatCatalogEvidenceKind.CaptureMiddleBossReward,
            "captureDeathDungeonSuccess" => CombatCatalogEvidenceKind.CaptureDeathDungeonSuccess,
            "localReference" => CombatCatalogEvidenceKind.LocalReference,
            "captureDungeonContext" => CombatCatalogEvidenceKind.CaptureDungeonContext,
            _ => throw Invalid("evidence kind")
        };
        var hash = Text(value["sha256"], 64);
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw Invalid("evidence sha256");
        var observations = Integer(value["observations"], 1);
        var captures = Integer(value["captures"], 0);
        var captureKind = kind is CombatCatalogEvidenceKind.CaptureMiddleBossReward or
            CombatCatalogEvidenceKind.CaptureDeathDungeonSuccess or CombatCatalogEvidenceKind.CaptureDungeonContext;
        if (captures > observations || captureKind && captures == 0) throw Invalid("evidence counts");
        return new(Text(value["id"], 128), kind, Text(value["sourceRef"], 1024), hash, observations, captures);
    }

    private static CombatCatalogDungeon ReadDungeon(JsonElement element,
        IReadOnlyDictionary<string, CombatCatalogEvidence> evidence, ref int npcCount)
    {
        var value = Object(element, ["dungeonDataId", "mapDataId", "names", "sourceRefs", "npcs"]);
        var sourceRefs = References(value["sourceRefs"], evidence, true);
        var localMap = Has(sourceRefs, evidence, CombatCatalogEvidenceKind.LocalMapData) ||
            Has(sourceRefs, evidence, CombatCatalogEvidenceKind.LocalActivityData);
        if (!localMap && !Has(sourceRefs, evidence, CombatCatalogEvidenceKind.CaptureDungeonContext))
            throw Invalid("dungeon map evidence");
        var names = Names(value["names"]);
        if (!localMap && Named(names)) throw Invalid("unconfirmed dungeon names");
        var npcElements = Elements(value["npcs"], MaximumNpcs);
        npcCount += npcElements.Length;
        if (npcCount > MaximumNpcs) throw Invalid("NPC limit");
        var npcs = npcElements.Select(npc => ReadNpc(npc, evidence, localMap)).ToArray();
        if (npcs.Select(npc => npc.NpcDataId).Distinct().Count() != npcs.Length) throw Invalid("duplicate NPC ID");
        return new(Integer(value["dungeonDataId"], 1), Integer(value["mapDataId"], 1), names,
            Array.AsReadOnly(sourceRefs), Array.AsReadOnly(npcs));
    }

    private static CombatCatalogNpc ReadNpc(JsonElement element,
        IReadOnlyDictionary<string, CombatCatalogEvidence> evidence, bool localMap)
    {
        var value = Object(element, ["npcDataId", "names", "role", "sourceRefs", "endProof", "endSourceRefs"]);
        var names = Names(value["names"]);
        var role = Text(value["role"], 64) switch
        {
            "unknown" => CombatCatalogNpcRole.Unknown,
            "boss" => CombatCatalogNpcRole.Boss,
            "intermediateBoss" => CombatCatalogNpcRole.IntermediateBoss,
            "finalBoss" => CombatCatalogNpcRole.FinalBoss,
            _ => throw Invalid("NPC role")
        };
        var sourceRefs = References(value["sourceRefs"], evidence, true);
        var localNpc = Has(sourceRefs, evidence, CombatCatalogEvidenceKind.LocalNpcData);
        if (Named(names) && !localNpc) throw Invalid("unconfirmed NPC names");
        if (role != CombatCatalogNpcRole.Unknown && (!localNpc || !localMap)) throw Invalid("unconfirmed NPC role");
        var proof = Text(value["endProof"], 64) switch
        {
            "none" => CombatCatalogEndProof.None,
            "middleBossReward" => CombatCatalogEndProof.MiddleBossReward,
            "dungeonSuccess" => CombatCatalogEndProof.DungeonSuccess,
            _ => throw Invalid("end proof")
        };
        var endRefs = References(value["endSourceRefs"], evidence, false);
        if (proof == CombatCatalogEndProof.MiddleBossReward && (role != CombatCatalogNpcRole.IntermediateBoss ||
            !endRefs.Any(id => evidence[id].Kind == CombatCatalogEvidenceKind.CaptureMiddleBossReward && evidence[id].Captures >= 1)) ||
            proof == CombatCatalogEndProof.DungeonSuccess && (role != CombatCatalogNpcRole.FinalBoss ||
            !endRefs.Any(id => evidence[id].Kind == CombatCatalogEvidenceKind.CaptureDeathDungeonSuccess && evidence[id].Captures >= 2)))
            throw Invalid("unconfirmed end proof");
        return new(Integer(value["npcDataId"], 1), names, role, Array.AsReadOnly(sourceRefs), proof, Array.AsReadOnly(endRefs));
    }

    private static string[] References(JsonElement element, IReadOnlyDictionary<string, CombatCatalogEvidence> evidence,
        bool required)
    {
        var refs = Elements(element, MaximumReferences).Select(value => Text(value, 128)).ToArray();
        if (required && refs.Length == 0 || refs.Distinct(StringComparer.Ordinal).Count() != refs.Length ||
            refs.Any(id => !evidence.ContainsKey(id))) throw Invalid("evidence references");
        return refs;
    }

    private static bool Has(IEnumerable<string> refs, IReadOnlyDictionary<string, CombatCatalogEvidence> evidence,
        CombatCatalogEvidenceKind kind) => refs.Any(id => evidence[id].Kind == kind);

    private static CombatCatalogNames Names(JsonElement element)
    {
        var value = Object(element, [], ["en", "de"]);
        string? Optional(string key) => !value.TryGetValue(key, out var text) || text.ValueKind == JsonValueKind.Null
            ? null : Text(text, 256);
        return new(Optional("en"), Optional("de"));
    }

    private static bool Named(CombatCatalogNames names) => names.En is not null || names.De is not null;

    private static Dictionary<string, JsonElement> Object(JsonElement element, string[] required, string[]? optional = null)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid("object");
        var value = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if ((!required.Contains(property.Name, StringComparer.Ordinal) &&
                 optional?.Contains(property.Name, StringComparer.Ordinal) != true) || !value.TryAdd(property.Name, property.Value))
                throw Invalid("unknown or duplicate property");
        }
        if (required.Any(key => !value.ContainsKey(key))) throw Invalid("missing property");
        return value;
    }

    private static JsonElement[] Elements(JsonElement element, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximum) throw Invalid("array limit");
        return element.EnumerateArray().ToArray();
    }

    private static int Integer(JsonElement element, int minimum)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < minimum)
            throw Invalid("integer");
        return value;
    }

    private static string Text(JsonElement element, int maximum)
    {
        if (element.ValueKind != JsonValueKind.String) throw Invalid("text");
        var value = element.GetString()!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) throw Invalid("text");
        return value;
    }

    private static MemoryStream ReadBounded(Stream stream)
    {
        var result = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, MaximumDocumentBytes + 1 - (int)result.Length));
                if (read == 0) break;
                result.Write(buffer, 0, read);
                if (result.Length > MaximumDocumentBytes) throw Invalid("document size");
            }
            result.Position = 0;
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static InvalidDataException Invalid(string field) => new($"Invalid combat encounter catalog: {field}.");
}
