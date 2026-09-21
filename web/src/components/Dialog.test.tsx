import { beforeEach, describe, it, expect, vi } from "vitest";
import { render, screen, waitFor, within, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { DialogActions } from "@mui/material";
import { Dialog, anyDialogOpen, onModalStateChange } from "./Dialog";
import i18n from "../i18n";
import { stubMatchMedia } from "../test/matchMedia";

// jsdom has no `matchMedia`, so left unstubbed every test below would render
// its dialog at the phone size (an unstubbed `useMediaQuery` reads "below the
// breakpoint" — see `test/matchMedia.ts`). None of the guarantees this file
// pins care about the phone margins, but testing them all against the phone
// variant by ACCIDENT is not the same as choosing to; the desktop width test
// below stubs its own case explicitly regardless.
beforeEach(() => { stubMatchMedia(true); });

// The Base UI spike (#674) ported this component onto MUI `Dialog` — 338 ->
// under 200 lines — with the same guarantees, reached by a different
// mechanism. Two of the five (#480 background out of the accessibility tree,
// #482 scroll lock + escape-only-topmost) are now MUI's own `Modal`/
// `ModalManager` job, verified directly against the installed package
// (node_modules/@mui/material/Modal/ModalManager.js): it tracks a real stack
// of open instances, `aria-hidden`s every sibling but the topmost, and locks/
// restores `document.body.style.overflow` once per stack rather than once per
// instance. Neither `inert`/`aria-hidden` containment nor a real scroll lock
// can be settled in jsdom (it computes no layout and does not enforce
// `inert`'s behaviour) — that half moves to Playwright's
// `a11y-live-regions.spec.ts` (#501, CDP through `src/ax.ts`). What is left
// here is what jsdom CAN settle: event routing (which dialog answers Escape,
// a backdrop click, closeDisabled), focus placement, and the
// `anyDialogOpen`/`onModalStateChange` pair #485 depends on.

function Body() {
  return (
    <form>
      <input aria-label="Name" />
      <input aria-label="Note" />
      <button type="submit">Save</button>
    </form>
  );
}

// A realistic host: a trigger button that opens the dialog, so focus return has
// somewhere to go back to.
function Host({ onClose, closeDisabled }: { onClose?: () => void; closeDisabled?: boolean } = {}) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button onClick={() => setOpen(true)}>New grade</button>
      <Dialog
        open={open}
        title="New grade"
        onClose={() => { setOpen(false); onClose?.(); }}
        closeDisabled={closeDisabled}
      >
        <Body />
      </Dialog>
    </>
  );
}

describe("Dialog", () => {
  it("renders nothing until opened, then exposes an accessible modal", async () => {
    const user = userEvent.setup();
    render(<Host />);
    expect(screen.queryByRole("dialog")).toBeNull();

    await user.click(screen.getByRole("button", { name: "New grade" }));

    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveAttribute("aria-modal", "true");
    // The heading names the dialog via aria-labelledby, not a duplicated label.
    expect(dialog).toHaveAccessibleName("New grade");
  });

  it("focuses the first field, not the close button", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await user.click(screen.getByRole("button", { name: "New grade" }));
    await waitFor(() => expect(screen.getByLabelText("Name")).toHaveFocus());
  });

  it("closes on Escape and returns focus to the trigger", async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<Host onClose={onClose} />);
    const trigger = screen.getByRole("button", { name: "New grade" });
    await user.click(trigger);
    await waitFor(() => expect(screen.getByLabelText("Name")).toHaveFocus());

    await user.keyboard("{Escape}");

    expect(onClose).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    await waitFor(() => expect(trigger).toHaveFocus());
  });

  it("closes on the close button", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await user.click(screen.getByRole("button", { name: "New grade" }));

    await user.click(screen.getByRole("button", { name: "Close" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("closes on a backdrop click but not on a click inside the panel", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await user.click(screen.getByRole("button", { name: "New grade" }));

    // A click that lands on the panel bubbles to the backdrop handler; it must
    // not be mistaken for a dismiss.
    await user.click(screen.getByRole("dialog"));
    expect(screen.getByRole("dialog")).toBeInTheDocument();

    // The backdrop keeps its legacy class name as a compatibility hook
    // (slotProps.backdrop.className on MUI's own Backdrop) — several
    // unconverted callers' own tests still select it this way.
    await user.click(document.querySelector(".dialog-backdrop")!);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  // #609 review — a caller with a write in flight (UsersPage's flock dialog)
  // needs every dismissal path suppressed, not just one; a close/reopen mid
  // write orphans the write's own eventual completion. MUI 9.4.0 has no
  // `disableEscapeKeyDown` (removed from `Modal` — verified against the
  // installed package), so this rests on the `onClose` reason-gate plus the
  // disabled button, not a third lever.
  it("closeDisabled suppresses Escape, the close button, and a backdrop click", async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<Host onClose={onClose} closeDisabled />);
    await user.click(screen.getByRole("button", { name: "New grade" }));

    expect(screen.getByRole("button", { name: "Close" })).toBeDisabled();

    await user.keyboard("{Escape}");
    expect(screen.getByRole("dialog")).toBeInTheDocument();

    await user.click(document.querySelector(".dialog-backdrop")!);
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
  });

  it("re-enables every dismissal path once closeDisabled clears", async () => {
    const user = userEvent.setup();
    function ToggleHost() {
      const [locked, setLocked] = useState(true);
      return (
        <>
          <button onClick={() => setLocked(false)}>unlock</button>
          <Host closeDisabled={locked} />
        </>
      );
    }
    render(<ToggleHost />);
    await user.click(screen.getByRole("button", { name: "New grade" }));
    expect(screen.getByRole("button", { name: "Close" })).toBeDisabled();

    await user.click(screen.getByRole("button", { name: "unlock", hidden: true }));
    expect(screen.getByRole("button", { name: "Close" })).toBeEnabled();

    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("traps Tab inside the panel in both directions", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await user.click(screen.getByRole("button", { name: "New grade" }));

    const name = screen.getByLabelText("Name");
    const close = screen.getByRole("button", { name: "Close" });
    const save = screen.getByRole("button", { name: "Save" });

    await waitFor(() => expect(name).toHaveFocus());
    await user.tab();
    expect(screen.getByLabelText("Note")).toHaveFocus();
    await user.tab();
    expect(save).toHaveFocus();
    // Past the last control it wraps to the first — the close button, which is
    // first in DOM order (inside the heading) — rather than escaping to the
    // page behind.
    await user.tab();
    expect(close).toHaveFocus();

    await user.tab({ shift: true });
    expect(save).toHaveFocus();
  });
});

// ---------------------------------------------------------------------------
// Review follow-ups (codex + feature-dev on PR #132)
// ---------------------------------------------------------------------------

// Mirrors the real screens: saving closes the dialog while the write is still
// in flight, and the row's trigger is `disabled={busy}` for one more render.
// focus() is a no-op on a disabled control, so a naive restore drops focus to
// <body> exactly when a keyboard user needs it most. MUI's own `FocusTrap`
// restores focus to the previously active element on close, but tries exactly
// once (`FocusTrap.js`: `nodeToRestore.current.focus()`, no retry) — so this
// guarantee is no longer free, and needs verifying against the real mechanism
// rather than assumed from #483's original fix.
function BusyHost() {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  async function save() {
    setBusy(true);
    setOpen(false); // closes while still busy — the trigger is disabled here
    await Promise.resolve();
    setBusy(false);
  }
  return (
    <>
      <button disabled={busy} onClick={() => setOpen(true)}>edit</button>
      <Dialog open={open} title="Edit grade" onClose={() => setOpen(false)}>
        <form onSubmit={(e) => { e.preventDefault(); void save(); }}>
          <input aria-label="Name" />
          <button type="submit">Save</button>
        </form>
      </Dialog>
    </>
  );
}

describe("Dialog focus return when the trigger is momentarily disabled", () => {
  it("lands focus on the trigger once it re-enables, not stranded on <body>", async () => {
    const user = userEvent.setup();
    render(<BusyHost />);
    const trigger = screen.getByRole("button", { name: "edit" });
    await user.click(trigger);
    await waitFor(() => expect(screen.getByLabelText("Name")).toHaveFocus());

    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    await waitFor(() => expect(trigger).toHaveFocus());
    expect(document.activeElement).not.toBe(document.body);
  });
});

// A 409 swaps the server's newer record into the dialog while it stays open.
function RebindHost() {
  const [record, setRecord] = useState<{ id: string } | null>(null);
  return (
    <>
      <button onClick={() => setRecord({ id: "v1" })}>correct</button>
      <button onClick={() => setRecord({ id: "v2" })}>simulate conflict</button>
      <Dialog
        open={record !== null}
        title="Correct"
        onClose={() => setRecord(null)}
        focusKey={record}
      >
        <form>
          <input aria-label="Amount" />
          <button type="submit">Save correction</button>
        </form>
      </Dialog>
    </>
  );
}

describe("Dialog focusKey (#483)", () => {
  it("pulls focus back to the first field when the record is swapped underneath", async () => {
    const user = userEvent.setup();
    render(<RebindHost />);
    await user.click(screen.getByRole("button", { name: "correct" }));
    const amount = screen.getByLabelText("Amount");
    await waitFor(() => expect(amount).toHaveFocus());

    // Move focus off the first field, then rebind to a different record.
    await user.click(screen.getByRole("button", { name: "Save correction" }));
    expect(amount).not.toHaveFocus();
    await user.click(screen.getByRole("button", { name: "simulate conflict", hidden: true }));

    // The form under the cursor is not the one being filled in any more.
    await waitFor(() => expect(screen.getByLabelText("Amount")).toHaveFocus());
  });

  it("does not re-grab focus on an unrelated re-render", async () => {
    const user = userEvent.setup();
    render(<RebindHost />);
    await user.click(screen.getByRole("button", { name: "correct" }));
    await waitFor(() => expect(screen.getByLabelText("Amount")).toHaveFocus());

    await user.click(screen.getByRole("button", { name: "Save correction" }));
    await user.keyboard("x"); // a re-render that does not change the record

    expect(screen.getByLabelText("Amount")).not.toHaveFocus();
  });
});

describe("Dialog focus trap skips controls the browser would not tab to", () => {
  function SkipHost() {
    return (
      <Dialog open title="New item" onClose={() => {}}>
        <form>
          <input type="hidden" aria-label="Hidden field" />
          <input aria-label="First real field" />
          <button type="submit">Save</button>
          <button type="button" tabIndex={-1} aria-label="Programmatic only">skip me</button>
        </form>
      </Dialog>
    );
  }

  it("lands initial focus past a hidden input", async () => {
    render(<SkipHost />);
    await waitFor(() => expect(screen.getByLabelText("First real field")).toHaveFocus());
  });

  it("treats the last TABBABLE control as the wrap boundary", async () => {
    const user = userEvent.setup();
    render(<SkipHost />);
    await waitFor(() => expect(screen.getByLabelText("First real field")).toHaveFocus());

    await user.tab(); // First real field -> Save
    expect(screen.getByRole("button", { name: "Save" })).toHaveFocus();
    // Save is the last tabbable control: the tabindex="-1" button is not a
    // boundary, so Tab must wrap inside rather than escape to the page.
    await user.tab();
    expect(screen.getByRole("button", { name: "Close" })).toHaveFocus();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 8, batch B1)
// ---------------------------------------------------------------------------

// `common` is a TRANSLATED namespace (see translations-status.ts), so asserting
// "Close" under the default lng:"en" would pass even if the label were still a
// hardcoded literal (CONTRIBUTING-i18n.md's fallback trap). Swapping the
// catalog value at runtime — the same i18n.addResource technique AppLayout's
// Task 7 wiring tests use — only renders the marker if Dialog actually reads
// the catalog.
describe("Dialog i18n wiring (#182, Task 8)", () => {
  async function withCommonOverride(key: string, value: string, run: () => Promise<void>) {
    const original = i18n.getResource("en", "common", key) as string;
    i18n.addResource("en", "common", key, value);
    try {
      await run();
    } finally {
      i18n.addResource("en", "common", key, original);
    }
  }

  it("reads the close button's accessible name from the catalog, not a hardcoded literal", async () => {
    const user = userEvent.setup();
    await withCommonOverride("close", "CLOSE-MARKER", async () => {
      render(<Host />);
      await user.click(screen.getByRole("button", { name: "New grade" }));
      expect(screen.getByRole("button", { name: "CLOSE-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Close" })).not.toBeInTheDocument();
    });
  });
});

// ---------------------------------------------------------------------------
// wide -> an explicit maxWidth (D2 pair 2). CSS-only in the old mechanism,
// now a component decision, so it is pinned here instead of in a CSS-cascade
// test (styles.dialog.test.ts is retired — see its removal in this PR).
// ---------------------------------------------------------------------------

// `toHaveStyle`/`getComputedStyle` do not reliably resolve a duplicate
// property inside one emotion-generated rule in jsdom (MUI's own base paper
// style declares `max-width: calc(100% - 64px)` and this component's `sx`
// appends its own `max-width` after it in the SAME rule — real CSS resolves
// duplicates by taking the last one, which is what makes the override work
// at all, but jsdom's CSSOM does not reproduce that reliably) — confirmed
// directly: reading the injected `<style>` text shows both declarations
// present with the `sx` one last, so the fix IS correct even though
// `toHaveStyle` cannot see it. This reads the source of truth instead: the
// actual generated rule text for the panel's own class, the same technique
// `styles.dialog.test.ts` used before this PR retired it.
function lastMaxWidth(el: HTMLElement): string | undefined {
  const panelClass = el.className.split(" ").find((c) => c.startsWith("css-"));
  if (!panelClass) throw new Error("panel carries no emotion class to inspect");
  const rule = Array.from(document.querySelectorAll("style"))
    .map((s) => s.textContent ?? "")
    .join("\n")
    .split("}")
    .find((block) => block.includes(`.${panelClass}{`));
  if (!rule) throw new Error(`no generated rule found for .${panelClass}`);
  const matches = [...rule.matchAll(/max-width:([^;]+);/g)];
  return matches.at(-1)?.[1];
}

describe("Dialog wide (#822 D2 pair 2)", () => {
  it("caps an ordinary dialog at 30rem and a wide one at 52rem", () => {
    const { rerender } = render(
      <Dialog open title="Adjust" onClose={() => {}}><Body /></Dialog>,
    );
    expect(lastMaxWidth(screen.getByRole("dialog"))).toBe("30rem");

    rerender(<Dialog open wide title="Adjust" onClose={() => {}}><Body /></Dialog>);
    expect(lastMaxWidth(screen.getByRole("dialog"))).toBe("52rem");
  });
});

// ---------------------------------------------------------------------------
// #482 (partial) — event routing across two open dialogs. The scroll-lock and
// background-inertness halves of this guarantee now rest on MUI's own
// `Modal`/`ModalManager` (verified directly against the installed package)
// and cannot be settled in jsdom either way (no layout, no `inert`
// enforcement) — that coverage is Playwright's (#501). What jsdom CAN prove:
// Escape answers the topmost dialog only, and closing one dialog leaves the
// other's content intact.
// ---------------------------------------------------------------------------

// Two independent dialogs on one page, as SalesPage has.
function TwoHost() {
  const [a, setA] = useState(false);
  const [b, setB] = useState(false);
  return (
    <>
      <button onClick={() => setA(true)}>Open A</button>
      <button onClick={() => setB(true)}>Open B</button>
      <Dialog open={a} title="Dialog A" onClose={() => setA(false)}>
        <input aria-label="A field" />
      </Dialog>
      <Dialog open={b} title="Dialog B" onClose={() => setB(false)}>
        <input aria-label="B field" />
      </Dialog>
    </>
  );
}

describe("Dialog with another dialog open (#482 event routing, #483 stacked focus)", () => {
  it("closes only the topmost dialog on Escape", async () => {
    // Both instances used to listen on `document`, so one Escape ran both
    // handlers and silently discarded whatever was typed in the lower form.
    // MUI's Modal now scopes this itself (`useModal.js`'s `isTopModal()`
    // check), so this test is really pinning MUI's own wiring is reached
    // correctly through this wrapper's props, not re-deriving the fix.
    const user = userEvent.setup();
    render(<TwoHost />);
    await user.click(screen.getByRole("button", { name: "Open A", hidden: true }));
    await user.click(screen.getByRole("button", { name: "Open B", hidden: true }));

    await user.keyboard("{Escape}");

    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Dialog B" })).toBeNull());
    expect(screen.getByRole("dialog", { name: "Dialog A" })).toBeInTheDocument();
  });

  it("moves focus into the remaining dialog when the closed one's trigger lives outside it", async () => {
    // Dialog B's own trigger ("Open B") is a PAGE button, not something inside
    // Dialog A. Closing B leaves A topmost, and MUI's FocusTrap restore
    // targets B's own trigger by default — which is exactly wrong here: A is
    // still open and still needs a live cursor inside it.
    const user = userEvent.setup();
    render(<TwoHost />);
    await user.click(screen.getByRole("button", { name: "Open A", hidden: true }));
    await user.click(screen.getByRole("button", { name: "Open B", hidden: true }));
    await waitFor(() => expect(screen.getByLabelText("B field")).toHaveFocus());

    await user.click(within(screen.getByRole("dialog", { name: "Dialog B" }))
      .getByRole("button", { name: "Close" }));

    await waitFor(() => expect(screen.getByLabelText("A field")).toHaveFocus());
  });

  // The redirect above must not fire when it is not needed. A (bottom) can
  // close from an unrelated effect while the user is genuinely mid-typing in
  // B (top) — B never moved, so A's cleanup running is not the user's cue to
  // yank focus off whatever field they are in.
  it("leaves focus alone when it is already inside the dialog that stays open", async () => {
    function TwoHostWithProgrammaticClose() {
      const [a, setA] = useState(false);
      const [b, setB] = useState(false);
      return (
        <>
          <button onClick={() => setA(true)}>Open A</button>
          <button onClick={() => setB(true)}>Open B</button>
          <button onClick={() => setA(false)}>Close A directly</button>
          <Dialog open={a} title="Dialog A" onClose={() => setA(false)}>
            <input aria-label="A field" />
          </Dialog>
          <Dialog open={b} title="Dialog B" onClose={() => setB(false)}>
            <input aria-label="B field one" />
            <input aria-label="B field two" />
          </Dialog>
        </>
      );
    }
    const user = userEvent.setup();
    render(<TwoHostWithProgrammaticClose />);
    await user.click(screen.getByRole("button", { name: "Open A", hidden: true }));
    await user.click(screen.getByRole("button", { name: "Open B", hidden: true }));
    await waitFor(() => expect(screen.getByLabelText("B field one")).toHaveFocus());

    const secondField = screen.getByLabelText("B field two");
    await user.click(secondField);
    expect(secondField).toHaveFocus();

    // fireEvent, not userEvent: a real click on a button focuses it first,
    // which would move focus off `secondField` regardless of Dialog's own
    // logic and prove nothing. This dispatches only the click, standing in
    // for a close that isn't a focus-moving user interaction at all — a prop
    // change, a timer, code elsewhere calling the same setter.
    fireEvent.click(screen.getByRole("button", { name: "Close A directly", hidden: true }));

    expect(secondField).toHaveFocus();
  });
});

// ---------------------------------------------------------------------------
// #485 — anyDialogOpen()/onModalStateChange(), now fed by a module-level
// counter rather than the retired open stack.
// ---------------------------------------------------------------------------

describe("Dialog anyDialogOpen / onModalStateChange (#485)", () => {
  it("reports open once the transition settles, and closed once the last dialog exits", async () => {
    const user = userEvent.setup();
    const seen: boolean[] = [];
    const unsubscribe = onModalStateChange((open) => seen.push(open));
    try {
      expect(anyDialogOpen()).toBe(false);
      render(<TwoHost />);

      await user.click(screen.getByRole("button", { name: "Open A", hidden: true }));
      await waitFor(() => expect(anyDialogOpen()).toBe(true));

      await user.click(screen.getByRole("button", { name: "Open B", hidden: true }));
      // Still true — a second dialog opening over the first is not a
      // transition into "open" (it already was), so it does not have to
      // re-notify, only stay correct.
      expect(anyDialogOpen()).toBe(true);

      await user.click(within(screen.getByRole("dialog", { name: "Dialog B" }))
        .getByRole("button", { name: "Close" }));
      // A is still open underneath.
      expect(anyDialogOpen()).toBe(true);

      // B's own exit transition may still be settling (aria-hidden clears once
      // it does), so A can briefly still read as hidden here too.
      await user.click(within(screen.getByRole("dialog", { name: "Dialog A", hidden: true }))
        .getByRole("button", { name: "Close", hidden: true }));
      await waitFor(() => expect(anyDialogOpen()).toBe(false));

      expect(seen).toContain(true);
      expect(seen[seen.length - 1]).toBe(false);
    } finally {
      unsubscribe();
    }
  });

  // Found by this rewrite, not carried over: `openCount` is fed by
  // `onTransitionEnter`/`onTransitionExited`, and the latter only fires after
  // a COMPLETED exit transition. An unmount that skips the transition
  // entirely — a route change while the dialog is still open, or this exact
  // scenario, `unmount()` mid-test — never pays back the increment, so
  // `anyDialogOpen()` would read `true` for the rest of the session. Caught
  // it firsthand: every test in this file left `anyDialogOpen()` poisoned for
  // the next one before `Dialog.tsx`'s own unmount-cleanup effect was added.
  it("does not leak an open count when a dialog unmounts without closing first", async () => {
    const user = userEvent.setup();
    expect(anyDialogOpen()).toBe(false);
    const { unmount } = render(<Host />);

    await user.click(screen.getByRole("button", { name: "New grade" }));
    await waitFor(() => expect(anyDialogOpen()).toBe(true));

    unmount();

    expect(anyDialogOpen()).toBe(false);
  });

  // Same counter, the other way a transition can be skipped: reopening a
  // dialog while its exit transition is still running. react-transition-group
  // fires `onEnter` again for the re-entry but never fires `onExited` for the
  // interrupted exit, so a naive pair of increment/decrement callbacks counts
  // one dialog twice and never gets back to zero.
  it("does not leak an open count when a dialog is reopened before its exit transition finishes", async () => {
    const user = userEvent.setup();
    expect(anyDialogOpen()).toBe(false);
    render(<Host />);

    await user.click(screen.getByRole("button", { name: "New grade" }));
    await waitFor(() => expect(anyDialogOpen()).toBe(true));

    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Close" }));
    await user.click(screen.getByRole("button", { name: "New grade", hidden: true }));
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Close" }));

    await waitFor(() => expect(anyDialogOpen()).toBe(false));
  });

  // #483, the same re-entry: the panel is still mounted during its exit
  // transition, so reopening focuses the first field synchronously, and a
  // capture of "what had focus before this dialog opened" taken after that
  // would record the dialog's own field. Closing would then restore focus to
  // a node about to unmount, and the trigger never gets it back (Codex review
  // of #892).
  it("restores focus to the trigger after a reopen during the exit transition", async () => {
    const user = userEvent.setup();
    render(<Host />);
    const trigger = () => screen.getByRole("button", { name: "New grade", hidden: true });

    await user.click(trigger());
    await waitFor(() => expect(anyDialogOpen()).toBe(true));
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Close" }));
    await user.click(trigger());
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Close" }));

    await waitFor(() => expect(anyDialogOpen()).toBe(false));
    await waitFor(() => expect(trigger()).toHaveFocus());
  });
});


describe("Dialog fixed actions", () => {
  it("keeps Save outside scrolling content, with native validation and Enter submission", async () => {
    const user = userEvent.setup();
    const submit = vi.fn((event: React.FormEvent<HTMLFormElement>) => event.preventDefault());
    render(
      <Dialog open title="Correct expense" onClose={() => {}}
        formProps={{ onSubmit: submit }}
        actions={<DialogActions><button type="submit">Save correction</button></DialogActions>}
      >
        <input aria-label="Description" required />
      </Dialog>,
    );
    const save = screen.getByRole("button", { name: "Save correction" });
    const input = screen.getByRole("textbox", { name: "Description" });
    expect(save.closest(".MuiDialogContent-root")).toBeNull();
    expect(save.closest("form")).toBe(input.closest("form"));
    await user.click(save);
    expect(submit).not.toHaveBeenCalled();
    await user.type(input, "Feed{Enter}");
    expect(submit).toHaveBeenCalledTimes(1);
    await user.click(save);
    expect(submit).toHaveBeenCalledTimes(2);
  });

  it("preserves noValidate for correction forms", async () => {
    const user = userEvent.setup();
    const submit = vi.fn((event: React.FormEvent<HTMLFormElement>) => event.preventDefault());
    render(
      <Dialog open title="Correct item" onClose={() => {}}
        formProps={{ onSubmit: submit, noValidate: true }}
        actions={<DialogActions><button type="submit">Save</button></DialogActions>}
      >
        <input aria-label="Cost" type="number" min="0" defaultValue="-1" />
      </Dialog>,
    );
    await user.click(screen.getByRole("button", { name: "Save" }));
    expect(submit).toHaveBeenCalledTimes(1);
  });
});
