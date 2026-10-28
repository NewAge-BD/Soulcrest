using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Services;

public enum LootTrackerReplayStatus { Recording, NotPrepared, Loading, Preparing, Ready, Stale, Canceled, Failed }
public enum LootTrackerReplayFailure { None, InvalidSession, InvalidCache, Storage, Preparation }
public sealed record LootTrackerReplayState(string SessionId, LootTrackerSessionKind Kind, LootTrackerReplayStatus Status,
    int Progress = 0, LootTrackerPreparedReplay? Replay = null, LootTrackerReplayFailure Reason = LootTrackerReplayFailure.None)
{
    public bool IsBusy => Status is LootTrackerReplayStatus.Loading or LootTrackerReplayStatus.Preparing;
    public bool CanPlay => Replay is not null && Status == LootTrackerReplayStatus.Ready;
}

/// <summary>Manually prepared terminal-session artifacts. No recorder, raw journal or UI thread is owned here.</summary>
public sealed class LootTrackerReplayService : IDisposable
{
    private const long MaximumCacheBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
        { MaxDepth = 64, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly object _gate = new();
    private readonly string _root;
    private readonly Func<LootTrackerSession, ReplayPositionReadResult> _readPositions;
    private readonly Func<string, CancellationToken, Task> _flush;
    private readonly Func<LootTrackerSession, ReplayPositionReadResult, CancellationToken, Action<int>, LootTrackerPreparedReplay> _prepare;
    private readonly Dictionary<(string Source, string Id, LootTrackerSessionKind Kind), Entry> _entries = [];
    private readonly HashSet<(string Source, string Id, LootTrackerSessionKind Kind)> _deleted = [];
    private bool _disposed;
    private sealed class Entry(LootTrackerReplayState state)
    {
        internal LootTrackerReplayState State = state;
        internal LootTrackerSession? Source;
        internal long Generation;
        internal CancellationTokenSource? Cancellation;
        internal Task<LootTrackerReplayState>? Job;
        internal bool IsPreparation;
    }

    public LootTrackerReplayService() : this(Path.Combine(AppPaths.DataDirectory, "cache", "loot-replays")) { }
    public LootTrackerReplayService(string root, string? positionRoot = null) : this(root,
        session => positionRoot is null ? ReplayPositionStore.Read(session.Id, session.Elapsed)
            : ReplayPositionStore.Read(positionRoot, session.Id, session.Elapsed),
        (id, token) => positionRoot is null ? ReplayPositionStore.FlushAsync(id, token)
            : ReplayPositionStore.FlushAsync(positionRoot, id, token),
        (session, positions, token, progress) => LootTrackerPreparedReplay.Prepare(session, positions, token, progress)) { }
    internal LootTrackerReplayService(string root, Func<LootTrackerSession, ReplayPositionReadResult> readPositions,
        Func<string, CancellationToken, Task> flush,
        Func<LootTrackerSession, ReplayPositionReadResult, CancellationToken, Action<int>, LootTrackerPreparedReplay> prepare)
    {
        _root = Path.GetFullPath(root); ValidatePath(_root);
        _readPositions = readPositions; _flush = flush; _prepare = prepare;
    }
    public event Action? Changed;
    public ImmutableArray<LootTrackerReplayState> Snapshot
    { get { lock (_gate) return _entries.Values.Select(row => row.State).OrderBy(row => row.Kind).ThenBy(row => row.SessionId, StringComparer.Ordinal).ToImmutableArray(); } }

    public LootTrackerReplayState GetState(LootTrackerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Status == LootTrackerSessionStatus.Live) return State(session, LootTrackerReplayStatus.Recording);
        if (!Valid(session)) return State(session, LootTrackerReplayStatus.Failed, LootTrackerReplayFailure.InvalidSession);
        lock (_gate)
        {
            if (_deleted.Contains(Key(session))) return State(session, LootTrackerReplayStatus.NotPrepared);
            if (!_entries.TryGetValue(Key(session), out var entry)) return State(session, LootTrackerReplayStatus.NotPrepared);
            // Original immutable snapshots are stable in history. A different reference is checked off-thread by Load/Prepare.
            return ReferenceEquals(entry.Source, session) ? entry.State : State(session, LootTrackerReplayStatus.Stale);
        }
    }
    public Task<LootTrackerReplayState> LoadAsync(LootTrackerSession session) => Begin(session, prepare: false, force: false);
    public Task<LootTrackerReplayState> PrepareAsync(LootTrackerSession session, bool force = false) => Begin(session, prepare: true, force);

    private async Task<LootTrackerReplayState> Begin(LootTrackerSession session, bool prepare, bool force)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Status == LootTrackerSessionStatus.Live) return State(session, LootTrackerReplayStatus.Recording);
        if (!Valid(session)) return State(session, LootTrackerReplayStatus.Failed, LootTrackerReplayFailure.InvalidSession);
        while (true)
        {
            Task<LootTrackerReplayState> job; bool waitThenRetry;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var key = Key(session);
                if (_deleted.Contains(key)) return State(session, LootTrackerReplayStatus.Failed, LootTrackerReplayFailure.InvalidSession);
                if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new(State(session, LootTrackerReplayStatus.NotPrepared));
                if (entry.Job is { IsCompleted: false } pending)
                { job = pending; waitThenRetry = entry.Cancellation?.IsCancellationRequested == true
                    || prepare && !entry.IsPreparation || !ReferenceEquals(entry.Source, session); }
                else
                {
                    var fallback = ReferenceEquals(entry.Source, session) ? entry.State.Replay : null;
                    entry.Source = session;
                    entry.State = new(session.Id, session.Kind, prepare ? LootTrackerReplayStatus.Preparing : LootTrackerReplayStatus.Loading,
                        Replay: fallback);
                    entry.Cancellation = new(); var cancellation = entry.Cancellation; var generation = ++entry.Generation;
                    var completion = new TaskCompletionSource<LootTrackerReplayState>(TaskCreationOptions.RunContinuationsAsynchronously);
                    entry.Job = job = completion.Task; entry.IsPreparation = prepare; waitThenRetry = false;
                    _ = Task.Run(() => Run(session, entry, generation, cancellation, prepare, force, fallback, completion));
                }
            }
            RaiseChanged();
            var result = await job.ConfigureAwait(false);
            if (!waitThenRetry) return result;
        }
    }

    private async Task Run(LootTrackerSession session, Entry entry, long generation, CancellationTokenSource cancellation,
        bool prepare, bool force, LootTrackerPreparedReplay? fallback, TaskCompletionSource<LootTrackerReplayState> completion)
    {
        var token = cancellation.Token;
        string? temporary = null;
        var result = State(session, LootTrackerReplayStatus.Failed, LootTrackerReplayFailure.Preparation);
        try
        {
            if (session.UnifiedVersion > 0) await _flush(session.Id, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var positions = session.UnifiedVersion > 0 ? _readPositions(session) : new ReplayPositionReadResult([], [], "", false, false); token.ThrowIfCancellationRequested();
            var fingerprint = LootTrackerPreparedReplay.Fingerprint(session, positions.SourceFingerprint);
            if (fallback is not null && !fallback.Matches(session, fingerprint))
            { fallback = null; SetFallback(session, entry, generation, token, null); }
            var path = CachePath(session.Id, session.Kind, Key(session).Source);
            var cached = ReadCache(path, session, fingerprint, out var invalid);
            fallback ??= cached;
            SetFallback(session, entry, generation, token, fallback);
            if ((!prepare || !force) && cached is not null) result = new(session.Id, session.Kind, LootTrackerReplayStatus.Ready, 100, cached);
            else if (!prepare) result = State(session, invalid ? LootTrackerReplayStatus.Stale : LootTrackerReplayStatus.NotPrepared,
                invalid ? LootTrackerReplayFailure.InvalidCache : LootTrackerReplayFailure.None);
            else
            {
                var prepared = _prepare(session, positions, token, progress => Progress(session, entry, generation, token, progress));
                token.ThrowIfCancellationRequested();
                if (!prepared.Matches(session, fingerprint)) throw new InvalidDataException("Prepared replay contract rejected.");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(prepared, JsonOptions);
                if (bytes.LongLength > MaximumCacheBytes) throw new IOException("Replay cache size limit.");
                var directory = Path.GetDirectoryName(path)!; ValidatePath(directory); Directory.CreateDirectory(directory); ValidatePath(directory);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; ValidatePath(temporary);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                lock (_gate)
                {
                    if (!Current(session, entry, generation, token)) throw new OperationCanceledException(token);
                    ValidatePath(path); File.Move(temporary, path, true); temporary = null;
                }
                result = new(session.Id, session.Kind, LootTrackerReplayStatus.Ready, 100, prepared);
            }
        }
        catch (OperationCanceledException)
        { result = fallback is null ? State(session, LootTrackerReplayStatus.Canceled) : new(session.Id, session.Kind, LootTrackerReplayStatus.Ready, 100, fallback); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            var reason = error is IOException or UnauthorizedAccessException ? LootTrackerReplayFailure.Storage : LootTrackerReplayFailure.Preparation;
            result = fallback is null ? State(session, LootTrackerReplayStatus.Failed, reason)
                : new(session.Id, session.Kind, LootTrackerReplayStatus.Ready, 100, fallback, reason);
        }
        catch (Exception)
        { result = fallback is null ? State(session, LootTrackerReplayStatus.Failed, LootTrackerReplayFailure.Preparation)
            : new(session.Id, session.Kind, LootTrackerReplayStatus.Ready, 100, fallback, LootTrackerReplayFailure.Preparation); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            var changed = false;
            lock (_gate)
            {
                if (Current(session, entry, generation, token)) { entry.State = result; changed = true; }
                else result = entry.State;
                if (ReferenceEquals(entry.Cancellation, cancellation)) entry.Cancellation = null;
            }
            cancellation.Dispose();
            completion.TrySetResult(result);
            if (changed) RaiseChanged();
        }
    }

    private LootTrackerPreparedReplay? ReadCache(string path, LootTrackerSession session, string fingerprint, out bool invalid)
    {
        invalid = false; ValidatePath(path);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumCacheBytes) { invalid = true; return null; }
            var document = JsonSerializer.Deserialize<LootTrackerPreparedReplay>(stream, JsonOptions);
            if (document is not null && document.Matches(session, fingerprint)) return document;
            invalid = true; return null;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        { invalid = true; return null; }
    }
    private void SetFallback(LootTrackerSession session, Entry entry, long generation, CancellationToken token, LootTrackerPreparedReplay? fallback)
    {
        lock (_gate)
            if (Current(session, entry, generation, token)) entry.State = entry.State with { Replay = fallback };
    }
    private void Progress(LootTrackerSession session, Entry entry, long generation, CancellationToken token, int progress)
    {
        var changed = false;
        lock (_gate)
            if (Current(session, entry, generation, token))
            {
                var next = Math.Clamp(progress, 0, 99);
                if (entry.State.Progress != next) { entry.State = entry.State with { Progress = next }; changed = true; }
            }
        if (changed) RaiseChanged();
    }
    private bool Current(LootTrackerSession session, Entry entry, long generation, CancellationToken token) => !_disposed
        && !token.IsCancellationRequested && entry.Generation == generation && !_deleted.Contains(Key(session));

    public void Cancel(string sessionId, LootTrackerSessionKind kind)
    {
        var changed = false;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var entry in _entries.Where(pair => pair.Key.Id == sessionId && pair.Key.Kind == kind).Select(pair => pair.Value))
            {
                if (entry.Job is not { IsCompleted: false }) continue;
                entry.Generation++; entry.Cancellation?.Cancel(); changed = true;
                entry.State = entry.State.Replay is { } saved ? new(sessionId, kind, LootTrackerReplayStatus.Ready, 100, saved)
                    : new(sessionId, kind, LootTrackerReplayStatus.Canceled);
            }
        }
        if (changed) RaiseChanged();
    }
    public async Task RemoveAsync(string sessionId, LootTrackerSessionKind kind)
    {
        if (!ValidKey(sessionId, kind)) return;
        var pending = new List<Task>();
        lock (_gate)
        {
            foreach (var source in Origins) _deleted.Add((source, sessionId, kind));
            foreach (var entry in _entries.Where(pair => pair.Key.Id == sessionId && pair.Key.Kind == kind).Select(pair => pair.Value))
            {
                entry.Generation++; entry.Cancellation?.Cancel();
                if (entry.Job is { } job) pending.Add(job);
                entry.State = new(sessionId, kind, LootTrackerReplayStatus.NotPrepared);
            }
        }
        RaiseChanged();
        await Task.WhenAll(pending).ConfigureAwait(false);
        await Task.Run(() =>
        {
            foreach (var source in Origins)
            {
                var directory = Path.GetDirectoryName(CachePath(sessionId, kind, source))!; ValidatePath(directory);
                if (Directory.Exists(directory)) { ValidateTree(directory); Directory.Delete(directory, true); }
            }
        }).ConfigureAwait(false);
    }
    private static readonly string[] Origins = ["unified", "lifeskill", "farm"];
    private static (string Source, string Id, LootTrackerSessionKind Kind) Key(LootTrackerSession session) =>
        (session.UnifiedVersion > 0 ? "unified" : session.Kind == LootTrackerSessionKind.Lifeskill ? "lifeskill" : "farm", session.Id, session.Kind);
    private string CachePath(string id, LootTrackerSessionKind kind, string source)
    {
        if (!ValidKey(id, kind) || !Origins.Contains(source)) throw new InvalidDataException("Invalid replay identity.");
        var result = Path.Combine(_root, source, kind.ToString().ToLowerInvariant(), id, $"replay-v{LootTrackerPreparedReplay.CurrentVersion}.json"); ValidatePath(result); return result;
    }
    private static bool ValidKey(string id, LootTrackerSessionKind kind) => Guid.TryParseExact(id, "N", out var parsed)
        && parsed != Guid.Empty && Enum.IsDefined(kind);
    private static bool Valid(LootTrackerSession session) => ValidKey(session.Id, session.Kind)
        && session.Status is LootTrackerSessionStatus.Completed or LootTrackerSessionStatus.Interrupted && session.Elapsed >= TimeSpan.Zero;
    private static LootTrackerReplayState State(LootTrackerSession session, LootTrackerReplayStatus status,
        LootTrackerReplayFailure reason = LootTrackerReplayFailure.None) => new(session.Id, session.Kind, status, Reason: reason);
    private static void ValidatePath(string path)
    {
        var root = Path.GetPathRoot(path)!;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path[root.Length..].Contains(':') || new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidDataException("Replay cache requires a local path.");
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); } catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Replay cache rejects reparse paths.");
        }
    }
    private static void ValidateTree(string path)
    { ValidatePath(path); foreach (var child in Directory.EnumerateFileSystemEntries(path)) { ValidatePath(child); if (Directory.Exists(child)) ValidateTree(child); } }
    private void RaiseChanged()
    {
        if (_disposed) return;
        foreach (var listener in Changed?.GetInvocationList().Cast<Action>() ?? [])
            try { listener(); } catch (Exception) { }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            foreach (var entry in _entries.Values)
            {
                entry.Generation++; entry.Cancellation?.Cancel();
                if (entry.State.IsBusy) entry.State = entry.State.Replay is { } saved
                    ? new(entry.State.SessionId, entry.State.Kind, LootTrackerReplayStatus.Ready, 100, saved)
                    : new(entry.State.SessionId, entry.State.Kind, LootTrackerReplayStatus.Canceled);
            }
        }
    }
}
