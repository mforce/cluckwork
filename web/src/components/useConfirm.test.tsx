import { describe, it, expect, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useConfirm } from "./useConfirm";
import i18n from "../i18n";

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
  user.click(screen.getByRole("button", { name: "deplete" }));
const openReason = async (user: ReturnType<typeof userEvent.setup>) =>
  user.click(screen.getByRole("button", { name: "void" }));

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
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("resolves false on Cancel, Escape and a backdrop click", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<Host onSettle={onSettle} />);

    await openConfirm(user);
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(onSettle).toHaveBeenNthCalledWith(1, false));

    await openConfirm(user);
    await user.keyboard("{Escape}");
    await waitFor(() => expect(onSettle).toHaveBeenNthCalledWith(2, false));

    await openConfirm(user);
    await user.click(document.querySelector(".dialog-backdrop")!);
    await waitFor(() => expect(onSettle).toHaveBeenNthCalledWith(3, false));
  });

  it("focuses Cancel on a yes/no, so a stray Enter cannot take the action", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await openConfirm(user);
    expect(screen.getByRole("button", { name: "Cancel" })).toHaveFocus();
  });

  it("focuses the reason field instead, where there is one to fill in", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await openReason(user);
    expect(screen.getByLabelText("Reason *")).toHaveFocus();
  });

  it("resolves the trimmed reason", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<Host onSettle={onSettle} />);
    await openReason(user);

    await user.type(screen.getByLabelText("Reason *"), "  miscounted the tray  ");
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
    await user.type(screen.getByLabelText("Reason *"), "   ");
    await user.click(screen.getByRole("button", { name: "Void order" }));

    expect(await screen.findByText("A reason is required.")).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(onSettle).not.toHaveBeenCalled();

    // The error clears as soon as they start fixing it, not on the next submit.
    await user.type(screen.getByLabelText("Reason *"), "wrong lot");
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
    expect(screen.getByRole("dialog")).toHaveAccessibleDescription(
      "The flock stops accepting new entries.");
  });

  it("wires the blank-reason error to the field and puts the cursor back in it", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await openReason(user);

    const field = screen.getByLabelText("Reason *");
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
    const user = userEvent.setup();
    const { unmount } = render(<Host destructive />);
    await openConfirm(user);
    expect(screen.getByRole("button", { name: "Deplete flock" })).toHaveClass("btn-danger");
    unmount();

    render(<Host />);
    await openConfirm(user);
    expect(screen.getByRole("button", { name: "Deplete flock" })).not.toHaveClass("btn-danger");
  });

  it("clears a stale reason rather than carrying it into the next question", async () => {
    const user = userEvent.setup();
    render(<Host />);

    await openReason(user);
    await user.type(screen.getByLabelText("Reason *"), "typed then abandoned");
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    await openReason(user);
    expect(screen.getByLabelText("Reason *")).toHaveValue("");
  });

  it("settles a pending question when another is asked over it", async () => {
    const user = userEvent.setup();
    const onSettle = vi.fn();
    render(<Host onSettle={onSettle} />);

    await openConfirm(user);
    // Nothing in the app can do this while the modal has focus, but a stranded
    // promise would hang its caller for ever, so it must not depend on that.
    await openReason(user);

    await waitFor(() => expect(onSettle).toHaveBeenCalledWith(false));
    expect(screen.getByRole("dialog")).toHaveAccessibleName("Void this order?");
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
      expect(screen.getByLabelText("REASON-LABEL-MARKER")).toBeInTheDocument();
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
