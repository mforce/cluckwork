import { useEffect, useRef } from "react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { X } from "lucide-react";
import {
  Dialog as MuiDialog, DialogTitle, DialogContent, IconButton, useMediaQuery,
} from "@mui/material";
import { MD_UP_QUERY } from "../lib/breakpoints";

// Everything the browser lets you tab to, minus the things that only LOOK
// focusable: hidden inputs, [hidden]/aria-hidden nodes, and anything parked at
// tabindex="-1". MUI's own FocusTrap lands initial focus on the PANEL (it
// looks for an element it marked itself via its own focusable-target
// attribute, which is the Paper, never the first field), so this list is
// still what decides the ONE thing MUI does not: which control the dialog
// exists to be filled in gets the cursor first.
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
   * Suppresses every dismissal path — Escape, the close button (rendered
   * disabled), and a backdrop click — while an in-flight write owns the
   * dialog's contents. Without this, closing and reopening the SAME record
   * mid-write loads data that predates the write, and the write's own
   * completion is discarded by the (correctly) bumped generation, leaving the
   * reopened dialog showing stale data no amount of retrying fixes until it
   * is closed and reopened again post-settle (#609 review).
   *
   * MUI 9.4.0 ships no `disableEscapeKeyDown` (removed from `Modal` — checked
   * directly against the installed package: zero matches for the prop name
   * anywhere under `node_modules/@mui/material`, in both the implementation
   * and its `.d.ts`). `onClose` below is what closes that gap instead: Modal
   * calls it with `reason: "escapeKeyDown"` for the topmost dialog exactly
   * like it does for a backdrop click, so gating there suppresses both paths
   * with the one handler, and #609 rests on that plus the disabled button.
   */
  closeDisabled?: boolean;
  /**
   * Identifies WHAT the dialog is editing. When it changes while the dialog
   * stays open — a 409 rebind swaps in the server's newer record — focus moves
   * back to the first field, because the form under the user's cursor is not
   * the one they were filling in any more. MUI's own auto-focus only runs on
   * the dialog's OWN open transition, never on a later prop change, so this
   * stays a explicit effect.
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
   * panes into one column on a desktop that has room for both.
   *
   * Applied as an explicit `sx` cap (30rem / 52rem, this app's existing
   * numbers) rather than MUI's own `maxWidth` breakpoint enum: `sm` (600px)
   * and `md` (900px) are not close enough to the shipped 480px/832px to
   * reuse without a visual regression nobody asked for, and no design record
   * names new numbers. Below 900px the cap gives way to a 16px side margin
   * (owner, 2026-09-17, from the three mockups on #892: a centred dialog
   * sized to its content, not the full-screen form D3.3 had planned), and
   * `fullScreen` suppresses it entirely.
   */
  wide?: boolean;
  /**
   * Whether this dialog takes the whole screen below 900px. Off by default:
   * form dialogs and confirmations alike stay a centred dialog sized to their
   * content at every width. The phone More menu (`BottomNav.tsx`) is the one
   * caller that turns it on, because a twenty-link menu needs the height.
   */
  fullScreenOnPhone?: boolean;
  children: ReactNode;
}

// #485 — everything outside the topmost dialog is inert, so it is out of the
// accessibility tree, and a live region cannot speak from there. Un-inerting
// it later replays nothing, so a region that was silenced under a dialog has
// to be told when the page belongs to it again. Subscribers are handed "is
// any dialog open", not "a dialog just closed": the settled state is the
// useful signal, and it is the one that survives the sequences below.
//
// #480 (background out of the accessibility tree) and #482 (scroll lock
// across stacked dialogs, one Escape closes one) are now MUI's own job —
// `Modal`'s `ModalManager` tracks a real stack of open instances, marks every
// sibling but the topmost `aria-hidden`, and locks/restores
// `document.body.style.overflow` once per stack rather than once per
// instance, which is exactly the #482 defect this file used to hand-roll a
// fix for. So the open STACK this file used to keep (an ordered array of
// backdrop elements) is gone; what is left is a plain counter, fed by the two
// transition callbacks `Modal` already exposes for exactly this.
let openCount = 0;
const modalStateListeners = new Set<(anyDialogOpen: boolean) => void>();
let notifyScheduled = false;

export function anyDialogOpen(): boolean {
  return openCount > 0;
}

export function onModalStateChange(
  listener: (anyDialogOpen: boolean) => void,
): () => void {
  modalStateListeners.add(listener);
  return () => modalStateListeners.delete(listener);
}

// Deferred on purpose, rather than fired inline from the transition
// callbacks, for one reason that is load-bearing and one that is
// housekeeping.
//
// Load-bearing: a subscriber mounted BELOW the dialog in the tree has not run
// its own effect yet at the point `onTransitionEnter`/`onTransitionExited`
// fire — an inline call would reach nobody, and the subscriber would keep an
// initial value it read before the dialog existed. A microtask runs once
// every effect in the commit has, so whoever is listening by then hears the
// truth.
//
// Housekeeping: a commit that swaps dialog A for dialog B pops to empty and
// pushes straight back. Coalescing collapses each of those into the single
// question worth asking — what is true now? Subscribers are expected to be
// idempotent regardless, so this is cheapness, not correctness.
function scheduleModalStateNotify() {
  if (notifyScheduled) return;
  notifyScheduled = true;
  queueMicrotask(() => {
    notifyScheduled = false;
    const open = anyDialogOpen();
    for (const listener of modalStateListeners) listener(open);
  });
}

function bumpOpenCount(delta: 1 | -1) {
  openCount += delta;
  scheduleModalStateNotify();
}

// #483 — focus restoration, kept as this file's own bookkeeping rather than
// handed to MUI. `FocusTrap` (Unstable_TrapFocus/FocusTrap.js) DOES restore
// focus to the previously active element on close, but two things it does not
// do broke this guarantee when tried against the rewritten test suite:
//
// - It tries exactly once (`nodeToRestore.current.focus()`, no retry), so the
//   busy-trigger case — the row's own trigger is still `disabled` for one more
//   render when the dialog closes — silently strands focus on <body>, which
//   is the regression #483's own review found the first time.
// - It restores to ITS OWN prior-active-element, with no notion of a dialog
//   stacked underneath. Closing dialog B when A is still open needs focus to
//   land in A, not on B's own (now `aria-hidden`, and in jsdom still
//   `.focus()`-able, which is exactly the gap #483's review closed) trigger.
//
// So this stays a real ordered stack of open instances — not the counter
// above, which only ever needs to answer "is anything open" for #485 — plus a
// captured "what had focus before this dialog opened" per instance. `panel`
// (the whole `role="dialog"` element, head included) is what a containment
// check ("is focus already somewhere in the dialog that stays open") has to
// test against; `content` (the `DialogContent` node alone) is what the
// fallback focus SEARCH has to be scoped to — searching the whole panel would
// find the close button before anything in the form, because it sits first
// in DOM order inside the heading.
interface OpenPanel { panel: HTMLElement; content: HTMLElement | null }
const openPanels: OpenPanel[] = [];

// #609 review — the trigger can be gone if the save re-rendered the row that
// owned it, and focus() is a no-op on a disabled control with no error, so a
// naive restore silently drops focus to <body> for a row that is disabled for
// exactly one more render after a save closes its dialog.
function restoreFocusOnClose(
  closed: OpenPanel | null,
  returnFocusTo: Element | null,
) {
  if (closed !== null) {
    const at = openPanels.indexOf(closed);
    if (at !== -1) openPanels.splice(at, 1);
  }

  // If another dialog is still open, IT is what the page now shows. Check
  // where focus actually IS, not this dialog's own (possibly irrelevant)
  // trigger: a lower dialog can close programmatically — an unrelated effect,
  // not the user's own click — while focus is already correctly inside the
  // dialog on top, mid-typing (codex review of #483). Moving it from there
  // based on THIS dialog's stale trigger would yank the cursor out from under
  // the user. Only redirect when focus is genuinely NOT already inside the
  // dialog that remains open.
  const remaining = openPanels[openPanels.length - 1] ?? null;
  if (remaining !== null) {
    const active = document.activeElement;
    if (!(active instanceof HTMLElement) || !remaining.panel.contains(active)) {
      if (!focusFirstThatTakes(focusableIn(remaining.content))) remaining.panel.focus();
    }
    return;
  }

  if (!(returnFocusTo instanceof HTMLElement)) return;
  const restore = () => document.body.contains(returnFocusTo) && focusFirstThatTakes([returnFocusTo]);
  // MUI's own FocusTrap already tried this restore once, synchronously, as
  // part of the same close. If it landed, `document.activeElement` is
  // already the trigger and this is a harmless no-op.
  //
  // If the trigger was disabled, the retry is guarded on "focus is still
  // where this closing dialog left it" — NOT on `document.activeElement ===
  // document.body`, which is what a pre-MUI, synchronous-close version would
  // check and is no longer the right test: MUI's exit runs on a real
  // transition (`closeAfterTransition`), so the closing panel stays mounted,
  // and a failed `.focus()` leaves the OLD focus (something inside that
  // still-mounted, about-to-vanish panel) in place rather than dropping to
  // <body> the way an immediate unmount used to. Retrying while focus is
  // either on <body> or still inside the panel that is closing covers both;
  // retrying unconditionally would risk yanking focus the user has since
  // deliberately moved elsewhere.
  if (!restore()) {
    requestAnimationFrame(() => {
      const active = document.activeElement;
      const stillInClosingPanel =
        closed !== null && active instanceof Node && closed.panel.contains(active);
      if (active === document.body || stillInClosingPanel) restore();
    });
  }
}

// F131: the shared modal shell. Add/edit forms used to sit inline above (or
// inside) the list they mutate, shoving the data around on every open. They
// live in here so the list stays put and the form gets full attention — MUI
// `Dialog` now supplies the portal, the backdrop, the focus trap and its
// restore-on-close, and the background/scroll-lock stacking (#480/#482); this
// wrapper keeps this app's own props (`closeDisabled`, `focusKey`,
// `describedBy`, `wide`, `fullScreenOnPhone`) and the `anyDialogOpen`/
// `onModalStateChange` pair #485 depends on.
export function Dialog({
  open, title, onClose, focusKey, describedBy, wide, closeDisabled,
  fullScreenOnPhone = false, children,
}: DialogProps) {
  const { t } = useTranslation("common");
  const bodyRef = useRef<HTMLDivElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const returnFocusTo = useRef<Element | null>(null);
  const isPhone = !useMediaQuery(MD_UP_QUERY);
  const fullScreen = fullScreenOnPhone && isPhone;

  // #485 — `openCount` above is fed by `onTransitionEnter`/`onTransitionExited`,
  // which only fire either side of a COMPLETED exit transition. An unmount
  // that skips that transition entirely — a route change while the dialog is
  // still open is the real one; the rewritten test suite found it by
  // unmounting mid-test the same way — never calls `onTransitionExited`, so
  // the increment from opening would never be paid back and `anyDialogOpen()`
  // would read `true` for the rest of the session. `entered` tracks whether
  // THIS instance is the one currently owed a decrement, and the unmount
  // effect below pays it if `onTransitionExited` never got the chance to.
  const entered = useRef(false);

  // onClose (and closeDisabled) read through a ref so the handler identity
  // handed to MUI stays stable across re-renders — callers pass inline
  // lambdas, and the screens re-render on every keystroke.
  const onCloseRef = useRef(onClose);
  useEffect(() => { onCloseRef.current = onClose; });
  const closeDisabledRef = useRef(closeDisabled);
  useEffect(() => { closeDisabledRef.current = closeDisabled; });

  // Unconditional cleanup, mount-once: pays back `openCount` on unmount if
  // this instance is still owed a decrement (see `entered` above).
  useEffect(() => () => {
    if (entered.current) {
      entered.current = false;
      bumpOpenCount(-1);
    }
  }, []);

  // #609 — the one gate every dismissal path funnels through. MUI calls this
  // for BOTH Escape (reason "escapeKeyDown", already scoped to the topmost
  // dialog by Modal itself) and a backdrop click (reason "backdropClick"), so
  // checking closeDisabled once here suppresses both; the close button below
  // is disabled separately, because `onClose` is never invoked for a click on
  // a disabled button in the first place.
  const handleClose = () => {
    if (closeDisabledRef.current) return;
    onCloseRef.current();
  };

  // Land on the first field rather than the close button — the dialog exists
  // to be filled in, and the heading is announced by aria-labelledby anyway.
  // Re-run when focusKey changes so a swapped-in record gets the cursor back.
  // MUI's own FocusTrap lands initial focus on the panel first (it is mounted
  // deeper in the tree, so its own effect fires before this one), which this
  // effect then overrides — except on the dialog's OWN opening render, the
  // content this effect looks for is not reliably in the DOM yet: measured
  // directly, `bodyRef.current` is still null on the first synchronous run of
  // this exact effect, on this exact transition. One frame later it is
  // populated, so the retry follows the same "try again next frame" shape the
  // busy-trigger restore below already uses, for the same reason — a target
  // that is not there yet is not a failure to fall back from, it is a target
  // to wait one frame for.
  useEffect(() => {
    if (!open) return;
    if (focusFirstThatTakes(focusableIn(bodyRef.current))) return;
    const raf = requestAnimationFrame(() => {
      focusFirstThatTakes(focusableIn(bodyRef.current));
    });
    return () => cancelAnimationFrame(raf);
  }, [open, focusKey]);

  // #483 — register with the stack above and remember where focus came from,
  // so `restoreFocusOnClose` can redirect a stacked close correctly instead
  // of trusting MUI's own (stacking-unaware, single-attempt) restore. Keyed
  // on `open` alone: a rebind must not re-capture the trigger.
  //
  // `pushed` tracks exactly what got onto `openPanels`, read fresh rather
  // than closed over once: `panelRef.current` is not reliably populated on
  // the SAME synchronous pass this effect runs on (the same one-frame gap
  // `bodyRef` above works around), so the push retries next frame — and
  // cleanup must remove the SAME reference it pushed, not re-read
  // `panelRef.current` at close time, which could by then point at nothing
  // (the panel is already unmounting).
  useEffect(() => {
    if (!open) return;
    returnFocusTo.current = document.activeElement;
    let pushed: OpenPanel | null = null;
    const push = () => {
      if (pushed !== null) return;
      const panel = panelRef.current;
      if (panel === null) return;
      pushed = { panel, content: bodyRef.current };
      openPanels.push(pushed);
    };
    push();
    const raf = pushed === null ? requestAnimationFrame(push) : null;
    return () => {
      if (raf !== null) cancelAnimationFrame(raf);
      restoreFocusOnClose(pushed, returnFocusTo.current);
    };
  }, [open]);

  return (
    <MuiDialog
      open={open}
      onClose={handleClose}
      // Both guarded on `entered`: reopening during the exit transition
      // fires `onTransitionEnter` again with no `onTransitionExited` for the
      // interrupted exit in between, so an unguarded pair counts one dialog
      // twice and `anyDialogOpen()` never returns to false.
      onTransitionEnter={() => {
        if (entered.current) return;
        entered.current = true;
        bumpOpenCount(1);
      }}
      onTransitionExited={() => {
        if (!entered.current) return;
        entered.current = false;
        bumpOpenCount(-1);
      }}
      // `restoreFocusOnClose` above is a full replacement for MUI's own
      // restore, not a supplement — measured directly against the rewritten
      // stacked-dialog tests, running BOTH produced a real conflict: closing
      // dialog A while B stays open (with focus deliberately left mid-typing
      // in B) let MUI's OWN focus-trap `contain()` listener for B (every
      // FocusTrap listens for `focusin` on the whole document) react to
      // whatever transient blur A's close caused and yank focus to B's PANEL
      // ROOT instead of leaving it on the field the user was in — exactly the
      // guarantee #483's own adversarial review is named for. Disabling MUI's
      // restore removes the second, uncoordinated actor; this file's own
      // stacking-aware logic is the only one left.
      disableRestoreFocus
      fullScreen={fullScreen}
      maxWidth={false}
      aria-describedby={describedBy}
      slotProps={{
        paper: {
          ref: panelRef,
          className: "dialog",
          sx: fullScreen
            ? undefined
            : isPhone
              ? { m: 2, width: "calc(100% - 32px)", maxWidth: wide ? "52rem" : "30rem" }
              : { maxWidth: wide ? "52rem" : "30rem" },
        },
        backdrop: { className: "dialog-backdrop" },
      }}
    >
      <DialogTitle
        component="h3"
        variant="h4"
        sx={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 2 }}
      >
        {title}
        <IconButton aria-label={t("close")} disabled={closeDisabled} onClick={onClose} size="small">
          <X size={18} aria-hidden />
        </IconButton>
      </DialogTitle>
      <DialogContent ref={bodyRef}>{children}</DialogContent>
    </MuiDialog>
  );
}
