import { useEffect, useId, useRef } from "react";
import type { ReactNode } from "react";
import { createPortal } from "react-dom";
import { useTranslation } from "react-i18next";
import { X } from "lucide-react";

// Everything the browser lets you tab to, minus the things that only LOOK
// focusable: hidden inputs, [hidden]/aria-hidden nodes, and anything parked at
// tabindex="-1". Including those would break the trap at both ends — a hidden
// first field swallows the initial focus, and a hidden last field stops being
// the boundary, letting Tab escape to the page behind.
//
// Visibility is deliberately NOT probed via offsetParent/getClientRects: jsdom
// reports every element as unrendered, which would empty the trap in tests.
const FOCUSABLE = [
  "a[href]",
  "button:not([disabled])",
  'input:not([disabled]):not([type="hidden"])',
  "select:not([disabled])",
  "textarea:not([disabled])",
  "[tabindex]",
]
  .map((sel) => `${sel}:not([tabindex="-1"]):not([hidden]):not([aria-hidden="true"])`)
  .join(",");

const focusableIn = (root: HTMLElement | null): HTMLElement[] =>
  root ? Array.from(root.querySelectorAll<HTMLElement>(FOCUSABLE)) : [];

// focus() is a no-op on a control the browser won't take focus for (disabled,
// or display:none), and it reports no error — so confirm it landed.
const focusFirstThatTakes = (candidates: HTMLElement[]): boolean =>
  candidates.some((el) => {
    el.focus();
    return document.activeElement === el;
  });

interface DialogProps {
  open: boolean;
  /** Rendered as the dialog's heading and used as its accessible name. */
  title: string;
  /** Called for Escape, the close button, and a backdrop click. */
  onClose: () => void;
  /**
   * Identifies WHAT the dialog is editing. When it changes while the dialog
   * stays open — a 409 rebind swaps in the server's newer record — focus moves
   * back to the first field, because the form under the user's cursor is not
   * the one they were filling in any more.
   */
  focusKey?: unknown;
  /**
   * Id of the element describing what the dialog is for, read out after its
   * name. Matters where the prose IS the content — a confirmation's whole job
   * is to say what is about to happen, and focus lands on a button, so without
   * this a screen reader announces the title and the control and nothing else.
   */
  describedBy?: string;
  children: ReactNode;
}

// F131: the shared modal shell. Add/edit forms used to sit inline above (or
// inside) the list they mutate, shoving the data around on every open. They now
// live in here: the list stays put and the form gets full attention.
//
// Portalled to <body> so the backdrop covers the whole viewport regardless of
// where it is mounted in the shell grid.
export function Dialog({ open, title, onClose, focusKey, describedBy, children }: DialogProps) {
  const { t } = useTranslation("common");
  const panelRef = useRef<HTMLDivElement>(null);
  const bodyRef = useRef<HTMLDivElement>(null);
  const returnFocusTo = useRef<Element | null>(null);

  // Escape/backdrop handlers read the LATEST onClose through a ref, so the
  // keydown listener is bound once per open instead of being torn down and
  // re-added on every parent render (callers pass inline lambdas, and the
  // screens re-render on every keystroke).
  const onCloseRef = useRef(onClose);
  useEffect(() => { onCloseRef.current = onClose; });

  // Remember where focus came from, and stop the page behind the backdrop from
  // scrolling. Keyed on `open` alone: a rebind must not re-capture the trigger.
  useEffect(() => {
    if (!open) return;
    returnFocusTo.current = document.activeElement;

    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";

    return () => {
      document.body.style.overflow = previousOverflow;
      const trigger = returnFocusTo.current;
      // The trigger can be gone if the save re-rendered the row that owned it.
      if (!(trigger instanceof HTMLElement)) return;

      const restore = () =>
        document.body.contains(trigger) && focusFirstThatTakes([trigger]);

      // A save that closes the dialog while still `busy` leaves its row trigger
      // disabled for one more render, and focus() does nothing to a disabled
      // control — focus would silently land on <body>. Try again next frame,
      // by which point the write has settled and the button is live again.
      // Guarded on <body> so a retry can never steal focus the user has since
      // moved somewhere else.
      if (!restore()) {
        requestAnimationFrame(() => {
          if (document.activeElement === document.body) restore();
        });
      }
    };
  }, [open]);

  // Land on the first field rather than the close button — the dialog exists to
  // be filled in, and the heading is announced by aria-labelledby anyway. Re-run
  // when focusKey changes so a swapped-in record gets the cursor back.
  useEffect(() => {
    if (!open) return;
    if (!focusFirstThatTakes(focusableIn(bodyRef.current))) panelRef.current?.focus();
  }, [open, focusKey]);

  // Escape closes; Tab cycles inside the panel instead of escaping to the page.
  useEffect(() => {
    if (!open) return;

    function onKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") {
        onCloseRef.current();
        return;
      }
      if (e.key !== "Tab") return;

      const panel = panelRef.current;
      if (!panel) return;
      const items = focusableIn(panel);
      if (items.length === 0) {
        e.preventDefault();
        return;
      }

      const first = items[0];
      const last = items[items.length - 1];
      const active = document.activeElement;
      const outside = !panel.contains(active);

      if (e.shiftKey && (active === first || outside)) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && (active === last || outside)) {
        e.preventDefault();
        first.focus();
      }
    }

    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [open]);

  const titleId = useId();
  if (!open) return null;

  return createPortal(
    <div
      className="dialog-backdrop"
      // Only a click on the backdrop itself dismisses — a click that lands on
      // the panel bubbles up here too.
      onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={describedBy}
        ref={panelRef}
        tabIndex={-1}
      >
        <div className="dialog-head">
          <h3 id={titleId}>{title}</h3>
          <button type="button" className="link dialog-close" aria-label={t("close")} onClick={onClose}>
            <X size={18} aria-hidden />
          </button>
        </div>
        <div className="dialog-body" ref={bodyRef}>{children}</div>
      </div>
    </div>,
    document.body,
  );
}
