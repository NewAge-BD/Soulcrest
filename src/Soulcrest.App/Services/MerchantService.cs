using Soulcrest.App.Network;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;

namespace Soulcrest.App.Services;

public sealed record MerchantSnapshot
{
    public byte GroupKey { get; }
    public long SourceGeneration { get; }
    public string Stream { get; }
    public int? ServerId { get; }
    public DateTimeOffset At { get; }
    public IReadOnlyList<MerchantOffer> Offers { get; }

    public MerchantSnapshot(byte groupKey, long sourceGeneration, string stream, int? serverId,
        DateTimeOffset at, IEnumerable<MerchantOffer> offers)
    {
        GroupKey = groupKey;
        SourceGeneration = sourceGeneration;
        Stream = stream;
        ServerId = serverId;
        At = at;
        Offers = Array.AsReadOnly(offers.ToArray());
    }
}

public sealed record MerchantCounterRow(int SlotIndex, string PetId, string Counter, bool IsFresh, bool IsMax,
    string FarmRegions = "");

/// <summary>Ephemeral, paired merchant lists projected onto the global pet collection.</summary>
public sealed class MerchantService : IDisposable
{
    private static readonly TimeSpan HandshakeWindow = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly INetworkMerchantSource _source;
    private readonly ProgressService _progress;
    private readonly Func<int, int?> _petLookup;
    private MerchantSnapshot? _snapshot;
    private DateTimeOffset _snapshotObservedAt;
    private PendingStatus? _pending;
    private long _minimumGeneration;
    private long _latestGeneration = -1;
    private bool _disposed;

    private sealed record PendingStatus(byte GroupKey, string Stream, long Generation, int? ServerId,
        DateTimeOffset At, MerchantSnapshot? ReusableSnapshot)
    {
        internal bool Matches(MerchantNetworkMessage message, byte groupKey) => GroupKey == groupKey &&
            string.Equals(Stream, message.Stream, StringComparison.Ordinal) &&
            Generation == message.Generation && ServerId == message.ServerId;
    }

    public MerchantService(INetworkMerchantSource source, ProgressService progress)
        : this(source, progress, MerchantPetItems.Find) { }

    internal MerchantService(INetworkMerchantSource source, ProgressService progress, Func<int, int?> petLookup)
    {
        _source = source;
        _progress = progress;
        _petLookup = petLookup;
        _source.MerchantMessageReceived += OnMessage;
        _source.MerchantSessionReset += Reset;
        _progress.Changed += ProgressChanged;
    }

    public event Action? Changed;

    public MerchantSnapshot? Snapshot { get { lock (_gate) return _snapshot; } }

    public IReadOnlyList<MerchantCounterRow> Rows
    {
        get
        {
            var snapshot = Snapshot;
            if (snapshot is null) return [];
            var rows = new List<MerchantCounterRow>();
            var offers = snapshot.Offers.OrderBy(offer => offer.GoodsId).ToArray();
            for (var slot = 0; slot < offers.Length; slot++)
            {
                if (_petLookup(offers[slot].ItemId) is not { } number ||
                    PetIdTable.Find(number)?.CatalogId is not { } petId) continue;
                var counter = _progress.ReadMerchantCounter(petId);
                rows.Add(new(slot, petId, counter.Counter, counter.IsFresh, counter.IsMax,
                    PetFarmRegions.Find(number) is { } regions ? string.Join(" / ", regions) : "?"));
            }
            lock (_gate)
                return !_disposed && ReferenceEquals(snapshot, _snapshot) ? rows.AsReadOnly() : [];
        }
    }

    private void OnMessage(MerchantNetworkMessage message)
    {
        var bytes = message.Message;
        if (bytes is { Length: >= 2 } && (bytes[0] | bytes[1] << 8) is not (0x5691 or 0x5693)) return;
        lock (_gate)
        {
            if (_disposed || message.Generation < _minimumGeneration) return;
            var previous = _snapshot;
            var previousObservedAt = _snapshotObservedAt;
            _snapshot = null;
            _snapshotObservedAt = default;
            if (string.IsNullOrWhiteSpace(message.Stream) || message.Generation < 0 || message.ServerId is <= 0)
            {
                // A malformed shop envelope cannot discard an independent pending status.
            }
            else if (bytes is { Length: >= 2 } && (bytes[0] | bytes[1] << 8) == 0x5693)
            {
                if (MerchantShopMessage.TryParseStatus(bytes) is { } group)
                {
                    _latestGeneration = Math.Max(_latestGeneration, message.Generation);
                    _minimumGeneration = Math.Max(_minimumGeneration, message.Generation);
                    // A status still hides the old page immediately. Retain its identity only
                    // inside this exact pending handshake, never as authority for a solo list.
                    var reusable = previous is not null && MatchesSource(previous, message, group) &&
                        message.At >= previousObservedAt ? previous :
                        _pending is { ReusableSnapshot: { } saved } pending && pending.Matches(message, group) &&
                        message.At >= pending.At ? saved : null;
                    _pending = new(group, message.Stream, message.Generation, message.ServerId, message.At, reusable);
                }
            }
            else if (bytes is not null && MerchantShopMessage.TryParse(bytes) is { } parsed)
            {
                // A fully validated repetition of the current page is a no-op. It cannot establish
                // a page, recover an invalidated snapshot or cross any source/context boundary.
                if (previous is not null && MatchesSource(previous, message, parsed.GroupKey) &&
                    message.At >= previousObservedAt && SameOffers(previous.Offers, parsed.Offers))
                {
                    _snapshot = previous;
                    _snapshotObservedAt = message.At;
                    return;
                }
                if (_pending is { } pending && pending.Matches(message, parsed.GroupKey) &&
                    message.At - pending.At >= TimeSpan.Zero && message.At - pending.At <= HandshakeWindow)
                {
                    _latestGeneration = Math.Max(_latestGeneration, message.Generation);
                    _snapshot = pending.ReusableSnapshot is { } reusable && SameOffers(reusable.Offers, parsed.Offers)
                        ? reusable : new(parsed.GroupKey, message.Generation, message.Stream, message.ServerId, message.At, parsed.Offers);
                    _snapshotObservedAt = message.At;
                    _pending = null;
                }
            }
        }
        SafeEvent.Raise(Changed, "Händler-Angebote");
    }

    private static bool MatchesSource(MerchantSnapshot snapshot, MerchantNetworkMessage message, byte groupKey) =>
        snapshot.GroupKey == groupKey && snapshot.SourceGeneration == message.Generation &&
        string.Equals(snapshot.Stream, message.Stream, StringComparison.Ordinal) && snapshot.ServerId == message.ServerId;

    private static bool SameOffers(IReadOnlyList<MerchantOffer> first, IReadOnlyList<MerchantOffer> second) =>
        first.OrderBy(offer => offer.GoodsId).SequenceEqual(second.OrderBy(offer => offer.GoodsId));

    private void Reset()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _snapshot = null;
            _snapshotObservedAt = default;
            _pending = null;
            var floor = Math.Max(_minimumGeneration, _latestGeneration);
            _minimumGeneration = floor == long.MaxValue ? long.MaxValue : floor + 1;
        }
        SafeEvent.Raise(Changed, "Händler-Angebote");
    }

    private void ProgressChanged()
    {
        lock (_gate) if (_disposed || _snapshot is null) return;
        SafeEvent.Raise(Changed, "Händler-Angebote");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _snapshot = null;
            _snapshotObservedAt = default;
            _pending = null;
        }
        _source.MerchantMessageReceived -= OnMessage;
        _source.MerchantSessionReset -= Reset;
        _progress.Changed -= ProgressChanged;
    }
}
