namespace Cluckwork.Application.Modules.Access.Contracts;

// #857 — the refresh-token purge's DELETE (#270). The sweep owns the cutoff,
// the grace and the batch bounds; this removes at most batchSize rows whose
// ExpiresAt is before the cutoff, oldest first, and returns how many it removed.
[ModuleContract("Access")]
public interface IRefreshTokenPurge
{
    Task<int> DeleteExpiredBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct);
}
