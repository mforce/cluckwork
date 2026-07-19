namespace Cluckwork.Infrastructure.Repositories;

using System.Text.Json;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Persistence;

// Appends to the handler's OWN unit of work — no SaveChanges here, so the
// event commits or rolls back with the change it records (tech spec: audit is
// domain data, same transaction).
public sealed class AuditWriter(
    AppDbContext db,
    TenantContext tenant,
    ICurrentUser user,
    IClock clock) : IAuditWriter
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(
        string action, string entityType, Guid entityId,
        string? reason = null, object? details = null,
        CancellationToken ct = default)
    {
        var actorId = user.IsResolved ? user.UserId : Guid.Empty;
        var actorEmail = user.IsResolved ? user.Email : "(unresolved)";

        await db.AuditEvents.AddAsync(AuditEvent.Create(
            Guid.NewGuid(), tenant.AccountId, clock.UtcNow,
            actorId, actorEmail, action, entityType, entityId,
            reason,
            details is null ? null : JsonSerializer.Serialize(details, JsonOptions)), ct);
    }
}
