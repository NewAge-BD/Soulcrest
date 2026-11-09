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
    private readonly GenusActivePresetSelection _preset = new();
    private int _savingSelection, _refreshPending;
    private bool _disposed;
    public event Action? Changed;
    public GenusInsightStateService(CharacterCombatStateService character, GenusInsightFavoriteService favorites,
        GenusMenuObservationService menu)
    {
        _character = character; _favorites = favorites; _menu = menu;
        character.Changed += Refresh; favorites.Changed += Refresh; menu.Changed += Refresh; menu.FrameObserved += FreshMenuFrame;
        Refresh();
    }
    public GenusInsightSnapshot Snapshot
    {
        get
        {
            if (_disposed) return GenusInsightSnapshot.Empty;
            var character = _character.Snapshot;
            var favorites = _favorites.Snapshot.Favorites;
            var menu = _menu.Snapshot;
            var live = ReadLive(character, favorites, menu, out _);
            var current = _character.Snapshot;
            var currentMenu = _menu.Snapshot;
            if (current.ProfileId != character.ProfileId || current.Generation != character.Generation
                || current.Sequence != character.Sequence || current.IsFresh != character.IsFresh
                || currentMenu.MenuVisible != menu.MenuVisible || currentMenu.Genus != menu.Genus
                || currentMenu.ViewedPreset != menu.ViewedPreset || currentMenu.SourceGeneration != menu.SourceGeneration)
                return ReadSaved(favorites);
            // Capture/menu expiry revokes live selection authority, never the saved rolls.
            // Partial or malformed source payloads must also leave the complete shared display intact.
            var usable = live.IsCurrent && live.Preset is not null && character.Pets.Status == NetworkCombatSourceStatus.Complete
                && character.Pets.Value is { } value && character.Pets.ObservedAt is { } at
                && ExplorationService.ValidGenusHistory(new(value.GenusBoards, at, character.Pets.Source));
            if (!usable && ReadSaved(favorites) is { IsHistorical: true } saved) return saved;
            return live;
        }
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
        var ownCurrent = character.IsFresh && character.Generation > 0 && !string.IsNullOrWhiteSpace(character.ProfileId);
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
                var menu = _menu.Snapshot;
                _ = ReadLive(character, _favorites.Snapshot.Favorites, menu, out var selection);
                var latest = _menu.Snapshot;
                if (selection.MenuQualified && menu.ViewedPreset is { } selected
                    && latest.MenuVisible == true && latest.Genus == menu.Genus
                    && latest.ViewedPreset == selected && latest.SourceGeneration == menu.SourceGeneration)
                    _character.PersistObservedGenusPreset(character, selected);
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
                        StatId = stat?.StatId, LabelDe = stat?.LabelDe ?? "Unbekannter Stat", LabelEn = stat?.LabelEn ?? "Unknown stat",
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
