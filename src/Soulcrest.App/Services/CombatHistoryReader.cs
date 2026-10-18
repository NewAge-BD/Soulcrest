using Soulcrest.Core.Combat;

namespace Soulcrest.App.Services;

public sealed record CombatHistoryRunView(CombatRunDocument Run, IReadOnlyList<CombatAttemptSummary> Attempts);
public enum CombatHistoryReadFailure { InvalidData, AccessDenied, Io, Unexpected }

public sealed class CombatHistoryReadException(CombatHistoryReadFailure failure, Exception cause)
    : IOException("The local combat history could not be read.", cause)
{
    public CombatHistoryReadFailure Failure { get; } = failure;
}

public interface ICombatHistoryReader
{
    Task<CombatHistoryReadOnlyOverview> LoadOverviewAsync(CancellationToken cancellationToken = default);
    Task<CombatHistoryRunView> LoadRunAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>Read-only local disk and projection work always runs outside the renderer and capture threads.</summary>
public sealed class CombatHistoryReader(CombatHistoryStore store) : ICombatHistoryReader
{
    public Task<CombatHistoryReadOnlyOverview> LoadOverviewAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(store.LoadOverviewReadOnly, cancellationToken);

    public Task<CombatHistoryRunView> LoadRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        ReadAsync(() =>
        {
            var loaded = store.LoadRunReadOnly(runId);
            var attempts = loaded.Run.Attempts.Select(attempt =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CombatProjection.Project(attempt);
            }).ToArray();
            return new CombatHistoryRunView(loaded.Run, attempts);
        }, cancellationToken);

    private static Task<T> ReadAsync<T>(Func<T> read, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var value = read();
            cancellationToken.ThrowIfCancellationRequested();
            return value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (CombatHistoryFormatException error) { throw new CombatHistoryReadException(CombatHistoryReadFailure.InvalidData, error); }
        catch (InvalidDataException error) { throw new CombatHistoryReadException(CombatHistoryReadFailure.InvalidData, error); }
        catch (UnauthorizedAccessException error) { throw new CombatHistoryReadException(CombatHistoryReadFailure.AccessDenied, error); }
        catch (IOException error) { throw new CombatHistoryReadException(CombatHistoryReadFailure.Io, error); }
        catch (Exception error) { throw new CombatHistoryReadException(CombatHistoryReadFailure.Unexpected, error); }
    }, cancellationToken);
}
