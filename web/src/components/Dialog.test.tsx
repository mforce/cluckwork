import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { Dialog } from "./Dialog";

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
function Host({ onClose }: { onClose?: () => void } = {}) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button onClick={() => setOpen(true)}>New grade</button>
      <Dialog
        open={open}
        title="New grade"
        onClose={() => { setOpen(false); onClose?.(); }}
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
    expect(screen.getByLabelText("Name")).toHaveFocus();
  });

  it("closes on Escape and returns focus to the trigger", async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<Host onClose={onClose} />);
    const trigger = screen.getByRole("button", { name: "New grade" });
    await user.click(trigger);

    await user.keyboard("{Escape}");

    expect(onClose).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(trigger).toHaveFocus();
  });

  it("closes on the close button", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await user.click(screen.getByRole("button", { name: "New grade" }));

    await user.click(screen.getByRole("button", { name: "Close" }));

    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("closes on a backdrop click but not on a click inside the panel", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await user.click(screen.getByRole("button", { name: "New grade" }));

    // A click that lands on the panel bubbles to the backdrop handler; it must
    // not be mistaken for a dismiss.
    await user.click(screen.getByRole("dialog"));
    expect(screen.getByRole("dialog")).toBeInTheDocument();

    await user.click(document.querySelector(".dialog-backdrop")!);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("traps Tab inside the panel in both directions", async () => {
    const user = userEvent.setup();
    render(<Host />);
    await user.click(screen.getByRole("button", { name: "New grade" }));

    const name = screen.getByLabelText("Name");
    const close = screen.getByRole("button", { name: "Close" });
    const save = screen.getByRole("button", { name: "Save" });

    expect(name).toHaveFocus();
    await user.tab();
    expect(screen.getByLabelText("Note")).toHaveFocus();
    await user.tab();
    expect(save).toHaveFocus();
    // Past the last control it wraps to the first — the close button, which is
    // first in DOM order — rather than escaping to the page behind.
    await user.tab();
    expect(close).toHaveFocus();

    await user.tab({ shift: true });
    expect(save).toHaveFocus();
  });

  it("locks page scroll while open and restores it on close", async () => {
    const user = userEvent.setup();
    document.body.style.overflow = "visible";
    render(<Host />);

    await user.click(screen.getByRole("button", { name: "New grade" }));
    expect(document.body.style.overflow).toBe("hidden");

    await user.keyboard("{Escape}");
    expect(document.body.style.overflow).toBe("visible");
  });
});
