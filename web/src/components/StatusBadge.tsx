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

export function StatusBadge({ status, label }: { status: string; label?: string }) {
  const variant = VARIANT[status.toLowerCase()];
  return <span className={variant ? `badge ${variant}` : "badge"}>{label ?? status}</span>;
}
