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
        // Every capture point sits behind an authenticated, tenant-resolved
        // endpoint today; this guard keeps a future non-HTTP caller (job,
        // seeder) from stamping AccountId = Guid.Empty rows that no tenant
        // filter would ever surface (background security review of #94).
        if (!tenant.IsResolved)
            throw new InvalidOperationException(
                "Audit events require a resolved tenant — do not call IAuditWriter outside a tenant-scoped request.");

        // #500 — symmetric with the tenant guard above, and for the same
        // reason. The old fallback stamped "(unresolved)" whenever nobody had
        // resolved an actor: silent, reachable only from non-HTTP callers, and
        // it shipped ~256 such rows into every demo farm — visible on five
        // screens once #494 rendered provenance. A caller with no human actor
        // must now say WHICH non-person it is (CurrentUserContext
        // .ResolveSystemActor) instead of defaulting into a placeholder.
        //
        // This throw fires before the AddAsync below, so a violation leaves
        // nothing behind: every audit-writing path runs inside a transaction
        // that commits only on success.
        if (!user.IsResolved)
            throw new InvalidOperationException(
                "Audit events require a resolved actor — the current user must be resolved before " +
                "calling IAuditWriter. A non-HTTP caller (CLI verb, seeder) must declare one: a real " +
                "user, or a system actor via the concrete CurrentUserContext.");

        var actorId = user.UserId;
        var actorEmail = user.Email;

        await db.AuditEvents.AddAsync(AuditEvent.Create(
            Guid.NewGuid(), tenant.AccountId, clock.UtcNow,
            actorId, actorEmail, action, entityType, entityId,
            reason,
            details is null ? null : JsonSerializer.Serialize(details, JsonOptions)), ct);
    }
}
