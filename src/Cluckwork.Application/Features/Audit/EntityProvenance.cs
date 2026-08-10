namespace Cluckwork.Application.Features.Audit;

// #494 — derived from the audit trail, never stored: the earliest AuditEvent
// for an entity id is its creation, the latest is its most recent change.
//
// LastChanged* is null when nothing has happened since creation — i.e. when
// the trail's two ends are the SAME event. That judgement belongs here and not
// to a caller, because only this layer holds the event ids: two DISTINCT events
// can share an instant, so a caller comparing timestamps would call a genuinely
// edited record untouched (codex review of #494).
public sealed record EntityProvenance(
    string CreatedByEmail, DateTimeOffset CreatedAtUtc,
    string? LastChangedByEmail, DateTimeOffset? LastChangedAtUtc);
