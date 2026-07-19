namespace Cluckwork.Application.Features.Audit;

using Cluckwork.Domain.Auditing;

// Read side of the audit trail (#93). Append happens through IAuditWriter;
// there is intentionally no update/remove surface anywhere.
public interface IAuditEventRepository
{
    Task<IReadOnlyList<AuditEvent>> ListAsync(
        string? action, Guid? entityId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default);
}
