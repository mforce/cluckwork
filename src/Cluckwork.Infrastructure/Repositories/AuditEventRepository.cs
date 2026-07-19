namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Audit;
using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class AuditEventRepository(AppDbContext db) : IAuditEventRepository
{
    public async Task<IReadOnlyList<AuditEvent>> ListAsync(
        string? action, Guid? entityId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default)
    {
        // Date filters are inclusive calendar days over the UTC timestamp.
        var fromUtc = from is { } f
            ? new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : (DateTimeOffset?)null;
        // MaxValue guard: AddDays(1) on 9999-12-31 throws (codex review of #94).
        var toUtc = to is { } t
            ? t == DateOnly.MaxValue
                ? DateTimeOffset.MaxValue
                : new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        return await db.AuditEvents
            .AsNoTracking()
            .Where(e => (action == null || e.Action == action)
                     && (entityId == null || e.EntityId == entityId)
                     && (fromUtc == null || e.OccurredAtUtc >= fromUtc)
                     && (toUtc == null || e.OccurredAtUtc < toUtc))
            // Id tiebreaker: same-instant events must page stably.
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
    }
}
