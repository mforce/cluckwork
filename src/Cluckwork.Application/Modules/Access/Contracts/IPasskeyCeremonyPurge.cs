namespace Cluckwork.Application.Modules.Access.Contracts;

// #1282 decision 3: the ceremony sweep's DELETE. Removes at most batchSize
// passkey_ceremonies rows whose ExpiresAt is before the cutoff and returns how
// many it removed.
public interface IPasskeyCeremonyPurge
{
    Task<int> DeleteExpiredBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct);
}
