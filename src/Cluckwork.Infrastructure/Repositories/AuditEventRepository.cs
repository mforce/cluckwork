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

    // One chunk, three round trips. Raw SQL because DISTINCT ON is Postgres-only
    // and has no LINQ translation. EF would otherwise compose the tenant query
    // filter over this — IgnoreQueryFilters opts out of that, so the hand-written
    // AccountId predicate is then the ONLY thing scoping the read to the tenant.
    //
    // There are THREE such predicates below, one per round trip, and the same is
    // true of the EntityType and EntityId predicates. Keeping it to one per query
    // is deliberate rather than tidy: this scoping triple was twice shipped
    // mostly unguarded, both times for the identical reason — the obvious test
    // gives each account (or type) a DISTINCT entityId, so a duplicate predicate
    // never runs in it and survives deletion against the whole suite. Copies of a
    // predicate are what let that happen, so the last-change query derives its
    // creator and its "was this draft shared" set from ONE `scoped` CTE rather
    // than re-stating the triple per CTE.
    //
    // What actually holds the three: Provenance_LastChange_IsScopedToTheTenant
    // and Provenance_EveryQueryIsScopedToTheEntityType share ONE id across
    // accounts and types respectively; Provenance_MadeOfficial_IsScopedToThe-
    // Tenant holds `promoted`. Do not trust this comment over a mutation run.
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

        // The last change is the latest event that is neither the creation NOR
        // the creator's OWN DRAFTING of it. That second exclusion is the whole of
        // #494's "writing a record and making it official is one act, not a
        // change" rule: filling in the day's numbers and submitting them should
        // not read as if somebody corrected your work.
        //
        // Drafting = editing a draft, plus the promotion that ends it
        // (DailyEntry.Submit, SalesOrder.Confirm — the moments stock is minted or
        // allocated). Excluded ONLY when the actor is the creator, which is what
        // makes a shared draft attributable: rewrite a colleague's numbers before
        // they submit and your edit is the last change, even though they are
        // still the creator and their own submit stays hidden.
        //
        // ...and only while the creator is the ONLY person who drafted it, which
        // is what the `shared` CTE decides. Once somebody else has edited the
        // draft, the creator's own later edits stop being quiet housekeeping and
        // become the answer to "whose numbers are these" — hiding them named the
        // colleague whose work was overwritten, and dated the record to an edit
        // that no longer exists in it. Their PROMOTION stays hidden either way:
        // it has its own line, and repeating it as the last change would say that
        // making a record official changed it. Held by Provenance_WhenTheCreator-
        // RevertsSomeoneElsesEdit_NamesTheCreatorsOwnEdit.
        //
        // "Somebody else" is IS DISTINCT FROM, so a creatorless legacy record
        // counts as shared. Harmless rather than overlooked: the exclusion this
        // gates on cannot fire there anyway — nothing IS NOT DISTINCT FROM a
        // creator that does not exist.
        //
        // `shared` counts DRAFTING by somebody else, not any event by somebody
        // else, and that narrowing has NO test — deliberately, because no fixture
        // can currently tell the two apart without being fiction. Every foreign
        // non-drafting action on these two aggregates (Adjust, Void, Cancel)
        // only becomes possible once drafting has ended, so the creator has no
        // later draft edit left for the wider rule to unhide. The line stays
        // because the narrow rule is the one intended, and the day something
        // audits an entry it did not draft — a Lock event from the sweep, a
        // payment against an order — the wider version would silently start
        // reporting the creator's own edits as changes. Reported as a surviving
        // mutant rather than papered over with a contrived test.
        //
        // Deliberately NOT every draft-to-terminal move — SalesOrder.Cancel kills
        // a draft rather than making it official, so cancelling your own order
        // stays a reportable change.
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
        // LEFT JOIN finds no creator, and it IS load-bearing. It was not while
        // the result was keyed off the creation event — such records never
        // reached the output — but that keying is gone (see below), so a legacy
        // record whose only event is a drafting action now does reach it. Under a
        // plain "=", Guid = NULL yields SQL NULL, NOT (drafting AND NULL) is NULL,
        // and WHERE drops the row outright, losing a real attributable change
        // rather than merely failing to exclude it. Held by
        // Provenance_ForALegacyRecordWhoseOnlyEventIsDrafting_StillReportsIt.
        //
        // This comment previously asserted the opposite and was left behind when
        // the keying changed in the same branch — the sentence that would have
        // licensed the next person to simplify the line.
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
        // uses a wildcard — any ".Create" IS a creation — but these are closed
        // sets, and a pattern would silently swallow a future action that merely
        // ends the same way (a Payment.Confirm, say), dropping its actor from
        // history with no error (pi review of PR #503).
        //
        // TWO sets, deliberately not one. Promotion alone answers "when did this
        // become official"; drafting is what the creator may do to their own
        // record without it counting as a change.
        var promotionActions = new[]
        {
            AuditActions.DailyEntrySubmit,
            AuditActions.SalesOrderConfirm,
        };
        var draftingActions = promotionActions.Concat(new[]
        {
            AuditActions.DailyEntryUpdate,
            AuditActions.SalesOrderAddItem,
            AuditActions.SalesOrderUpdateItem,
            AuditActions.SalesOrderRemoveItem,
        }).ToArray();
        var latest = await db.AuditEvents.FromSqlInterpolated($"""
            WITH scoped AS (
                SELECT *
                FROM "AuditEvents"
                WHERE "AccountId" = {accountId}
                  AND "EntityType" = {entityType}
                  AND "EntityId" = ANY({ids})
            ),
            creator AS (
                SELECT DISTINCT ON ("EntityId") "EntityId", "ActorUserId"
                FROM scoped
                WHERE "Action" LIKE {createAction}
                ORDER BY "EntityId", "OccurredAtUtc" ASC, "Id" ASC
            ),
            shared AS (
                SELECT DISTINCT s."EntityId"
                FROM scoped s
                LEFT JOIN creator c ON c."EntityId" = s."EntityId"
                WHERE s."Action" = ANY({draftingActions})
                  AND s."ActorUserId" IS DISTINCT FROM c."ActorUserId"
            )
            SELECT DISTINCT ON (e."EntityId") e.*
            FROM scoped e
            LEFT JOIN creator c ON c."EntityId" = e."EntityId"
            LEFT JOIN shared sh ON sh."EntityId" = e."EntityId"
            WHERE e."Action" NOT LIKE {createAction}
              AND NOT (e."Action" = ANY({draftingActions})
                       AND e."ActorUserId" IS NOT DISTINCT FROM c."ActorUserId"
                       AND (e."Action" = ANY({promotionActions})
                            OR sh."EntityId" IS NULL))
            ORDER BY e."EntityId", e."OccurredAtUtc" DESC, e."Id" DESC
            """)
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToListAsync(ct);

        // WHEN the record became official, reported whether or not the promoter
        // was named as the last changer. Excluding a self-promotion from "last
        // changed by" must not lose the instant stock was minted — it appears
        // nowhere else on the record's own page, since DailyEntry carries no
        // SubmittedAt (only LockedAtUtc).
        //
        // A separate round trip rather than something clever folded into the
        // queries above: those two are the ones under mutation guard, and this
        // is a plain indexed lookup on the same (AccountId, EntityId) index.
        // At most one promotion exists per entity — neither DailyEntry nor
        // SalesOrder has a path back to Draft — so ordering here is a formality.
        var promoted = await db.AuditEvents.FromSqlInterpolated($"""
            SELECT DISTINCT ON ("EntityId") *
            FROM "AuditEvents"
            WHERE "AccountId" = {accountId}
              AND "EntityType" = {entityType}
              AND "EntityId" = ANY({ids})
              AND "Action" = ANY({promotionActions})
            ORDER BY "EntityId", "OccurredAtUtc" ASC, "Id" ASC
            """)
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToListAsync(ct);

        // Keyed off EVERY id with any event, not off `created` alone. A record
        // predating #494 has no creation event and never gets one — but it can
        // still have changes with real attribution, and keying off `created`
        // discarded those too, rendering the column completely blank for a flock
        // somebody archived yesterday (adversarial review of PR #503).
        //
        // Refusing to invent a creator and throwing away a provable change are
        // separate decisions. Only the first is intended: absent creation stays
        // null rather than being filled in from the first correction.
        var createdById = created.ToDictionary(e => e.EntityId);
        var latestById = latest.ToDictionary(e => e.EntityId);
        var promotedById = promoted.ToDictionary(e => e.EntityId);

        return createdById.Keys
            .Concat(latestById.Keys)
            .Concat(promotedById.Keys)
            .Distinct()
            .ToDictionary(
                id => id,
                id =>
                {
                    var create = createdById.GetValueOrDefault(id);
                    var last = latestById.GetValueOrDefault(id);
                    return new EntityProvenance(
                        create?.ActorEmail, create?.OccurredAtUtc,
                        last?.ActorEmail, last?.OccurredAtUtc,
                        promotedById.GetValueOrDefault(id)?.OccurredAtUtc);
                });
    }
}
