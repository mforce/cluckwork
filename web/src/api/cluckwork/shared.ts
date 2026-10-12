/**
 * #494 — who created a record and who last changed it, derived server-side
 * from the audit trail rather than stored on the record.
 *
 * `createdBy*` are null for a record created before #494 shipped: there is no
 * backfill, so its trail has no creation event to report.
 *
 * `lastChanged*` are null when nothing has happened since creation. That is the
 * SERVER's judgement, made from the audit event ids — do NOT re-derive it by
 * comparing the two timestamps, because two distinct events can share an
 * instant and that comparison would hide a real edit.
 */
export interface RecordHistory {
  createdByEmail: string | null;
  createdAtUtc: string | null;
  lastChangedByEmail: string | null;
  lastChangedAtUtc: string | null;
  // #494 — when the record became official. Only resources with a promotion
  // step send it (daily entries submit, sales orders confirm), hence optional:
  // flocks, egg grades and expenses have no such moment.
  madeOfficialAtUtc?: string | null;
}

export interface Created {
  id: string;
}

// Display formatting lives in lib/format.ts (#650): formatMoney there takes
// the farm locale, which this module has no business knowing.

// Parses a user-entered decimal amount into integer minor
// units at the currency's scale. The `Math.round` absorbs binary-float artifacts
// (e.g. 0.29 * 100 = 28.999… which would truncate to 28 without it). Returns NaN
// for non-numeric input and may return a negative — callers apply their own
// finite/non-negative guard and context-specific error message.
export function parseMoneyToMinorUnits(text: string, minorUnit: number): number {
  return Math.round(parseFloat(text) * 10 ** minorUnit);
}
