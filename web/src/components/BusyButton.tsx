import type { ButtonHTMLAttributes } from "react";
import { useTranslation } from "react-i18next";

type Props = ButtonHTMLAttributes<HTMLButtonElement> & { busy?: boolean };

// #236 — the busy trigger. Children pass through untouched (dynamic labels
// like Login's "Signing in…" swap stay the caller's); the wrapper's inline-flex
// gap is what seats the spinner beside them. The ring sits INLINE before the
// label at full brightness — an earlier overlay version dimmed the label and
// stacked the ring on top, and read as barely-there on the terracotta buttons
// (owner call, 2026-07-28). The button widening slightly while busy is the
// accepted cost; it is disabled for the duration, so nothing under the cursor
// is clickable anyway.
//
// The live region is a SIBLING of the button, not a child: aria-busy tells AT
// to defer announcing changes inside the busy element, so a region in there
// may never speak. It stays MOUNTED with its text swapped — a region that
// mounts already populated is unreliably announced (same pattern as the
// Settings logo status). It is .sr-only (absolute-positioned), so the
// fragment adds no layout. The spinner is aria-hidden so the accessible name
// stays exactly the children text — screen tests assert names verbatim.
export function BusyButton({ busy = false, disabled, children, ...rest }: Props) {
  const { t } = useTranslation("common");
  return (
    <>
      <button {...rest} disabled={disabled || busy} aria-busy={busy || undefined}>
        <span className="busy-label">
          {busy && <span className="spinner" aria-hidden="true" />}
          {children}
        </span>
      </button>
      <span role="status" className="sr-only">
        {busy ? t("working") : ""}
      </span>
    </>
  );
}
