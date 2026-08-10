import { describe, it, expect, vi } from "vitest";
import { render, screen, act, fireEvent } from "@testing-library/react";
import { useState } from "react";
import { Dialog } from "../components/Dialog";
import { UpdatePrompt } from "./UpdatePrompt";
import { registerServiceWorker } from "./registerServiceWorker";

// #485 — #483 makes everything outside the topmost dialog `inert`, which takes
// the update banner out of the accessibility tree, and un-inerting it later
// replays nothing. The fix is an always-mounted, initially-empty live region
// that is written into once the page is the user's own again.
//
// What these tests can and cannot prove, stated plainly because the previous
// version of this file got it wrong: jsdom implements no accessibility tree
// and (as of 29.1.1) not even the `inert` IDL, so NOTHING here observes a real
// announcement. What they do pin is the contract the announcement rests on —
// the region is present and EMPTY while a dialog is open, and carries the
// message only once none is. An implementation that spoke while inert, or that
// never spoke on the way out, fails these. Whether a given screen reader then
// voices that mutation is a browser/AT matter, verified by hand, not here.

vi.mock("./registerServiceWorker", () => ({ registerServiceWorker: vi.fn() }));
const mockRegister = vi.mocked(registerServiceWorker);

/** The always-mounted announcer, distinct from the visible banner. */
const announcer = () => screen.getByRole("status");
const visibleBanner = () => document.querySelector(".update-banner");

/** Renders the given tree and hands back the captured update callback. */
async function renderWithUpdate(ui: React.ReactElement) {
  let announce: ((activate: () => Promise<void>) => void) | undefined;
  mockRegister.mockImplementation(async (onUpdate) => {
    announce = onUpdate;
    return null;
  });
  await act(async () => { render(ui); });
  return {
    announce: async () => {
      await act(async () => { announce?.(vi.fn().mockResolvedValue(undefined)); });
    },
  };
}

describe("UpdatePrompt announcement survives a dialog's inertness (#485)", () => {
  it("stays silent while a dialog is open, then speaks once it closes", async () => {
    function Harness() {
      const [open, setOpen] = useState(true);
      return (
        <>
          <UpdatePrompt />
          <Dialog open={open} title="Edit" onClose={() => setOpen(false)}>
            <input aria-label="Name" />
          </Dialog>
        </>
      );
    }

    const { announce } = await renderWithUpdate(<Harness />);
    // The update lands while the dialog is ALREADY open — the case where the
    // banner mounts straight into an inert subtree and its first render is
    // never announced either.
    await announce();

    // Visible immediately (it is only unreachable, not hidden)...
    expect(visibleBanner()).not.toBeNull();
    // ...but the announcer must NOT be carrying the message yet: writing it
    // into an inert region spends the announcement on a moment nobody hears,
    // and leaves nothing to change on the way out.
    expect(announcer()).toHaveTextContent("");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });

    expect(announcer()).toHaveTextContent("A new version of Cluckwork is ready");
  });

  it("speaks only when the LAST dialog closes, not the first", async () => {
    function TwoDialogs() {
      const [a, setA] = useState(true);
      const [b, setB] = useState(true);
      return (
        <>
          <UpdatePrompt />
          <Dialog open={a} title="A" onClose={() => setA(false)}>
            <input aria-label="A field" />
          </Dialog>
          <Dialog open={b} title="B" onClose={() => setB(false)}>
            <input aria-label="B field" />
          </Dialog>
        </>
      );
    }

    const { announce } = await renderWithUpdate(<TwoDialogs />);
    await announce();
    expect(announcer()).toHaveTextContent("");

    // One down, one still open — the page is still inert, so still silence.
    await act(async () => {
      fireEvent.click(screen.getAllByRole("button", { name: "Close" })[0]);
    });
    expect(announcer()).toHaveTextContent("");

    // The last one closes: NOW it speaks. Asserting this positive half is what
    // stops an implementation that simply never fires from passing (codex +
    // both agents flagged its absence).
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });
    expect(announcer()).toHaveTextContent("A new version of Cluckwork is ready");
  });

  it("goes quiet again under a second dialog, and speaks again when it closes", async () => {
    // A live region only speaks on a CHANGE, so a re-announcement is only
    // possible if the region was blanked in between. This is the test that
    // fails if the message is left sitting in the region permanently.
    function Reopenable() {
      const [open, setOpen] = useState(false);
      return (
        <>
          <UpdatePrompt />
          <button onClick={() => setOpen(true)}>Open</button>
          <Dialog open={open} title="Edit" onClose={() => setOpen(false)}>
            <input aria-label="Name" />
          </Dialog>
        </>
      );
    }

    const { announce } = await renderWithUpdate(<Reopenable />);
    await announce();
    expect(announcer()).toHaveTextContent("A new version of Cluckwork is ready");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Open" }));
    });
    expect(announcer()).toHaveTextContent("");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });
    expect(announcer()).toHaveTextContent("A new version of Cluckwork is ready");
  });

  it("stays silent when one dialog is swapped for another in a single commit", async () => {
    // The stack is momentarily empty between the outgoing dialog's effect
    // cleanup and the incoming one's setup, but the page never becomes the
    // user's own — it is inert before and after. Announcing into that gap
    // spends the message on a moment nobody can hear and leaves nothing to
    // change when a dialog finally does close for real.
    function Swap() {
      const [which, setWhich] = useState<"a" | "b">("a");
      return (
        <>
          <UpdatePrompt />
          <button onClick={() => setWhich("b")}>Swap</button>
          <Dialog open={which === "a"} title="A" onClose={() => {}}>
            <input aria-label="A field" />
          </Dialog>
          <Dialog open={which === "b"} title="B" onClose={() => {}}>
            <input aria-label="B field" />
          </Dialog>
        </>
      );
    }

    const { announce } = await renderWithUpdate(<Swap />);
    await announce();
    expect(announcer()).toHaveTextContent("");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Swap" }));
    });

    // Dialog B is now the open one; the banner is still unreachable.
    expect(screen.getByRole("dialog", { name: "B" })).toBeInTheDocument();
    expect(announcer()).toHaveTextContent("");
  });

  it("stays silent for a banner mounted BELOW the dialog in the tree", async () => {
    // Ordering, not cosmetics: Dialog pushes onto the stack from its own
    // effect, and effects run in tree order, so a banner rendered after it has
    // not subscribed yet at that moment. Told inline, nobody would be
    // listening, and the banner would keep the "no dialog open" reading it
    // took before the dialog existed — announcing into an inert region.
    function DialogFirst() {
      const [open, setOpen] = useState(true);
      return (
        <>
          <Dialog open={open} title="Edit" onClose={() => setOpen(false)}>
            <input aria-label="Name" />
          </Dialog>
          <UpdatePrompt />
        </>
      );
    }

    const { announce } = await renderWithUpdate(<DialogFirst />);
    await announce();
    expect(announcer()).toHaveTextContent("");

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });
    expect(announcer()).toHaveTextContent("A new version of Cluckwork is ready");
  });

  it("says nothing when there is no update, dialog or not", async () => {
    function Harness() {
      const [open, setOpen] = useState(true);
      return (
        <>
          <UpdatePrompt />
          <Dialog open={open} title="Edit" onClose={() => setOpen(false)}>
            <input aria-label="Name" />
          </Dialog>
        </>
      );
    }

    await renderWithUpdate(<Harness />);
    expect(announcer()).toHaveTextContent("");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Close" }));
    });
    // Closing a dialog is not itself news — the region only ever carries a
    // message there is a reason to make.
    expect(announcer()).toHaveTextContent("");
    expect(visibleBanner()).toBeNull();
  });
});
