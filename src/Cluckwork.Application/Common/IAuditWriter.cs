namespace Cluckwork.Application.Common;

// Appends an audit event to the CURRENT unit of work (#93): the row commits
// or rolls back with the change it records — callers never save through this
// port, they call it just before their own SaveChanges/transaction commit.
public interface IAuditWriter
{
    Task WriteAsync(
        string action, string entityType, Guid entityId,
        string? reason = null, object? details = null,
        CancellationToken ct = default);
}
