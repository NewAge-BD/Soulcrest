using System.Diagnostics;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Transient, monotonic animation state shared by progress events and the overlay paint timer.</summary>
internal sealed class PetLevelUpAnimation
{
    internal const double DurationSeconds = 30;
    internal sealed record Frame(PetLevelUp Change, double Progress);
    private const int MaximumPending = 8;
    private readonly object _gate = new();
    private readonly LinkedList<PetLevelUp> _pending = new();
    private bool _accepting = true, _closed;
    private PetLevelUp? _active;
    private long _startedAt;

    internal void Enqueue(PetLevelUp change, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            if (!_accepting) return;
            if (_active is { } active && SamePet(active, change))
            {
                if (change.Level > active.Level)
                {
                    _active = change;
                    _startedAt = timestamp;
                }
                return;
            }
            for (var node = _pending.First; node is not null; node = node.Next)
            {
                if (!SamePet(node.Value, change)) continue;
                // A queued upgrade keeps its position; lower/duplicate notifications cannot renew it.
                if (change.Level > node.Value.Level) node.Value = change;
                return;
            }
            if (_active is null)
            {
                _active = change;
                _startedAt = timestamp;
                return;
            }
            if (_pending.Count == MaximumPending) _pending.RemoveFirst();
            _pending.AddLast(change);
        }
    }

    internal Frame? Sample(long timestamp)
    {
        lock (_gate)
        {
            if (_active is null) return null;
            // Convert before subtraction so even extreme or backwards caller timestamps remain bounded.
            var elapsed = ((double)timestamp - _startedAt) / Stopwatch.Frequency;
            if (elapsed >= DurationSeconds)
            {
                _active = null;
                if (_pending.First is not { } next) return null;
                _active = next.Value;
                _pending.RemoveFirst();
                _startedAt = timestamp;
                // Start exactly one waiting message when sampled, however late this paint occurs.
                return new(_active, 0);
            }
            return new(_active, Math.Clamp(elapsed / DurationSeconds, 0, 1));
        }
    }

    internal void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _accepting = enabled && !_closed;
            if (!_accepting) ClearLocked();
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _accepting = false;
            ClearLocked();
        }
    }

    internal void Clear()
    {
        lock (_gate) ClearLocked();
    }

    private void ClearLocked()
    {
        _active = null;
        _startedAt = 0;
        _pending.Clear();
    }

    private static bool SamePet(PetLevelUp first, PetLevelUp second) =>
        string.Equals(first.PetId, second.PetId, StringComparison.Ordinal);
}
