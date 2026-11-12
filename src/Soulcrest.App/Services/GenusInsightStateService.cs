using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.Core.Combat;
using Soulcrest.Core.GenusInsight;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>Shared rolls and a character-local preset; saved display data never grants live overlay authority.</summary>
public sealed class GenusInsightStateService : IDisposable
{
    private readonly CharacterCombatStateService _character;
    private readonly GenusInsightFavoriteService _favorites;
    private readonly GenusMenuObservationService _menu;
    private readonly INetworkGenusInsightSource? _shared;
    private readonly GenusActivePresetSelection _preset = new();
    private int _savingSelection, _refreshPending;
    private bool _disposed;
    public event Action? Changed;
    public GenusInsightStateService(CharacterCombatStateService character, GenusInsightFavoriteService favorites,
        GenusMenuObservationService menu, INetworkGenusInsightSource? shared = null)
    {
        _character = character; _favorites = favorites; _menu = menu; _shared = shared;
        character.Changed += Refresh; favorites.Changed += Refresh; menu.Changed += Refresh; menu.FrameObserved += FreshMenuFrame;
        if (shared is not null) shared.GenusInsightChanged += Refresh;
        Refresh();
    }

    /// <summary>Global roll evidence never supplies a character identity or its active preset.</summary>
    internal static CharacterCombatSnapshot ForGenusSource(CharacterCombatSnapshot character, NetworkGenusInsightSnapshot? shared)
    {
        if (shared is null) return character;
        var domain = shared.Domain;
        var usable = shared.Generation > 0 && shared.Sequence > 0 && domain.Sequence == shared.Sequence
            && domain is { IsFresh: true, Status: NetworkCombatSourceStatus.Complete, ObservedAt: not null, Value: not null }
            && domain.Source is "own-pet-genus-list" or "own-pet-genus-lock-update" or "own-pet-genus-roll-update";
        return character with
        {
            Sequence = shared.Sequence,
            Pets = usable ? domain with { Value = domain.Value! with { SelectedGenusPanel = null } }
                : NetworkCombatDomain<PetProgressSnapshot>.Unknown
        };
    }
    public GenusInsightSnapshot Snapshot
    {
        get
        {
            if (_disposed) return GenusInsightSnapshot.Empty;
            var character = _character.Snapshot;
            var shared = _shared?.GenusInsightSnapshot;
            var favorites = _favorites.Snapshot.Favorites;
            var menu = _menu.Snapshot;
            var source = ForGenusSource(character, shared);
            var live = ReadLive(source, favorites, menu, out _);
            var current = _character.Snapshot;
            var currentMenu = _menu.Snapshot;
            if (!SameOwn(character, current) || !SameShared(shared)
                || currentMenu.MenuVisible != menu.MenuVisible || currentMenu.Genus != menu.Genus
                || currentMenu.ViewedPreset != menu.ViewedPreset || currentMenu.SourceGeneration != menu.SourceGeneration)
                return ReadSaved(favorites);
            // Capture/menu expiry revokes live selection authority, never the saved rolls.
            // Partial or malformed source payloads must also leave the complete shared display intact.
            var usable = live.IsCurrent && live.Preset is not null && source.Pets.Status == NetworkCombatSourceStatus.Complete
                && source.Pets.Value is { } value && source.Pets.ObservedAt is { } at
                && ExplorationService.ValidGenusHistory(new(value.GenusBoards, at, source.Pets.Source));
            if (!usable && ReadSaved(favorites) is { IsHistorical: true } saved) return saved;
            return live;
        }
    }
    private static bool SameOwn(CharacterCombatSnapshot expected, CharacterCombatSnapshot current) =>
        current.ProfileId == expected.ProfileId && current.Generation == expected.Generation
        && current.Sequence == expected.Sequence && current.IsFresh == expected.IsFresh;
    private bool SameShared(NetworkGenusInsightSnapshot? expected)
    {
        if (_shared is null) return expected is null;
        var current = _shared.GenusInsightSnapshot;
        return expected is not null && current.Generation == expected.Generation && current.Sequence == expected.Sequence
            && ReferenceEquals(current.Domain, expected.Domain);
    }
    private GenusInsightSnapshot ReadSaved(IReadOnlySet<GenusInsightFavoriteKey> favorites)
    {
        var saved = _character.SavedGenusInsight;
        if (saved is null) return GenusInsightSnapshot.Empty;
        var result = GenusInsightProjection.CreateSaved(saved, favorites);
        // Selecting a different character while projecting shared boards cannot reuse its predecessor's preset.
        return saved == _character.SavedGenusInsight ? result : GenusInsightSnapshot.Empty;
    }
    private GenusInsightSnapshot ReadLive(CharacterCombatSnapshot character, IReadOnlySet<GenusInsightFavoriteKey> favorites,
        GenusMenuSnapshot menu, out GenusPresetSelectionResult selection)
    {
        var result = GenusInsightProjection.Create(character, favorites);
        // Menu selection belongs to the current character even if the shared roll
        // payload is available only from disk. It does not make those rolls live.
        var ownCurrent = character.IsFresh && character.Generation > 0 && !string.IsNullOrWhiteSpace(character.ProfileId)
            && (_shared is null || result.IsCurrent);
        selection = _preset.Evaluate(character, ownCurrent, menu);
        if (result.IsCurrent && selection.Preset != character.Pets.Value!.SelectedGenusPanel)
            result = GenusInsightProjection.Create(character with { Pets = character.Pets with
                { Value = character.Pets.Value with { SelectedGenusPanel = selection.Preset } } }, favorites);
        GenusInsightBoardView? viewed = null;
        if (result.IsCurrent && selection.MenuQualified && menu.ViewedPreset is >= 1 and <= 3)
        {
            var viewedState = character with { Pets = character.Pets with { Value = character.Pets.Value! with { SelectedGenusPanel = menu.ViewedPreset } } };
            viewed = GenusInsightProjection.Create(viewedState, favorites).Boards.FirstOrDefault(board => board.Genus == menu.Genus);
        }
        return result with { MenuVisible = menu.MenuVisible, SelectedGenus = menu.Genus,
            ViewedPreset = menu.ViewedPreset, ViewedBoard = viewed, MenuSourceGeneration = menu.SourceGeneration };
    }
    public IDisposable ObserveMenu() => _menu.Acquire();
    private void FreshMenuFrame()
    {
        if (_disposed) return;
        var menu = _menu.Snapshot;
        // Snapshot reads may already have consumed the live selection. Persistence
        // is complete only when this profile's stored choice agrees with it.
        if (_preset.NeedsFreshMenu(menu) || _preset.IsQualifiedMenuFrame(menu)
            && _character.SavedGenusInsight is { } saved && saved.ActivePreset != menu.ViewedPreset) Refresh();
    }
    private void Refresh()
    {
        if (_disposed) return;
        Volatile.Write(ref _refreshPending, 1);
        if (Interlocked.CompareExchange(ref _savingSelection, 1, 0) != 0) return;
        try
        {
            // A profile save raises Character.Changed reentrantly. Drain the latest evidence
            // after that callback rather than letting an older menu choice overwrite it.
            while (!_disposed && Interlocked.Exchange(ref _refreshPending, 0) != 0)
            {
                var character = _character.Snapshot;
                var shared = _shared?.GenusInsightSnapshot;
                var menu = _menu.Snapshot;
                var source = ForGenusSource(character, shared);
                if (shared is not null && source.Pets.IsFresh && SameShared(shared))
                    _character.PersistObservedSharedGenus(character, source.Pets, () => SameShared(shared));
                _ = ReadLive(source, _favorites.Snapshot.Favorites, menu, out var selection);
                var latest = _menu.Snapshot;
                if (selection.MenuQualified && menu.ViewedPreset is { } selected
                    && latest.MenuVisible == true && latest.Genus == menu.Genus
                    && latest.ViewedPreset == selected && latest.SourceGeneration == menu.SourceGeneration
                    && SameOwn(character, _character.Snapshot) && SameShared(shared))
                    _character.PersistObservedGenusPreset(character, selected, () => SameShared(shared)
                        && _menu.Snapshot is { MenuVisible: true } currentMenu && currentMenu.Genus == menu.Genus
                        && currentMenu.ViewedPreset == selected && currentMenu.SourceGeneration == menu.SourceGeneration);
            }
        }
        finally { Volatile.Write(ref _savingSelection, 0); }
        if (!_disposed && Volatile.Read(ref _refreshPending) != 0) Refresh();
        if (!_disposed) SafeEvent.Raise(Changed, "Genus Insight");
    }
    public void Dispose()
    {
        _disposed = true; _character.Changed -= Refresh; _favorites.Changed -= Refresh;
        _menu.Changed -= Refresh; _menu.FrameObserved -= FreshMenuFrame;
        if (_shared is not null) _shared.GenusInsightChanged -= Refresh;
    }
}

public static class GenusInsightProjection
{
    public static GenusInsightSnapshot Create(CharacterCombatSnapshot character,
        IReadOnlySet<GenusInsightFavoriteKey>? favorites = null, GenusInsightCatalog? catalog = null,
        IReadOnlyDictionary<ushort, string>? bindings = null, int? slotOffset = null)
    {
        catalog ??= GenusInsightCatalog.Default;
        bindings ??= GenusInsightNativeBindings.Stats;
        slotOffset ??= GenusInsightNativeBindings.SlotOffset;
        var domain = character.Pets;
        var current = character.IsFresh && !string.IsNullOrWhiteSpace(character.ProfileId) && character.Generation > 0
            && domain.IsFresh && domain.ObservedAt is not null
            && domain.Status is NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial
            && (domain.Source is "own-pet-genus-list" or "own-pet-genus-lock-update" or "own-pet-genus-roll-update") && domain.Value is not null;
        var result = ProjectBoards(current ? domain.Value!.GenusBoards : null, current ? domain.Value!.SelectedGenusPanel : null,
            current && domain.Status == NetworkCombatSourceStatus.Complete, current, favorites, catalog, bindings, slotOffset);
        return result with
        {
            ProfileId = current ? character.ProfileId : null, Generation = current ? character.Generation : 0,
            Sequence = current ? character.Sequence : 0, IsCurrent = current, ObservedAt = current ? domain.ObservedAt : null
        };
    }

    /// <summary>Project persisted shared rolls with this profile's last confirmed preset; never creates network freshness.</summary>
    public static GenusInsightSnapshot CreateSaved(ProfileGenusInsightSnapshot saved,
        IReadOnlySet<GenusInsightFavoriteKey>? favorites = null)
    {
        var result = ProjectBoards(saved.Boards, saved.ActivePreset, true, false, favorites,
            GenusInsightCatalog.Default, GenusInsightNativeBindings.Stats, GenusInsightNativeBindings.SlotOffset);
        return result with { ProfileId = saved.ProfileId, IsHistorical = true, ObservedAt = saved.ObservedAt };
    }

    private static GenusInsightSnapshot ProjectBoards(ImmutableDictionary<byte, CharacterGenusBoard>? nativeBoards,
        byte? preset, bool completeSource, bool current, IReadOnlySet<GenusInsightFavoriteKey>? favorites,
        GenusInsightCatalog catalog, IReadOnlyDictionary<ushort, string> bindings, int? slotOffset)
    {
        var boards = new List<GenusInsightBoardView>();
        var complete = completeSource && nativeBoards is not null
            && nativeBoards.Values.All(board => Genus(board.CreatureType) is not null);
        var applied = new Dictionary<string, GenusInsightAppliedEffect>(StringComparer.Ordinal);
        foreach (var genus in GenusInsightPresentation.Genera)
        {
            var matches = nativeBoards is not null ? nativeBoards.Values.Where(board => Genus(board.CreatureType) == genus).ToArray() : [];
            var board = matches.Length == 1 ? matches[0] : null;
            var panel = board is not null && preset is { } selected
                && board.Panels.TryGetValue(selected, out var effects) ? effects : default;
            var slots = new List<GenusInsightSlotView>();
            for (byte slot = 1; slot <= 9; slot++)
            {
                var candidates = !panel.IsDefault && slotOffset is { } offset
                    ? panel.Where(effect => effect.Slot + offset == slot).ToArray() : [];
                var effect = candidates.Length == 1 ? candidates[0] : null;
                var view = new GenusInsightSlotView { Slot = slot };
                if (effect is not null)
                {
                    var notAnalyzed = effect.Grade == 0 && effect.StatType == 0 && effect.Value == 0;
                    var rank = effect.Grade is >= 1 and <= 5 ? effect.Grade : (byte?)null;
                    var stat = bindings.TryGetValue(effect.StatType, out var id) ? catalog.FindStat(id) : null;
                    var rankRange = stat is not null && rank is not null ? catalog.Range(genus, slot, stat.StatId, rank) : null;
                    var range = stat is not null ? catalog.Range(genus, slot, stat.StatId) : null;
                    var value = stat is not null ? effect.Value / stat.RawDivisor : (decimal?)null;
                    // Unknown mappings, future slots and out-of-range values must never look like a confirmed perfect roll.
                    var qualified = value is { } v && rankRange is not null && v >= rankRange.Minimum && v <= rankRange.Maximum;
                    var fraction = qualified && range is not null && value is { } number
                        ? range.Minimum == range.Maximum ? 1d : (double)((number - range.Minimum) / (range.Maximum - range.Minimum))
                        : (double?)null;
                    view = view with
                    {
                        StatId = stat?.StatId,
                        LabelDe = stat?.LabelDe ?? (notAnalyzed ? "Nicht analysiert" : "Unbekannter Stat"),
                        LabelEn = stat?.LabelEn ?? (notAnalyzed ? "Not analyzed" : "Unknown stat"),
                        Unit = stat?.Unit, Value = value, Rank = rank, Minimum = range?.Minimum, Maximum = range?.Maximum,
                        RollFraction = fraction, IsMaxRoll = qualified && value == range?.Maximum,
                        IsFavorite = stat is not null && favorites?.Contains(new(genus, slot, stat.StatId)) == true,
                        IsLocked = effect.IsLocked, IsActivated = effect.IsActivated, IsCurrent = current && qualified
                    };
                    if (effect.IsActivated && qualified && stat is not null)
                    {
                        applied.TryGetValue(stat.StatId, out var previous);
                        applied[stat.StatId] = new(stat.StatId, stat.LabelDe, stat.LabelEn, stat.Unit, (previous?.Value ?? 0) + value!.Value);
                    }
                    if (effect.IsActivated && !qualified) complete = false;
                }
                else complete = false;
                slots.Add(view);
            }
            if (panel.IsDefault || panel.Length != 9 || panel.Select(effect => effect.Slot).Distinct().Count() != 9) complete = false;
            boards.Add(new(genus, board is { Level: >= 0 and <= 10 } ? board.Level : null, Array.AsReadOnly(slots.ToArray())));
        }
        return new()
        {
            Preset = preset, Boards = Array.AsReadOnly(boards.ToArray()),
            AppliedEffects = Array.AsReadOnly(applied.Values.OrderBy(effect => effect.StatId, StringComparer.Ordinal).ToArray()),
            AppliedEffectsStatus = complete ? CombatKnowledgeStatus.Complete : applied.Count > 0 ? CombatKnowledgeStatus.Partial : CombatKnowledgeStatus.Unknown
        };
    }
    private static string? Genus(byte type) => type switch { 2 => "cogni", 3 => "fera", 4 => "natura", 5 => "varian", 6 => "special", _ => null };
}
