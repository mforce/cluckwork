import { useTranslation } from "react-i18next";
import type { RecordHistory } from "../api/cluckwork";

// #494 — one table cell reporting who created a record and who last changed it.
//
// Whether a change happened at all is the SERVER's call: it sends
// lastChanged* only when the trail's latest event is a different event from the
// creation, and null otherwise. This component must not re-derive that by
// comparing timestamps — two distinct events can share an instant, and that
// comparison would hide a real edit (codex review of #494).
//
// Timestamp rendering follows AuditPage's existing convention (UTC, trimmed to
// seconds) rather than the farm clock: these are audit instants, and the trail
// is displayed in UTC everywhere else it appears.
function formatInstant(iso: string): string {
  return iso.replace("T", " ").slice(0, 19);
}

// `official` names the promotion step this record HAS — submitting a daily
// entry, confirming a sales order. Records with no such step (flocks, egg
// grades, expenses) pass nothing and can never render the line, even if a
// madeOfficialAtUtc somehow arrived.
//
// A full phrase per step rather than a shared "{{label}} {{at}}" template:
// composing a verb with a date puts word order in the caller's hands, which is
// exactly what a translator needs to control.
export function ProvenanceCell({
  history,
  official,
}: {
  history: RecordHistory;
  official?: "submitted" | "confirmed";
}) {
  const { t } = useTranslation("common");
  const { createdByEmail, createdAtUtc, lastChangedByEmail, lastChangedAtUtc } = history;

  // Null together: a record created before #494 shipped has no creation event
  // and gets no backfill, so there is nothing to claim about it.
  if (!createdByEmail || !createdAtUtc) {
    return <td className="muted">—</td>;
  }

  const changed = lastChangedByEmail && lastChangedAtUtc;
  const officialAt = official && history.madeOfficialAtUtc;

  return (
    <td>
      <div>{t("recordHistory.createdBy", {
        email: createdByEmail,
        at: formatInstant(createdAtUtc),
      })}</div>
      {officialAt && (
        <div className="muted">{t(
          official === "submitted"
            ? "recordHistory.submittedAt"
            : "recordHistory.confirmedAt",
          { at: formatInstant(officialAt) },
        )}</div>
      )}
      {changed && (
        <div className="muted">{t("recordHistory.lastChangedBy", {
          email: lastChangedByEmail,
          at: formatInstant(lastChangedAtUtc),
        })}</div>
      )}
    </td>
  );
}
