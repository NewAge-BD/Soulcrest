using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private const int MaximumFarmStacks = 10000;
    private const int MaximumQueuedFarmDrops = 10000;
    private readonly Dictionary<long, CurrencyInventoryItem> _farmStacks = [];
    private readonly HashSet<long> _ambiguousFarmStacks = [];
    private readonly Queue<(string Stream, long Revision, NetworkFarmDrop Drop)> _farmDropQueue = [];
    private OwnCharacterContext? _farmCharacter;
    // Event sequence is monotone for this process, including source invalidations and Own changes.
    private long _farmDropSequence, _farmDropRevision;
    private bool _dispatchingFarmDrops;

    public event Action<NetworkFarmDrop>? FarmDropGained;

    /// <summary>Called only after the common entry transaction has qualified this list and Own.</summary>
    private void BindFarmDropBaseline(CurrencyInventoryList? list)
    {
        ResetFarmDropContext();
        var character = _ownCharacter;
        if (character is null) return;
        _farmCharacter = character;
        if (list is null) return;
        if (list.AllItems.Count > MaximumFarmStacks) return;
        foreach (var item in list.AllItems)
        {
            if (item.InstanceKey <= 0 || !_farmStacks.TryAdd(item.InstanceKey, item))
            {
                _farmStacks.Clear();
                return;
            }
        }
    }

    /// <summary>Uses the common decoder result; no second inventory parser or capture.</summary>
    private void ProcessFarmInventoryChanges(string stream, CurrencyInventoryChanges? changes, DateTimeOffset at)
    {
        if (!TryGetFarmCharacter(stream, out var character)) return;
        if (changes is null || changes.AllChanges.Count > MaximumFarmStacks)
        {
            ResetFarmDropContext();
            return;
        }
        EnsureFarmCharacter(character);
        var inInstance = FarmDropInInstance(character, at);
        var drops = new List<(int DataId, long Quantity)>(Math.Min(changes.AllChanges.Count, 64));
        try
        {
            foreach (var change in changes.AllChanges)
            {
                var item = change.Item;
                if (item.InstanceKey <= 0)
                {
                    ResetFarmDropContext();
                    return;
                }
                _farmStacks.TryGetValue(item.InstanceKey, out var previous);
                if (change.ModificationType == 2)
                {
                    _farmStacks.Remove(item.InstanceKey);
                    _ambiguousFarmStacks.Remove(item.InstanceKey);
                    continue;
                }
                if (_ambiguousFarmStacks.Contains(item.InstanceKey))
                {
                    // An explicit absolute modification may establish a new base without income.
                    // Repeated creation packets alone can never rehabilitate an ambiguous key.
                    if (change.ModificationType == 1)
                    {
                        if (_farmStacks.Count >= MaximumFarmStacks) { ResetFarmDropContext(); return; }
                        _ambiguousFarmStacks.Remove(item.InstanceKey);
                        _farmStacks[item.InstanceKey] = item;
                    }
                    continue;
                }
                if (change.ModificationType == 0 && previous is not null)
                {
                    // A second creation for a known key cannot safely replace its comparison base.
                    _farmStacks.Remove(item.InstanceKey);
                    if (_ambiguousFarmStacks.Count >= MaximumFarmStacks) { ResetFarmDropContext(); return; }
                    _ambiguousFarmStacks.Add(item.InstanceKey);
                    continue;
                }
                if (!_farmStacks.ContainsKey(item.InstanceKey) && _farmStacks.Count >= MaximumFarmStacks)
                {
                    ResetFarmDropContext();
                    return;
                }
                _farmStacks[item.InstanceKey] = item;
                // Only the observed personal-inventory drop is qualified; storage still updates the cache.
                if (item.Container != 1 || change.IncreaseReason != 5
                    || change.DecreaseReason != 0 || change.UpdateReason != 0) continue;
                long quantity = 0;
                if (change.ModificationType == 0 && previous is null)
                    quantity = item.Count;
                else if (change.ModificationType == 1 && previous is not null
                    && previous.ItemId == item.ItemId && previous.Container == item.Container && item.Count > previous.Count)
                    quantity = item.Count - previous.Count;
                if (quantity <= 0) continue;
                var catalog = MonsterLootItemCatalog.Default;
                // Prepared exact personal-inventory variant only; no cube-origin inference.
                if ((catalog.IsCurrency(item.ItemId) && item.ItemId != NetworkFarmDrop.UniversalSoulCrystalItemId)
                    || catalog.IsAutomaticallyConsumedSoul(item.ItemId)) continue;
                drops.Add((item.ItemId, quantity));
            }
        }
        catch (InvalidDataException error)
        {
            // Classification is part of duplicate exclusion; missing/corrupt labels fail closed.
            ResetFarmDropContext();
            LogFile.Error("Farmdrop-Katalog", error);
            return;
        }
        if (!TryReserveFarmDrops(drops.Count)) return;
        foreach (var drop in drops)
            EnqueueFarmDrop(stream, character, at, drop.DataId, drop.Quantity,
                drop.DataId == NetworkFarmDrop.UniversalSoulCrystalItemId ? NetworkFarmDropKind.UniversalSoulCrystal : NetworkFarmDropKind.Item,
                inInstance);
        DispatchFarmDrops();
    }

    private void ProcessFarmCrystalMessage(string stream, byte[] message, DateTimeOffset at)
    {
        if (!TryGetFarmCharacter(stream, out var character)) return;
        var gains = SoulCrystalMessage.TryParseGains(message);
        if (gains is null) return;
        EnsureFarmCharacter(character);
        var inInstance = FarmDropInInstance(character, at);
        if (!TryReserveFarmDrops(gains.Count)) return;
        foreach (var gain in gains)
            EnqueueFarmDrop(stream, character, at, gain.Type, gain.Amount, NetworkFarmDropKind.SoulCrystal, inInstance);
        DispatchFarmDrops();
    }

    private void ProcessFarmPetSoulGain(string stream, int petNumber, int quantity, DateTimeOffset at)
    {
        if (petNumber <= 0 || quantity <= 0 || !TryGetFarmCharacter(stream, out var character)) return;
        EnsureFarmCharacter(character);
        if (!TryReserveFarmDrops(1)) return;
        EnqueueFarmDrop(stream, character, at, petNumber, quantity, NetworkFarmDropKind.PetSoul,
            FarmDropInInstance(character, at));
        DispatchFarmDrops();
    }

    private bool TryGetFarmCharacter(string stream, out OwnCharacterContext character)
    {
        character = _ownCharacter!;
        return Running && character is not null && _identityStream == stream;
    }

    private void EnsureFarmCharacter(OwnCharacterContext character)
    {
        if (ReferenceEquals(_farmCharacter, character)) return;
        ResetFarmDropContext();
        _farmCharacter = character;
    }

    private bool FarmDropInInstance(OwnCharacterContext character, DateTimeOffset at)
    {
        var world = WorldContext.Snapshot;
        return world.Status == WorldContextStatus.Confirmed && world.Kind == WorldContextKind.Instance
            && world.BindingGeneration == character.Generation && (world.ObservedAt is null || world.ObservedAt <= at);
    }

    private bool TryReserveFarmDrops(int count)
    {
        if (count <= MaximumQueuedFarmDrops - _farmDropQueue.Count) return true;
        ResetFarmDropContext();
        return false;
    }

    private void EnqueueFarmDrop(string stream, OwnCharacterContext character, DateTimeOffset at, int dataId,
        long quantity, NetworkFarmDropKind kind, bool inInstance) =>
        _farmDropQueue.Enqueue((stream, _farmDropRevision,
            new(at, ++_farmDropSequence, character, dataId, quantity, kind, inInstance)));

    private void DispatchFarmDrops()
    {
        if (_dispatchingFarmDrops) return;
        _dispatchingFarmDrops = true;
        try
        {
            while (_farmDropQueue.TryDequeue(out var queued))
                foreach (var subscriber in FarmDropGained?.GetInvocationList().Cast<Action<NetworkFarmDrop>>() ?? [])
                {
                    if (!Running || queued.Revision != _farmDropRevision || _identityStream != queued.Stream
                        || !ReferenceEquals(queued.Drop.Character, _ownCharacter)
                        || !ReferenceEquals(_farmCharacter, _ownCharacter)) break;
                    try { subscriber(queued.Drop); }
                    catch (Exception error) { LogFile.Error("Farmdrop-Netzwerkereignis", error); }
                }
        }
        finally { _dispatchingFarmDrops = false; }
    }

    private void ResetFarmDropContext()
    {
        _farmStacks.Clear();
        _ambiguousFarmStacks.Clear();
        _farmCharacter = null;
        _farmDropRevision++;
        _farmDropQueue.Clear();
    }
}
