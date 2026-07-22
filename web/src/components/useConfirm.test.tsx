import { describe, it, expect, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useConfirm } from "./useConfirm";

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
