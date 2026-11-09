using System.Diagnostics;

namespace Soulcrest.App.Services;

internal readonly record struct GenusPresetSelectionResult(byte? Preset, bool MenuQualified);

/// <summary>Session-local selection. User confirmed 2026-11-09: clicking a game preset immediately applies it.</summary>
internal sealed class GenusActivePresetSelection(Func<long>? clock = null)
{
    private readonly object _gate = new();
    private readonly Func<long> _clock = clock ?? Stopwatch.GetTimestamp;
    private string? _profile;
    private long _generation, _fullSequence, _rollSequence, _menuGeneration, _barrier;
    private byte? _observed;
    private bool _networkValid;

    internal byte? Resolve(CharacterCombatSnapshot character, bool current, GenusMenuSnapshot menu) => Evaluate(character, current, menu).Preset;
    internal GenusPresetSelectionResult Evaluate(CharacterCombatSnapshot character, bool current, GenusMenuSnapshot menu)
    {
        lock (_gate)
        {
            if (!current)
            {
                _profile = null; _generation = 0; _fullSequence = 0; _rollSequence = 0;
                _networkValid = false; _observed = null; _barrier = _clock();
                return new(null, false);
            }
            var newContext = _profile != character.ProfileId || _generation != character.Generation;
            var newList = character.Pets.Source == "own-pet-genus-list" && character.Pets.Sequence != _fullSequence;
            if (newContext || newList)
            {
                _profile = character.ProfileId; _generation = character.Generation;
                _fullSequence = character.Pets.Source == "own-pet-genus-list" ? character.Pets.Sequence : 0;
                _rollSequence = 0; _menuGeneration = menu.SourceGeneration;
                _observed = null; _networkValid = character.Pets.Source == "own-pet-genus-list";
                _barrier = _clock();
            }
            if (_menuGeneration != menu.SourceGeneration)
            {
                // During a lease/capture gap a game click can change the active preset without a network acknowledgement.
                _menuGeneration = menu.SourceGeneration; _observed = null; _networkValid = false; _barrier = _clock();
            }
            if (character.Pets.Source == "own-pet-genus-roll-update" && character.Pets.Sequence != _rollSequence)
            {
                _rollSequence = character.Pets.Sequence; _observed = null; _networkValid = false; _barrier = _clock();
            }
            var qualified = MenuQualified(menu);
            if (qualified) _observed = menu.ViewedPreset;
            return new(_observed ?? (_networkValid && character.Pets.Value?.SelectedGenusPanel is >= 1 and <= 3
                ? character.Pets.Value.SelectedGenusPanel : null), qualified);
        }
    }
    internal bool NeedsFreshMenu(GenusMenuSnapshot menu)
    {
        lock (_gate) return _profile is not null && MenuQualified(menu) && _observed != menu.ViewedPreset;
    }
    internal bool IsQualifiedMenuFrame(GenusMenuSnapshot menu)
    {
        lock (_gate) return _profile is not null && MenuQualified(menu);
    }
    private bool MenuQualified(GenusMenuSnapshot menu) => menu.SourceGeneration == _menuGeneration && menu.FrameAt > _barrier
        && menu.MenuVisible == true && GenusInsightPresentation.KnownGenus(menu.Genus) && menu.ViewedPreset is >= 1 and <= 3;
}
