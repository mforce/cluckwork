import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Box, TableCell, Tooltip } from "@mui/material";
import type { RecordHistory } from "../api/cluckwork";
import { useFarm } from "../farm/useFarm";
import { relativeTime } from "../lib/relativeTime";

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

// The local part of an email address ("sim-sales-1@sim.local" ->
// "sim-sales-1") — #653's whole point is that the actor stops being the
// widest thing in the column. Not a validation: a value with no "@" (should
// never arrive from the server) is returned as-is rather than thrown on.
function actorHandle(email: string): string {
  const at = email.indexOf("@");
  return at === -1 ? email : email.slice(0, at);
}

// `official` names the promotion step this record HAS — submitting a daily
// entry, confirming a sales order. Records with no such step (flocks, egg
// grades, expenses) pass nothing and can never render the line, even if a
// madeOfficialAtUtc somehow arrived.
//
// `auditHref` (#493) is optional and admin-gated by the caller, so this
// component stays admin-agnostic. Flocks passes it to keep its Actions cell
// narrow; the other callers keep their own audit link or pass nothing.
export function ProvenanceCell({
  history,
  official,
  auditHref,
}: {
  history: RecordHistory;
  official?: "submitted" | "confirmed";
  auditHref?: string;
}) {
  const { t } = useTranslation("common");
  const { farm } = useFarm();
  const { createdByEmail, createdAtUtc, lastChangedByEmail, lastChangedAtUtc } = history;

  // The three underlying facts are INDEPENDENT, exactly as before #653: a
  // record predating #494 has no creation event, but it can still carry a
  // change with real attribution — so a missing creator drops one fact, not
  // the whole cell. The placeholder below is for a record with nothing at
  // all to say.
  const created = createdByEmail && createdAtUtc ? { email: createdByEmail, at: createdAtUtc } : null;
  const changed = lastChangedByEmail && lastChangedAtUtc
    ? { email: lastChangedByEmail, at: lastChangedAtUtc }
    : null;
  const officialAt = official ? (history.madeOfficialAtUtc ?? null) : null;

  if (!created && !changed && !officialAt) {
    return (
      <TableCell sx={{ padding: "0.6rem 1rem 0.6rem 0" }}>
        <Box sx={{ display: "flex", flexDirection: "column", gap: 0.25 }}>
          <span className="muted">—</span>
          {auditHref && (
            <Link className="link" to={auditHref}>{t("recordHistory.viewHistoryLink")}</Link>
          )}
        </Box>
      </TableCell>
    );
  }

  // The full stamp — everything the pre-#653 three-line cell said — moves
  // into `title` verbatim, still in UTC (#494's decision, untouched). Nothing
  // that was visible becomes unavailable; it is just not the DEFAULT view.
  const fullStamp = [
    created && t("recordHistory.createdBy", { email: created.email, at: formatInstant(created.at) }),
    officialAt && t(
      official === "submitted" ? "recordHistory.submittedAt" : "recordHistory.confirmedAt",
      { at: formatInstant(officialAt) },
    ),
    changed && t("recordHistory.lastChangedBy", { email: changed.email, at: formatInstant(changed.at) }),
  ].filter((line): line is string => Boolean(line)).join("\n");

  // #653 — the ONE visible line names whoever touched the record most
  // recently among the two events that carry an actor: a change supersedes
  // the creation it followed, per #494's rule above (never re-derived by
  // comparing instants — `changed` is already the server's own signal that a
  // distinct later event exists). The promotion step has no actor of its own
  // (Daily Entry/SalesOrder send only the instant), so it never drives this
  // line — it is still in `fullStamp` above. A record with neither a create
  // nor a change event but SOME official step (only reachable on data this
  // old #494 predates entirely) falls back to the instant alone.
  const summary = changed ?? created;

  return (
    // Padding is pinned to `table.data td`'s value because three callers
    // (Sales, Expenses, History, until #831) still render this inside a plain
    // `table.data`. The ellipsis sits on the summary line alone so a stacked
    // audit link is never clipped.
    // #828 — describeChild: the full stamp DESCRIBES the visible summary, it
    // is not the cell's accessible name; MUI's default would otherwise
    // replace the cell's name (its visible actor/date text) with the stamp.
    <Tooltip title={fullStamp} describeChild>
      <TableCell sx={{ padding: "0.6rem 1rem 0.6rem 0", whiteSpace: "nowrap" }}>
        <Box sx={{ display: "flex", flexDirection: "column", gap: 0.25, maxWidth: "14rem" }}>
          <Box
            component="span"
            className="muted"
            sx={{ display: "block", overflow: "hidden", textOverflow: "ellipsis" }}
          >
            {relativeTime(summary ? summary.at : (officialAt as string), farm?.timeZoneId)}
            {summary && <> · {actorHandle(summary.email)}</>}
          </Box>
          {auditHref && (
            <Link className="link" to={auditHref}>{t("recordHistory.viewHistoryLink")}</Link>
          )}
        </Box>
      </TableCell>
    </Tooltip>
  );
}
