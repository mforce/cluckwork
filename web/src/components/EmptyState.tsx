import type { LucideIcon } from "lucide-react";

// #655 — one component for every list/table's empty branch, replacing a bare
// `<p className="muted">` with an icon, one sentence, and (when there is one)
// the screen's own primary action as a real button.
//
// Role-awareness is NOT re-derived here: the caller passes `action` only when
// the current user may perform it — the exact same boolean the page-head
// button already gates on (see FlocksPage's `isAdmin &&`) — so a user who may
// not act gets the sentence alone, matching the page-head affordance it sits
// under. Likewise `action.onClick` is always the SAME handler the page-head
// button calls; this component never opens its own dialog or duplicates one.
export function EmptyState({
  icon: Icon,
  message,
  action,
}: {
  icon: LucideIcon;
  message: string;
  action?: { label: string; onClick: () => void };
}) {
  return (
    <div className="empty-state">
      <Icon size={32} aria-hidden focusable="false" />
      <p>{message}</p>
      {action && (
        <button type="button" onClick={action.onClick}>
          {action.label}
        </button>
      )}
    </div>
  );
}
