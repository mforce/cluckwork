// A status pill (#52). Maps a domain status string to a tinted badge variant so
// lifecycle states read at a glance across the app's tables. The label text is
// preserved verbatim, so screens (and the tests that query status by text) keep
// working — this is presentation only.
const VARIANT: Record<string, string> = {
  // healthy / done
  active: "badge-ok",
  submitted: "badge-ok",
  confirmed: "badge-ok",
  saleable: "badge-ok",
  paid: "badge-ok",
  // in-progress / notable
  locked: "badge-accent",
  manageradjusted: "badge-warn",
  adjusted: "badge-warn",
  partial: "badge-warn",
  // undone / inactive
  voided: "badge-danger",
  cancelled: "badge-danger",
  inactive: "badge-danger",
  denied: "badge-danger",
  // neutral: draft, archived, depleted fall through to the base badge
};

// The canonical closed vocabulary of RAW status values this badge is asked to
// render across the app (#182). It is the union of the domain status enums that
// reach a status pill — DailyEntryStatus (Draft/Submitted/Locked/
// ManagerAdjusted/Voided), SalesOrderStatus (Draft/Confirmed/Shipped/Invoiced/
// Cancelled/Voided), FlockStatus (Active/Depleted/Archived) — plus the
// Active/Inactive toggle used by the catalog screens (grades/products/items).
//
// This is a SUPERSET of the render-relevant VARIANT keys above and does NOT
// change how the badge renders: VARIANT keeps a few defensive tints
// (saleable/paid/partial/adjusted/denied) that have no current render site (and
// so need no label — "Denied" is a role, "Saleable" a grade column), while a
// couple of real values (Shipped/Invoiced/Draft/Archived/Depleted) intentionally
// fall through to the neutral base badge. Exported so the enums module derives
// its `status` union from here and enums.test.ts can assert full label coverage
// without re-hardcoding the list.
export const STATUS_VALUES = [
  "Active",
  "Inactive",
  "Draft",
  "Submitted",
  "Locked",
  "ManagerAdjusted",
  "Voided",
  "Confirmed",
  "Shipped",
  "Invoiced",
  "Cancelled",
  "Depleted",
  "Archived",
] as const;

export function StatusBadge({ status, label }: { status: string; label?: string }) {
  const variant = VARIANT[status.toLowerCase()];
  return <span className={variant ? `badge ${variant}` : "badge"}>{label ?? status}</span>;
}
