namespace Cluckwork.Application.Features.Audit;

using Cluckwork.Domain.Auditing;

// Read side of the audit trail (#93). Append happens through IAuditWriter;
// there is intentionally no update/remove surface anywhere.
public interface IAuditEventRepository
{
    // Ids per round trip. NOT a caller contract — the egg-grade list is
    // unpaginated and grades are never deleted, so that catalog outgrows any
    // fixed cap eventually; a larger input is chunked, never refused.
    public const int MaxBatchIds = 500;

    Task<IReadOnlyList<AuditEvent>> ListAsync(
        string? action, Guid? entityId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default);

    // #494 — created/last-changed per entity id, for one entity type. An id
    // with no events is ABSENT from the result rather than present-and-blank:
    // records that predate #494 have no creation event and get no backfill.
    Task<IReadOnlyDictionary<Guid, EntityProvenance>> GetProvenanceAsync(
        string entityType, IReadOnlyCollection<Guid> entityIds, CancellationToken ct = default);
}
