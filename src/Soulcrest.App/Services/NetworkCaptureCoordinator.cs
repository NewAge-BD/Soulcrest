using Soulcrest.App.Network;

namespace Soulcrest.App.Services;

public enum NetworkCaptureConsumer { Loot, Combat }

/// <summary>One passive source, independent consumers. Stopping one consumer does not stop another.</summary>
public sealed class NetworkCaptureCoordinator(INetworkLootSource source) : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<NetworkCaptureConsumer> _demands = [];
    private bool _disposed;
    private bool _reconciling, _sourceWasRequired;
    private long _revision;

    public bool IsRequested(NetworkCaptureConsumer consumer)
    {
        lock (_gate) return _demands.Contains(consumer);
    }

    public void SetDemand(NetworkCaptureConsumer consumer, bool enabled)
    {
        if (!Enum.IsDefined(consumer)) throw new ArgumentOutOfRangeException(nameof(consumer));
        lock (_gate)
        {
            if (_disposed)
            {
                if (enabled) throw new ObjectDisposedException(nameof(NetworkCaptureCoordinator));
                return;
            }
            var changed = enabled ? _demands.Add(consumer) : _demands.Remove(consumer);
            if (changed) _revision++;
            // A reentrant capture callback only changes demand; it never waits for a source lock.
            if (_reconciling) return;
            _reconciling = true;
        }
        Reconcile();
    }

    private void Reconcile()
    {
        try
        {
            while (true)
            {
                long revision;
                bool any, progress;
                lock (_gate)
                {
                    revision = _revision;
                    any = !_disposed && _demands.Count > 0;
                    progress = !_disposed && _demands.Contains(NetworkCaptureConsumer.Loot);
                }
                if (source is INetworkProgressControl control) control.SetProgressProcessingEnabled(progress);
                lock (_gate) { if (_revision != revision) continue; }
                if (any)
                {
                    _sourceWasRequired = true;
                    if (!source.Running) source.Start();
                }
                else if (_sourceWasRequired || source.Running)
                {
                    source.Stop();
                    _sourceWasRequired = false;
                }
                lock (_gate)
                {
                    if (_revision != revision) continue;
                    _reconciling = false;
                    return;
                }
            }
        }
        catch { lock (_gate) _reconciling = false; throw; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _demands.Clear();
            _revision++;
            if (_reconciling) return;
            _reconciling = true;
        }
        Reconcile();
    }
}
