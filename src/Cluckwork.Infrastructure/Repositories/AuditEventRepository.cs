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

    // #494 — who created a record and who last changed it, read out of the
    // append-only trail rather than from new columns on every aggregate.
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
        // Creation is identified by its ACTION, never by its position in the
        // trail (codex review of PR #503). Position cannot answer it: events
        // sharing an OccurredAtUtc have no knowable order, because AuditWriter
        // mints a random v4 Guid — so an Id tiebreaker would name whichever
        // event happens to sort first, reversing creator and changer at random.
        // The action is unambiguous, and there is at most one per entity.
        var createAction = $"%.Create";

        var created = await db.AuditEvents.FromSqlInterpolated($"""
            SELECT DISTINCT ON ("EntityId") *
            FROM "AuditEvents"
            WHERE "AccountId" = {accountId}
              AND "EntityType" = {entityType}
              AND "EntityId" = ANY({ids})
              AND "Action" LIKE {createAction}
            ORDER BY "EntityId", "OccurredAtUtc" ASC, "Id" ASC
            """)
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToListAsync(ct);

        // The last change is the latest event that is neither the creation NOR a
        // Submit by the creator. That second exclusion is the whole of #494's
        // "saving a draft and submitting it is one act, not a change" rule:
        // writing the day's numbers down and making them official, with nothing
        // altered in between, should not read as if somebody corrected them.
        //
        // Stated as an EXCLUSION from the candidate set, deliberately — not as
        // "if the last event is a self-submit, report nothing". The latter also
        // discards any earlier change the submit happens to sit on top of, which
        // would hide a real edit by a different person.
        //
        // Keyed on the action, never on the actor alone: correcting a locked
        // entry is a stock-altering change and must show even when the person
        // correcting it is the one who created it.
        //
        // Identity is ActorUserId, not the email — the email is a snapshot label.
        // IS NOT DISTINCT FROM rather than "=", so an entity with no creation row
        // (a record predating #494) keeps its events instead of having them all
        // NULL-propagate out of the result.
        var submitAction = $"%.Submit";
        var latest = await db.AuditEvents.FromSqlInterpolated($"""
            WITH creator AS (
                SELECT DISTINCT ON ("EntityId") "EntityId", "ActorUserId"
                FROM "AuditEvents"
                WHERE "AccountId" = {accountId}
                  AND "EntityType" = {entityType}
                  AND "EntityId" = ANY({ids})
                  AND "Action" LIKE {createAction}
                ORDER BY "EntityId", "OccurredAtUtc" ASC, "Id" ASC
            )
            SELECT DISTINCT ON (e."EntityId") e.*
            FROM "AuditEvents" e
            LEFT JOIN creator c ON c."EntityId" = e."EntityId"
            WHERE e."AccountId" = {accountId}
              AND e."EntityType" = {entityType}
              AND e."EntityId" = ANY({ids})
              AND e."Action" NOT LIKE {createAction}
              AND NOT (e."Action" LIKE {submitAction}
                       AND e."ActorUserId" IS NOT DISTINCT FROM c."ActorUserId")
            ORDER BY e."EntityId", e."OccurredAtUtc" DESC, e."Id" DESC
            """)
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToListAsync(ct);

        // Keyed off `created`: a record whose trail holds no creation event —
        // anything predating #494 — reports nothing at all rather than
        // attributing it to whoever happened to correct it first.
        var latestById = latest.ToDictionary(e => e.EntityId);
        return created.ToDictionary(
            e => e.EntityId,
            e =>
            {
                var last = latestById.GetValueOrDefault(e.EntityId);
                return new EntityProvenance(
                    e.ActorEmail, e.OccurredAtUtc,
                    last?.ActorEmail, last?.OccurredAtUtc);
            });
    }
}
