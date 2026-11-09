namespace Soulcrest.App.Services;

public sealed partial class UnifiedLootTrackerService
{
    private readonly HashSet<string> _retentionProtectedHistory = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retentionFailedIds = new(StringComparer.Ordinal);
    private bool _retentionInitialized;
    private long? _retentionThreshold;
    private long _automaticallyDeletedSessionCount;
    private int _retentionIncomplete;

    /// <summary>Anonymous lifetime counter; no session identities or item details are exported.</summary>
    public long AutomaticallyDeletedSmallSessionCount => Interlocked.Read(ref _automaticallyDeletedSessionCount);
    public bool SmallSessionCleanupIncomplete => Volatile.Read(ref _retentionIncomplete) != 0;

    /// <summary>
    /// User request 2026-11-09: automatically remove all known-small, ended non-Abyss sessions,
    /// including existing history. The single session worker owns this method and all deletion.
    /// </summary>
    private async Task SweepSmallSessionsAsync()
    {
        if (!_retentionInitialized)
        {
            _retentionInitialized = true;
            // Store issues do not identify the damaged row. Never use a recovered prefix's
            // possibly incomplete item total as authorization to delete existing history.
            if (_storageIssue)
                foreach (var session in _history) _retentionProtectedHistory.Add(session.Id);
        }

        // Existing observations/receipts take precedence over background history cleanup.
        // Otherwise a large old history could fill the recording queue while deleting files.
        if (_commands.Reader.TryPeek(out _)) return;

        var threshold = _settings.GetLootTrackerSmallSessionItemThreshold();
        if (_retentionThreshold != threshold)
        {
            _retentionThreshold = threshold;
            // Retry a previously failed deletion only on an explicit policy change or restart,
            // rather than repeating filesystem failures every one-second session tick.
            _retentionFailedIds.Clear();
        }
        // Waiting for a settings transaction may itself have queued new recording work.
        if (threshold == 0 || Volatile.Read(ref _fatal) != 0 || _commands.Reader.TryPeek(out _)) return;

        var ambiguous = _history.GroupBy(row => row.Id, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var session in _history.ToArray())
        {
            if (_commands.Reader.TryPeek(out _)) break;
            // A settings change may arrive while a replay writer is being stopped. Finish the
            // already committed delete, then let the next command apply the new policy.
            if (_settings.GetLootTrackerSmallSessionItemThreshold() != threshold) break;
            if (!LootTrackerSessionSize.IsBelowItemThreshold(session, threshold)) continue;
            if (_retentionProtectedHistory.Contains(session.Id) || ambiguous.Contains(session.Id)
                || !Guid.TryParseExact(session.Id, "N", out var id) || id == Guid.Empty)
            {
                Interlocked.Exchange(ref _retentionIncomplete, 1);
                _storageIssue = true;
                continue;
            }
            if (_retentionFailedIds.Contains(session.Id)) continue;
            if (session.UnifiedVersion == 0 && _deleteLegacy is null)
            {
                _retentionFailedIds.Add(session.Id);
                Interlocked.Exchange(ref _retentionIncomplete, 1);
                _storageIssue = true;
                continue;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                // Keep the normal replay shutdown, original-store tombstone, dependent-price
                // cancellation and SessionDeleted publication order. Never delete directories here.
                await DeleteSession(new(session.Id, completion)).ConfigureAwait(false);
                await completion.Task.ConfigureAwait(false);
                Interlocked.Increment(ref _automaticallyDeletedSessionCount);
            }
            catch (Exception error) when (StorageFailure(error) || error is InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                _retentionFailedIds.Add(session.Id);
                Interlocked.Exchange(ref _retentionIncomplete, 1);
                _storageIssue = true;
            }
        }
    }
}
