using System.Globalization;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;

namespace Soulcrest.Core.Combat;

/// <summary>Maps qualified own observations to offline rules. It never treats a native total as a source contribution.</summary>
public static class CombatSourceGraphBuilder
{
    public static CombatSourceGraph Build(CombatObservation observation, CombatEffectCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        catalog ??= CombatEffectCatalog.Default;
        var sources = new List<CombatSource>();
        var baseLevel = catalog.BaseLevelTable.SingleOrDefault(row => row.ClassId == observation.ClassId && row.Level == observation.CharacterLevel);
        sources.Add(new()
        {
            Id = "base", Kind = CombatSourceKind.Base,
            Status = baseLevel is null ? CombatKnowledgeStatus.Unknown : CombatKnowledgeStatus.Complete,
            Effects = baseLevel?.Effects ?? [], Reason = baseLevel is null ? "Missing qualified class/level base-stat row." : null
        });
        var knownSkills = catalog.Skills.Where(skill => skill.ClassId == observation.ClassId).ToArray();
        var observedSkills = observation.Skills.Value?.ToDictionary(skill => skill.SkillId);
        var skillsUsable = Usable(observation.Skills.Status);
        if (skillsUsable && observedSkills is not null)
        {
            var mappedSkillIds = knownSkills.Select(skill => skill.SkillId).ToHashSet();
            foreach (var id in observedSkills.Keys.Where(id => !mappedSkillIds.Contains(id)).Order())
                sources.Add(new() { Id = "skills:unmapped:" + id, Kind = CombatSourceKind.Skill, Status = CombatKnowledgeStatus.Unknown,
                    Reason = "The observed skill category and source-effect rules are not qualified: " + id + "." });
        }
        var boardSources = BuildBoards(observation, out var completeBoards).ToArray();
        sources.AddRange(boardSources);
        var boardSkills = boardSources.SelectMany(source => source.Skills).GroupBy(skill => skill.SkillId)
            .ToDictionary(group => group.Key, group => group.Sum(skill => skill.Level ?? 0));
        var original = new List<CombatSkill>(); var equipment = new List<CombatSkill>(); var arcana = new List<CombatSkill>();
        var fixedSkills = new List<CombatSkill>(); var boardFallback = new List<CombatSkill>(); var abnormal = new List<CombatSkill>();
        var missingSkill = false; var unknownComponents = false; var boardConflict = false;
        foreach (var skill in knownSkills)
        {
            var state = skillsUsable ? observedSkills?.GetValueOrDefault(skill.SkillId) : null;
            missingSkill |= state is null;
            original.Add(new(skill.SkillId, skill.Kind, state?.OriginalLevel, state is null ? CombatApplication.Unknown : CombatApplication.Confirmed));
            var bonuses = state?.AdditionalLevels;
            unknownComponents |= bonuses is null;
            equipment.Add(Component(skill, bonuses?.Equipment));
            arcana.Add(Component(skill, bonuses?.Arcana));
            fixedSkills.Add(Component(skill, bonuses?.Fixed));
            if (!completeBoards)
            {
                var knownBoardLevels = boardSkills.GetValueOrDefault(skill.SkillId);
                if (bonuses?.Daevanion is { } totalBoardLevels && totalBoardLevels < knownBoardLevels) boardConflict = true;
                boardFallback.Add(Component(skill, bonuses?.Daevanion is { } total && total >= knownBoardLevels ? total - knownBoardLevels : null));
            }
            else if (bonuses is not null && bonuses.Daevanion != boardSkills.GetValueOrDefault(skill.SkillId)) boardConflict = true;
            if (bonuses?.Abnormal is not 0)
                abnormal.Add(new(skill.SkillId, skill.Kind, bonuses?.Abnormal, CombatApplication.Unknown));
        }
        AddSkills("skills:original", CombatSourceKind.Skill, original,
            !skillsUsable ? observation.Skills.Status : missingSkill ? CombatKnowledgeStatus.Partial : CombatKnowledgeStatus.Complete,
            missingSkill ? "Missing skill IDs do not establish learned level zero." : null);
        var componentStatus = !skillsUsable ? observation.Skills.Status : unknownComponents ? CombatKnowledgeStatus.Partial : CombatKnowledgeStatus.Complete;
        AddSkills("equipment:skill-bonuses", CombatSourceKind.Gear, equipment, componentStatus);
        AddSkills("arcana:skill-bonuses", CombatSourceKind.Arcana, arcana, componentStatus);
        AddSkills("fixed:skill-bonuses", CombatSourceKind.Fixed, fixedSkills, componentStatus);
        if (!completeBoards) AddSkills("daevanion:unresolved-skill-bonuses", CombatSourceKind.Daevanion, boardFallback, componentStatus,
            "Skill bonus is observed, but its distribution across boards is not complete.");
        if (boardConflict) sources.Add(new() { Id = "daevanion:skill-check", Kind = CombatSourceKind.Daevanion,
            Status = CombatKnowledgeStatus.Conflict, Reason = "Opened board nodes and observed Daevanion skill bonuses disagree." });
        if (abnormal.Count != 0) AddSkills("skills:abnormal", CombatSourceKind.Unknown, abnormal, CombatKnowledgeStatus.Unknown,
            "The combat-power eligibility of abnormal skill-level bonuses is not qualified.");

        var ownership = observation.TitleOwnership ?? new(observation.Titles.Value?.OwnedTitleIds, observation.Titles.Status);
        if (Usable(ownership.Status) && ownership.Value is { } ownedTitles)
        {
            foreach (var id in ownedTitles.Order())
                AddCatalog(CombatSourceKind.TitleOwned, id.ToString(CultureInfo.InvariantCulture), "Owned", "title-owned:" + id, ownership.Status);
            if (ownedTitles.Count == 0) sources.Add(new() { Id = "title-owned:empty", Kind = CombatSourceKind.TitleOwned, Status = ownership.Status });
        }
        else sources.Add(new() { Id = "title-owned:unknown", Kind = CombatSourceKind.TitleOwned, Status = ownership.Status,
            Reason = "Current character title ownership is unknown." });
        if (Usable(observation.Titles.Status) && observation.Titles.Value is { } titles)
        {
            foreach (var id in titles.EquippedByCategory.Values.Where(id => id != 0).Distinct().Order())
                AddCatalog(CombatSourceKind.TitleEquipped, id.ToString(CultureInfo.InvariantCulture), "Equipped", "title-equipped:" + id, observation.Titles.Status);
            if (titles.EquippedByCategory.Values.All(id => id == 0)) sources.Add(new() { Id = "title-equipped:empty", Kind = CombatSourceKind.TitleEquipped, Status = observation.Titles.Status });
        }
        else sources.Add(new() { Id = "title-equipped:unknown", Kind = CombatSourceKind.TitleEquipped, Status = observation.Titles.Status,
            Reason = "Current equipped title slots are unknown." });

        if (Usable(observation.Equipment.Status) && observation.Equipment.Value is { } gear)
        {
            var index = 0;
            foreach (var item in gear.Items)
            {
                var id = "gear:" + item.Detail.EquipSlot + ":" + index++ + ":" + item.ItemId;
                AddCatalog(CombatSourceKind.Gear, item.ItemId.ToString(CultureInfo.InvariantCulture), "Base", id, observation.Equipment.Status);
                var stats = item.Detail.ArcanaMainStats.Concat(item.Detail.SubStats).Concat(item.Detail.AdditionalSubStats)
                    .Concat(item.Detail.SoulAdditionalSubStats).ToArray();
                var effects = new List<CombatEffect>(); var unknown = false;
                foreach (var stat in stats)
                {
                    var value = catalog.GetNativeNormalized(stat.StatType, stat.Value, CombatNativeValueDomain.EquipmentOptions);
                    if (value is null || value.Role is not (CombatNativeStatRole.FlatComponent or CombatNativeStatRole.IncreasePercentComponent))
                    { unknown = true; continue; }
                    effects.Add(new(value.StatId, value.Value, Application: CombatApplication.Confirmed));
                }
                // Level components come from the own skill list, never a second sum of item sub-skills.
                if (stats.Length != 0) sources.Add(new() { Id = id + ":options", Kind = CombatSourceKind.Gear,
                    Status = unknown ? CombatKnowledgeStatus.Partial : observation.Equipment.Status, Effects = Array.AsReadOnly(effects.ToArray()),
                    Reason = unknown ? "Some equipment option stat IDs or native fixed-point scales are not qualified." : null });
                if (item.Detail.EnchantLevel != 0 || item.Detail.SurpassEnchantLevel != 0)
                    sources.Add(new() { Id = id + ":enchantment", Kind = CombatSourceKind.Enchantment, Status = CombatKnowledgeStatus.Unknown,
                        Reason = "Enchantment state is observed; the matching item/stage application rule remains unqualified." });
                if (item.Detail.SoulBindCount != 0)
                    sources.Add(new() { Id = id + ":soulbind", Kind = CombatSourceKind.Soulbind, Status = CombatKnowledgeStatus.Unknown,
                        Reason = "Soulbind state is observed; source-specific effect application remains unqualified." });
            }
        }
        else sources.Add(new() { Id = "equipment:unknown", Kind = CombatSourceKind.Gear, Status = observation.Equipment.Status,
            Reason = "Current equipped items and options are unknown." });
        sources.Add(new() { Id = "achievements", Kind = CombatSourceKind.Achievement, Status = CombatKnowledgeStatus.Unknown,
            Reason = observation.Achievements.Value is null ? "Current achievement state is unknown."
                : "Achievement state is observed; permanent effects and reward-source deduplication are not fully qualified." });
        sources.AddRange(BuildPets(observation.Pets, catalog));
        sources.AddRange(observation.AdditionalSources);
        foreach (var coverage in catalog.SourceCoverage.Where(coverage => sources.All(source => source.Kind != coverage.Kind)))
            sources.Add(new() { Id = "coverage:" + coverage.Kind, Kind = coverage.Kind, Status = CombatKnowledgeStatus.Unknown, Reason = coverage.Reason });
        var status = sources.Any(source => source.Status == CombatKnowledgeStatus.Conflict) ? CombatKnowledgeStatus.Conflict
            : sources.All(source => source.Status == CombatKnowledgeStatus.Complete) ? CombatKnowledgeStatus.Complete
            : sources.Any(source => Usable(source.Status)) ? CombatKnowledgeStatus.Partial : CombatKnowledgeStatus.Unknown;
        return CombatScoreModel.Freeze(new()
        {
            ClassId = observation.ClassId, Sources = sources, Status = status,
            NativeTotals = observation.NativeStats.Value is { } native && Usable(observation.NativeStats.Status)
                ? native.OrderBy(row => row.Key).Select(row => new CombatNativeStat(row.Key, row.Value)).ToArray() : [],
            NativeSkillTotals = observation.Skills.Value is { } nativeSkills && skillsUsable
                ? nativeSkills.Select(skill => new CombatNativeSkill(skill.SkillId, skill.TotalLevel)).ToArray() : [],
            ActiveAbnormalIds = observation.ActiveAbnormals.Value is { } abnormals && Usable(observation.ActiveAbnormals.Status)
                ? abnormals.Where(row => row.AbnormalId > 0 && !row.ReadyToRemove).Select(row => row.AbnormalId).Distinct().ToArray()
                : observation.ActiveAbnormalIds.Value ?? [],
            ActiveAbnormalStatus = observation.ActiveAbnormals.Value is not null ? observation.ActiveAbnormals.Status : observation.ActiveAbnormalIds.Status,
            ActiveAbnormals = observation.ActiveAbnormals.Value?.Select(row => new CombatAbnormalWitness(row.AbnormalId, row.Level, row.ReadyToRemove)).ToArray() ?? []
        });

        void AddSkills(string id, CombatSourceKind kind, IReadOnlyList<CombatSkill> values, CombatKnowledgeStatus sourceStatus, string? reason = null)
            => sources.Add(new() { Id = id, Kind = kind, Skills = values, Status = sourceStatus, Reason = reason });
        void AddCatalog(CombatSourceKind kind, string catalogId, string channel, string sourceId, CombatKnowledgeStatus observedStatus)
        {
            var rule = catalog.GetSource(kind, catalogId, channel);
            sources.Add(new()
            {
                Id = sourceId, Kind = kind,
                Status = rule is null || rule.Application != CombatApplication.Confirmed ? CombatKnowledgeStatus.Partial : observedStatus,
                Effects = rule?.Effects.Select(effect => effect with { Application = rule.Application, RuleId = kind + ":" + catalogId + ":" + channel }).ToArray() ?? [],
                Reason = rule is null ? "No qualified offline source effect rule: " + kind + "/" + catalogId + "."
                    : rule.Application != CombatApplication.Confirmed ? "Effect values are known; their current application is not qualified." : null
            });
        }
    }

    public static CombatSource BoardSource(string classId, int boardId, IEnumerable<int> nodeIds)
    {
        var rules = DaevanionRuleCatalog.Default.GetBoardRules(boardId);
        if (rules is null || rules.Board.ClassId != classId) throw new ArgumentException("Unknown or foreign Daevanion board.");
        var ids = nodeIds.ToHashSet();
        var nodes = rules.Nodes.Where(row => ids.Contains(row.Node.Id)).ToArray();
        if (nodes.Length != ids.Count) throw new ArgumentException("Unknown or foreign Daevanion node.");
        return new()
        {
            Id = "daevanion:" + boardId, Kind = CombatSourceKind.Daevanion, Status = CombatKnowledgeStatus.Complete,
            Effects = nodes.SelectMany(row => row.Node.Effects).Where(effect => effect.StatId is not null).Select(effect =>
                new CombatEffect(effect.StatId!, decimal.Parse(effect.Amount, CultureInfo.InvariantCulture), Application: CombatApplication.Confirmed,
                    RuleId: "daevanion:" + boardId)).ToArray(),
            Skills = nodes.SelectMany(row => row.Node.Effects).Where(effect => effect.SkillId is not null).Select(effect =>
                new CombatSkill(effect.SkillId!.Value, DaevanionCatalog.Default.GetSkill(effect.SkillId.Value)!.Kind,
                    effect.LevelIncrement, CombatApplication.Confirmed)).ToArray()
        };
    }

    private static IEnumerable<CombatSource> BuildBoards(CombatObservation observation, out bool complete)
    {
        var result = new List<CombatSource>();
        var rules = DaevanionRuleCatalog.Default.ForClass(observation.ClassId);
        complete = rules.Count != 0 && Usable(observation.Boards.Status) && observation.Boards.Value is not null;
        foreach (var board in rules)
        {
            if (!Usable(observation.Boards.Status) || observation.Boards.Value?.Boards.TryGetValue(board.Board.Id, out var ids) != true)
            {
                complete = false;
                result.Add(new() { Id = "daevanion:" + board.Board.Id, Kind = CombatSourceKind.Daevanion,
                    Status = CombatKnowledgeStatus.Unknown, Reason = "An absent board does not establish an empty node list." });
                continue;
            }
            try { result.Add(BoardSource(observation.ClassId, board.Board.Id, ids!)); }
            catch (ArgumentException)
            {
                complete = false;
                result.Add(new() { Id = "daevanion:" + board.Board.Id, Kind = CombatSourceKind.Daevanion,
                    Status = CombatKnowledgeStatus.Conflict, Reason = "Observed board nodes do not match the selected class catalog." });
            }
        }
        return result;
    }

    private static bool Usable(CombatKnowledgeStatus status) => status is CombatKnowledgeStatus.Complete or CombatKnowledgeStatus.Partial;
    private static CombatSkill Component(CombatCatalogSkill skill, int? value) => new(skill.SkillId, skill.Kind, value,
        value is null ? CombatApplication.Unknown : CombatApplication.Confirmed);

    private static IEnumerable<CombatSource> BuildPets(CombatObserved<PetProgressSnapshot> observed, CombatEffectCatalog catalog)
    {
        if (!Usable(observed.Status) || observed.Value is not { } pets)
        {
            return [new() { Id = "pets:unknown", Kind = CombatSourceKind.Pet, Status = observed.Status,
                Reason = "Current pet levels and genus panels are unknown." }];
        }
        var result = new List<CombatSource>();
        var bindings = catalog.PetLevelBindings.ToDictionary(row => (row.PetNumber, row.Level));
        foreach (var pet in pets.Levels.OrderBy(row => row.Key))
        {
            var binding = bindings.GetValueOrDefault((pet.Key, pet.Value));
            var rule = binding is not null ? catalog.GetSource(CombatSourceKind.Pet, binding.SourceId, "Collection") : null;
            var confirmed = binding?.Application == CombatApplication.Confirmed && rule is not null;
            result.Add(new()
            {
                Id = "pet:" + pet.Key + ":level:" + pet.Value, Kind = CombatSourceKind.Pet,
                Status = confirmed ? observed.Status : CombatKnowledgeStatus.Partial,
                Effects = rule?.Effects.Select(effect => effect with { Application = confirmed ? CombatApplication.Confirmed : CombatApplication.Unknown,
                    RuleId = "pet-level:" + pet.Key + ":" + pet.Value }).ToArray() ?? [],
                Reason = confirmed ? null : binding is null ? "No qualified pet/stat-group binding for this exact native level."
                    : "The pet stat-group numeric join is known; native application is not yet qualified."
            });
        }
        if (pets.Levels.Count == 0) result.Add(new() { Id = "pets:empty", Kind = CombatSourceKind.Pet, Status = observed.Status });
        // AcquiredCollections are reward claims, not VehicleStat row IDs. Never turn them into another stat source.
        foreach (var board in pets.GenusBoards.OrderBy(row => row.Key))
        {
            var effects = new List<CombatEffect>(); var unknown = false;
            if (pets.SelectedGenusPanel is not { } panel || !board.Value.Panels.TryGetValue(panel, out var selected))
                unknown = true;
            else
            {
                foreach (var effect in selected.Where(effect => effect.IsActivated))
                {
                    var amount = catalog.GetNativeNormalized(effect.StatType, effect.Value, CombatNativeValueDomain.GenusEffects);
                    if (amount is null || amount.Role is not (CombatNativeStatRole.FlatComponent or CombatNativeStatRole.IncreasePercentComponent))
                    { unknown = true; continue; }
                    effects.Add(new(amount.StatId, amount.Value, Application: CombatApplication.Confirmed,
                        RuleId: "genus:" + board.Key + ":" + panel + ":" + effect.Slot));
                }
            }
            result.Add(new()
            {
                Id = "genus:" + board.Key, Kind = CombatSourceKind.Genus, Status = unknown ? CombatKnowledgeStatus.Partial : observed.Status,
                Effects = Array.AsReadOnly(effects.ToArray()), Reason = unknown
                    ? "Selected genus panel, native stat mapping or fixed-point scale is not fully qualified." : null
            });
        }
        if (pets.GenusBoards.Count == 0) result.Add(new() { Id = "genus:empty", Kind = CombatSourceKind.Genus, Status = observed.Status });
        return result;
    }
}
