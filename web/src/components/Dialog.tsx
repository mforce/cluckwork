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
  /**
   * Widens the panel past the default single-column form width. For a dialog
   * whose content is itself a two-pane layout — History's adjust form mirrors
   * Daily entry's side-by-side steps — the narrow panel would fold the two
   * panes into one column on a desktop that has room for both. Undone at
   * ≤900px, where the panel is a full-width sheet: by an explicit
   * `.dialog.wide` rule inside that media query, NOT by the sheet's own
   * `.dialog` reset, which this modifier outranks.
   */
  wide?: boolean;
  children: ReactNode;
}

// #482 — the page has one scrollbar and one accessibility tree, so the state
// that belongs to the PAGE lives here, once, not once per instance. Every
// instance used to snapshot and restore `body.style.overflow` on its own:
// closing two dialogs in first-opened-first order unlocked the page while one
// was still up, then re-locked it permanently — a page that never scrolls
// again with nothing open, reachable by a single Escape (which every instance
// answered). The stack is in open order, so the last entry is the topmost.
const openStack: HTMLElement[] = [];
let overflowBeforeAnyDialog: string | null = null;

// Everything except the topmost dialog is inert: the page behind it, and any
// dialog underneath it. `aria-modal` is a hint some ATs honour, not
// containment — without this, every control behind the backdrop stays
// focusable and activatable by a virtual cursor, which is how a second dialog
// came to be open at all (#480).
function syncModalBackground() {
  const top = openStack[openStack.length - 1] ?? null;
  for (const child of Array.from(document.body.children)) {
    if (!(child instanceof HTMLElement)) continue;
    if (top !== null && child !== top) child.setAttribute("inert", "");
    else child.removeAttribute("inert");
  }
}

function pushModal(backdrop: HTMLElement) {
  // Snapshotted once, by the first dialog to open, and restored by the last to
  // close — never per instance.
  if (openStack.length === 0) overflowBeforeAnyDialog = document.body.style.overflow;
  openStack.push(backdrop);
  document.body.style.overflow = "hidden";
  syncModalBackground();
  scheduleModalStateNotify();
}

// #485 — everything outside the topmost dialog is inert, so it is out of the
// accessibility tree, and a live region cannot speak from there. Un-inerting
// it later replays nothing, so a region that was silenced under a dialog has
// to be told when the page belongs to it again. Subscribers are handed "is
// any dialog open", not "a dialog just closed": the settled state is the
// useful signal, and it is the one that survives the sequences below.
const modalStateListeners = new Set<(anyDialogOpen: boolean) => void>();
let notifyScheduled = false;

export function anyDialogOpen(): boolean {
  return openStack.length > 0;
}

export function onModalStateChange(
  listener: (anyDialogOpen: boolean) => void,
): () => void {
  modalStateListeners.add(listener);
  return () => modalStateListeners.delete(listener);
}

// Deferred on purpose, rather than fired inline from push/popModal, for one
// reason that is load-bearing and one that is housekeeping.
//
// Load-bearing: push/popModal run from Dialog's own effect, and a subscriber
// mounted BELOW the dialog in the tree has not run its own effect yet at that
// point — an inline call would reach nobody, and the subscriber would keep an
// initial value it read before the dialog existed. A microtask runs once every
// effect in the commit has, so whoever is listening by then hears the truth.
//
// Housekeeping: a commit that swaps dialog A for dialog B pops to empty and
// pushes straight back, and StrictMode's dev-mode replay does setup ->
// cleanup -> setup on every first open. Coalescing collapses each of those
// into the single question worth asking — what is true now? Subscribers are
// expected to be idempotent regardless, so this is cheapness, not correctness.
function scheduleModalStateNotify() {
  if (notifyScheduled) return;
  notifyScheduled = true;
  queueMicrotask(() => {
    notifyScheduled = false;
    const open = anyDialogOpen();
    for (const listener of modalStateListeners) listener(open);
  });
}

function popModal(backdrop: HTMLElement) {
  const at = openStack.indexOf(backdrop);
  if (at !== -1) openStack.splice(at, 1);
  if (openStack.length === 0 && overflowBeforeAnyDialog !== null) {
    document.body.style.overflow = overflowBeforeAnyDialog;
    overflowBeforeAnyDialog = null;
  }
  syncModalBackground();
  scheduleModalStateNotify();
}

// F131: the shared modal shell. Add/edit forms used to sit inline above (or
// inside) the list they mutate, shoving the data around on every open. They now
// live in here: the list stays put and the form gets full attention.
//
// Portalled to <body> so the backdrop covers the whole viewport regardless of
// where it is mounted in the shell grid.
export function Dialog({ open, title, onClose, focusKey, describedBy, wide, children }: DialogProps) {
  const { t } = useTranslation("common");
  const panelRef = useRef<HTMLDivElement>(null);
  const backdropRef = useRef<HTMLDivElement>(null);
  const bodyRef = useRef<HTMLDivElement>(null);
  const returnFocusTo = useRef<Element | null>(null);

  // Escape/backdrop handlers read the LATEST onClose through a ref, so the
  // keydown listener is bound once per open instead of being torn down and
  // re-added on every parent render (callers pass inline lambdas, and the
  // screens re-render on every keystroke).
  const onCloseRef = useRef(onClose);
  useEffect(() => { onCloseRef.current = onClose; });

  // Remember where focus came from, and hand this dialog to the page-level
  // bookkeeping above (scroll lock + inertness). Keyed on `open` alone: a
  // rebind must not re-capture the trigger.
  useEffect(() => {
    if (!open) return;
    returnFocusTo.current = document.activeElement;

    const backdrop = backdropRef.current;
    if (backdrop !== null) pushModal(backdrop);

    return () => {
      if (backdrop !== null) popModal(backdrop);
      const trigger = returnFocusTo.current;
      // If another dialog is still open, IT is what the page now shows. Check
      // where focus actually IS, not this dialog's own (possibly irrelevant)
      // trigger: a lower dialog can close programmatically — an unrelated
      // effect, not the user's own click — while focus is already correctly
      // inside the dialog on top, mid-typing (codex review of #483). Moving
      // it from there based on THIS dialog's stale trigger would yank the
      // cursor out from under the user. Only redirect when focus is genuinely
      // NOT already inside the dialog that remains open — because it's on
      // this closing dialog's own (about-to-vanish) content, or on a page
      // element the remaining dialog has made inert, reachable the same way
      // a screen reader's virtual cursor reached this dialog's own opener in
      // the first place (#480).
      const remainingTop = openStack[openStack.length - 1] ?? null;
      if (remainingTop !== null) {
        const active = document.activeElement;
        if (!(active instanceof HTMLElement) || !remainingTop.contains(active)) {
          // The BODY, not the whole panel — same reasoning as the initial-
          // focus effect below: the panel's own DOM order puts the close
          // button before the form fields, so querying the panel would land
          // there instead of on the content the dialog exists to show.
          const body = remainingTop.querySelector<HTMLElement>(".dialog-body");
          if (!focusFirstThatTakes(focusableIn(body))) {
            remainingTop.querySelector<HTMLElement>('[role="dialog"]')?.focus();
          }
        }
        return;
      }

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
      // Every open instance listens on `document`, so without this one Escape
      // ran every handler and closed every dialog — discarding a lower form's
      // input on a keystroke meant for the top one. The same check keeps the
      // Tab traps from fighting: only the topmost pulls focus back (#482).
      const backdrop = backdropRef.current;
      if (backdrop !== null && openStack[openStack.length - 1] !== backdrop) return;

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
      ref={backdropRef}
      // Only a click on the backdrop itself dismisses — a click that lands on
      // the panel bubbles up here too.
      onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div
        className={wide ? "dialog wide" : "dialog"}
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
