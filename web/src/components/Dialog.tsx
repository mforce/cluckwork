import { useEffect, useId, useRef } from "react";
import type { ReactNode } from "react";
import { createPortal } from "react-dom";
import { X } from "lucide-react";

// Everything the browser lets you tab to. `offsetParent`-style visibility
// filtering is deliberately omitted: jsdom reports every element as unrendered,
// which would empty the trap in tests.
const FOCUSABLE = [
  "a[href]",
  "button:not([disabled])",
  "input:not([disabled])",
  "select:not([disabled])",
  "textarea:not([disabled])",
  '[tabindex]:not([tabindex="-1"])',
].join(",");

interface DialogProps {
  open: boolean;
  /** Rendered as the dialog's heading and used as its accessible name. */
  title: string;
  /** Called for Escape, the close button, and a backdrop click. */
  onClose: () => void;
  children: ReactNode;
}

// F131: the shared modal shell. Add/edit forms used to sit inline above (or
// inside) the list they mutate, shoving the data around on every open. They now
// live in here: the list stays put and the form gets full attention.
//
// Portalled to <body> so the backdrop covers the whole viewport regardless of
// where it is mounted in the shell grid.
export function Dialog({ open, title, onClose, children }: DialogProps) {
  const panelRef = useRef<HTMLDivElement>(null);
  const bodyRef = useRef<HTMLDivElement>(null);
  const returnFocusTo = useRef<Element | null>(null);

  // Move focus in on open, restore it to the trigger on close, and stop the
  // page behind the backdrop from scrolling while we are up.
  useEffect(() => {
    if (!open) return;
    returnFocusTo.current = document.activeElement;
    // Land on the first field rather than the close button — the dialog exists
    // to be filled in, and the heading is announced by aria-labelledby anyway.
    const first = bodyRef.current?.querySelector<HTMLElement>(FOCUSABLE);
    (first ?? panelRef.current)?.focus();

    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";

    return () => {
      document.body.style.overflow = previousOverflow;
      const trigger = returnFocusTo.current;
      // The trigger can be gone if the save re-rendered the row that owned it.
      if (trigger instanceof HTMLElement && document.body.contains(trigger)) {
        trigger.focus();
      }
    };
  }, [open]);

  // Escape closes; Tab cycles inside the panel instead of escaping to the page.
  useEffect(() => {
    if (!open) return;

    function onKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") {
        onClose();
        return;
      }
      if (e.key !== "Tab") return;

      const panel = panelRef.current;
      if (!panel) return;
      const items = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE));
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
  }, [open, onClose]);

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
        ref={panelRef}
        tabIndex={-1}
      >
        <div className="dialog-head">
          <h3 id={titleId}>{title}</h3>
          <button type="button" className="link dialog-close" aria-label="Close" onClick={onClose}>
            <X size={18} aria-hidden />
          </button>
        </div>
        <div className="dialog-body" ref={bodyRef}>{children}</div>
      </div>
    </div>,
    document.body,
  );
}
