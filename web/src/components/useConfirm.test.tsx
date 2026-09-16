import { describe, it, expect, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useConfirm } from "./useConfirm";
import type { ChoiceResult } from "./useConfirm";
import i18n from "../i18n";

// #674/#827 — converted onto MUI `Dialog`, `DialogContentText`, `RadioGroup` /
// `FormControlLabel` / `Radio`, `TextField` and `DialogActions` + `Button`.
// Every trigger below lives on the PAGE, alongside the dialog it opens — MUI's
// own `aria-hidden` sweep (#480, now real ARIA rather than the old `inert`,
// which jsdom never enforced) genuinely removes it from role queries while a
// dialog is open, so re-asking over an open dialog needs `hidden: true` to
// find its own trigger. MUI's Fade defers the actual unmount to its exit
// transition (`closeAfterTransition`), so "the dialog is gone" is `waitFor`,
// not an immediate assertion, the same adjustment Dialog.test.tsx needed.

// A realistic host: real triggers, so focus has somewhere to return to, and the
// settled value is reported out rather than scraped from the DOM — that is what
// callers actually consume, and it survives the host unmounting.
function Host({
  onSettle = () => {},
  destructive = false,
}: { onSettle?: (value: boolean | string | null) => void; destructive?: boolean } = {}) {
  const { confirm, askReason, confirmDialog } = useConfirm();
  return (
    <>
      <button
        onClick={() => void confirm({
          title: "Deplete this flock?",
          body: "The flock stops accepting new entries.",
          confirmLabel: "Deplete flock",
          destructive,
        }).then(onSettle)}
      >
        deplete
      </button>
      <button
        onClick={() => void askReason({
          title: "Void this order?",
          body: "The allocated stock returns to the lots it came from.",
          confirmLabel: "Void order",
          destructive,
        }).then(onSettle)}
      >
        void
      </button>
      {confirmDialog}
    </>
  );
}

const openConfirm = async (user: ReturnType<typeof userEvent.setup>) =>
  user.click(screen.getByRole("button", { name: "deplete", hidden: true }));
const openReason = async (user: ReturnType<typeof userEvent.setup>) =>
  user.click(screen.getByRole("button", { name: "void", hidden: true }));

describe("useConfirm", () => {
  it("renders no dialog until something is asked", () => {
    render(<Host />);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("resolves true when the action is taken", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<Host onSettle={onSettle} />);
    await openConfirm(user);

    expect(screen.getByRole("dialog")).toHaveAccessibleName("Deplete this flock?");
    await user.click(screen.getByRole("button", { name: "Deplete flock" }));

    await waitFor(() => expect(onSettle).toHaveBeenCalledWith(true));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("resolves false on Cancel, Escape and a backdrop click", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<Host onSettle={onSettle} />);

    await openConfirm(user);
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(onSettle).toHaveBeenNthCalledWith(1, false));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    await openConfirm(user);
    await user.keyboard("{Escape}");
    await waitFor(() => expect(onSettle).toHaveBeenNthCalledWith(2, false));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    await openConfirm(user);
    // The backdrop keeps its legacy class name as a compatibility hook
    // (slotProps.backdrop.className on MUI's own Backdrop).
    await user.click(document.querySelector(".dialog-backdrop")!);
    await waitFor(() => expect(onSettle).toHaveBeenNthCalledWith(3, false));
  });

  it("focuses Cancel on a yes/no, so a stray Enter cannot take the action", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await openConfirm(user);
    await waitFor(() => expect(screen.getByRole("button", { name: "Cancel" })).toHaveFocus());
  });

  it("focuses the reason field instead, where there is one to fill in", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await openReason(user);
    // "Reason *" is the translated catalog string itself (en.ts's
    // `reasonLabel`), not MUI's own `required` asterisk — that one is
    // `aria-hidden` in this MUI version (checked directly:
    // FormLabel.js's AsteriskComponent carries `aria-hidden: true`), so it
    // would never reach the accessible name on its own. The native
    // `required` attribute is what a screen reader actually announces as
    // required, asserted separately below.
    await waitFor(() => expect(screen.getByRole("textbox", { name: "Reason *" })).toHaveFocus());
    expect(screen.getByRole("textbox", { name: "Reason *" })).toBeRequired();
  });

  it("resolves the trimmed reason", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<Host onSettle={onSettle} />);
    await openReason(user);

    await user.type(screen.getByRole("textbox", { name: "Reason *" }), "  miscounted the tray  ");
    await user.click(screen.getByRole("button", { name: "Void order" }));

    await waitFor(() => expect(onSettle).toHaveBeenCalledWith("miscounted the tray"));
  });

  it("keeps the dialog open on a blank reason and never resolves empty", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<Host onSettle={onSettle} />);
    await openReason(user);

    // Whitespace only: window.prompt's own check ran after it had closed, which
    // is the failure this replaces — the typed text has to survive the error.
    await user.type(screen.getByRole("textbox", { name: "Reason *" }), "   ");
    await user.click(screen.getByRole("button", { name: "Void order" }));

    expect(await screen.findByText("A reason is required.")).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(onSettle).not.toHaveBeenCalled();

    // The error clears as soon as they start fixing it, not on the next submit.
    await user.type(screen.getByRole("textbox", { name: "Reason *" }), "wrong lot");
    expect(screen.queryByText("A reason is required.")).toBeNull();

    await user.click(screen.getByRole("button", { name: "Void order" }));
    await waitFor(() => expect(onSettle).toHaveBeenCalledWith("wrong lot"));
  });

  it("describes the dialog with the consequence, not just the title", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await openConfirm(user);

    // Focus goes straight to a button, so without an accessible description a
    // screen reader announces the question and the control and never what the
    // action actually does — which is the only reason the dialog exists.
    await waitFor(() => expect(screen.getByRole("dialog")).toHaveAccessibleDescription(
      "The flock stops accepting new entries."));
  });

  it("wires the blank-reason error to the field and puts the cursor back in it", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await openReason(user);

    const field = screen.getByRole("textbox", { name: "Reason *" });
    expect(field).toHaveAttribute("aria-invalid", "false");
    expect(field).not.toHaveAttribute("aria-describedby");

    await user.click(screen.getByRole("button", { name: "Void order" }));

    // Announced rather than merely displayed: a screen reader reaches the
    // message through the field, and focus is moved back to hear it.
    const error = screen.getByText("A reason is required.");
    expect(field).toHaveAttribute("aria-invalid", "true");
    expect(field).toHaveAttribute("aria-describedby", error.id);
    expect(field).toHaveFocus();

    await user.keyboard("lot was double-counted");
    expect(field).toHaveAttribute("aria-invalid", "false");
    expect(field).not.toHaveAttribute("aria-describedby");
  });

  it("paints the action red only when the caller says it is destructive", async () => {
    // "Red" is `color="error"` on the MUI `Button` (mapped to `--danger` in
    // FarmThemeProvider), not the hand-rolled `.btn-danger` class — that class
    // stays alive for the many OTHER raw-button callers this slice does not
    // touch (SettingsPage, UsersPage), so it is not a guard on this component.
    const user = userEvent.setup();
    const { unmount } = render(<Host destructive />);
    await openConfirm(user);
    expect(screen.getByRole("button", { name: "Deplete flock" })).toHaveClass("MuiButton-colorError");
    unmount();

    render(<Host />);
    await openConfirm(user);
    expect(screen.getByRole("button", { name: "Deplete flock" })).toHaveClass("MuiButton-colorPrimary");
  });

  it("clears a stale reason rather than carrying it into the next question", async () => {
    const user = userEvent.setup();
    render(<Host />);

    await openReason(user);
    await user.type(screen.getByRole("textbox", { name: "Reason *" }), "typed then abandoned");
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    await openReason(user);
    expect(screen.getByRole("textbox", { name: "Reason *" })).toHaveValue("");
  });

  it("settles a pending question when another is asked over it", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<Host onSettle={onSettle} />);

    await openConfirm(user);
    // Nothing in the app can do this while the modal has focus, but a stranded
    // promise would hang its caller for ever, so it must not depend on that.
    // The dialog stays open the whole time (pending swaps shape, `open` never
    // goes false), so "void" stays reachable through the SAME aria-hidden
    // sweep that hides it from an outside trigger otherwise.
    await user.click(screen.getByRole("button", { name: "void", hidden: true }));

    await waitFor(() => expect(onSettle).toHaveBeenCalledWith(false));
    await waitFor(() => expect(screen.getByRole("dialog")).toHaveAccessibleName("Void this order?"));
  });

  it("settles a pending question when the screen unmounts under it", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    const { unmount } = render(<Host onSettle={onSettle} />);

    await openReason(user);
    unmount();

    // null, not false: askReason's caller reads null as "they backed out".
    await waitFor(() => expect(onSettle).toHaveBeenCalledWith(null));
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 9, batch B1)
// ---------------------------------------------------------------------------

// `useConfirm` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language its rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render — asserting it, even under a non-English locale, would prove nothing
// (CONTRIBUTING-i18n.md's fallback trap). `common` IS translated, but asserting
// "Cancel" under the default lng:"en" has the identical problem. Swap the
// catalog value at runtime instead, the same i18n.addResource technique the
// other Task 8/9 wiring tests use, so each marker only renders if useConfirm
// actually reads the catalog.
describe("useConfirm i18n wiring (#182, Task 9)", () => {
  function withOverride(ns: string, key: string, value: string, run: () => Promise<void> | void) {
    const original = i18n.getResource("en", ns, key) as string;
    i18n.addResource("en", ns, key, value);
    return Promise.resolve(run()).finally(() => {
      i18n.addResource("en", ns, key, original);
    });
  }

  it("reads the reason field's label from the catalog, not a hardcoded literal", async () => {
    const user = userEvent.setup();
    await withOverride("useConfirm", "reasonLabel", "REASON-LABEL-MARKER", async () => {
      render(<Host />);
      await openReason(user);
      // getByRole, not getByLabelText: this field is always `required`, which
      // makes MUI render its own `aria-hidden` asterisk `<span>` alongside the
      // label text -- `getByLabelText`'s association heuristic does not
      // resolve through that reliably (confirmed directly: it fails to find
      // ANY required MUI TextField by its label in this suite, while the
      // identical non-required field resolves fine), where the full
      // accessible-name algorithm behind `getByRole` does.
      expect(screen.getByRole("textbox", { name: "REASON-LABEL-MARKER" })).toBeInTheDocument();
    });
  });

  it("reads the blank-reason error from the catalog", async () => {
    const user = userEvent.setup();
    await withOverride("useConfirm", "reasonRequired", "REASON-REQUIRED-MARKER", async () => {
      render(<Host />);
      await openReason(user);
      await user.click(screen.getByRole("button", { name: "Void order" }));
      expect(await screen.findByText("REASON-REQUIRED-MARKER")).toBeInTheDocument();
    });
  });

  it("reads the Cancel button's label from the shared common.cancel atom", async () => {
    const user = userEvent.setup();
    await withOverride("common", "cancel", "CANCEL-MARKER", async () => {
      render(<Host />);
      await openConfirm(user);
      expect(screen.getByRole("button", { name: "CANCEL-MARKER" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Cancel" })).not.toBeInTheDocument();
    });
  });
});

// #721 — the third shape. Its own host, so the two shapes above stay exactly as
// they were: the union widened underneath them and nothing else may have moved.
function ChoiceHost({
  onSettle = () => {},
  onOtherSettle = () => {},
}: {
  onSettle?: (value: ChoiceResult | null) => void;
  onOtherSettle?: (value: boolean) => void;
} = {}) {
  const { askChoice, confirm, confirmDialog } = useConfirm();
  return (
    <>
      {/* A second shape on the same host, so the supersede case below can ask
          one over the other the way the two older shapes are tested. */}
      <button
        onClick={() => void confirm({
          title: "Deplete this flock?",
          body: "The flock stops accepting new entries.",
          confirmLabel: "Deplete flock",
        }).then(onOtherSettle)}
      >
        deplete
      </button>
      <button
        onClick={() => void askChoice({
          title: "Why is this order below list price?",
          body: "At least one line is priced under list.",
          confirmLabel: "Confirm order",
          choiceLabel: "Discount reason",
          choices: [
            { value: "Volume", label: "Volume" },
            { value: "Other", label: "Other" },
          ],
          noteRequiredFor: ["Other"],
          noteLabel: "Note",
          noteRequiredMessage: "Describe the reason.",
          choiceRequiredMessage: "Choose a discount reason.",
        }).then(onSettle)}
      >
        confirm order
      </button>
      {confirmDialog}
    </>
  );
}

const openChoice = async (user: ReturnType<typeof userEvent.setup>) =>
  user.click(screen.getByRole("button", { name: "confirm order", hidden: true }));

describe("useConfirm askChoice (#721)", () => {
  it("resolves the chosen value with a null note when none is typed", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<ChoiceHost onSettle={onSettle} />);
    await openChoice(user);

    await user.click(screen.getByRole("radio", { name: "Volume" }));
    await user.click(screen.getByRole("button", { name: "Confirm order" }));

    await waitFor(() => expect(onSettle).toHaveBeenCalledWith({ value: "Volume", note: null }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("trims the note it resolves", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<ChoiceHost onSettle={onSettle} />);
    await openChoice(user);

    await user.click(screen.getByRole("radio", { name: "Volume" }));
    await user.type(screen.getByRole("textbox", { name: "Note" }), "  bulk order  ");
    await user.click(screen.getByRole("button", { name: "Confirm order" }));

    await waitFor(() =>
      expect(onSettle).toHaveBeenCalledWith({ value: "Volume", note: "bulk order" }));
  });

  it("refuses with nothing chosen, inline, and keeps the dialog open", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<ChoiceHost onSettle={onSettle} />);
    await openChoice(user);

    await user.click(screen.getByRole("button", { name: "Confirm order" }));

    expect(await screen.findByText("Choose a discount reason.")).toBeInTheDocument();
    expect(onSettle).not.toHaveBeenCalled();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    // Back to the field that refused, not left on the button (same rule the
    // blank-reason path follows).
    expect(screen.getByRole("radio", { name: "Volume" })).toHaveFocus();
  });

  it("demands the note only for the options that name nothing on their own", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<ChoiceHost onSettle={onSettle} />);
    await openChoice(user);

    await user.click(screen.getByRole("radio", { name: "Other" }));
    await user.click(screen.getByRole("button", { name: "Confirm order" }));

    expect(await screen.findByText("Describe the reason.")).toBeInTheDocument();
    expect(onSettle).not.toHaveBeenCalled();
    expect(screen.getByRole("textbox", { name: "Note" })).toHaveFocus();
    // The choice survives the refusal — the whole point of settling inline.
    expect(screen.getByRole("radio", { name: "Other" })).toBeChecked();

    await user.type(screen.getByRole("textbox", { name: "Note" }), "agreed with the buyer");
    await user.click(screen.getByRole("button", { name: "Confirm order" }));
    await waitFor(() =>
      expect(onSettle).toHaveBeenCalledWith({ value: "Other", note: "agreed with the buyer" }));
  });

  it("clears a stale note error when the chosen option changes", async () => {
    const user = userEvent.setup();
    render(<ChoiceHost />);
    await openChoice(user);

    await user.click(screen.getByRole("radio", { name: "Other" }));
    await user.click(screen.getByRole("button", { name: "Confirm order" }));
    expect(await screen.findByText("Describe the reason.")).toBeInTheDocument();

    await user.click(screen.getByRole("radio", { name: "Volume" }));

    expect(screen.queryByText("Describe the reason.")).toBeNull();
  });

  it("resolves null on Cancel", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<ChoiceHost onSettle={onSettle} />);
    await openChoice(user);

    await user.click(screen.getByRole("radio", { name: "Volume" }));
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(onSettle).toHaveBeenCalledWith(null));
  });

  // Design §6 Risk 2: widening the settle union is what this slice does to a
  // hook two screens share, so the new shape owes the same two lifecycle tests
  // the older ones carry.
  it("settles a pending choice when another question is asked over it", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<ChoiceHost onSettle={onSettle} />);

    await openChoice(user);
    await user.click(screen.getByRole("radio", { name: "Volume" }));
    await user.click(screen.getByRole("button", { name: "deplete", hidden: true }));

    // null, not the half-answered choice: they never went through with it.
    await waitFor(() => expect(onSettle).toHaveBeenCalledWith(null));
    await waitFor(() => expect(screen.getByRole("dialog")).toHaveAccessibleName("Deplete this flock?"));
  });

  it("settles a pending confirmation when a choice is asked over it", async () => {
    const user = userEvent.setup();
    const onOtherSettle = vi.fn();
    render(<ChoiceHost onOtherSettle={onOtherSettle} />);

    await user.click(screen.getByRole("button", { name: "deplete", hidden: true }));
    await openChoice(user);

    // false, the confirm shape's own dismissal value — the widened union must
    // not leak the incoming shape's null into the outgoing promise.
    await waitFor(() => expect(onOtherSettle).toHaveBeenCalledWith(false));
    await waitFor(() => expect(screen.getByRole("dialog")).toHaveAccessibleName(
      "Why is this order below list price?"));
  });

  it("settles a pending choice when the screen unmounts under it", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    const { unmount } = render(<ChoiceHost onSettle={onSettle} />);

    await openChoice(user);
    await user.click(screen.getByRole("radio", { name: "Volume" }));
    unmount();

    await waitFor(() => expect(onSettle).toHaveBeenCalledWith(null));
  });

  it("forgets a previous answer when the same question is asked again", async () => {
    const user = userEvent.setup();
    render(<ChoiceHost />);
    await openChoice(user);
    await user.click(screen.getByRole("radio", { name: "Volume" }));
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    await openChoice(user);

    expect(screen.getByRole("radio", { name: "Volume" })).not.toBeChecked();
    expect(screen.getByRole("textbox", { name: "Note" })).toHaveValue("");
  });
});
