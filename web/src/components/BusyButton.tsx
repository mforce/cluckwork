import type { ComponentPropsWithoutRef, ElementType, ReactNode } from "react";
import { useTranslation } from "react-i18next";

type Props<C extends ElementType> = {
  busy?: boolean;
  /**
   * #830 — the underlying element BusyButton renders. Defaults to a plain
   * `<button>` (the other 40-odd call sites, byte-identical to before this
   * prop existed); a caller that wants MUI's own root, variant class and sx
   * — the daily-entry footer's `Button` — passes it here instead of this
   * file growing a second component.
   */
  component?: C;
  children?: ReactNode;
} & Omit<ComponentPropsWithoutRef<C>, "component" | "children" | "disabled"> & {
  disabled?: boolean;
};

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
export function BusyButton<C extends ElementType = "button">({
  busy = false, disabled, children, component, ...rest
}: Props<C>) {
  const { t } = useTranslation("common");
  const Component = (component ?? "button") as ElementType;
  return (
    <>
      <Component {...rest} disabled={disabled || busy} aria-busy={busy || undefined}>
        <span className="busy-label">
          {busy && <span className="spinner" aria-hidden="true" />}
          {children}
        </span>
      </Component>
      <span role="status" className="sr-only">
        {busy ? t("working") : ""}
      </span>
    </>
  );
}
