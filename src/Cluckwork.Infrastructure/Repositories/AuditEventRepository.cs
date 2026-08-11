namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Common;
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
    //
    // There are THREE such predicates below, not one: the `created` query, the
    // `creator` CTE, and the outer last-change query. Each is held by a named
    // test, and it took an adversarial pass to notice that two of them were not:
    // Provenance_IsScopedToTheTenant covers only `created`, because it gives each
    // account a DISTINCT entityId, so the other two predicates never ran in it
    // and survived deletion against the whole suite. Provenance_LastChange_
    // IsScopedToTheTenant shares ONE id across accounts and is what actually
    // holds them. Do not trust this comment over a mutation run.
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
        // PROMOTION by the creator. That second exclusion is the whole of #494's
        // "drafting a record and making it official is one act, not a change"
        // rule: writing the day's numbers down and submitting them, with nothing
        // altered in between, should not read as if somebody corrected them.
        //
        // A promotion is the action that turns a draft into the official record:
        // DailyEntry.Submit and SalesOrder.Confirm are the two, and both mint
        // stock at that moment. Deliberately NOT every draft-to-terminal move —
        // SalesOrder.Cancel kills a draft rather than promoting it, so
        // cancelling your own order stays a reportable change.
        //
        // Stated as an EXCLUSION from the candidate set, deliberately — not as
        // "if the last event is a self-promotion, report nothing". The latter
        // also discards any earlier change the promotion happens to sit on top
        // of, which would hide a real edit by a different person.
        //
        // Keyed on the action, never on the actor alone: correcting a locked
        // entry is a stock-altering change and must show even when the person
        // correcting it is the one who created it.
        //
        // Identity is ActorUserId, not the email — the email is a snapshot label.
        //
        // IS NOT DISTINCT FROM rather than "=" keeps the predicate total when the
        // LEFT JOIN finds no creator. Be honest about its status: it is NOT
        // currently load-bearing. A record with no creation row is dropped from
        // the result by `created` keying below, before the difference could be
        // observed, so mutating this to a plain "=" changes no outcome and no
        // test can catch it (adversarial review of PR #503). It stays because a
        // NULL-propagating predicate is a trap for whoever next changes that
        // keying — not because it prevents something reachable today.
        //
        // KNOWN RESIDUAL, accepted rather than overlooked (#508). Which candidate
        // wins is still decided by ORDER BY, and the "Id" tiebreak carries no
        // chronology — AuditWriter mints a random v4 Guid. So two reportable
        // changes sharing an OccurredAtUtc resolve arbitrarily, and the wrong
        // actor can be named as the last changer. The timestamp shown is right
        // either way; only the name is at risk.
        //
        // It is narrow but genuinely reachable, so do not dismiss it: most
        // concurrent changes to one record cannot collide, because the aggregate
        // Version token serialises them and the loser 409s having written
        // nothing. RecordBirdMovement escapes that — it writes an audit event
        // against the FLOCK's id while only inserting a movement row, never
        // touching Flock.Version, so it does not serialise against Flock.Update.
        //
        // Fixing it properly needs a durable monotonic column on AuditEvents,
        // i.e. a migration — the one thing #494 was specified not to do. Tracked
        // separately in #508 rather than smuggled in here. There is deliberately
        // NO test pinning the current arbitrary outcome: that would promote a
        // known loss into a specification.
        // EXACT actions, not a "%.Submit"/"%.Confirm" pattern. Creation rightly
        // uses a wildcard — any ".Create" IS a creation — but promotion is a
        // closed two-member set, and a pattern would silently swallow a future
        // action that merely ends the same way (a Payment.Confirm, say), dropping
        // its actor from history with no error (pi review of PR #503).
        var promotionActions = new[]
        {
            AuditActions.DailyEntrySubmit,
            AuditActions.SalesOrderConfirm,
        };
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
              AND NOT (e."Action" = ANY({promotionActions})
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
