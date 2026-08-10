namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Audit;
using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class AuditEventRepository(AppDbContext db, TenantContext tenant) : IAuditEventRepository
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

    // #494 — earliest event per id is the creation, latest is the last change.
    //
    // MaxBatchIds is how many ids go into ONE round trip, not a limit on the
    // caller: the egg-grade list has no pagination and grades are never
    // deleted, so that catalog outgrows any fixed cap eventually and must not
    // fail the whole endpoint when it does. Oversized inputs are chunked.
    public async Task<IReadOnlyDictionary<Guid, EntityProvenance>> GetProvenanceAsync(
        string entityType, IReadOnlyCollection<Guid> entityIds, CancellationToken ct = default)
    {
        var all = new Dictionary<Guid, EntityProvenance>();
        foreach (var chunk in entityIds.Distinct().Chunk(IAuditEventRepository.MaxBatchIds))
        {
            foreach (var (id, provenance) in await GetProvenanceChunkAsync(entityType, chunk, ct))
                all[id] = provenance;
        }
        return all;
    }

    // One chunk, two round trips. Raw SQL because DISTINCT ON is Postgres-only
    // and has no LINQ translation. EF would otherwise compose the tenant query
    // filter over this — IgnoreQueryFilters opts out of that, so the hand-written
    // AccountId predicate is then the ONLY thing scoping the read to the tenant.
    // Both must stay: drop the predicate and every account's trail is visible.
    private async Task<Dictionary<Guid, EntityProvenance>> GetProvenanceChunkAsync(
        string entityType, Guid[] ids, CancellationToken ct)
    {
        var accountId = tenant.AccountId;

        // Id tiebreaker matches ListAsync: same-instant events resolve stably.
        var created = await db.AuditEvents.FromSqlInterpolated($"""
            SELECT DISTINCT ON ("EntityId") *
            FROM "AuditEvents"
            WHERE "AccountId" = {accountId}
              AND "EntityType" = {entityType}
              AND "EntityId" = ANY({ids})
            ORDER BY "EntityId", "OccurredAtUtc" ASC, "Id" ASC
            """)
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToListAsync(ct);

        var latest = await db.AuditEvents.FromSqlInterpolated($"""
            SELECT DISTINCT ON ("EntityId") *
            FROM "AuditEvents"
            WHERE "AccountId" = {accountId}
              AND "EntityType" = {entityType}
              AND "EntityId" = ANY({ids})
            ORDER BY "EntityId", "OccurredAtUtc" DESC, "Id" DESC
            """)
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToListAsync(ct);

        // Both queries share a WHERE clause and the log is append-only, so every
        // id in `created` is in `latest` too — they can only differ in WHICH row
        // won per id.
        var latestById = latest.ToDictionary(e => e.EntityId);
        return created.ToDictionary(
            e => e.EntityId,
            e =>
            {
                var last = latestById[e.EntityId];
                // Compare EVENT IDENTITY, not timestamps: two distinct events
                // can share an instant (hence the Id tiebreaker above), and
                // treating those as one would hide a real change.
                var unchanged = last.Id == e.Id;
                return new EntityProvenance(
                    e.ActorEmail, e.OccurredAtUtc,
                    unchanged ? null : last.ActorEmail,
                    unchanged ? null : last.OccurredAtUtc);
            });
    }
}
