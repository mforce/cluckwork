import { describe, it, expect, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { Dialog } from "./Dialog";
import i18n from "../i18n";

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

// ---------------------------------------------------------------------------
// Review follow-ups (codex + feature-dev on PR #132)
// ---------------------------------------------------------------------------

// Mirrors the real screens: saving closes the dialog while the write is still
// in flight, and the row's trigger is `disabled={busy}` for one more render.
// focus() is a no-op on a disabled control, so a naive restore drops focus to
// <body> exactly when a keyboard user needs it most.
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
  it("retries on the next frame instead of dropping focus to the page", async () => {
    const user = userEvent.setup();
    render(<BusyHost />);
    const trigger = screen.getByRole("button", { name: "edit" });
    await user.click(trigger);

    await user.click(screen.getByRole("button", { name: "Save" }));

    expect(screen.queryByRole("dialog")).toBeNull();
    // The first restore attempt hit a disabled button; the retry lands it.
    await waitFor(() => expect(trigger).toHaveFocus());
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

describe("Dialog focusKey", () => {
  it("pulls focus back to the first field when the record is swapped underneath", async () => {
    const user = userEvent.setup();
    render(<RebindHost />);
    await user.click(screen.getByRole("button", { name: "correct" }));
    const amount = screen.getByLabelText("Amount");
    expect(amount).toHaveFocus();

    // Move focus off the first field, then rebind to a different record.
    await user.click(screen.getByRole("button", { name: "Save correction" }));
    expect(amount).not.toHaveFocus();
    await user.click(screen.getByRole("button", { name: "simulate conflict" }));

    // The form under the cursor is not the one being filled in any more.
    expect(screen.getByLabelText("Amount")).toHaveFocus();
  });

  it("does not re-grab focus on an unrelated re-render", async () => {
    const user = userEvent.setup();
    render(<RebindHost />);
    await user.click(screen.getByRole("button", { name: "correct" }));

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

  it("lands initial focus past a hidden input", () => {
    render(<SkipHost />);
    expect(screen.getByLabelText("First real field")).toHaveFocus();
  });

  it("treats the last TABBABLE control as the wrap boundary", async () => {
    const user = userEvent.setup();
    render(<SkipHost />);

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
