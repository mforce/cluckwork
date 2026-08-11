namespace Cluckwork.Application.Features.Audit;

// #494 — derived from the audit trail, never stored. Creation is the entity's
// "*.Create" event, identified by its ACTION and NOT by being earliest on the
// trail: events sharing an instant have no knowable order, so position would
// name creator and changer at random (codex review of PR #503).
//
// LastChanged* is null when nothing REPORTABLE has happened since creation,
// which is not the same as nothing having happened. Two cases produce it:
// there is genuinely no later event, or the only later event is a PROMOTION by
// the person who created the record — submitting your own daily entry, or
// confirming your own sales order. Those are two distinct events but one act,
// and reporting them as a change reads as if somebody corrected your work.
//
// So this is deliberately NOT a "same event at both ends" test, and a caller
// must not re-derive it by comparing timestamps or ids — it needs the actions
// and the actor, which only the repository query has. See
// AuditEventRepository.GetProvenanceChunkAsync for the exclusion, including the
// known same-instant residual it does not solve (#508).
//
// MadeOfficialAtUtc is WHEN the promotion happened, reported whether or not the
// promoter was named above. Suppressing a self-promotion from "last changed by"
// must not also lose the instant stock was minted, which is recorded nowhere
// else on the record's own page — DailyEntry has no SubmittedAt, only
// LockedAtUtc. Null for anything with no promotion step (flocks, egg grades,
// expenses) and for a draft still awaiting one.
// Created* is nullable because the two halves are INDEPENDENT. A record from
// before #494 has no creation event and never gets one — but it may well have
// changes with real attribution, and an earlier revision discarded those too by
// keying the whole result off the creation. Refusing to invent a creator and
// throwing away a change we can prove are separate decisions; only the first
// was intended (adversarial review of PR #503).
public sealed record EntityProvenance(
    string? CreatedByEmail, DateTimeOffset? CreatedAtUtc,
    string? LastChangedByEmail, DateTimeOffset? LastChangedAtUtc,
    DateTimeOffset? MadeOfficialAtUtc);
