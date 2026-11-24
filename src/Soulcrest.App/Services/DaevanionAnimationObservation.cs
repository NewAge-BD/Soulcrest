using Soulcrest.App.Overlay;

namespace Soulcrest.App.Services;

/// <summary>A passive reader for an already constructed window; never creates or starts one.</summary>
internal sealed class DaevanionAnimationObservation : IDisposable
{
    private readonly object _gate = new();
    private Registration? _current;
    private bool _disposed;
    internal DaevanionAnimationHealth? Snapshot => Volatile.Read(ref _current)?.Read();

    internal IDisposable Register(Func<DaevanionAnimationHealth> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var registration = new Registration(this, read);
            Volatile.Write(ref _current, registration);
            return registration;
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; Volatile.Write(ref _current, null); }
    }

    private sealed class Registration(DaevanionAnimationObservation owner, Func<DaevanionAnimationHealth> read) : IDisposable
    {
        internal DaevanionAnimationHealth Read() => read();
        public void Dispose() => Interlocked.CompareExchange(ref owner._current, null, this);
    }
}
