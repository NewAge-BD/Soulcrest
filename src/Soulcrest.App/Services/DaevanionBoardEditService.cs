using System.Collections.Frozen;
using Soulcrest.App.Network;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public sealed record DaevanionBoardEditBoard
{
    public DaevanionBoard Board { get; init; } = null!;
    public bool IsKnown { get; init; }
    public bool IsUnlocked { get; init; }
    public bool CanEdit { get; init; }
    public IReadOnlySet<int> ActualNodeIds { get; init; } = FrozenSet<int>.Empty;
    public IReadOnlySet<int> DesiredNodeIds { get; init; } = FrozenSet<int>.Empty;
    public IReadOnlySet<int> AddedNodeIds { get; init; } = FrozenSet<int>.Empty;
    public IReadOnlySet<int> RemovedNodeIds { get; init; } = FrozenSet<int>.Empty;
}

public sealed record DaevanionBoardEditSnapshot
{
    public string? ProfileId { get; init; }
    public string? ClassId { get; init; }
    public int? CharacterLevel { get; init; }
    public long Generation { get; init; }
    public long Revision { get; init; }
    public bool IsFresh { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public IReadOnlyList<DaevanionBoardEditBoard> Boards { get; init; } = [];
}

public sealed record DaevanionBoardEditOverlay
{
    public bool CanPresent { get; init; }
    public long Revision { get; init; }
    public IReadOnlySet<int> AddedNodeIds { get; init; } = FrozenSet<int>.Empty;
    public IReadOnlySet<int> RemovedNodeIds { get; init; } = FrozenSet<int>.Empty;
}

/// <summary>RAM-only local intentions against independently fresh own board observations.</summary>
public sealed class DaevanionBoardEditService : IDisposable
{
    private readonly CharacterCombatStateService _character;
    private readonly INetworkCharacterCombatSource _network;
    private readonly object _gate = new();
    // Private RAM keys deliberately omit the transient entity key and generation. A fresh
    // rejoin may resume intentions only after the same native owner and profile are bound.
    private sealed record Binding(string ProfileId, long CharacterId, int ServerId, string ClassId, string Fingerprint)
    {
        public override string ToString() => "Private board edit binding";
    }
    private sealed class Draft
    {
        internal bool Active;
        internal long EditVersion;
        internal readonly Dictionary<int, Dictionary<int, bool>> Intentions = [];
    }
    private sealed record Input(NetworkCharacterCombatSnapshot Network, bool Running, bool Coherent,
        Binding? Binding, long ReadRevision);
    private readonly Dictionary<Binding, Draft> _drafts = [];
    private Binding? _publishedBinding;
    private DaevanionBoardEditSnapshot _snapshot = new();
    private long _readRevision, _publishedReadRevision;
    private bool _disposed;

    public DaevanionBoardEditService(CharacterCombatStateService character, INetworkCharacterCombatSource network)
    {
        _character = character; _network = network;
        character.Changed += Refresh;
        network.Changed += Refresh;
        network.OwnCharacterChanged += Refresh;
        Refresh();
    }

    public DaevanionBoardEditSnapshot Snapshot
    {
        get { Refresh(); lock (_gate) return _snapshot; }
    }
    public event Action? Changed;
    public bool ToggleNode(int boardId, int nodeId) => Edit(boardId, nodeId, EditAction.Toggle);
    public bool ResetToCurrent() => Edit(0, 0, EditAction.Reset);
    public bool EndEdit() => Edit(0, 0, EditAction.End);
    public bool BeginFromCurrent() => Edit(0, 0, EditAction.Begin);
    private enum EditAction { Toggle, Reset, End, Begin }

    private Input ReadInput()
    {
        // Never acquire the network/character locks while holding our publication gate.
        var revision = Interlocked.Increment(ref _readRevision);
        Input? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var evidence = _network.CombatSnapshot;
            var profileId = _character.ResolveCurrentOwnProfileId(evidence);
            var running = _network.Running;
            var coherent = ReferenceEquals(evidence, _network.CombatSnapshot) && running == _network.Running;
            Binding? binding = null;
            if (coherent && running && !string.IsNullOrWhiteSpace(profileId) && evidence.Character is { } own
                && own.Generation > 0 && evidence.Generation == own.Generation && evidence.Sequence > 0
                && evidence.ClassId == own.Identity.ClassId && DaevanionRuleCatalog.Default.ForClass(evidence.ClassId ?? "").Count != 0)
                binding = new(profileId, own.Identity.CharacterId, own.Identity.ServerId, evidence.ClassId!,
                    DaevanionRuleCatalog.Default.CatalogFingerprint);
            last = new(evidence, running, coherent, binding, revision);
            if (last.Coherent) return last;
        }
        return last!;
    }

    private bool StillCurrent(Input input)
    {
        if (Volatile.Read(ref _disposed) || !input.Coherent || !input.Running || input.Binding is not { } binding
            || !_network.Running || !ReferenceEquals(input.Network, _network.CombatSnapshot)) return false;
        // Profile resolution can disappear without a network object change. The centralized
        // authority read deliberately does not wait for the skill/graph snapshot to catch up.
        var profileId = _character.ResolveCurrentOwnProfileId(input.Network);
        return !Volatile.Read(ref _disposed) && profileId == binding.ProfileId
            && _network.Running && ReferenceEquals(input.Network, _network.CombatSnapshot);
    }

    private void Refresh()
    {
        var input = ReadInput();
        bool changed;
        lock (_gate) changed = !_disposed && Publish(input);
        if (changed) SafeEvent.Raise(Changed, "Lokaler Daevanion-Entwurf");
    }

    private bool Edit(int boardId, int nodeId, EditAction action)
    {
        var input = ReadInput();
        if (!StillCurrent(input)) { Refresh(); return false; }
        bool changed, accepted = false;
        Binding? editedBinding = null;
        Draft? editedDraft = null;
        bool priorActive = false;
        long editVersion = 0;
        Dictionary<int, Dictionary<int, bool>>? priorIntentions = null;
        lock (_gate)
        {
            if (_disposed) return false;
            changed = Publish(input);
            var binding = input.Binding;
            var canBegin = _snapshot.IsFresh && _snapshot.Boards.Any(board => board.CanEdit);
            if (input.ReadRevision == _publishedReadRevision && binding is not null && (action == EditAction.End || canBegin))
            {
                _drafts.TryGetValue(binding, out var draft);
                var board = action == EditAction.Toggle ? _snapshot.Boards.FirstOrDefault(row => row.Board.Id == boardId) : null;
                var node = board?.Board.Nodes.FirstOrDefault(row => row.Id == nodeId);
                var valid = action != EditAction.Toggle || board is { CanEdit: true } && node is not null
                    && node.Kind != DaevanionNodeKind.Start;
                if (valid)
                {
                    draft ??= new();
                    _drafts[binding] = draft;
                    editedBinding = binding; editedDraft = draft; priorActive = draft.Active;
                    priorIntentions = draft.Intentions.ToDictionary(pair => pair.Key, pair => new Dictionary<int, bool>(pair.Value));
                    editVersion = ++draft.EditVersion;
                    switch (action)
                    {
                        case EditAction.End:
                            draft.Active = false; draft.Intentions.Clear(); accepted = true; break;
                        case EditAction.Reset:
                            draft.Active = true; draft.Intentions.Clear(); accepted = true; break;
                        case EditAction.Begin:
                            if (!draft.Active) { draft.Intentions.Clear(); draft.Active = true; }
                            accepted = true; break;
                        case EditAction.Toggle:
                            if (!draft.Active) { draft.Intentions.Clear(); draft.Active = true; }
                            var desired = !board!.DesiredNodeIds.Contains(nodeId);
                            if (!draft.Intentions.TryGetValue(boardId, out var intentions))
                                draft.Intentions[boardId] = intentions = [];
                            if (desired == board.ActualNodeIds.Contains(nodeId)) intentions.Remove(nodeId);
                            else intentions[nodeId] = desired;
                            if (intentions.Count == 0) draft.Intentions.Remove(boardId);
                            accepted = true; break;
                    }
                    changed |= Publish(input);
                }
            }
        }
        if (changed) SafeEvent.Raise(Changed, "Lokaler Daevanion-Entwurf");
        if (!StillCurrent(input))
        {
            // Reject the stale click itself, not merely its return value. A later rejoin must
            // not revive an intention created by this rejected operation. Concurrent edits
            // own a later version and are never overwritten by this rollback.
            var current = ReadInput();
            bool rolledChanged;
            lock (_gate)
            {
                if (!_disposed && editedBinding is not null && editedDraft is not null
                    && _drafts.GetValueOrDefault(editedBinding) == editedDraft && editedDraft.EditVersion == editVersion)
                {
                    editedDraft.Active = priorActive;
                    if (action == EditAction.Toggle && priorActive)
                    {
                        // Undo only our node edit; other live-fulfilled intentions stay consumed.
                        if (priorIntentions!.TryGetValue(boardId, out var previous) && previous.TryGetValue(nodeId, out var wanted))
                        {
                            if (!editedDraft.Intentions.TryGetValue(boardId, out var currentIntentions))
                                editedDraft.Intentions[boardId] = currentIntentions = [];
                            currentIntentions[nodeId] = wanted;
                        }
                        else if (editedDraft.Intentions.TryGetValue(boardId, out var currentIntentions))
                        {
                            currentIntentions.Remove(nodeId);
                            if (currentIntentions.Count == 0) editedDraft.Intentions.Remove(boardId);
                        }
                    }
                    else if (action != EditAction.Begin || !priorActive)
                    {
                        editedDraft.Intentions.Clear();
                        foreach (var pair in priorIntentions!) editedDraft.Intentions[pair.Key] = pair.Value;
                    }
                    ++editedDraft.EditVersion;
                }
                rolledChanged = !_disposed && Publish(current);
            }
            if (rolledChanged) SafeEvent.Raise(Changed, "Lokaler Daevanion-Entwurf");
            return false;
        }
        return accepted;
    }

    // Caller owns only our gate. No callbacks, source reads or filesystem writes occur here.
    private bool Publish(Input input)
    {
        if (!input.Coherent || input.ReadRevision < _publishedReadRevision) return false;
        _publishedReadRevision = input.ReadRevision;
        var binding = input.Binding;
        Draft? draft = null;
        if (binding is not null) _drafts.TryGetValue(binding, out draft);
        var own = input.Network.Character;
        var classId = binding?.ClassId ?? own?.Identity.ClassId;
        var boards = DaevanionRuleCatalog.Default.ForClass(classId ?? "");
        var domain = input.Network.DaevanionBoards;
        var fresh = binding is not null && input.Coherent && domain is
            { IsFresh: true, ObservedAt: not null, Sequence: > 0, Value: not null, Source: "own-daevanion-board-lists" }
            && domain.Sequence <= input.Network.Sequence
            && domain.Status is NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial;
        var rows = new List<DaevanionBoardEditBoard>();
        foreach (var rules in boards)
        {
            var known = fresh && domain.Value!.Boards.TryGetValue(rules.Board.Id, out _);
            HashSet<int> actual = known ? domain.Value!.Boards[rules.Board.Id].ToHashSet() : [];
            if (known && actual.Any(id => !rules.Board.Nodes.Any(node => node.Id == id))) known = false;
            if (known) actual.Add(rules.Board.Nodes.Single(node => node.Kind == DaevanionNodeKind.Start).Id);
            else actual.Clear();
            var desired = actual.ToHashSet();
            if (known && draft is { Active: true } && draft.Intentions.TryGetValue(rules.Board.Id, out var intentions))
            {
                foreach (var id in intentions.Keys.ToArray())
                {
                    if (intentions[id] == actual.Contains(id)) { intentions.Remove(id); continue; }
                    if (intentions[id]) desired.Add(id); else desired.Remove(id);
                }
                if (intentions.Count == 0) draft.Intentions.Remove(rules.Board.Id);
            }
            var unlocked = binding is not null && input.Network.CharacterLevel is { } level && level >= rules.MinimumLevel;
            rows.Add(new()
            {
                Board = rules.Board, IsKnown = known, IsUnlocked = unlocked, CanEdit = known && unlocked,
                ActualNodeIds = actual.ToFrozenSet(), DesiredNodeIds = desired.ToFrozenSet(),
                AddedNodeIds = desired.Except(actual).ToFrozenSet(), RemovedNodeIds = actual.Except(desired).ToFrozenSet()
            });
        }
        var next = new DaevanionBoardEditSnapshot
        {
            ProfileId = binding?.ProfileId, ClassId = classId, CharacterLevel = binding is not null ? input.Network.CharacterLevel : null,
            Generation = own?.Generation ?? input.Network.Generation, IsFresh = fresh, IsActive = draft?.Active == true,
            ObservedAt = fresh ? domain.ObservedAt : null, Boards = rows.AsReadOnly()
        };
        var bindingChanged = _publishedBinding != binding;
        _publishedBinding = binding;
        if (!bindingChanged && Equivalent(_snapshot, next)) return false;
        _snapshot = next with { Revision = _snapshot.Revision + 1 };
        return true;
    }

    private static bool Equivalent(DaevanionBoardEditSnapshot a, DaevanionBoardEditSnapshot b) =>
        a.ProfileId == b.ProfileId && a.ClassId == b.ClassId && a.CharacterLevel == b.CharacterLevel
        && a.Generation == b.Generation && a.IsFresh == b.IsFresh && a.IsActive == b.IsActive && a.ObservedAt == b.ObservedAt
        && a.Boards.Count == b.Boards.Count && a.Boards.Zip(b.Boards).All(pair =>
            pair.First.Board.Id == pair.Second.Board.Id && pair.First.IsKnown == pair.Second.IsKnown
            && pair.First.IsUnlocked == pair.Second.IsUnlocked && pair.First.CanEdit == pair.Second.CanEdit
            && pair.First.ActualNodeIds.SetEquals(pair.Second.ActualNodeIds)
            && pair.First.DesiredNodeIds.SetEquals(pair.Second.DesiredNodeIds)
            && pair.First.AddedNodeIds.SetEquals(pair.Second.AddedNodeIds)
            && pair.First.RemovedNodeIds.SetEquals(pair.Second.RemovedNodeIds));

    public DaevanionBoardEditOverlay? ForOverlay(OwnCharacterContext own, DaevanionBoard board)
    {
        var input = ReadInput();
        DaevanionBoardEditOverlay? result;
        bool changed;
        lock (_gate)
        {
            if (_disposed) return null;
            changed = Publish(input);
            bool Matches(Binding key) => key.CharacterId == own.Identity.CharacterId && key.ServerId == own.Identity.ServerId
                && key.ClassId == own.Identity.ClassId && key.ClassId == board.ClassId
                && key.Fingerprint == DaevanionRuleCatalog.Default.CatalogFingerprint;
            var binding = input.Binding is { } current && Matches(current) ? current
                : _drafts.Keys.FirstOrDefault(key => Matches(key) && _drafts[key].Active);
            if (binding is null || !_drafts.TryGetValue(binding, out var draft) || !draft.Active) result = null;
            else
            {
                var row = _snapshot.Boards.FirstOrDefault(item => item.Board.Id == board.Id && item.Board.FamilyId == board.FamilyId);
                var present = input.ReadRevision == _publishedReadRevision && input.Binding == binding && ReferenceEquals(input.Network.Character, own)
                    && own.Generation == _snapshot.Generation && _snapshot.IsFresh && row is { CanEdit: true };
                result = new()
                {
                    CanPresent = present, Revision = _snapshot.Revision,
                    AddedNodeIds = present ? row!.AddedNodeIds : FrozenSet<int>.Empty,
                    RemovedNodeIds = present ? row!.RemovedNodeIds : FrozenSet<int>.Empty
                };
            }
        }
        if (changed) SafeEvent.Raise(Changed, "Lokaler Daevanion-Entwurf");
        return result is not null && !StillCurrent(input) ? result with
            { CanPresent = false, AddedNodeIds = FrozenSet<int>.Empty, RemovedNodeIds = FrozenSet<int>.Empty } : result;
    }

    public void Dispose()
    {
        _character.Changed -= Refresh; _network.Changed -= Refresh; _network.OwnCharacterChanged -= Refresh;
        lock (_gate) { _disposed = true; _drafts.Clear(); _publishedBinding = null; _snapshot = new() { Revision = _snapshot.Revision + 1 }; }
    }
}
