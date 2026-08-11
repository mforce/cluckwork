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
public sealed record EntityProvenance(
    string CreatedByEmail, DateTimeOffset CreatedAtUtc,
    string? LastChangedByEmail, DateTimeOffset? LastChangedAtUtc);
