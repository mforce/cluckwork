import type { ButtonHTMLAttributes } from "react";
import { useTranslation } from "react-i18next";

type Props = ButtonHTMLAttributes<HTMLButtonElement> & { busy?: boolean };

// #236 — the busy trigger. Children pass through untouched (dynamic labels
// like Login's "Signing in…" swap stay the caller's), wrapped only so the
// label can dim in one opacity layer while the spinner overlays it.
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
        {busy && <span className="spinner" aria-hidden="true" />}
        <span className="busy-label">{children}</span>
      </button>
      <span role="status" className="sr-only">
        {busy ? t("working") : ""}
      </span>
    </>
  );
}
